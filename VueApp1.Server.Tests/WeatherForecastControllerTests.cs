using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using VueApp1.Server.Controllers;
using Xunit;

namespace VueApp1.Server.Tests;

public class WeatherForecastControllerTests
{
    [Fact]
    public void GetWeatherForecasts_ReturnsOkResult_WithFiveForecasts()
    {
        // Arrange
        var mockLogger = new Mock<ILogger<WeatherForecastController>>();
        var controller = new WeatherForecastController(mockLogger.Object);

        // Act
        var result = controller.GetWeatherForecasts();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var forecasts = Assert.IsAssignableFrom<IEnumerable<WeatherForecast>>(okResult.Value);
        Assert.Equal(5, forecasts.Count());
    }

    [Fact]
    public void GetWeatherForecasts_ReturnsValidTemperatureRange()
    {
        // Arrange
        var mockLogger = new Mock<ILogger<WeatherForecastController>>();
        var controller = new WeatherForecastController(mockLogger.Object);

        // Act
        var result = controller.GetWeatherForecasts();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var forecasts = Assert.IsAssignableFrom<IEnumerable<WeatherForecast>>(okResult.Value);
        
        foreach (var forecast in forecasts)
        {
            Assert.InRange(forecast.TemperatureC, -20, 55);
            Assert.NotNull(forecast.Summary);
        }
    }
}