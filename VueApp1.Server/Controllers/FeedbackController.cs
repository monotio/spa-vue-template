using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using VueApp1.Server.Idempotency;
using VueApp1.Server.Services;

namespace VueApp1.Server.Controllers;

/// <summary>
/// The template's create-style POST teaching surface: request validation
/// (400 ValidationProblemDetails), a domain rule through the ServiceResponse
/// pipeline (422), and safe client retries via the Idempotency-Key seam
/// (replay / 409 in-progress / 422 payload-mismatch). The GET counterpart
/// lives in WeatherForecastController.
/// </summary>
public class FeedbackController(IFeedbackService feedbackService) : ApiControllerBase
{
    /// <summary>Submits feedback; retries carrying the same Idempotency-Key replay the stored response.</summary>
    [HttpPost]
    [ServiceFilter<IdempotencyKeyFilter>]
    [ProducesResponseType<FeedbackReceipt>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<FeedbackReceipt>> SubmitFeedback(
        FeedbackRequest request,
        // Declared as a [Required] parameter so the key is part of the
        // OpenAPI contract and missing-key requests 400 during binding;
        // IdempotencyKeyFilter reads it from the header itself.
        [FromHeader(Name = IdempotencyKeyFilter.HeaderName)]
        [Required]
        [StringLength(128, MinimumLength = 1)]
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var response = await feedbackService.SubmitAsync(request, cancellationToken);
        // 201 without a Location: storeless sample, so there is no
        // GET-by-id to point at — add one when feedback gets a real store.
        return HandleServiceResponse(response, () => Created((string?)null, response.Value));
    }
}
