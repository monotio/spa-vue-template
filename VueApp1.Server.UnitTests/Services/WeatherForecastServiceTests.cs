using VueApp1.Server.Services;
using Xunit;

namespace VueApp1.Server.UnitTests.Services;

public class WeatherForecastServiceTests
{
    private static readonly DateTimeOffset _testNow = new(2026, 2, 9, 12, 0, 0, TimeSpan.Zero);
    private readonly WeatherForecastService _service = new(new FixedTimeProvider(_testNow));

    [Fact]
    public async Task GetForecastsAsync_ReturnsSuccess_WithFiveForecasts()
    {
        var response = await _service.GetForecastsAsync(TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccess);
        Assert.Equal(5, response.Value.Count);
    }

    [Fact]
    public async Task GetForecastsAsync_ReturnsValidTemperatureRange()
    {
        var response = await _service.GetForecastsAsync(TestContext.Current.CancellationToken);

        foreach (var forecast in response.Value)
        {
            Assert.InRange(forecast.TemperatureC, -20, 54);
            Assert.NotNull(forecast.Summary);
        }
    }

    [Fact]
    public async Task GetForecastsAsync_ReturnsFutureDates()
    {
        var today = DateOnly.FromDateTime(_testNow.UtcDateTime);

        var response = await _service.GetForecastsAsync(TestContext.Current.CancellationToken);

        foreach (var forecast in response.Value)
        {
            Assert.True(forecast.Date > today);
        }
    }

    [Fact]
    public async Task GetForecastsAsync_Throws_WhenCancellationIsRequested()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _service.GetForecastsAsync(cts.Token));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
