using Scalar.AspNetCore;
using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using VueApp1.Server.Middleware;
using VueApp1.Server.Services;

var builder = WebApplication.CreateBuilder(args);
var performance = builder.Configuration.GetSection(PerformanceTuningOptions.SectionName)
                      .Get<PerformanceTuningOptions>() ??
                  new PerformanceTuningOptions();

// -- Services --
SetupApi(builder);
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
    builder.Services.AddProblemDetails();
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddScoped<IWeatherForecastService, WeatherForecastService>();
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

static void ConfigurePipeline(WebApplication app, PerformanceTuningOptions performance)
{
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

    if (!app.Environment.IsDevelopment())
    {
        app.UseHsts();
    }

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
