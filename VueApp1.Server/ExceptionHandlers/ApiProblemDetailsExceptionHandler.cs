using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace VueApp1.Server.ExceptionHandlers;

/// <summary>
/// Converts unhandled exceptions into RFC 9457 problem+json responses with a
/// <c>traceId</c> extension for log correlation. Exception details are exposed
/// in Development only — production clients get a generic 500 problem.
/// </summary>
public sealed partial class ApiProblemDetailsExceptionHandler(
    IProblemDetailsService problemDetailsService,
    IHostEnvironment environment,
    ILogger<ApiProblemDetailsExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        // UseExceptionHandler rewrites Request.Path before invoking handlers;
        // IExceptionHandlerPathFeature carries the original request path.
        var feature = httpContext.Features.Get<IExceptionHandlerPathFeature>();
        var originalPath = feature?.Path is { Length: > 0 } path
            ? new PathString(path)
            : httpContext.Request.Path;

        LogUnhandledException(logger, exception, httpContext.Request.Method, originalPath.ToString());

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "An unexpected error occurred.",
        };
        problem.Extensions["traceId"] = httpContext.TraceIdentifier;

        if (environment.IsDevelopment())
        {
            problem.Detail = exception.Message;
            problem.Extensions["exceptionType"] = exception.GetType().FullName;
        }

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;

        if (!await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                ProblemDetails = problem,
                Exception = exception,
            }))
        {
            // The default writer refuses when content negotiation fails;
            // never send an empty 500 body.
            await httpContext.Response.WriteAsJsonAsync(
                problem,
                options: null,
                contentType: "application/problem+json",
                cancellationToken);
        }

        return true;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception for {Method} {Path}")]
    private static partial void LogUnhandledException(
        ILogger logger,
        Exception exception,
        string method,
        string path);
}
