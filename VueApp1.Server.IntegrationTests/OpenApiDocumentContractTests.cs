using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
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

    private async Task<JsonDocument> GetOpenApiDocumentAsync()
    {
        // OpenAPI is Development-only by default; the Testing environment
        // opts in via the same flag the contract-sync script uses.
        using var client = factory
            .WithWebHostBuilder(builder => builder.UseSetting("OpenApi:Enabled", "true"))
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
                tooManyRequests.GetProperty("headers").TryGetProperty("Retry-After", out _),
                $"{method.ToUpperInvariant()} {route}: 429 lacks the Retry-After header.");

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
}
