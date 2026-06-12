using Scalar.AspNetCore;
using System.Globalization;
using System.Net.ServerSentEvents;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Text.Json;
using System.Text.Json.Serialization;
using VueApp1.Server;
using VueApp1.Server.Agent;
using VueApp1.Server.Agent.Attachments;
using VueApp1.Server.Agent.Skills;
using VueApp1.Server.ExceptionHandlers;
using VueApp1.Server.Idempotency;
using VueApp1.Server.Mcp;
using VueApp1.Server.Mcp.Tools;
using VueApp1.Server.Middleware;
using VueApp1.Server.OpenApi;
using VueApp1.Server.Services;

var builder = WebApplication.CreateBuilder(args);

// Fail fast on configuration drift: an unknown key under "Performance" fails
// the bind (ErrorOnUnknownConfiguration) and invalid values throw HERE — this
// instance configures the host itself (Kestrel limits, caches, rate limiter)
// before Build(), so it cannot wait for the ValidateOnStart() pass that
// guards the DI-resolved copy in SetupOptionsValidation.
var performance = builder.Configuration.GetSection(PerformanceTuningOptions.SectionName)
        .Get<PerformanceTuningOptions>(binder => binder.ErrorOnUnknownConfiguration = true)
    ?? new PerformanceTuningOptions();
ThrowIfInvalid<PerformanceTuningOptions>(
    new PerformanceTuningOptionsValidator().Validate(name: null, performance));

// -- Services --
SetupOptionsValidation(builder);
SetupApi(builder);
SetupKestrelLimits(builder, performance);
SetupCors(builder);
SetupHealthChecks(builder);
SetupCompression(builder);
SetupOutputCache(builder, performance);
SetupRateLimiting(builder, performance);
if (performance.RequestTimeout.Enabled)
{
    SetupRequestTimeouts(builder, performance);
}
if (builder.Configuration.GetValue<bool>("OpenTelemetry:Enabled"))
{
    SetupTelemetry(builder);
}
var mcpEnabled = builder.Configuration.GetValue<bool>("Mcp:Enabled");
var agentEnabled = builder.Configuration.GetValue<bool>("Agent:Enabled");
if (mcpEnabled || agentEnabled)
{
    // ONE tool registry, two consumers: the /mcp endpoint (external MCP
    // clients) and the in-process agent loop both consume the same
    // McpServerTool singletons this registers. The HTTP transport is added
    // only when /mcp is actually mapped — agent-only boots carry tool
    // definitions but zero MCP endpoint surface.
    var mcpBuilder = SetupMcpTools(builder);
    if (mcpEnabled)
    {
        mcpBuilder.WithHttpTransport(options => options.Stateless = true);
    }
}

if (agentEnabled)
{
    SetupAgent(builder);
}

var app = builder.Build();

// -- Middleware pipeline --
ConfigurePipeline(app, performance);

app.Run();

// ---------------------------------------------------------------------------

static void SetupOptionsValidation(WebApplicationBuilder builder)
{
    // The backend twin of the frontend's strictImportMetaEnv: configuration is
    // a validated contract. Bind + ValidateOnStart kill the host during
    // startup (pre-traffic) with every violation listed, instead of running
    // on silent defaults until rate limits or generated links misbehave in
    // production. Bind new options classes the same way.
    builder.Services.AddSingleton<IValidateOptions<PerformanceTuningOptions>, PerformanceTuningOptionsValidator>();
    builder.Services.AddOptions<PerformanceTuningOptions>()
        .Bind(
            builder.Configuration.GetSection(PerformanceTuningOptions.SectionName),
            binder => binder.ErrorOnUnknownConfiguration = true)
        .ValidateOnStart();

    builder.Services.AddSingleton<IValidateOptions<PublicUriOptions>, PublicUriOptionsValidator>();
    builder.Services.AddOptions<PublicUriOptions>()
        .Bind(
            builder.Configuration.GetSection(PublicUriOptions.SectionName),
            binder => binder.ErrorOnUnknownConfiguration = true)
        .ValidateOnStart();
}

static void ThrowIfInvalid<TOptions>(ValidateOptionsResult validation)
{
    if (validation.Failed)
    {
        throw new OptionsValidationException(
            Options.DefaultName, typeof(TOptions), validation.Failures);
    }
}

