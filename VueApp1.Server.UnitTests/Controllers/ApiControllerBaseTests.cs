using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using VueApp1.Server.Controllers;
using Xunit;

namespace VueApp1.Server.UnitTests.Controllers;

public class ApiControllerBaseTests
{
    private sealed class TestController : ApiControllerBase
    {
        public ActionResult Handle(ServiceResponse response) =>
            HandleServiceResponse(response, Ok);

        public ActionResult<string> Handle(ServiceResponse<string> response) =>
            HandleServiceResponse(response);
    }

    private readonly TestController _controller = new();

    [Fact]
    public void Success_InvokesOnSuccess()
    {
        var result = _controller.Handle(ServiceResponse.Success());

        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public void TypedSuccess_ReturnsOkWithValue()
    {
        var result = _controller.Handle(ServiceResponse<string>.Success("value"));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("value", ok.Value);
    }

    [Fact]
    public void BadRequest_WithoutDetails_ReturnsBareBadRequest()
    {
        var result = _controller.Handle(ServiceResponse.BadRequest());

        Assert.IsType<BadRequestResult>(result);
    }

    [Fact]
    public void BadRequest_WithDetail_ReturnsBadRequestWithProblemDetails()
    {
        var result = _controller.Handle(ServiceResponse.BadRequest("broken"));

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var problem = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal("broken", problem.Detail);
    }

    [Fact]
    public void BadRequest_WithTypeAndTitle_PopulatesProblemFields()
    {
        var result = _controller.Handle(
            ServiceResponse.BadRequest(ProblemDetailTypes.ValidationFailed, "Invalid input", "broken"));

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var problem = Assert.IsType<ProblemDetails>(badRequest.Value);
        Assert.Equal(ProblemDetailTypes.ValidationFailed, problem.Type);
        Assert.Equal("Invalid input", problem.Title);
    }

    [Fact]
    public void PreconditionFailed_ForwardsStatus412()
    {
        var result = _controller.Handle(ServiceResponse.PreconditionFailed("stale version"));

        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status412PreconditionFailed, statusResult.StatusCode);
    }

    [Fact]
    public void UnprocessableEntity_ForwardsStatus422()
    {
        var result = _controller.Handle(ServiceResponse.UnprocessableEntity("semantically wrong"));

        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, statusResult.StatusCode);
    }

    [Fact]
    public void NotFound_ReturnsNotFound()
    {
        var result = _controller.Handle(ServiceResponse.NotFound());

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public void NotAuthorized_ReturnsForbid()
    {
        var result = _controller.Handle(new ServiceResponse { Result = ServiceResult.NotAuthorized });

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public void Conflict_WithDetail_ReturnsConflictWithProblemDetails()
    {
        var result = _controller.Handle(ServiceResponse.Conflict("already exists"));

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        var problem = Assert.IsType<ProblemDetails>(conflict.Value);
        Assert.Equal("already exists", problem.Detail);
    }

    [Fact]
    public void Conflict_WithoutDetail_ReturnsBareConflict()
    {
        var result = _controller.Handle(ServiceResponse.Conflict());

        Assert.IsType<ConflictResult>(result);
    }

    [Fact]
    public void TooManyRequests_Returns429()
    {
        var result = _controller.Handle(new ServiceResponse { Result = ServiceResult.TooManyRequests });

        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, statusResult.StatusCode);
    }
}
