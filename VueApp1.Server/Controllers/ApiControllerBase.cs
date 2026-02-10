using Microsoft.AspNetCore.Mvc;

namespace VueApp1.Server.Controllers;

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