static void SetupApi(WebApplicationBuilder builder)
{
    // Strict JSON numbers: the Web defaults quietly accept numbers-as-strings
    // ("5" for an int), and the schema exporter truthfully documents that as
    // ["integer","string"] unions — which generated TypeScript clients then
    // inherit as `number | string`. Strict keeps runtime and contract clean
    // (set on BOTH options objects so controllers and minimal APIs agree).
    builder.Services.AddControllers()
        .AddJsonOptions(options =>
            options.JsonSerializerOptions.NumberHandling = JsonNumberHandling.Strict);
    builder.Services.ConfigureHttpJsonOptions(options =>
        options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict);
    // Generated URLs and the OpenAPI contract use lowercase paths
    // (route matching is case-insensitive either way).
    builder.Services.AddRouting(options => options.LowercaseUrls = true);
    builder.Services.AddOpenApi(options =>
    {
        options.AddScalarTransformers();
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info.Title = "VueApp1 API";
            document.Info.Description =
                "RFC 9457 problem details on every error; see /scalar/v1 for interactive docs.";
            return Task.CompletedTask;
        });
        // Error-contract truth (docs/API.md "Error contract in the OpenAPI
        // document"): document the global 429, relabel declared 4xx/5xx as
        // problem+json, keep computed properties required in responses, and
        // declare the replay marker on Idempotency-Key-guarded actions.
        options.AddSchemaTransformer<ComputedPropertySchemaTransformer>();
        options.AddOperationTransformer<RateLimitResponseTransformer>();
        options.AddOperationTransformer<IdempotencyReplayedHeaderTransformer>();
        options.AddOperationTransformer<ProblemDetailsContentTypeTransformer>();
        options.AddOperationTransformer<CanonicalJsonContentTransformer>();
    });
    // The handler enriches unhandled exceptions with traceId (+ details in
    // Development) before AddProblemDetails' default machinery writes them.
    builder.Services.AddExceptionHandler<ApiProblemDetailsExceptionHandler>();
    builder.Services.AddProblemDetails();
    builder.Services.AddSingleton(TimeProvider.System);
    // HybridCache: in-process L1 with stampede protection; add a distributed
    // L2 by registering IDistributedCache (e.g. AddStackExchangeRedisCache) —
    // HybridCache picks it up automatically. (The in-memory IDistributedCache
    // that AddIdempotency registers below does NOT become an L2: HybridCache
    // special-cases MemoryDistributedCache as not actually distributed and
    // ignores it.) Rule of thumb: L1 expiry ~1/6 of L2.
    builder.Services.AddHybridCache();
    // Outbound HTTP with retries/timeouts/circuit-breaker when you add a client
    // (package: Microsoft.Extensions.Http.Resilience). WARNING: the standard
    // handler retries unsafe methods (POST/PATCH/DELETE) by default — an
    // auto-retried POST is a duplicate payment/webhook/email. Keep retries
    // idempotent-only; full discipline (typed clients, User-Agent, and the
    // SSRF-safe handler for user-supplied URLs): docs/PATTERNS.md "Outbound HTTP".
    // builder.Services.AddHttpClient("backend")
    //     .AddStandardResilienceHandler(o => o.Retry.DisableForUnsafeHttpMethods());
    // Run-after-the-response work (the post-signup email, cache warmup, ...):
    // BackgroundWork/ ships a fully tested bounded-channel queue + draining
    // hosted service that captures/restores ambient context (trace, culture,
    // initiator stamp) across the enqueue boundary. Dormant until the first
    // consumer uncomments it — decision guide: docs/BACKGROUND.md.
    // builder.Services.AddBackgroundWorkQueue();
    // Idempotency-Key seam for unsafe endpoints that clients retry
    // (mobile/PWA networks, agent tool callers). Single-node guarantee with
    // the in-memory defaults; cross-process upgrades in docs/PATTERNS.md.
    // FeedbackController is the usage shape.
    builder.Services.AddIdempotency();
    builder.Services.AddScoped<IWeatherForecastService, WeatherForecastService>();
    builder.Services.AddScoped<IFeedbackService, FeedbackService>();
    // Host-header-injection-safe absolute links (emails, notifications):
    // generated from the configured PublicUri (bound + validated in
    // SetupOptionsValidation), never from request headers.
    builder.Services.AddSingleton<IUriLinkGenerator, UriLinkGenerator>();
}

static void SetupKestrelLimits(WebApplicationBuilder builder, PerformanceTuningOptions performance)
{
    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        // Generous defaults, config-driven via the Performance section:
        // a body-size cap plus a minimum upload rate (slow-loris guard).
        kestrel.Limits.MaxRequestBodySize = performance.RequestLimits.MaxRequestBodySizeBytes;
        kestrel.Limits.MinRequestBodyDataRate = new Microsoft.AspNetCore.Server.Kestrel.Core.MinDataRate(
            bytesPerSecond: performance.RequestLimits.MinBodyDataRateBytesPerSecond,
            gracePeriod: TimeSpan.FromSeconds(performance.RequestLimits.MinBodyDataRateGraceSeconds));
    });
}

static void SetupCors(WebApplicationBuilder builder)
{
    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            policy.WithOrigins(
                    builder.Configuration["SpaProxyServerUrl"] ?? "https://localhost:57292")
                .AllowAnyHeader()
                .AllowAnyMethod();
        });
    });
}

