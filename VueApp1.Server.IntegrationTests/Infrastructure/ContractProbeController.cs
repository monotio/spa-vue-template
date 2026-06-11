using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace VueApp1.Server.IntegrationTests.Infrastructure;

/// <summary>
/// NOT part of the API surface: this controller lives in the test assembly
/// and is mounted via <c>AddApplicationPart</c> only inside
/// <c>OpenApiDocumentContractTests</c>, so the committed contract stays
/// untouched. It exists to exercise every ApiExplorer description shape
/// <c>ProblemDetailsContentTypeTransformer</c> must rewrite (verified
/// empirically — each declaration style describes errors differently):
/// a typed declaration (content-negotiated <c>application/json</c> +
/// <c>text/plain</c>/<c>text/json</c>, schema present), a bodiless
/// declaration (no content at all), and a bodiless declaration on a
/// <c>[Produces]</c> action (an EMPTY <c>application/json</c> media type —
/// the shape where a naive relabel ships an untyped error body).
/// </summary>
[ApiController]
[Route("api/contract-probe")]
public sealed class ContractProbeController : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public IActionResult Get() => Ok();

    [HttpGet("produces")]
    [Produces("application/json")]
    [ProducesResponseType<string>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<string> GetProduces() => Ok("probe");
}
