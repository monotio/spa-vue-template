using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using VueApp1.Server.Idempotency;

namespace VueApp1.Server.OpenApi;

/// <summary>
/// Declares the <c>Idempotency-Replayed</c> response header on every 2xx of
/// an action guarded by <see cref="IdempotencyKeyFilter"/>: a replayed
/// success carries it (value <c>true</c>); a first execution does not. Like
/// <see cref="RateLimitResponseTransformer"/>, the contract follows the
/// filter registration mechanically instead of relying on per-action
/// documentation discipline — wherever the filter is applied, generated
/// clients can see the replay marker.
/// </summary>
public sealed class IdempotencyReplayedHeaderTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var guarded = context.Description.ActionDescriptor.FilterDescriptors
            .Any(descriptor => descriptor.Filter is ServiceFilterAttribute serviceFilter
                && serviceFilter.ServiceType == typeof(IdempotencyKeyFilter));
        if (!guarded || operation.Responses is null)
        {
            return Task.CompletedTask;
        }

        foreach (var (status, response) in operation.Responses)
        {
            // Only successes can replay: errors are never committed (the
            // don't-poison rule), so declaring the header there would lie.
            if (!status.StartsWith('2') || response is not OpenApiResponse concrete)
            {
                continue;
            }

            concrete.Headers ??= new Dictionary<string, IOpenApiHeader>();
            if (concrete.Headers.ContainsKey(IdempotencyKeyFilter.ReplayedHeaderName))
            {
                continue;
            }

            concrete.Headers[IdempotencyKeyFilter.ReplayedHeaderName] = new OpenApiHeader
            {
                Description =
                    "Present with value \"true\" when this response was replayed from the "
                    + "idempotency store for a retried Idempotency-Key instead of re-executing "
                    + "the operation.",
                // Only replays carry it — first executions don't, so unlike
                // Retry-After on the 429 it must stay optional.
                Required = false,
                Schema = new OpenApiSchema { Type = JsonSchemaType.String },
            };
        }

        return Task.CompletedTask;
    }
}