static void SetupHealthChecks(WebApplicationBuilder builder)
{
    builder.Services.AddHealthChecks();
    // When you add a database, tag the check "ready" so it gates readiness
    // (drain traffic while the dependency is down) without failing liveness
    // (which would make the orchestrator restart a healthy process):
    // .AddDbContextCheck<AppDbContext>(tags: ["ready"]);
}

static void SetupCompression(WebApplicationBuilder builder)
{
    builder.Services.AddResponseCompression(options =>
    {
        options.EnableForHttps = true;
        // Missing from the built-in defaults; static copies are pre-compressed
        // at publish, but dynamic/dev responses of these types benefit too.
        options.MimeTypes =
        [
            .. Microsoft.AspNetCore.ResponseCompression.ResponseCompressionDefaults.MimeTypes,
            "application/manifest+json",
            "image/svg+xml",
        ];
    });
}

static void SetupOutputCache(WebApplicationBuilder builder, PerformanceTuningOptions performance)
{
    builder.Services.AddOutputCache(options =>
    {
        options.SizeLimit = performance.OutputCache.SizeLimitBytes;
        options.MaximumBodySize = performance.OutputCache.MaximumBodySizeBytes;
        options.DefaultExpirationTimeSpan = TimeSpan.FromSeconds(performance.OutputCache.DefaultExpirationSeconds);
        options.AddPolicy(
            "api-read",
            policy => policy.Expire(TimeSpan.FromSeconds(performance.OutputCache.ReadExpirationSeconds)));
    });
}

static void SetupRateLimiting(WebApplicationBuilder builder, PerformanceTuningOptions performance)
{
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = async (context, _) =>
        {
            // Prefer the rejected lease's own reset time (fixed-window leases
            // expose it as RetryAfter metadata); the configured window length
            // is only the fallback. Hardcoding the window would overstate the
            // wait for rejections late in the window.
            var retryAfterSeconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var reset)
                ? Math.Max(1, (int)Math.Ceiling(reset.TotalSeconds))
                : performance.RateLimiting.WindowSeconds;
            context.HttpContext.Response.Headers.RetryAfter =
                retryAfterSeconds.ToString(CultureInfo.InvariantCulture);

            // IProblemDetailsService, not WriteAsJsonAsync: the rejection must
            // go out as application/problem+json like every other error —
            // WriteAsJsonAsync would mislabel it as plain application/json.
            // TryWriteAsync, not WriteAsync: when no registered writer can
            // satisfy an exotic Accept header (e.g. text/html without */*),
            // a bodiless 429 — status and Retry-After are already set — beats
            // an exception escaping into the exception handler.
            var problemDetailsService =
                context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
            await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = context.HttpContext,
                ProblemDetails =
                {
                    Status = StatusCodes.Status429TooManyRequests,
                    Title = "Too many requests",
                    Detail = $"Rate limit exceeded. Retry after {retryAfterSeconds} seconds.",
                },
            });
        };

        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        {
            var partitionKey = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = performance.RateLimiting.PermitLimit,
                QueueLimit = performance.RateLimiting.QueueLimit,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                Window = TimeSpan.FromSeconds(performance.RateLimiting.WindowSeconds),
                AutoReplenishment = true,
            });
        });
    });
}

static void SetupRequestTimeouts(WebApplicationBuilder builder, PerformanceTuningOptions performance)
{
    builder.Services.AddRequestTimeouts(options =>
    {
        options.DefaultPolicy = new()
        {
            Timeout = TimeSpan.FromSeconds(performance.RequestTimeout.DefaultTimeoutSeconds),
        };
        options.AddPolicy("long-running", TimeSpan.FromSeconds(performance.RequestTimeout.LongRunningTimeoutSeconds));
    });
}

static void SetupTelemetry(WebApplicationBuilder builder)
{
    var serviceName = builder.Environment.ApplicationName;
    // Config key wins; the standard OTEL_EXPORTER_OTLP_ENDPOINT env var is
    // honored natively by AddOtlpExporter when no explicit endpoint is set.
    var endpoint = builder.Configuration["OpenTelemetry:Otlp:Endpoint"];
    var hasOtlpEndpoint = !string.IsNullOrWhiteSpace(endpoint)
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT"));

    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService(serviceName))
        .WithTracing(tracing =>
        {
            tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                // Wildcard: every ActivitySource under the app namespace is
                // collected without touching this setup again.
                .AddSource("VueApp1.*");

            if (hasOtlpEndpoint)
            {
                tracing.AddOtlpExporter(options =>
                {
                    if (!string.IsNullOrWhiteSpace(endpoint))
                    {
                        options.Endpoint = new Uri(endpoint);
                    }
                });
            }
        })
        .WithMetrics(metrics =>
        {
            metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter("VueApp1.*");

            if (hasOtlpEndpoint)
            {
                metrics.AddOtlpExporter(options =>
                {
                    if (!string.IsNullOrWhiteSpace(endpoint))
                    {
                        options.Endpoint = new Uri(endpoint);
                    }
                });
            }
        });
}

