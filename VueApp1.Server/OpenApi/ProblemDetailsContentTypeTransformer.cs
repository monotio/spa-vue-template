using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace VueApp1.Server.OpenApi;

/// <summary>
/// Makes declared error responses match the wire: every 4xx/5xx this API
/// actually sends is <c>application/problem+json</c> (service layer,
/// exception handler, status-code pages, rate limiter), but ApiExplorer
/// describes <c>ProducesResponseType</c> error declarations as plain
/// <c>application/json</c> — and a bodiless declaration as no content at all.
/// This pass relabels the former and fills in an RFC 9457 body for the
/// latter, so generated clients see the true error shape.
/// </summary>
public sealed class ProblemDetailsContentTypeTransformer : IOpenApiOperationTransformer
{
    private const string JsonMediaType = "application/json";
    private const string ProblemJsonMediaType = "application/problem+json";

    public async Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (operation.Responses is null)
        {
            return;
        }

        foreach (var (statusCode, response) in operation.Responses)
        {
            if (!IsErrorStatusCode(statusCode) || response is not OpenApiResponse concrete)
            {
                continue;
            }

            if (concrete.Content is { } content
                && content.TryGetValue(JsonMediaType, out var mediaType))
            {
                // Relabel, preserving the declared schema (e.g. a typed
                // ValidationProblemDetails) — only the media type was a lie.
                content.Remove(JsonMediaType);
                content.TryAdd(ProblemJsonMediaType, mediaType);
            }
            else if (concrete.Content is not { Count: > 0 })
            {
                concrete.Content = new Dictionary<string, OpenApiMediaType>
                {
                    [ProblemJsonMediaType] = new OpenApiMediaType
                    {
                        Schema = await context.GetOrRegisterProblemDetailsSchemaAsync(cancellationToken),
                    },
                };
            }
        }
    }

    private static bool IsErrorStatusCode(string statusCode) =>
        statusCode.Length == 3 && (statusCode[0] is '4' or '5');
}
