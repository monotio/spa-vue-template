using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using VueApp1.Server.IntegrationTests.Infrastructure;
using VueApp1.Server.Services;
using Xunit;

namespace VueApp1.Server.IntegrationTests;

/// <summary>
/// Exercises the opt-in /mcp endpoint end to end. Happy and error paths go
/// through the official SDK client (initialize handshake + tools/list +
/// tools/call), NOT hand-rolled JSON-RPC: the MCP spec is still revising its
/// transport, and SDK-level tests ride those wire changes out via the package
/// bump instead of breaking. Raw HTTP appears only in the hardening tests,
/// whose assertions target the template's own middleware (403/405/429), not
/// the protocol.
/// </summary>
public class McpEndpointTests(IntegrationTestWebApplicationFactory factory)
    : IClassFixture<IntegrationTestWebApplicationFactory>
{
    private static readonly TimeSpan _requestTimeout = TimeSpan.FromSeconds(30);

    private static CancellationTokenSource CreateRequestCts()
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(_requestTimeout);
        return cts;
    }

    private WebApplicationFactory<Program> CreateMcpFactory(Action<IWebHostBuilder>? configure = null) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Mcp:Enabled", "true");
            configure?.Invoke(builder);
        });

    private static async Task<McpClient> ConnectAsync(HttpClient httpClient, CancellationToken cancellationToken)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(httpClient.BaseAddress!, "mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
            },
            httpClient);
        return await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
    }

    [Fact]
    public async Task InitializeAndListTools_ExposesAnnotatedWeatherForecastTool()
    {
        using var mcpFactory = CreateMcpFactory();
        using var httpClient = mcpFactory.CreateClient();
        using var cts = CreateRequestCts();
        await using var client = await ConnectAsync(httpClient, cts.Token);

        Assert.Equal("VueApp1", client.ServerInfo.Name);
        Assert.False(string.IsNullOrWhiteSpace(client.ServerInstructions));

        var tools = await client.ListToolsAsync(cancellationToken: cts.Token);
        var tool = Assert.Single(tools, t => t.Name == "get_weather_forecast");

        Assert.False(string.IsNullOrWhiteSpace(tool.Description));
        // The five-annotations doctrine (docs/MCP.md) made observable: the
        // spec defaults destructiveHint/openWorldHint to TRUE, so these must
        // arrive explicitly false for a read-only tool.
        var annotations = tool.ProtocolTool.Annotations;
        Assert.NotNull(annotations);
        Assert.True(annotations.ReadOnlyHint);
        Assert.True(annotations.IdempotentHint);
        Assert.False(annotations.DestructiveHint);
        Assert.False(annotations.OpenWorldHint);

        // OutputSchemaType on a CallToolResult-returning tool: the schema is
        // advertised, and because the tool's value type is a non-object the
        // SDK wraps it in a required "result" property — the exact shape
        // McpToolResults.Success() emits.
        var outputSchema = tool.ProtocolTool.OutputSchema;
        Assert.NotNull(outputSchema);
        Assert.Equal("object", outputSchema.Value.GetProperty("type").GetString());
        Assert.True(outputSchema.Value.GetProperty("properties").TryGetProperty("result", out _));
    }

    [Fact]
    public async Task CallTool_HappyPath_ReturnsForecastsAsStructuredContentAndText()
    {
        using var mcpFactory = CreateMcpFactory();
        using var httpClient = mcpFactory.CreateClient();
        using var cts = CreateRequestCts();
        await using var client = await ConnectAsync(httpClient, cts.Token);

        var result = await client.CallToolAsync("get_weather_forecast", cancellationToken: cts.Token);

        Assert.NotEqual(true, result.IsError);
        Assert.NotNull(result.StructuredContent);

        // structuredContent must be a JSON OBJECT (spec; SEP-2106 proposes
        // relaxing this), so the array value arrives wrapped as
        // { "result": [...] } — conforming to the advertised outputSchema.
        var structured = result.StructuredContent.Value;
        Assert.Equal(JsonValueKind.Object, structured.ValueKind);
        var forecasts = structured.GetProperty("result");
        Assert.Equal(JsonValueKind.Array, forecasts.ValueKind);
        Assert.Equal(5, forecasts.GetArrayLength());
        var first = forecasts[0];
        // Same camelCase shape the REST endpoint serves — one service layer,
        // one contract.
        Assert.True(first.TryGetProperty("date", out _));
        Assert.True(first.TryGetProperty("temperatureC", out _));
        Assert.True(first.TryGetProperty("temperatureF", out _));
        Assert.True(first.TryGetProperty("summary", out _));

        // Dual emission: the text block carries the RAW (unwrapped) value
        // JSON for runtimes that only surface text content.
        var textBlock = result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Single();
        using var document = JsonDocument.Parse(textBlock.Text);
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        Assert.Equal(5, document.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task CallTool_ServiceFailure_SurfacesErrorEnvelopeThroughProtocol()
    {
        // Swap the service for a failing stub: the test pins the FULL path
        // from a ServiceResponse failure to the protocol-level isError result
        // an agent runtime branches on.
        using var mcpFactory = CreateMcpFactory(builder =>
            builder.ConfigureServices(services =>
                services.AddScoped<IWeatherForecastService, NotFoundWeatherForecastService>()));
        using var httpClient = mcpFactory.CreateClient();
        using var cts = CreateRequestCts();
        await using var client = await ConnectAsync(httpClient, cts.Token);

        var result = await client.CallToolAsync("get_weather_forecast", cancellationToken: cts.Token);

        Assert.True(result.IsError);
        var textBlock = result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Single();
        using var document = JsonDocument.Parse(textBlock.Text);
        Assert.Equal("not_found", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task GetRequest_IsRejectedInStatelessMode()
    {
        // Stateless Streamable HTTP maps no GET handler (nothing to resume,
        // no unsolicited server messages). The explicit 405 matters: without
        // it the request would fall through to the SPA fallback and serve
        // index.html to a protocol client expecting an event stream.
        using var mcpFactory = CreateMcpFactory();
        using var httpClient = mcpFactory.CreateClient();
        using var cts = CreateRequestCts();

        var response = await httpClient.GetAsync("/mcp", cts.Token);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("POST", Assert.Single(response.Content.Headers.Allow));
    }

    [Fact]
    public async Task DisallowedOrigin_IsRejectedWith403Problem()
    {
        // AllowedHosts drives BOTH host filtering and the origin allowlist;
        // "localhost" keeps the TestServer's own Host header valid while
        // making the origin check restrictive.
        using var mcpFactory = CreateMcpFactory(builder =>
            builder.UseSetting("AllowedHosts", "localhost"));
        using var httpClient = mcpFactory.CreateClient();
        using var cts = CreateRequestCts();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Origin", "https://attacker.test");
        request.Headers.Add("Accept", "application/json, text/event-stream");

        var response = await httpClient.SendAsync(request, cts.Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var payload = await response.Content.ReadAsStringAsync(cts.Token);
        using var document = JsonDocument.Parse(payload);
        Assert.Equal("Origin not allowed.", document.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task AllowlistedOrigin_IsNotRejected()
    {
        using var mcpFactory = CreateMcpFactory(builder =>
            builder.UseSetting("AllowedHosts", "localhost"));
        using var httpClient = mcpFactory.CreateClient();
        using var cts = CreateRequestCts();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        // Same-origin dev-server Origin: host matches the allowlist entry
        // regardless of scheme/port (host-only comparison).
        request.Headers.Add("Origin", "https://localhost:57292");
        request.Headers.Add("Accept", "application/json, text/event-stream");

        var response = await httpClient.SendAsync(request, cts.Token);

        // The empty JSON-RPC body is free to fail at protocol level — the
        // assertion is only that the origin gate did not fire.
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task McpEndpoint_IsCoveredByGlobalRateLimiter()
    {
        // No per-endpoint policy needed: the GlobalLimiter partitions by
        // client IP and applies to every endpoint, /mcp included. Same
        // single-permit determinism trick as RateLimitingTests.
        using var mcpFactory = CreateMcpFactory(builder =>
        {
            builder.UseSetting("Performance:RateLimiting:PermitLimit", "1");
            builder.UseSetting("Performance:RateLimiting:QueueLimit", "0");
            builder.UseSetting("Performance:RateLimiting:WindowSeconds", "60");
        });
        using var httpClient = mcpFactory.CreateClient();
        using var cts = CreateRequestCts();

        using var first = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        first.Headers.Add("Accept", "application/json, text/event-stream");
        var accepted = await httpClient.SendAsync(first, cts.Token);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, accepted.StatusCode);

        using var second = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        second.Headers.Add("Accept", "application/json, text/event-stream");
        var rejected = await httpClient.SendAsync(second, cts.Token);

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task McpDisabled_NoSessionCanBeEstablished()
    {
        // The default-off gate: with the flag at its appsettings.json default
        // no MCP endpoint is mapped, so the official client cannot complete
        // even the initialize handshake — there is no dormant-but-reachable
        // MCP surface. (The raw status /mcp answers with is an artifact of
        // the SPA fallback chain, deliberately not pinned here.)
        using var httpClient = factory.CreateClient();
        using var cts = CreateRequestCts();

        var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var client = await ConnectAsync(httpClient, cts.Token);
        });

        // A transport/protocol failure proves the gate; a cancellation would
        // only prove the 30s safety timeout fired — don't let it pass.
        Assert.IsNotAssignableFrom<OperationCanceledException>(exception);
    }

    private sealed class NotFoundWeatherForecastService : IWeatherForecastService
    {
        public Task<ServiceResponse<IReadOnlyList<WeatherForecast>>> GetForecastsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ServiceResponse<IReadOnlyList<WeatherForecast>>.NotFound());
    }
}
