using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using VueApp1.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace VueApp1.Server.IntegrationTests;

/// <summary>
/// Pins the error-contract invariants of the generated OpenAPI document (the
/// committed copy in docs/openapi/ is gated separately by openapi:check).
/// The document's own info.description promises "RFC 9457 problem details on
/// every error" — these tests make that promise structural: every operation
/// documents the global 429, no error response claims plain application/json,
/// and the ProblemDetails component the error responses reference actually
/// exists (a dangling $ref breaks client generators).
/// </summary>
public class OpenApiDocumentContractTests(IntegrationTestWebApplicationFactory factory)
    : IClassFixture<IntegrationTestWebApplicationFactory>
{
    private static readonly TimeSpan _requestTimeout = TimeSpan.FromSeconds(10);

    private static readonly string[] _httpMethods =
        ["get", "put", "post", "delete", "options", "head", "patch", "trace"];

    private async Task<JsonDocument> GetOpenApiDocumentAsync(bool withProbeController = false)
    {
        // OpenAPI is Development-only by default; the Testing environment
        // opts in via the same flag the contract-sync script uses.
        using var client = factory
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("OpenApi:Enabled", "true");
                if (withProbeController)
                {
                    // Mount ContractProbeController (test assembly only) so the
                    // transformer paths can be pinned against declared error
                    // responses without adding fake ones to the real surface.
                    builder.ConfigureServices(services =>
                        services.AddControllers()
                            .AddApplicationPart(typeof(ContractProbeController).Assembly));
                }
            })
            .CreateClient();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(_requestTimeout);
        var payload = await client.GetStringAsync("/openapi/v1.json", cts.Token);
        return JsonDocument.Parse(payload);
    }

    private static IEnumerable<(string Route, string Method, JsonElement Operation)> EnumerateOperations(
        JsonElement root)
    {
        foreach (var path in root.GetProperty("paths").EnumerateObject())
        {
            foreach (var member in path.Value.EnumerateObject())
            {
                if (_httpMethods.Contains(member.Name, StringComparer.Ordinal))
                {
                    yield return (path.Name, member.Name, member.Value);
                }
            }
        }
    }

    [Fact]
    public async Task EveryOperation_DocumentsGlobal429WithRetryAfterAndProblemJson()
    {
        using var document = await GetOpenApiDocumentAsync();

        var operations = EnumerateOperations(document.RootElement).ToList();
        Assert.NotEmpty(operations);

        foreach (var (route, method, operation) in operations)
        {
            // The rate limiter is a GlobalLimiter — an operation without a
            // documented 429 would understate the API's real behavior.
            Assert.True(
                operation.GetProperty("responses").TryGetProperty("429", out var tooManyRequests),
                $"{method.ToUpperInvariant()} {route} does not document the global 429.");

            Assert.True(
                tooManyRequests.GetProperty("headers").TryGetProperty("Retry-After", out var retryAfter),
                $"{method.ToUpperInvariant()} {route}: 429 lacks the Retry-After header.");

            // OnRejected sets Retry-After unconditionally — documenting it as
            // optional would make generated clients null-check a guarantee.
            Assert.True(
                retryAfter.TryGetProperty("required", out var required) && required.GetBoolean(),
                $"{method.ToUpperInvariant()} {route}: Retry-After must be marked required.");

            var schemaRef = tooManyRequests
                .GetProperty("content")
                .GetProperty("application/problem+json")
                .GetProperty("schema")
                .GetProperty("$ref")
                .GetString();
            Assert.Equal("#/components/schemas/ProblemDetails", schemaRef);
        }
    }

    [Fact]
    public async Task ErrorResponses_NeverClaimPlainApplicationJson()
    {
        using var document = await GetOpenApiDocumentAsync();

        foreach (var (route, method, operation) in EnumerateOperations(document.RootElement))
        {
            foreach (var response in operation.GetProperty("responses").EnumerateObject())
            {
                var isError = response.Name.Length == 3
                    && (response.Name[0] is '4' or '5');
                if (!isError || !response.Value.TryGetProperty("content", out var content))
                {
                    continue;
                }

                // Runtime errors are always application/problem+json; a plain
                // application/json error declaration is a contract lie.
                Assert.False(
                    content.TryGetProperty("application/json", out _),
                    $"{method.ToUpperInvariant()} {route}: {response.Name} claims plain application/json.");
            }
        }
    }

    [Fact]
    public async Task SuccessResponsesAndRequestBodies_DeclareOnlyCanonicalJson()
    {
        using var document = await GetOpenApiDocumentAsync();

        foreach (var (route, method, operation) in EnumerateOperations(document.RootElement))
        {
            // Without a class-level [Produces] (deliberately absent — it
            // would relabel problem+json error bodies on the wire; see
            // ApiControllerBase), ApiExplorer emits content-negotiation noise
            // (text/plain + alias entries). CanonicalJsonContentTransformer
            // collapses it; this pin keeps future endpoints noise-free.
            if (operation.TryGetProperty("requestBody", out var requestBody))
            {
                var requestMediaTypes = requestBody.GetProperty("content")
                    .EnumerateObject().Select(property => property.Name).ToList();
                Assert.True(
                    requestMediaTypes.SequenceEqual((string[])["application/json"]),
                    $"{method.ToUpperInvariant()} {route}: request body declares "
                    + $"[{string.Join(", ", requestMediaTypes)}] instead of canonical application/json.");
            }

            foreach (var response in operation.GetProperty("responses").EnumerateObject())
            {
                var isError = response.Name.Length == 3
                    && (response.Name[0] is '4' or '5');
                if (isError || !response.Value.TryGetProperty("content", out var content))
                {
                    continue;
                }

                var responseMediaTypes = content.EnumerateObject()
                    .Select(property => property.Name).ToList();
                Assert.True(
                    responseMediaTypes.SequenceEqual((string[])["application/json"]),
                    $"{method.ToUpperInvariant()} {route}: {response.Name} declares "
                    + $"[{string.Join(", ", responseMediaTypes)}] instead of canonical application/json.");
            }
        }
    }

    [Fact]
    public async Task DeclaredErrorResponses_AreProblemJsonWithSchema()
    {
        // The probe pins rewrite paths the committed surface doesn't
        // exercise (feedback declares typed errors, but nothing bodiless or
        // [Produces]-annotated): a schema-less rewrite (untyped error
        // body for generated clients) could ship unnoticed. Media-type
        // presence alone is NOT enough — assert the schema too.
        using var document = await GetOpenApiDocumentAsync(withProbeController: true);
        var paths = document.RootElement.GetProperty("paths");

        (string Route, string StatusCode)[] cases =
        [
            // Typed declaration: the rewrite must preserve the declared schema.
            ("/api/contract-probe", "404"),
            // Bodiless declaration: ApiExplorer emits no content — backfill.
            ("/api/contract-probe", "503"),
            // Bodiless + [Produces]: ApiExplorer emits an EMPTY
            // application/json media type — a naive relabel ships it untyped.
            ("/api/contract-probe/produces", "503"),
        ];

        foreach (var (route, statusCode) in cases)
        {
            var responses = paths.GetProperty(route).GetProperty("get").GetProperty("responses");
            Assert.True(
                responses.TryGetProperty(statusCode, out var response),
                $"{route} does not document the declared {statusCode}.");
            var content = response.GetProperty("content");

            // problem+json is the ONLY media type the runtime sends on errors;
            // ApiExplorer's content-negotiated variants (application/json,
            // text/plain, text/json) are all the same contract lie.
            var mediaTypes = content.EnumerateObject().Select(property => property.Name).ToList();
            Assert.Equal((string[])["application/problem+json"], mediaTypes);

            Assert.True(
                content.GetProperty("application/problem+json").TryGetProperty("schema", out var schema),
                $"{route} {statusCode} has no schema — generated clients would see an untyped error body.");
            Assert.Equal("#/components/schemas/ProblemDetails", schema.GetProperty("$ref").GetString());
        }
    }

    [Fact]
    public async Task ProblemDetailsSchema_IsRegisteredComponent()
    {
        using var document = await GetOpenApiDocumentAsync();

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.True(
            schemas.TryGetProperty("ProblemDetails", out var problemDetails),
            "ProblemDetails schema is not registered — the 429 $refs would dangle.");

        var properties = problemDetails.GetProperty("properties");
        foreach (var field in (string[])["type", "title", "status", "detail", "instance"])
        {
            Assert.True(properties.TryGetProperty(field, out _), $"ProblemDetails schema lacks '{field}'.");
        }
    }

    [Fact]
    public async Task WeatherForecastSchema_HasGeneratorCleanTypes()
    {
        using var document = await GetOpenApiDocumentAsync();

        var schema = document.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("WeatherForecast");

        // Strict number handling: without it the Web defaults document ints
        // as ["integer","string"] unions and generated TypeScript clients
        // inherit `number | string`.
        Assert.Equal(JsonValueKind.String, schema.GetProperty("properties")
            .GetProperty("temperatureC").GetProperty("type").ValueKind);

        // Computed get-only properties are serialized on every response and
        // must be required, or generated clients see them as optional.
        var required = schema.GetProperty("required").EnumerateArray()
            .Select(element => element.GetString())
            .ToList();
        Assert.Contains("temperatureF", required);
    }

    [Fact]
    public async Task FeedbackRequestSchema_PublishesTheMessageLengthBounds()
    {
        using var document = await GetOpenApiDocumentAsync();

        var message = document.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("FeedbackRequest")
            .GetProperty("properties")
            .GetProperty("message");

        // The runtime rejects 1-2-char messages with a 400; the contract must
        // say so or generated clients hit undocumented errors. Guards the
        // non-positional-record shape of FeedbackRequest: attributes on
        // positional-record parameters silently vanish from the schema
        // (docs/API.md "Error contract in the OpenAPI document").
        Assert.Equal(3, message.GetProperty("minLength").GetInt32());
        Assert.Equal(2000, message.GetProperty("maxLength").GetInt32());
    }

    [Fact]
    public async Task IdempotentActions_DocumentTheReplayMarkerOnSuccess()
    {
        using var document = await GetOpenApiDocumentAsync();

        var created = document.RootElement
            .GetProperty("paths")
            .GetProperty("/api/feedback")
            .GetProperty("post")
            .GetProperty("responses")
            .GetProperty("201");

        Assert.True(
            created.GetProperty("headers").TryGetProperty("Idempotency-Replayed", out var header),
            "The Idempotency-Key-guarded 201 must declare the Idempotency-Replayed header.");

        // Only replays carry it — marking it required would make generated
        // clients expect it on first executions too.
        Assert.False(
            header.TryGetProperty("required", out var required) && required.GetBoolean(),
            "Idempotency-Replayed must stay optional: first executions don't send it.");
    }
}
