using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VueApp1.Server.Mcp;
using VueApp1.Server.Agent;
using Xunit;

namespace VueApp1.Server.UnitTests.Agent;

/// <summary>
/// Pins the in-process bridge mechanics against the public 1.4.0 SDK
/// surface: schema lift from ProtocolTool, scoped-DI dispatch through the
/// synthetic RequestContext, the isError→envelope mapping, raw-JSON result
/// fidelity (no parse→re-serialize), and cancellation propagation.
/// </summary>
public class McpToolAdapterTests
{
    [Fact]
    public void NameDescriptionSchema_AreLiftedFromProtocolTool()
    {
        var tool = McpServerTool.Create(
            (string city) => $"\"{city}\"",
            new McpServerToolCreateOptions { Name = "lookup_city", Description = "Looks up a city." });
        var function = CreateAdapter().Bridge(tool);

        Assert.Equal(tool.ProtocolTool.Name, function.Name);
        Assert.Equal(tool.ProtocolTool.Description, function.Description);
        // Structural fidelity: the model sees the IDENTICAL input schema the
        // /mcp endpoint advertises — same JsonElement, not a re-derivation.
        Assert.Equal(tool.ProtocolTool.InputSchema.GetRawText(), function.JsonSchema.GetRawText());
        Assert.True(function.JsonSchema.GetProperty("properties").TryGetProperty("city", out _));
    }

    [Fact]
    public async Task Dispatch_ResolvesToolDependenciesFromTheProvidedScope()
    {
        var services = new ServiceCollection();
        services.AddScoped<IProbeService, ProbeService>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IProbeService>().Value = "scoped-123";

        var tool = McpServerTool.Create(
            typeof(ProbeTools).GetMethod(nameof(ProbeTools.GetValue))!,
            request => ActivatorUtilities.CreateInstance<ProbeTools>(request.Services!),
            new McpServerToolCreateOptions { Name = "probe" });
        var function = CreateAdapter().Bridge(tool);

        var outcome = await function.InvokeToolAsync(
            new AIFunctionArguments { Services = scope.ServiceProvider },
            TestContext.Current.CancellationToken);

        Assert.False(outcome.IsError);
        Assert.Contains("scoped-123", outcome.ResultJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServiceFailure_SurfacesAsTheSameEnvelopeExternalMcpClientsSee()
    {
        var tool = McpServerTool.Create(
            () => McpToolResults.FromServiceResponse(ServiceResponse<string>.NotFound()),
            new McpServerToolCreateOptions { Name = "failing_tool" });
        var function = CreateAdapter().Bridge(tool);

        var outcome = await function.InvokeToolAsync(
            new AIFunctionArguments(), TestContext.Current.CancellationToken);

        Assert.True(outcome.IsError);
        using var document = JsonDocument.Parse(outcome.ResultJson);
        Assert.Equal("not_found", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task StructuredContent_IsTakenAsRawJson_NeverReserialized()
    {
        // The deliberately odd whitespace survives only if the bridge takes
        // GetRawText() — a parse→re-serialize round trip would normalize it.
        var rawJson = """{"a":  1,  "b":"x"}""";
        var tool = McpServerTool.Create(
            () => new CallToolResult
            {
                Content = [new TextContentBlock { Text = rawJson }],
                StructuredContent = JsonSerializer.Deserialize<JsonElement>(rawJson),
            },
            new McpServerToolCreateOptions { Name = "raw_tool" });
        var function = CreateAdapter().Bridge(tool);

        var outcome = await function.InvokeToolAsync(
            new AIFunctionArguments(), TestContext.Current.CancellationToken);

        Assert.False(outcome.IsError);
        Assert.Equal(rawJson, outcome.ResultJson);
    }

    [Fact]
    public async Task TextBlocks_AreTheFallbackWhenNoStructuredContentExists()
    {
        var tool = McpServerTool.Create(
            () => new CallToolResult { Content = [new TextContentBlock { Text = "plain text answer" }] },
            new McpServerToolCreateOptions { Name = "text_tool" });
        var function = CreateAdapter().Bridge(tool);

        var outcome = await function.InvokeToolAsync(
            new AIFunctionArguments(), TestContext.Current.CancellationToken);

        Assert.Equal("plain text answer", outcome.ResultJson);
    }

    [Fact]
    public async Task Cancellation_PropagatesThroughTheBridge()
    {
        var tool = McpServerTool.Create(
            async (CancellationToken cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return "\"never\"";
            },
            new McpServerToolCreateOptions { Name = "slow_tool" });
        var function = CreateAdapter().Bridge(tool);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await function.InvokeToolAsync(new AIFunctionArguments(), cts.Token));
    }

    [Fact]
    public async Task Arguments_FlowAsJsonToTheToolParameters()
    {
        var tool = McpServerTool.Create(
            (string target, int count) => $"\"{target}:{count}\"",
            new McpServerToolCreateOptions { Name = "args_tool" });
        var function = CreateAdapter().Bridge(tool);

        var outcome = await function.InvokeToolAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["target"] = "x42", ["count"] = 3 }),
            TestContext.Current.CancellationToken);

        Assert.False(outcome.IsError);
        Assert.Contains("x42:3", outcome.ResultJson, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------

    private static McpToolAdapter CreateAdapter()
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        return new McpToolAdapter(provider, NullLoggerFactory.Instance);
    }

    public interface IProbeService
    {
        string Value { get; set; }
    }

    private sealed class ProbeService : IProbeService
    {
        public string Value { get; set; } = "unset";
    }

    public sealed class ProbeTools(IProbeService probe)
    {
        public string GetValue() => $"\"{probe.Value}\"";
    }
}
