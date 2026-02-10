using Microsoft.AspNetCore.Mvc;
using Moq;
using VueApp1.Server.Controllers;
using VueApp1.Server.Services;
using Xunit;

namespace VueApp1.Server.UnitTests.Controllers;
public class WeatherForecastControllerTests
{
    private readonly Mock<IWeatherForecastService> _mockService = new();
    private readonly WeatherForecastController _controller;

    public WeatherForecastControllerTests()
    {
        _controller = new WeatherForecastController(_mockService.Object);
    }

    [Fact]
    public async Task GetWeatherForecasts_ReturnsOkResult_WithForecasts()
    {
        // Arrange
        var forecasts = new List<WeatherForecast>
        {
            new(DateOnly.FromDateTime(DateTime.Now.AddDays(1)), 20, "Warm"),
            new(DateOnly.FromDateTime(DateTime.Now.AddDays(2)), 15, "Cool"),
        };
        _mockService.Setup(s => s.GetForecastsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ServiceResponse<IReadOnlyList<WeatherForecast>>.Success(forecasts));

        // Act
        var result = await _controller.GetWeatherForecasts(CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var returned = Assert.IsAssignableFrom<IReadOnlyList<WeatherForecast>>(okResult.Value);
        Assert.Equal(2, returned.Count);
    }

    [Fact]
    public async Task GetWeatherForecasts_ReturnsNotFound_WhenServiceReturnsNotFound()
    {
        // Arrange
        _mockService.Setup(s => s.GetForecastsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ServiceResponse<IReadOnlyList<WeatherForecast>>.NotFound());

        // Act
        var result = await _controller.GetWeatherForecasts(CancellationToken.None);

        // Assert
        Assert.IsType<NotFoundResult>(result.Result);
    }
}
