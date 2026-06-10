using System.Diagnostics;

namespace VueApp1.Server.Middleware;

/// <summary>
/// Optional contributor of extra Server-Timing entries (e.g. an EF Core
/// interceptor reporting <c>db;dur=12.3;desc="4 queries"</c>). Register a
/// scoped implementation and the middleware picks it up; without one, only
/// the total duration is reported. See docs/PATTERNS.md for the EF wiring.
/// </summary>
public interface IServerTimingMetrics
{
    /// <summary>Server-Timing metric entries, e.g. <c>"db;dur=12.3"</c>.</summary>
    IEnumerable<string> GetEntries();
}

/// <summary>
/// Adds a Server-Timing header to API responses, visible in browser DevTools.
/// </summary>
public sealed class ServerTimingMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await next(context);
            return;
        }

        var start = Stopwatch.GetTimestamp();

        context.Response.OnStarting(() =>
        {
            // Snapshot metrics INSIDE OnStarting: it runs on the request's
            // ExecutionContext, so AsyncLocal-backed implementations see their
            // values — capturing them outside this callback silently reads
            // from a different context.
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var entries = new List<string> { $"total;dur={elapsed:F1}" };

            var metrics = context.RequestServices.GetService<IServerTimingMetrics>();
            if (metrics is not null)
            {
                entries.AddRange(metrics.GetEntries());
            }

            context.Response.Headers["Server-Timing"] = string.Join(", ", entries);
            return Task.CompletedTask;
        });

        await next(context);
    }
}

public static class ServerTimingMiddlewareExtensions
{
    public static IApplicationBuilder UseServerTiming(this IApplicationBuilder app) =>
        app.UseMiddleware<ServerTimingMiddleware>();
}
