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
    builder.Services.AddOpenApi();
    // The handler enriches unhandled exceptions with traceId (+ details in
    // Development) before AddProblemDetails' default machinery writes them.
    builder.Services.AddExceptionHandler<ApiProblemDetailsExceptionHandler>();
    builder.Services.AddProblemDetails();
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddScoped<IWeatherForecastService, WeatherForecastService>();
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
    var endpoint = builder.Configuration["OpenTelemetry:Otlp:Endpoint"];
    var hasOtlpEndpoint = !string.IsNullOrWhiteSpace(endpoint);

    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService(serviceName))
        .WithTracing(tracing =>
        {
            tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation();

            if (hasOtlpEndpoint)
            {
                tracing.AddOtlpExporter(options => options.Endpoint = new Uri(endpoint!));
            }
        })
        .WithMetrics(metrics =>
        {
            metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation();

            if (hasOtlpEndpoint)
            {
                metrics.AddOtlpExporter(options => options.Endpoint = new Uri(endpoint!));
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
    }

    if (app.Environment.IsDevelopment())
    {
        app.MapScalarApiReference();
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

    app.MapStaticAssets();
    app.MapControllers();
    app.MapHealthChecks("/health");

    app.MapFallback(async context =>
    {
        if (context.Request.Path.StartsWithSegments("/api"))
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

            return;
        }

        // no-cache (revalidate, not "never cache") so deployments and service-worker
        // updates propagate promptly; fingerprinted assets served by MapStaticAssets
        // remain immutable-cached.
        context.Response.Headers.CacheControl = "no-cache";
        var indexPath = Path.Combine(app.Environment.WebRootPath, "index.html");
        await context.Response.SendFileAsync(indexPath);
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
