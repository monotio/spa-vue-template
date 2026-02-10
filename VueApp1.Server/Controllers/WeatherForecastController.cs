using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using VueApp1.Server.Services;

namespace VueApp1.Server.Controllers;

public class WeatherForecastController(IWeatherForecastService weatherService) : ApiControllerBase
{
    [HttpGet]
    [OutputCache(PolicyName = "api-read")]
    [ProducesResponseType<IReadOnlyList<WeatherForecast>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<WeatherForecast>>> GetWeatherForecasts(
        CancellationToken cancellationToken)
    {
        var response = await weatherService.GetForecastsAsync(cancellationToken);
        return HandleServiceResponse(response);
    }
}