static IMcpServerBuilder SetupMcpTools(WebApplicationBuilder builder)
{
    // Opt-in MCP server hosted INSIDE this API (docs/MCP.md). Stateless
    // Streamable HTTP: every POST is self-contained — no session affinity,
    // so it scales horizontally and matches where the MCP spec is heading.
    // Tools live in Mcp/Tools and delegate to the SAME services the
    // controllers use; add new tool classes with another WithTools<T>() call.
    // This method registers serverInfo/instructions/tools ONLY — no
    // endpoints, no hosted service, no transport. It runs for
    // Mcp:Enabled OR Agent:Enabled (the agent loop dispatches in-process to
    // these same tool singletons via McpToolAdapter); the /mcp surface
    // itself stays gated on Mcp:Enabled alone.
    return builder.Services.AddMcpServer(options =>
        {
            options.ServerInfo = new()
            {
                Name = "VueApp1",
                Title = "VueApp1 MCP server",
                Version = "1.0.0",
            };
            // Instructions prime every connected agent. Keep them short and
            // contract-focused: how to read results and how to react to the
            // error envelope (docs/MCP.md).
            options.ServerInstructions =
                "Tools delegate to the same service layer as the REST API. "
                + "Successful calls return JSON in structuredContent (non-object values wrapped as "
                + "{ result: ... }, mirrored as raw JSON text). "
                + "Failures set isError with a JSON envelope { code, status?, type?, title?, detail? }; "
                + "branch on the stable `code` (e.g. not_found, invalid_parameter, conflict, rate_limited) "
                + "instead of parsing messages, and back off before retrying rate_limited.";
        })
        .WithTools<WeatherTools>();
}

static void SetupAgent(WebApplicationBuilder builder)
{
    // Opt-in agent loop (docs/AGENT.md): SSE turns over the in-process MCP
    // tool registry, approval gate, finally-accounted usage ledger. Same
    // honest banner as Idempotency/BackgroundWork: the conversation store,
    // pending approvals, turn lock and ledger are IN-MEMORY — process-local,
    // single-node, lost on restart. Durable seams: docs/AGENT.md.
    builder.Services.AddSingleton<IValidateOptions<AgentOptions>>(
        provider => new AgentOptionsValidator(provider));
    builder.Services.AddOptions<AgentOptions>()
        .Bind(
            builder.Configuration.GetSection(AgentOptions.SectionName),
            binder => binder.ErrorOnUnknownConfiguration = true)
        .ValidateOnStart();
    // The provider seam (docs/AGENT.md "Providers"): AgentChatClientFactory
    // adapts the configured provider SDK to IChatClient once at startup.
    // LAST registration wins resolution, so anything registered after this —
    // the tests' scripted FakeChatClient, or your own adapter — replaces the
    // factory wholesale; TryAdd additionally skips it when a client was
    // registered earlier. The lambda deliberately re-binds AgentOptions from
    // IConfiguration instead of resolving IOptions<AgentOptions>: the
    // validator below resolves IChatClient DURING options validation, and an
    // IOptions dependency here would re-enter options creation mid-validation.
    builder.Services.TryAddSingleton<IChatClient>(provider =>
    {
        var configuration = provider.GetRequiredService<IConfiguration>();
        var options = configuration.GetSection(AgentOptions.SectionName).Get<AgentOptions>() ?? new AgentOptions();
        return AgentChatClientFactory.Create(options, configuration);
    });
    builder.Services.AddSingleton<McpToolAdapter>();
    builder.Services.AddSingleton<AgentToolPolicy>();
    // App-runtime skills (docs/AGENT.md "Skills") — distinct from the repo's
    // .claude/skills (those teach coding agents working ON this codebase;
    // these teach the agent running INSIDE the app). Same SKILL.md format,
    // different consumers. Constructed lazily; the options validator resolves
    // it at boot when the module is enabled, so a malformed shipped skill
    // fails startup, never a first turn.
    builder.Services.AddSingleton(static _ => new FileSystemSkillCatalog(
        Path.Combine(AppContext.BaseDirectory, "Agent", "Skills")));
    builder.Services.AddSingleton<IAgentConversationStore, InMemoryAgentConversationStore>();
    // Attachment blobs: in-memory and bounded (oldest evicted) — refs whose
    // bytes are gone hydrate as a placeholder, never as a failed turn.
    // Durable upgrade: an IAttachmentStore over blob storage (docs/AGENT.md).
    builder.Services.AddSingleton<IAttachmentStore, InMemoryAttachmentStore>();
    builder.Services.AddSingleton<AgentMessageBuilder>();
    builder.Services.AddSingleton<AgentUsageLedger>();
    // Scoped: the loop's DI scope IS the turn scope — bridged tool calls
    // resolve their constructor dependencies from it.
    builder.Services.AddScoped<AgentLoopService>();
}

