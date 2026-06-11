using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Options;

namespace VueApp1.Server.Idempotency;

/// <summary>
/// HTTP wiring for <see cref="IIdempotencyService"/>. Apply with
/// <c>[ServiceFilter&lt;IdempotencyKeyFilter&gt;]</c> on unsafe actions whose
/// clients retry (see FeedbackController for the full teaching shape).
/// Passes through when no <c>Idempotency-Key</c> header is present — actions
/// that REQUIRE one declare a <c>[Required] [FromHeader]</c> parameter,
/// which 400s during binding, before this filter runs.
/// </summary>
public sealed class IdempotencyKeyFilter(
    IIdempotencyService idempotencyService,
    IOptions<JsonOptions> jsonOptions) : IAsyncActionFilter
{
    public const string HeaderName = "Idempotency-Key";

    /// <summary>Marks replayed responses so clients/tests can tell a replay from a re-run.</summary>
    public const string ReplayedHeaderName = "Idempotency-Replayed";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // IsNullOrEmpty, not IsNullOrWhiteSpace: the endpoint's binding rule
        // ([StringLength], MinimumLength 1) accepts a whitespace key as
        // present, so the filter must treat it as a real key too — dropping
        // it here would silently run a key-carrying request unprotected.
        var headerValue = context.HttpContext.Request.Headers[HeaderName].ToString();
        if (string.IsNullOrEmpty(headerValue))
        {
            await next();
            return;
        }

        // Scope the key per operation: the same client key on two different
        // endpoints must not collide. The path is lowercased because routing
        // is case-insensitive — /api/Feedback and /api/feedback are one
        // action and must be ONE idempotency scope. Once auth exists, scope
        // per caller too (append the user id) — otherwise one client could
        // replay another's stored response by guessing its key.
        var httpRequest = context.HttpContext.Request;
        var canonicalPath = httpRequest.Path.ToString().ToLowerInvariant();
        var key = $"idempotency:{httpRequest.Method}:{canonicalPath}:{headerValue}";
        var payloadHash = HashBodyArgument(context, jsonOptions.Value.JsonSerializerOptions);

        await using var reservation = await idempotencyService.BeginAsync(
            key, payloadHash, context.HttpContext.RequestAborted);

        switch (reservation.Outcome)
        {
            case IdempotencyOutcome.CachedResponse:
                var record = reservation.Record!;
                context.HttpContext.Response.Headers[ReplayedHeaderName] = "true";
                // Bodiless successes were stored without a content type —
                // replay them as a bare status code instead of labeling an
                // empty body with a content type it never had.
                context.Result = record.ContentType.Length == 0
                    ? new StatusCodeResult(record.StatusCode)
                    : new ContentResult
                    {
                        Content = record.Body,
                        ContentType = record.ContentType,
                        StatusCode = record.StatusCode,
                    };
                return;

            case IdempotencyOutcome.PayloadConflict:
                context.Result = Problem(
                    StatusCodes.Status422UnprocessableEntity,
                    "Idempotency-Key payload mismatch",
                    ProblemDetailTypes.IdempotencyPayloadMismatch,
                    "This Idempotency-Key was already used with a different request payload. "
                    + "Reuse a key only for identical retries; new requests need a fresh key.");
                return;

            case IdempotencyOutcome.InProgress:
                context.Result = Problem(
                    StatusCodes.Status409Conflict,
                    "Request already in progress",
                    ProblemDetailTypes.IdempotencyInProgress,
                    "The original request with this Idempotency-Key is still executing. "
                    + "Retry after it completes to receive the stored response.");
                return;
        }

        var executed = await next();

        if (executed.Exception is not null && !executed.ExceptionHandled)
        {
            // Dispose-without-commit: nothing is stored, so the key is not
            // poisoned and the client's retry re-executes the operation.
            return;
        }

        var (statusCode, contentType, body) = executed.Result switch
        {
            ObjectResult objectResult => (
                objectResult.StatusCode ?? StatusCodes.Status200OK,
                "application/json; charset=utf-8",
                objectResult.Value is null
                    ? string.Empty
                    : JsonSerializer.Serialize(objectResult.Value, jsonOptions.Value.JsonSerializerOptions)),
            // Bodiless success (204-style): no content type, replayed as a
            // bare status code — a JSON label would only be a lie there.
            StatusCodeResult statusCodeResult => (statusCodeResult.StatusCode, string.Empty, string.Empty),
            // Streaming/file results are not replayable from a cache record;
            // they pass through uncommitted rather than committing a lie.
            _ => (0, string.Empty, string.Empty),
        };

        if (statusCode is >= StatusCodes.Status200OK and < StatusCodes.Status300MultipleChoices)
        {
            await reservation.CommitAsync(statusCode, contentType, body);
        }

        // Non-2xx (binding already passed, so: domain rejection) commits
        // nothing — same don't-poison rule as the exception path above.
    }

    private static string HashBodyArgument(
        ActionExecutingContext context, JsonSerializerOptions serializerOptions)
    {
        var bodyParameter = context.ActionDescriptor.Parameters
            .FirstOrDefault(parameter => parameter.BindingInfo?.BindingSource == BindingSource.Body);
        object? payload = null;
        if (bodyParameter is not null)
        {
            context.ActionArguments.TryGetValue(bodyParameter.Name, out payload);
        }

        // Canonicalized through the app's own serializer: byte-level request
        // differences that deserialize identically (whitespace, key order
        // quirks) hash equal; any changed VALUE does not.
        var canonical = JsonSerializer.SerializeToUtf8Bytes(payload, serializerOptions);
        return Convert.ToHexString(SHA256.HashData(canonical));
    }

    private static ObjectResult Problem(int statusCode, string title, string type, string detail) =>
        new(new ProblemDetails { Status = statusCode, Title = title, Type = type, Detail = detail })
        {
            StatusCode = statusCode,
            ContentTypes = { "application/problem+json" },
        };
}
