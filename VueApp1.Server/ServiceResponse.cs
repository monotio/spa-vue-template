namespace VueApp1.Server;

public enum ServiceResult
{
    Success,
    BadRequest,
    NotFound,
    Conflict,
    NotAuthorized,
    TooManyRequests,
}

/// <summary>
/// Wraps the result of a service operation for consistent controller handling.
/// </summary>
public class ServiceResponse
{
    public ServiceResult Result { get; init; } = ServiceResult.Success;
    public Microsoft.AspNetCore.Mvc.ProblemDetails? Details { get; init; }
    public bool IsSuccess => Result == ServiceResult.Success;

    public static ServiceResponse Success() => new();

    public static ServiceResponse BadRequest(string? detail = null) => new()
    {
        Result = ServiceResult.BadRequest,
        Details = detail is not null
            ? new() { Status = StatusCodes.Status400BadRequest, Detail = detail }
            : null,
    };

    public static ServiceResponse NotFound() => new() { Result = ServiceResult.NotFound };

    public static ServiceResponse Conflict(string? detail = null) => new()
    {
        Result = ServiceResult.Conflict,
        Details = detail is not null
            ? new() { Status = StatusCodes.Status409Conflict, Detail = detail }
            : null,
    };
}

/// <summary>
/// Wraps a service result with a typed value on success.
/// </summary>
#pragma warning disable CA1000 // Static factory methods on generic types are intentional here
public class ServiceResponse<T> : ServiceResponse
{
    public T Value { get; init; } = default!;

    public static ServiceResponse<T> Success(T value) => new() { Value = value };

    public static new ServiceResponse<T> BadRequest(string? detail = null) => new()
    {
        Result = ServiceResult.BadRequest,
        Details = detail is not null
            ? new() { Status = StatusCodes.Status400BadRequest, Detail = detail }
            : null,
    };

    public static new ServiceResponse<T> NotFound() => new() { Result = ServiceResult.NotFound };
}
#pragma warning restore CA1000