static void ConfigureSecurityHeaders(WebApplication app)
{
    var isDevelopment = app.Environment.IsDevelopment();

    app.UseSecurityHeaders(policies =>
    {
        // nosniff, frame deny, referrer policy, HSTS (HTTPS, non-localhost),
        // and Server-header removal. HSTS here replaces a separate UseHsts().
        policies.AddDefaultSecurityHeaders();
        policies.AddCrossOriginOpenerPolicy(b => b.SameOrigin());
        policies.AddPermissionsPolicy(b =>
        {
            b.AddCamera().None();
            b.AddMicrophone().None();
            b.AddGeolocation().None();
        });
        policies.AddContentSecurityPolicy(csp =>
        {
            csp.AddDefaultSrc().Self();
            csp.AddBaseUri().Self();
            csp.AddFormAction().Self();
            csp.AddFrameAncestors().None();
            csp.AddObjectSrc().None();
            csp.AddImgSrc().Self().Data();
            csp.AddManifestSrc().Self();
            csp.AddWorkerSrc().Self();
            csp.AddScriptSrc().Self();
            var style = csp.AddStyleSrc().Self();
            var connect = csp.AddConnectSrc().Self();
            if (isDevelopment)
            {
                // Vite dev server/HMR injects inline styles and uses websockets.
                style.UnsafeInline();
                connect.From("ws:").From("wss:");
            }
        });
    });
}

