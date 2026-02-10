using System.Diagnostics;

namespace VueApp1.Server.Middleware;

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
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            context.Response.Headers["Server-Timing"] = $"total;dur={elapsed:F1}";
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
