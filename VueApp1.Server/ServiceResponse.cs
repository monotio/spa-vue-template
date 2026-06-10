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
/// Error helpers can carry RFC 9457 fields; give recurring, client-actionable
/// errors a stable <see cref="ProblemDetailTypes"/> identifier so frontends
/// branch on <c>type</c> rather than parsing detail strings.
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

    public static ServiceResponse BadRequest(string type, string title, string? detail = null) => new()
    {
        Result = ServiceResult.BadRequest,
        Details = new()
        {
            Status = StatusCodes.Status400BadRequest,
            Type = type,
            Title = title,
            Detail = detail,
        },
    };

    public static ServiceResponse NotFound() => new() { Result = ServiceResult.NotFound };

    public static ServiceResponse Conflict(string? detail = null) => new()
    {
        Result = ServiceResult.Conflict,
        Details = detail is not null
            ? new() { Status = StatusCodes.Status409Conflict, Detail = detail }
            : null,
    };

    public static ServiceResponse Conflict(string type, string title, string? detail = null) => new()
    {
        Result = ServiceResult.Conflict,
        Details = new()
        {
            Status = StatusCodes.Status409Conflict,
            Type = type,
            Title = title,
            Detail = detail,
        },
    };

    /// <summary>
    /// 412: a precondition (If-Match, expected state/version) was not met.
    /// Reuses the BadRequest result; the status on Details drives the HTTP code.
    /// </summary>
    public static ServiceResponse PreconditionFailed(string? detail = null, string? type = null) => new()
    {
        Result = ServiceResult.BadRequest,
        Details = new()
        {
            Status = StatusCodes.Status412PreconditionFailed,
            Type = type,
            Detail = detail,
        },
    };

    /// <summary>
    /// 422: the request was well-formed but semantically invalid.
    /// Reuses the BadRequest result; the status on Details drives the HTTP code.
    /// </summary>
    public static ServiceResponse UnprocessableEntity(string? detail = null, string? type = null) => new()
    {
        Result = ServiceResult.BadRequest,
        Details = new()
        {
            Status = StatusCodes.Status422UnprocessableEntity,
            Type = type ?? ProblemDetailTypes.ValidationFailed,
            Detail = detail,
        },
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

    public static new ServiceResponse<T> BadRequest(string type, string title, string? detail = null) => new()
    {
        Result = ServiceResult.BadRequest,
        Details = new()
        {
            Status = StatusCodes.Status400BadRequest,
            Type = type,
            Title = title,
            Detail = detail,
        },
    };

    public static new ServiceResponse<T> NotFound() => new() { Result = ServiceResult.NotFound };

    /// <inheritdoc cref="ServiceResponse.PreconditionFailed" />
    public static new ServiceResponse<T> PreconditionFailed(string? detail = null, string? type = null) => new()
    {
        Result = ServiceResult.BadRequest,
        Details = new()
        {
            Status = StatusCodes.Status412PreconditionFailed,
            Type = type,
            Detail = detail,
        },
    };

    /// <inheritdoc cref="ServiceResponse.UnprocessableEntity" />
    public static new ServiceResponse<T> UnprocessableEntity(string? detail = null, string? type = null) => new()
    {
        Result = ServiceResult.BadRequest,
        Details = new()
        {
            Status = StatusCodes.Status422UnprocessableEntity,
            Type = type ?? ProblemDetailTypes.ValidationFailed,
            Detail = detail,
        },
    };
}
#pragma warning restore CA1000