static void ConfigurePipeline(WebApplication app, PerformanceTuningOptions performance)
{
    // Order matters: headers apply to every response (including errors),
    // and scanner probes die before touching routing or telemetry.
    ConfigureSecurityHeaders(app);
    app.UseExploitProbeDenyList();

    var exposeOpenApi = app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("OpenApi:Enabled");
    if (exposeOpenApi)
    {
        app.MapOpenApi();

        // RFC 9727 API catalog: machine-readable discovery of this host's API docs.
        var apiCatalog = new Dictionary<string, object>
        {
            ["linkset"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["anchor"] = "/api",
                    ["service-desc"] = new[]
                    {
                        new Dictionary<string, string>
                        {
                            ["href"] = "/openapi/v1.json",
                            ["type"] = "application/json",
                        },
                    },
                },
            },
        };
        app.MapGet(
            "/.well-known/api-catalog",
            () => Results.Json(apiCatalog, contentType: "application/linkset+json"));
    }

    if (app.Environment.IsDevelopment())
    {
        app.MapScalarApiReference(options => options
            .WithTitle("VueApp1 API")
            .WithDefaultHttpClient(ScalarTarget.CSharp, ScalarClient.HttpClient));
    }

    app.UseExceptionHandler();
    app.UseStatusCodePages();

    // HSTS is emitted by the security headers policy above.
    app.UseHttpsRedirection();
    app.UseResponseCompression();
    app.UseRouting();
    app.UseServerTiming();
    app.UseCors();
    if (performance.RequestTimeout.Enabled)
    {
        app.UseRequestTimeouts();
    }
    app.UseRateLimiter();
    app.UseOutputCache();
    app.UseAuthorization();

    // Long-term caching contract for the SPA:
    //   /assets/* and workbox-*.js  -> immutable (Vite emits ONLY content-hashed
    //                                  filenames there; a new build = new URL)
    //   index.html, sw.js, manifest.webmanifest -> no-cache (revalidate, so
    //                                  deployments and SW updates propagate)
    // MapStaticAssets can't infer this itself: it only marks assets immutable
    // when fingerprinted by its OWN naming convention, and Vite's hashes are
    // opaque to it — measured result was no-cache on every hashed asset, i.e.
    // a conditional-GET round trip per file on every warm load. The override
    // runs in OnStarting (LIFO: registered first, runs last) so it wins over
    // whatever the static endpoint wrote.
    app.Use(async (context, next) =>
    {
        var path = context.Request.Path;
        var isHashedAsset = path.StartsWithSegments("/assets")
            || (path.Value is { } p
                && p.StartsWith("/workbox-", StringComparison.Ordinal)
                && p.EndsWith(".js", StringComparison.Ordinal));
        if (isHashedAsset)
        {
            context.Response.OnStarting(() =>
            {
                if (context.Response.StatusCode
                    is StatusCodes.Status200OK
                    or StatusCodes.Status304NotModified)
                {
                    context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
                }

                return Task.CompletedTask;
            });
        }

        await next();
    });

    // Opt-in MCP endpoint (SetupMcp). Hardening, in the same spirit as the
    // REST surface:
    // - ValidateOriginFilter: AllowedHosts-derived Origin allowlist
    //   (DNS-rebinding / CSRF defense-in-depth; non-browser MCP clients send
    //   no Origin header and pass).
    // - The global IP-partitioned rate limiter already covers /mcp — the
    //   GlobalLimiter applies to every endpoint, no per-endpoint opt-in.
    // - ExcludeFromDescription keeps /mcp out of the OpenAPI contract: it
    //   speaks JSON-RPC, not the documented REST error contract.
    // - NO auth by default (template-wide stance): do not expose /mcp beyond
    //   trusted networks without adding authentication (docs/MCP.md, docs/AUTH.md).
    if (app.Configuration.GetValue<bool>("Mcp:Enabled"))
    {
        app.MapMcp("/mcp")
            .AddEndpointFilter(new ValidateOriginFilter(app.Configuration))
            .ExcludeFromDescription();

        // Stateless Streamable HTTP maps ONLY POST (no stream to resume, no
        // server-initiated messages, no session to delete). Without this,
        // GET/HEAD /mcp would fall through to the SPA fallback and hand
        // index.html to a protocol client expecting an event stream — the
        // /api philosophy applies: protocol paths answer with ProblemDetails,
        // never the SPA shell. (Remove if you switch Stateless off — the
        // transport then maps GET and DELETE itself.)
        app.MapMethods("/mcp", ["GET", "HEAD", "DELETE"], async context =>
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers.Allow = "POST";
            var problemDetailsService = context.RequestServices.GetRequiredService<IProblemDetailsService>();
            await problemDetailsService.WriteAsync(new ProblemDetailsContext
            {
                HttpContext = context,
                ProblemDetails =
                {
                    Status = StatusCodes.Status405MethodNotAllowed,
                    Title = "Method Not Allowed",
                    Detail = "The MCP endpoint is stateless Streamable HTTP: POST only.",
                },
            });
        }).ExcludeFromDescription();
    }

    // Opt-in agent surface (SetupAgent). Same hardening posture as /mcp:
    // the global IP rate limiter already covers /api/agent, the endpoints
    // are ExcludeFromDescription (a flag-gated surface must never drift the
    // committed OpenAPI contract), and there is NO auth by default — do not
    // expose beyond trusted networks without adding authentication
    // (docs/AGENT.md, docs/AUTH.md).
    if (app.Configuration.GetValue<bool>("Agent:Enabled"))
    {
        MapAgentEndpoints(app);
    }

    app.MapStaticAssets();
    // Safety net for wwwroot content added AFTER publish (e.g. the Docker image
    // copies the SPA dist in at image-assembly time): MapStaticAssets only
    // serves files from its build-time manifest, and MapFallback skips
    // file-like paths — without this, /assets/*.js and sw.js 404 in containers.
    app.UseStaticFiles();
    app.MapControllers();

    // Orchestrator probe pair (Kubernetes, Azure Container Apps, ...) via the
    // standard tag-filter idiom:
    // - liveness runs NO checks (Predicate = _ => false): it answers only
    //   "is the process up?", so a dependency outage can never trigger a
    //   restart loop of an otherwise healthy process;
    // - readiness runs the "ready"-tagged checks (none by default — the
    //   DbContext seam in SetupHealthChecks plugs in here), so a failing
    //   dependency drains traffic instead of killing the container.
    app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
    app.MapHealthChecks(
        "/health/ready",
        new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
    // /health stays as a readiness-filtered alias for single-path consumers
    // (uptime monitors, platform defaults that probe one path). Filtered
    // on purpose: an unfiltered catch-all would feed future "ready"-tagged
    // checks to anything probing it as liveness — the restart foot-gun the
    // split exists to prevent.
    app.MapHealthChecks(
        "/health",
        new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

    // Unmatched /api routes get an RFC 9457 404 instead of the SPA shell.
    // Registered as a more specific fallback pattern, so it wins over the
    // file fallback below for anything under /api.
    app.MapFallback("/api/{**path}", async context =>
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        var problemDetailsService = context.RequestServices.GetRequiredService<IProblemDetailsService>();
        await problemDetailsService.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails =
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Not Found",
                Detail = $"No API endpoint matches path '{context.Request.Path}'.",
            },
        });
    });

    // SPA fallback via the static-file machinery (NOT a manual SendFileAsync):
    // this sets Content-Type (nosniff is in play), emits ETag/Last-Modified so
    // warm navigations revalidate to 304 instead of re-downloading, and lets
    // response compression apply. no-cache = revalidate, not "never cache",
    // so deployments and service-worker updates propagate promptly.
    app.MapFallbackToFile("index.html", new StaticFileOptions
    {
        OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
    });
}

