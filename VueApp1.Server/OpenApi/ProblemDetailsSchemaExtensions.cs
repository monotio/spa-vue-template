using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace VueApp1.Server.OpenApi;

/// <summary>
/// Registers the RFC 9457 <see cref="ProblemDetails"/> schema as a shared
/// component and hands out references to it. Error responses added by
/// transformers must point at a registered component — an unregistered
/// <c>$ref</c> would dangle and break contract consumers (client generators
/// validate references).
/// </summary>
internal static class ProblemDetailsSchemaExtensions
{
    public const string SchemaId = "ProblemDetails";

    public static async Task<IOpenApiSchema> GetOrRegisterProblemDetailsSchemaAsync(
        this OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var schema = await context.GetOrCreateSchemaAsync(
            typeof(ProblemDetails), cancellationToken: cancellationToken);
        if (context.Document is not { } document)
        {
            return schema;
        }

        document.AddComponent<IOpenApiSchema>(SchemaId, schema);
        return new OpenApiSchemaReference(SchemaId, document);
    }
}
