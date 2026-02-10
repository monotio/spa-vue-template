namespace VueApp1.Server.Services;

public interface IWeatherForecastService
{
    Task<ServiceResponse<IReadOnlyList<WeatherForecast>>> GetForecastsAsync(
        CancellationToken cancellationToken = default);
}

public class WeatherForecastService(TimeProvider timeProvider) : IWeatherForecastService
{
    private static readonly string[] _summaries =
    [
        "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
    ];

    public Task<ServiceResponse<IReadOnlyList<WeatherForecast>>> GetForecastsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var forecasts = Enumerable.Range(1, 5).Select(index => new WeatherForecast(
            Date: DateOnly.FromDateTime(now.AddDays(index)),
            TemperatureC: Random.Shared.Next(-20, 55),
            Summary: _summaries[Random.Shared.Next(_summaries.Length)]
        )).ToArray();

        return Task.FromResult(ServiceResponse<IReadOnlyList<WeatherForecast>>.Success(forecasts));
    }
}
