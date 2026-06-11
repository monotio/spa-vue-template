using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace VueApp1.Server.OpenApi;

/// <summary>
/// Schema fidelity for computed (get-only) properties: System.Text.Json
/// unconditionally serializes them, so they are present in every response —
/// but the schema exporter only marks members required when deserialization
/// demands them, which would surface e.g. <c>WeatherForecast.TemperatureF</c>
/// as optional in generated clients. Marks such properties
/// <c>required</c> + <c>readOnly</c>: always present in responses, never
/// accepted on requests.
/// </summary>
public sealed class ComputedPropertySchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (schema.Properties is not { Count: > 0 } properties)
        {
            return Task.CompletedTask;
        }

        foreach (var property in context.JsonTypeInfo.Properties)
        {
            // Get-only and unconditionally serialized — i.e. present in every
            // payload. Conditional members (ShouldSerialize) keep their
            // optionality; extension-data members never appear in Properties.
            if (property is not { Get: not null, Set: null, ShouldSerialize: null }
                || !properties.TryGetValue(property.Name, out var propertySchema))
            {
                continue;
            }

            schema.Required ??= new HashSet<string>();
            schema.Required.Add(property.Name);
            if (propertySchema is OpenApiSchema concrete)
            {
                concrete.ReadOnly = true;
            }
        }

        return Task.CompletedTask;
    }
}
