using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace VueApp1.Server.OpenApi;

/// <summary>
/// Documents the global rate limiter on every operation: a 429 with a
/// <c>Retry-After</c> header and an RFC 9457 problem body. The limiter is
/// registered as a <c>GlobalLimiter</c> (Program.cs), so "every endpoint can
/// answer 429" is runtime truth — this transformer keeps the committed
/// contract honest mechanically, for current and future endpoints alike,
/// instead of relying on per-action <c>ProducesResponseType</c> discipline.
/// </summary>
public sealed class RateLimitResponseTransformer : IOpenApiOperationTransformer
{
    public async Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var problemSchema = await context.GetOrRegisterProblemDetailsSchemaAsync(cancellationToken);

        operation.Responses ??= [];
        operation.Responses["429"] = new OpenApiResponse
        {
            Description = "Too Many Requests",
            Headers = new Dictionary<string, IOpenApiHeader>
            {
                // Delta-seconds only (RFC 9110 §10.2.3) — exactly what the
                // rejection handler emits. RateLimit-* headers are deliberately
                // NOT documented: the runtime never sends them and the IETF
                // draft defining them is still unstable.
                ["Retry-After"] = new OpenApiHeader
                {
                    Description = "Seconds to wait before retrying the request.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.Integer },
                },
            },
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/problem+json"] = new OpenApiMediaType { Schema = problemSchema },
            },
        };
    }
}
