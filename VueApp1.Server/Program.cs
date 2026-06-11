using Scalar.AspNetCore;
using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using VueApp1.Server.ExceptionHandlers;
using VueApp1.Server.Middleware;
using VueApp1.Server.Services;

var builder = WebApplication.CreateBuilder(args);
var performance = builder.Configuration.GetSection(PerformanceTuningOptions.SectionName)
                      .Get<PerformanceTuningOptions>() ??
                  new PerformanceTuningOptions();

// -- Services --
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

var app = builder.Build();

// -- Middleware pipeline --
ConfigurePipeline(app, performance);

app.Run();

// ---------------------------------------------------------------------------

static void SetupApi(WebApplicationBuilder builder)
{
    builder.Services.AddControllers();
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
    });
    // The handler enriches unhandled exceptions with traceId (+ details in
    // Development) before AddProblemDetails' default machinery writes them.
    builder.Services.AddExceptionHandler<ApiProblemDetailsExceptionHandler>();
    builder.Services.AddProblemDetails();
    builder.Services.AddSingleton(TimeProvider.System);
    // HybridCache: in-process L1 with stampede protection; add a distributed
    // L2 by registering IDistributedCache (e.g. AddStackExchangeRedisCache) —
    // HybridCache picks it up automatically. Rule of thumb: L1 expiry ~1/6 of L2.
    builder.Services.AddHybridCache();
    // Outbound HTTP with retries/timeouts/circuit-breaker when you add a client:
    // builder.Services.AddHttpClient("backend").AddStandardResilienceHandler();
    builder.Services.AddScoped<IWeatherForecastService, WeatherForecastService>();
    // Host-header-injection-safe absolute links (emails, notifications):
    // generated from the configured PublicUri, never from request headers.
    builder.Services.Configure<PublicUriOptions>(
        builder.Configuration.GetSection(PublicUriOptions.SectionName));
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
    // When you add a database: .AddDbContextCheck<AppDbContext>();
}

static void SetupCompression(WebApplicationBuilder builder)
{
    builder.Services.AddResponseCompression(options => { options.EnableForHttps = true; });
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
        options.OnRejected = async (context, cancellationToken) =>
        {
            var retryAfterSeconds = performance.RateLimiting.WindowSeconds;
            context.HttpContext.Response.Headers.RetryAfter =
                retryAfterSeconds.ToString(CultureInfo.InvariantCulture);

            var details = new ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Too many requests",
                Detail = $"Rate limit exceeded. Retry after {retryAfterSeconds} seconds.",
            };

            await context.HttpContext.Response.WriteAsJsonAsync(details, cancellationToken);
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

    app.MapStaticAssets();
    // Safety net for wwwroot content added AFTER publish (e.g. the Docker image
    // copies the SPA dist in at image-assembly time): MapStaticAssets only
    // serves files from its build-time manifest, and MapFallback skips
    // file-like paths — without this, /assets/*.js and sw.js 404 in containers.
    app.UseStaticFiles();
    app.MapControllers();
    app.MapHealthChecks("/health");

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

internal sealed class PerformanceTuningOptions
{
    public const string SectionName = "Performance";
    public OutputCachingSettings OutputCache { get; init; } = new();
    public RateLimitingSettings RateLimiting { get; init; } = new();
    public RequestTimeoutSettings RequestTimeout { get; init; } = new();
    public RequestLimitSettings RequestLimits { get; init; } = new();
}

internal sealed class RequestLimitSettings
{
    public long MaxRequestBodySizeBytes { get; init; } = 10 * 1024 * 1024;
    public double MinBodyDataRateBytesPerSecond { get; init; } = 100;
    public int MinBodyDataRateGraceSeconds { get; init; } = 10;
}

internal sealed class OutputCachingSettings
{
    public int DefaultExpirationSeconds { get; init; } = 30;
    public int ReadExpirationSeconds { get; init; } = 30;
    public long SizeLimitBytes { get; init; } = 64 * 1024 * 1024;
    public long MaximumBodySizeBytes { get; init; } = 4 * 1024 * 1024;
}

internal sealed class RateLimitingSettings
{
    public int PermitLimit { get; init; } = 5000;
    public int QueueLimit { get; init; } = 100;
    public int WindowSeconds { get; init; } = 60;
}

internal sealed class RequestTimeoutSettings
{
    public bool Enabled { get; init; }
    public int DefaultTimeoutSeconds { get; init; } = 10;
    public int LongRunningTimeoutSeconds { get; init; } = 30;
}
