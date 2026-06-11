using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace VueApp1.Server.OpenApi;

/// <summary>
/// Collapses content-negotiation noise on success declarations to the one
/// media type clients actually exchange. Without a class-level
/// <c>[Produces("application/json")]</c> (deliberately absent — see the
/// rationale on <c>ApiControllerBase</c>), ApiExplorer describes every typed
/// 2xx as <c>text/plain</c> + <c>application/json</c> + <c>text/json</c> and
/// every JSON request body as <c>application/json</c> + <c>text/json</c> +
/// <c>application/*+json</c>. Of those, <c>text/plain</c> is a lie for
/// object results (no registered formatter writes objects as plain text);
/// the rest are formatter aliases that only bloat generated clients. This
/// pass keeps the canonical <c>application/json</c> entry (schema and
/// examples intact). Error responses are out of scope:
/// <see cref="ProblemDetailsContentTypeTransformer"/> owns those.
/// </summary>
public sealed class CanonicalJsonContentTransformer : IOpenApiOperationTransformer
{
    private const string JsonMediaType = "application/json";

    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (operation.RequestBody is OpenApiRequestBody requestBody)
        {
            requestBody.Content = Collapse(requestBody.Content);
        }

        if (operation.Responses is not null)
        {
            foreach (var (statusCode, response) in operation.Responses)
            {
                if (!IsErrorStatusCode(statusCode) && response is OpenApiResponse concrete)
                {
                    concrete.Content = Collapse(concrete.Content);
                }
            }
        }

        return Task.CompletedTask;
    }

    private static IDictionary<string, OpenApiMediaType>? Collapse(
        IDictionary<string, OpenApiMediaType>? content)
    {
        // Only collapse when the canonical entry exists — never invent
        // content for declarations that genuinely have none (204, 304).
        return content is not null && content.TryGetValue(JsonMediaType, out var json)
            ? new Dictionary<string, OpenApiMediaType> { [JsonMediaType] = json }
            : content;
    }

    private static bool IsErrorStatusCode(string statusCode) =>
        statusCode.Length == 3 && (statusCode[0] is '4' or '5');
}
