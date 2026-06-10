using Microsoft.AspNetCore.Http;
using Xunit;

namespace VueApp1.Server.UnitTests;

public class ServiceResponseTests
{
    [Fact]
    public void Success_IsSuccess()
    {
        Assert.True(ServiceResponse.Success().IsSuccess);
        Assert.True(ServiceResponse<int>.Success(42).IsSuccess);
        Assert.Equal(42, ServiceResponse<int>.Success(42).Value);
    }

    [Fact]
    public void BadRequest_WithTypeAndTitle_SetsAllProblemFields()
    {
        var response = ServiceResponse.BadRequest(
            ProblemDetailTypes.ValidationFailed, "Invalid input", "name is required");

        Assert.False(response.IsSuccess);
        Assert.NotNull(response.Details);
        Assert.Equal(StatusCodes.Status400BadRequest, response.Details.Status);
        Assert.Equal(ProblemDetailTypes.ValidationFailed, response.Details.Type);
        Assert.Equal("Invalid input", response.Details.Title);
        Assert.Equal("name is required", response.Details.Detail);
    }

    [Fact]
    public void Conflict_WithType_SetsStatus409AndType()
    {
        var response = ServiceResponse.Conflict(
            ProblemDetailTypes.ConflictingState, "Already processed");

        Assert.Equal(ServiceResult.Conflict, response.Result);
        Assert.Equal(StatusCodes.Status409Conflict, response.Details?.Status);
        Assert.Equal(ProblemDetailTypes.ConflictingState, response.Details?.Type);
    }

    [Fact]
    public void PreconditionFailed_RidesBadRequestResultWithStatus412()
    {
        var response = ServiceResponse.PreconditionFailed("version mismatch");

        Assert.Equal(ServiceResult.BadRequest, response.Result);
        Assert.Equal(StatusCodes.Status412PreconditionFailed, response.Details?.Status);
    }

    [Fact]
    public void UnprocessableEntity_DefaultsTypeToValidationFailed()
    {
        var response = ServiceResponse.UnprocessableEntity("out of range");

        Assert.Equal(ServiceResult.BadRequest, response.Result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, response.Details?.Status);
        Assert.Equal(ProblemDetailTypes.ValidationFailed, response.Details?.Type);
    }

    [Fact]
    public void TypedHelpers_MatchUntypedBehavior()
    {
        Assert.Equal(
            StatusCodes.Status412PreconditionFailed,
            ServiceResponse<string>.PreconditionFailed().Details?.Status);
        Assert.Equal(
            StatusCodes.Status422UnprocessableEntity,
            ServiceResponse<string>.UnprocessableEntity().Details?.Status);
    }
}