static void MapAgentEndpoints(WebApplication app)
{
    var agent = app.MapGroup("/api/agent").ExcludeFromDescription();

    // One POST = one agent request (up to Agent:MaxTurnsPerRequest provider
    // calls), streamed as SSE per docs/REALTIME.md invariants: under /api
    // (SW-denylisted, dev-proxied), text/event-stream never compressed.
    // Guards that must be HTTP statuses (409 turn-in-progress, 429 budget)
    // are decided before the first SSE byte. Idempotency-Key gives stream
    // POSTs in-flight duplicate suppression (see IdempotencyEndpointFilter).
    agent.MapPost(
            "/conversations/{conversationId}/turns",
            async (string conversationId, AgentTurnRequest request, HttpContext httpContext,
                AgentLoopService loop, AgentMessageBuilder messageBuilder) =>
            {
                if (!IsValidAgentConversationId(conversationId))
                {
                    return InvalidConversationIdProblem();
                }

                if (!AgentLoopService.IsValidUserMessage(request.Message))
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "Validation failed",
                        type: ProblemDetailTypes.ValidationFailed,
                        detail: "message must be non-empty and at most 32000 characters.");
                }

                // Attachment ids resolve BEFORE the turn starts: a turn must
                // not begin against references that cannot hydrate.
                var resolution = await messageBuilder.ResolveRefsAsync(
                    request.AttachmentIds, httpContext.RequestAborted);
                if (resolution.Error is not null)
                {
                    return Results.Problem(
                        statusCode: StatusCodes.Status400BadRequest,
                        title: "Validation failed",
                        type: ProblemDetailTypes.ValidationFailed,
                        detail: resolution.Error);
                }

                return MapAgentTurnStart(
                    loop.TryStartTurn(
                        conversationId, request, resolution.Refs!, httpContext.User, httpContext.RequestAborted));
            })
        .AddEndpointFilter<IdempotencyEndpointFilter<AgentTurnRequest>>()
        .WithRequestTimeout("long-running");

    // Resumes the loop frozen at the approval gate; the continuation streams
    // on THIS response. Body mirrors MEAI's ToolApprovalResponseContent
    // shape {approved, reason}.
    agent.MapPost(
            "/conversations/{conversationId}/approvals/{toolCallId}",
            (string conversationId, string toolCallId, AgentApprovalRequest request, HttpContext httpContext,
                AgentLoopService loop) =>
            {
                if (!IsValidAgentConversationId(conversationId))
                {
                    return InvalidConversationIdProblem();
                }

                return MapAgentTurnStart(
                    loop.TryStartApprovalTurn(
                        conversationId, toolCallId, request, httpContext.User, httpContext.RequestAborted));
            })
        .WithRequestTimeout("long-running");

    // Canonical replay: completed UI parts derived from the transcript via
    // the same AgentUiParts mapper the live stream uses — one renderer for
    // live and replayed conversations.
    agent.MapGet(
        "/conversations/{conversationId}",
        (string conversationId, IAgentConversationStore store) =>
        {
            if (!IsValidAgentConversationId(conversationId))
            {
                return InvalidConversationIdProblem();
            }

            if (!store.Exists(conversationId))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Not Found",
                    type: ProblemDetailTypes.AgentConversationNotFound,
                    detail: $"No agent conversation '{conversationId}' exists.");
            }

            var snapshot = new AgentConversationSnapshot(
                conversationId,
                [.. store.GetMessages(conversationId).Select(message => AgentUiParts.ToSnapshot(message, conversationId))],
                [.. store.GetPendingApprovals(conversationId).Select(pending => AgentUiParts.ApprovalRequired(pending, conversationId))]);
            return Results.Json(snapshot, AgentJsonContext.Default.AgentConversationSnapshot);
        });

    // The visible-spend endpoint: a first-time cloner sees what the agent
    // costs within minutes, not on next month's vendor bill.
    agent.MapGet(
        "/usage",
        (AgentUsageLedger ledger) => Results.Json(ledger.GetSummary(), AgentJsonContext.Default.AgentUsageSummary));

    // Upload-then-reference: one multipart file in, {attachmentId, mediaType,
    // fileName} out; the turn POST references the id and the loop re-hydrates
    // bytes from the store every request (docs/AGENT.md "Attachments"). Form
    // reading is manual (HttpContext, not IFormFile binding) so the endpoint
    // works without antiforgery services and owns its limit responses.
    agent.MapPost(
        "/attachments",
        async (HttpContext httpContext, IAttachmentStore attachmentStore, IOptions<AgentOptions> agentOptions) =>
        {
            var limits = agentOptions.Value.Attachments;
            var request = httpContext.Request;
            if (!request.HasFormContentType)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Validation failed",
                    type: ProblemDetailTypes.ValidationFailed,
                    detail: "Attachment uploads must be multipart/form-data with exactly one file field.");
            }

            // Cheap flood guard BEFORE the body is buffered; the authoritative
            // per-file check is below. The allowance covers multipart framing.
            const long MultipartOverheadAllowanceBytes = 16 * 1024;
            if (request.ContentLength is { } contentLength
                && contentLength > limits.MaxBytes + MultipartOverheadAllowanceBytes)
            {
                return AttachmentTooLargeProblem(limits.MaxBytes);
            }

            var form = await request.ReadFormAsync(httpContext.RequestAborted);
            if (form.Files.Count != 1)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Validation failed",
                    type: ProblemDetailTypes.ValidationFailed,
                    detail: "Exactly one file per upload; send several files as several uploads.");
            }

            var file = form.Files[0];
            var mediaType = AgentAttachmentOptions.NormalizeMediaType(file.ContentType);
            if (mediaType is null || !limits.IsAllowedContentType(mediaType))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status415UnsupportedMediaType,
                    title: "Attachment type not allowed",
                    type: ProblemDetailTypes.AgentAttachmentTypeNotAllowed,
                    detail: $"Content type '{file.ContentType}' is not accepted. Allowed: "
                        + $"{string.Join(", ", limits.AllowedContentTypes.Distinct(StringComparer.OrdinalIgnoreCase))}.");
            }

            if (file.Length > limits.MaxBytes)
            {
                return AttachmentTooLargeProblem(limits.MaxBytes);
            }

            byte[] bytes;
            var bufferStream = new MemoryStream(capacity: (int)file.Length);
            await using (bufferStream.ConfigureAwait(false))
            {
                var fileStream = file.OpenReadStream();
                await using (fileStream.ConfigureAwait(false))
                {
                    await fileStream.CopyToAsync(bufferStream, httpContext.RequestAborted);
                }

                bytes = bufferStream.ToArray();
            }

            var reference = await attachmentStore.SaveAsync(
                file.FileName, mediaType, bytes, httpContext.RequestAborted);
            return Results.Json(
                new AgentAttachmentUploadResponse(reference.AttachmentId, reference.MediaType, reference.FileName),
                AgentJsonContext.Default.AgentAttachmentUploadResponse);
        });
}

