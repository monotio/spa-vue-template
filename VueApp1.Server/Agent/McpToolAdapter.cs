using System.Security.Claims;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace VueApp1.Server.Agent;

/// <summary>
/// The in-process MCP bridge: the agent loop's toolbox IS the
/// <see cref="McpServerTool"/> registry that <c>/mcp</c> serves — one tool
/// definition, two consumers, dispatched through DI with zero loopback HTTP
/// and zero JSON-RPC double-serialization.
///
/// Mechanism (only public, stable 1.4.0 surface): the SDK keeps its
/// <c>AIFunctionMcpServerTool</c> wrapper and its inner <c>AIFunction</c>
/// internal, so unwrapping is impossible; the one public dispatch seam is
/// <see cref="McpServerTool.InvokeAsync"/>, which needs a
/// <see cref="RequestContext{TParams}"/>, which needs an
/// <see cref="McpServer"/>. We anchor it with a lazily-created server over a
/// do-nothing transport. The server is NEVER run (<c>RunAsync</c> is never
/// called, no transport lifecycle engages) — it exists solely so the request
/// context has a non-null server. Per-call <c>Services</c> carries the
/// CURRENT request scope, so constructor-injected scoped services inside tool
/// classes resolve exactly as they do for an external <c>/mcp</c> call.
/// </summary>
public sealed class McpToolAdapter(IServiceProvider rootServices, ILoggerFactory loggerFactory)
{
    private readonly Lazy<McpServer> _inProcessServer = new(
        () => McpServer.Create(
            new NullTransport(),
            new McpServerOptions
            {
                ServerInfo = new Implementation { Name = "VueApp1.Agent.InProcess", Version = "1.0.0" },
            },
            loggerFactory,
            rootServices),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public McpServerToolAIFunction Bridge(McpServerTool tool) => new(tool, _inProcessServer);

    /// <summary>
    /// ~15 lines of "no": a never-delivering, never-completing transport. The
    /// reader channel is never written to and never completed, so even if
    /// something DID await server messages it would simply wait forever
    /// instead of observing a fake end-of-stream.
    /// </summary>
    internal sealed class NullTransport : ITransport
    {
        private readonly Channel<JsonRpcMessage> _never =
            Channel.CreateBounded<JsonRpcMessage>(capacity: 1);

        public string? SessionId => null;

        public ChannelReader<JsonRpcMessage> MessageReader => _never.Reader;

        public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>
/// Adapts one registry <see cref="McpServerTool"/> into the
/// <see cref="AIFunction"/> shape <c>ChatOptions.Tools</c> wants.
/// Name/description/schema are LIFTED from the tool's
/// <see cref="McpServerTool.ProtocolTool"/>, so the model sees the identical
/// JSON schema through the loop and through <c>/mcp</c> — schema fidelity is
/// structural, not re-derived.
/// </summary>
public sealed class McpServerToolAIFunction(McpServerTool tool, Lazy<McpServer> inProcessServer) : AIFunction
{
    /// <summary>Key under <see cref="AIFunctionArguments.Context"/> carrying the caller's principal.</summary>
    public static readonly object UserContextKey = typeof(ClaimsPrincipal);

    public McpServerTool Tool { get; } = tool;

    public override string Name => Tool.ProtocolTool.Name;

    public override string Description => Tool.ProtocolTool.Description ?? string.Empty;

    public override JsonElement JsonSchema => Tool.ProtocolTool.InputSchema;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var outcome = await InvokeToolAsync(arguments, cancellationToken).ConfigureAwait(false);
        return outcome.ResultJson;
    }

    /// <summary>
    /// The loop calls this richer overload instead of the plain
    /// <see cref="AIFunction"/> face: it preserves the protocol-level
    /// <c>isError</c> bit, which drives the SSE part, the error-spiral
    /// counter and the fail-soft envelope semantics. Failures return the
    /// SAME <c>McpToolResults</c> envelope text an external MCP client sees —
    /// one error vocabulary across REST, /mcp and the loop.
    /// </summary>
    public async ValueTask<McpToolCallOutcome> InvokeToolAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var requestContext = new RequestContext<CallToolRequestParams>(
            inProcessServer.Value,
            new JsonRpcRequest { Method = RequestMethods.ToolsCall },
            new CallToolRequestParams
            {
                Name = Tool.ProtocolTool.Name,
                Arguments = ConvertArguments(arguments),
            })
        {
            // The whole point of the synthetic context: this call resolves
            // tool-class constructor dependencies from the SAME scope as the
            // rest of the turn, and sees the same principal.
            Services = arguments.Services ?? rootServicesFallback(),
            User = arguments.Context?.TryGetValue(UserContextKey, out var user) == true
                ? user as ClaimsPrincipal
                : null,
            MatchedPrimitive = Tool,
        };

        var result = await Tool.InvokeAsync(requestContext, cancellationToken).ConfigureAwait(false);

        if (result.IsError == true)
        {
            // Fail SOFT: the envelope becomes a model-visible tool result the
            // model can branch on, byte-identical to the /mcp wire shape.
            return new McpToolCallOutcome(ConcatenateTextBlocks(result), IsError: true);
        }

        // StructuredContent raw JSON ?? concatenated text blocks — taken as
        // raw text, NEVER parse→re-serialize (tool-results fidelity rule).
        return new McpToolCallOutcome(
            result.StructuredContent?.GetRawText() ?? ConcatenateTextBlocks(result),
            IsError: false);

        // The in-process server is created over the root provider, so its
        // Services is never null in this composition.
        IServiceProvider rootServicesFallback() => inProcessServer.Value.Services!;
    }

    private static Dictionary<string, JsonElement> ConvertArguments(AIFunctionArguments arguments)
    {
        Dictionary<string, JsonElement> converted = new(arguments.Count, StringComparer.Ordinal);
        foreach (var (key, value) in arguments)
        {
            // Adapters hand us argument values as JsonElements already;
            // pass those through untouched (fidelity again). Anything else
            // (test-constructed primitives) serializes once, forward-only.
            converted[key] = value is JsonElement element
                ? element
                : JsonSerializer.SerializeToElement(value, AIJsonUtilities.DefaultOptions);
        }

        return converted;
    }

    private static string ConcatenateTextBlocks(CallToolResult result) =>
        string.Join(
            Environment.NewLine,
            result.Content.OfType<TextContentBlock>().Select(block => block.Text));
}

public readonly record struct McpToolCallOutcome(string ResultJson, bool IsError);
