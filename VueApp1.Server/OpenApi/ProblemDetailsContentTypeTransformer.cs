using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace VueApp1.Server.OpenApi;

/// <summary>
/// Makes declared error responses match the wire: every 4xx/5xx this API
/// actually sends goes through <c>IProblemDetailsService</c> as
/// <c>application/problem+json</c> (service layer, exception handler,
/// status-code pages, rate limiter). ApiExplorer describes error
/// declarations differently per shape (each verified empirically): a typed
/// <c>ProducesResponseType</c> as content-negotiated <c>application/json</c>
/// plus <c>text/plain</c>/<c>text/json</c>, a bodiless declaration as no
/// content at all — EXCEPT on a <c>[Produces]</c>-annotated action, where it
/// becomes an EMPTY <c>application/json</c> media type (content present, no
/// schema). This pass rewrites every error response to a single
/// <c>application/problem+json</c> entry, preserving a declared schema
/// (e.g. a typed <c>ValidationProblemDetails</c>) and backfilling RFC 9457
/// <c>ProblemDetails</c> when none was declared, so generated clients see
/// the true error shape — never an untyped body.
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

            // Carry over the declared media type so its schema (and any
            // examples) survive: prefer the canonical application/json
            // description, then a pre-existing problem+json entry, then any
            // other entry that actually has a schema.
            var mediaType = PickDeclaredMediaType(concrete.Content) ?? new OpenApiMediaType();

            // A bodiless declaration carries no schema (with [Produces] it is
            // an empty media type) — relabeling alone would ship an untyped
            // error body, so backfill the ProblemDetails the runtime sends.
            mediaType.Schema ??= await context.GetOrRegisterProblemDetailsSchemaAsync(cancellationToken);

            concrete.Content = new Dictionary<string, OpenApiMediaType>
            {
                [ProblemJsonMediaType] = mediaType,
            };
        }
    }

    private static OpenApiMediaType? PickDeclaredMediaType(
        IDictionary<string, OpenApiMediaType>? content)
    {
        if (content is null)
        {
            return null;
        }

        if (content.TryGetValue(JsonMediaType, out var json))
        {
            return json;
        }

        if (content.TryGetValue(ProblemJsonMediaType, out var problemJson))
        {
            return problemJson;
        }

        return content.Values.FirstOrDefault(candidate => candidate.Schema is not null);
    }

    private static bool IsErrorStatusCode(string statusCode) =>
        statusCode.Length == 3 && (statusCode[0] is '4' or '5');
}
