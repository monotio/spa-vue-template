using Microsoft.AspNetCore.Mvc;

namespace VueApp1.Server.Controllers;

// Deliberately NO class-level [Produces("application/json")]: ProducesAttribute
// is a result filter that REPLACES the content types on every ObjectResult,
// which relabels RFC 9457 error bodies (the automatic 400
// ValidationProblemDetails, filter-produced 409/422 problems) as plain
// application/json on the wire — silently breaking the "problem details on
// every error" contract. JSON needs no forcing here: the JSON formatters are
// the only registered output formatters.
[ApiController]
[Route("api/[controller]")]
public abstract class ApiControllerBase : ControllerBase
{
    protected ActionResult HandleServiceResponse(ServiceResponse response, Func<ActionResult> onSuccess)
    {
        return response.IsSuccess ? onSuccess() : HandleError(response);
    }

    protected ActionResult<TResult> HandleServiceResponse<T, TResult>(
        ServiceResponse<T> response,
        Func<T, TResult> onSuccess)
    {
        return response.IsSuccess ? onSuccess(response.Value) : HandleError(response);
    }

    protected ActionResult<T> HandleServiceResponse<T>(ServiceResponse<T> response)
    {
        return response.IsSuccess ? Ok(response.Value) : HandleError(response);
    }

    private ActionResult HandleError(ServiceResponse response)
    {
        return response.Result switch
        {
            ServiceResult.BadRequest when response.Details is not null =>
                response.Details.Status is not null and not StatusCodes.Status400BadRequest
                    ? StatusCode(response.Details.Status.Value, response.Details)
                    : BadRequest(response.Details),
            ServiceResult.BadRequest => BadRequest(),
            ServiceResult.NotAuthorized => Forbid(),
            ServiceResult.NotFound => NotFound(),
            ServiceResult.Conflict when response.Details is not null => Conflict(response.Details),
            ServiceResult.Conflict => Conflict(),
            ServiceResult.TooManyRequests => StatusCode(StatusCodes.Status429TooManyRequests, response.Details),
            _ => throw new InvalidOperationException($"Unhandled service result: {response.Result}"),
        };
    }
}