static IResult AttachmentTooLargeProblem(long maxBytes) => Results.Problem(
    statusCode: StatusCodes.Status413PayloadTooLarge,
    title: "Attachment too large",
    type: ProblemDetailTypes.AgentAttachmentTooLarge,
    detail: $"Attachments are capped at {maxBytes} bytes (Agent:Attachments:MaxBytes).");

static IResult MapAgentTurnStart(AgentTurnStart start) => start.Status switch
{
    AgentTurnStartStatus.Started => TypedResults.ServerSentEvents(ToSseItems(start.Stream!)),
    AgentTurnStartStatus.TurnInProgress => Results.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "Agent turn already in progress",
        type: ProblemDetailTypes.AgentTurnInProgress,
        detail: "Another turn is streaming for this conversation. Wait for it to finish (or abort it) and retry."),
    AgentTurnStartStatus.BudgetExceeded => Results.Problem(
        statusCode: StatusCodes.Status429TooManyRequests,
        title: "Daily agent budget exhausted",
        type: ProblemDetailTypes.AgentBudgetExceeded,
        detail: "Today's Agent:DailyUsdBudget is spent. The budget resets at resetAtUtc (UTC midnight).",
        extensions: new Dictionary<string, object?> { ["resetAtUtc"] = start.ResetAtUtc }),
    AgentTurnStartStatus.ApprovalNotFound => Results.Problem(
        statusCode: StatusCodes.Status404NotFound,
        title: "Approval not found",
        type: ProblemDetailTypes.AgentApprovalNotFound,
        detail: "No pending approval matches this tool call id."),
    AgentTurnStartStatus.ApprovalConflict => Results.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "Tool policy changed since approval was requested",
        type: ProblemDetailTypes.AgentApprovalConflict,
        detail: "The tool-policy surface diverged between freeze and execution; the frozen call was not run. "
            + "Reload the conversation and request the action again."),
    _ => throw new InvalidOperationException($"Unhandled agent turn start status: {start.Status}"),
};

static async IAsyncEnumerable<SseItem<string>> ToSseItems(IAsyncEnumerable<AgentStreamPart> parts)
{
    // Each part is serialized through the agent module's source-generated
    // context: deterministic camelCase bytes, locked by wire-shape snapshot
    // tests — the composable on the other end parses exactly this.
    await foreach (var part in parts)
    {
        yield return new SseItem<string>(JsonSerializer.Serialize(part, AgentJsonContext.Default.AgentStreamPart));
    }
}

static bool IsValidAgentConversationId(string conversationId) =>
    conversationId.Length is >= 1 and <= 64
    && conversationId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

static IResult InvalidConversationIdProblem() => Results.Problem(
    statusCode: StatusCodes.Status400BadRequest,
    title: "Validation failed",
    type: ProblemDetailTypes.ValidationFailed,
    detail: "conversationId must be 1-64 characters of [A-Za-z0-9_-].");
