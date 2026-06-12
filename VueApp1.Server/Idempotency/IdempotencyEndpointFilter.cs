using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace VueApp1.Server.Idempotency;

/// <summary>
/// Minimal-API twin of <see cref="IdempotencyKeyFilter"/> (which is MVC-only:
/// an <c>IAsyncActionFilter</c> cannot run on endpoint routing). Built for
/// STREAM-returning endpoints like the agent turn POST, where the response
/// can never be replayed from a cache record — so this filter deliberately
/// NEVER commits. What the seam still buys on a stream:
/// <list type="bullet">
/// <item>in-flight duplicate suppression: a network-level retry while the
/// original stream is still running gets 409 instead of a second
/// (billable) generation;</item>
/// <item>payload-mismatch detection: a reused key with a different body is a
/// client bug and gets 422.</item>
/// </list>
/// The reservation is released when the RESPONSE completes (not when the
/// handler returns — the stream runs long after that), via
/// <see cref="HttpResponse.RegisterForDisposeAsync"/>. Dispose-without-commit
/// stores nothing, so a completed key is fresh again: replay protection for
/// streams is the in-flight window only, by design.
/// </summary>
public sealed class IdempotencyEndpointFilter<TBody>(
    IIdempotencyService idempotencyService,
    IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions) : IEndpointFilter
    where TBody : class
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var headerValue = context.HttpContext.Request.Headers[IdempotencyKeyFilter.HeaderName].ToString();
        if (string.IsNullOrEmpty(headerValue))
        {
            return await next(context);
        }

        // Same scoping rule as the MVC filter: method + lowercased path + key.
        var request = context.HttpContext.Request;
        var key = $"idempotency:{request.Method}:{request.Path.ToString().ToLowerInvariant()}:{headerValue}";
        var body = context.Arguments.OfType<TBody>().FirstOrDefault();
        var canonical = JsonSerializer.SerializeToUtf8Bytes(body, jsonOptions.Value.SerializerOptions);
        var payloadHash = Convert.ToHexString(SHA256.HashData(canonical));

        var reservation = await idempotencyService.BeginAsync(
            key, payloadHash, context.HttpContext.RequestAborted);

        switch (reservation.Outcome)
        {
            case IdempotencyOutcome.CachedResponse:
                // Unreachable in practice (this filter never commits), but
                // handled for correctness if the key is shared with a
                // committing endpoint.
                await reservation.DisposeAsync();
                var record = reservation.Record!;
                context.HttpContext.Response.Headers[IdempotencyKeyFilter.ReplayedHeaderName] = "true";
                return record.ContentType.Length == 0
                    ? Results.StatusCode(record.StatusCode)
                    : Results.Content(record.Body, record.ContentType, statusCode: record.StatusCode);

            case IdempotencyOutcome.PayloadConflict:
                await reservation.DisposeAsync();
                return Results.Problem(
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    title: "Idempotency-Key payload mismatch",
                    type: ProblemDetailTypes.IdempotencyPayloadMismatch,
                    detail: "This Idempotency-Key was already used with a different request payload. "
                        + "Reuse a key only for identical retries; new requests need a fresh key.");

            case IdempotencyOutcome.InProgress:
                await reservation.DisposeAsync();
                return Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Request already in progress",
                    type: ProblemDetailTypes.IdempotencyInProgress,
                    detail: "The original request with this Idempotency-Key is still executing. "
                        + "Retry after it completes.");

            default:
                // Reserved: hold the per-key lock for the WHOLE response
                // lifetime (the stream), then release without committing.
                context.HttpContext.Response.RegisterForDisposeAsync(reservation);
                return await next(context);
        }
    }
}
