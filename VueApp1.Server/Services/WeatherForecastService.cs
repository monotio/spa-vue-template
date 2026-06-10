using System.Diagnostics;
using Microsoft.Extensions.Caching.Hybrid;

namespace VueApp1.Server.Services;

public interface IWeatherForecastService
{
    Task<ServiceResponse<IReadOnlyList<WeatherForecast>>> GetForecastsAsync(
        CancellationToken cancellationToken = default);
}

public class WeatherForecastService(TimeProvider timeProvider, HybridCache cache) : IWeatherForecastService
{
    // Any source under the "VueApp1.*" namespace is collected automatically —
    // telemetry setup registers the wildcard, so new sources need no wiring.
    private static readonly ActivitySource _activitySource = new("VueApp1.Services");

    private static readonly string[] _summaries =
    [
        "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
    ];

    public async Task<ServiceResponse<IReadOnlyList<WeatherForecast>>> GetForecastsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var activity = _activitySource.StartActivity("GenerateForecasts");

        // HybridCache demo: stampede-protected data caching beneath the HTTP
        // output cache. Invalidate by tag with cache.RemoveByTagAsync("weather").
        var forecasts = await cache.GetOrCreateAsync(
            "weather:forecasts",
            _ => ValueTask.FromResult(GenerateForecasts()),
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(30) },
            tags: ["weather"],
            cancellationToken: cancellationToken);

        activity?.SetTag("forecast.count", forecasts.Count);

        return ServiceResponse<IReadOnlyList<WeatherForecast>>.Success(forecasts);
    }

    private IReadOnlyList<WeatherForecast> GenerateForecasts()
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return [.. Enumerable.Range(1, 5).Select(index => new WeatherForecast(
            Date: DateOnly.FromDateTime(now.AddDays(index)),
            TemperatureC: Random.Shared.Next(-20, 55),
            Summary: _summaries[Random.Shared.Next(_summaries.Length)]
        ))];
    }
}
