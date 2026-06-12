using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Server;
using VueApp1.Server.Agent;
using VueApp1.Server.IntegrationTests.Infrastructure;
using VueApp1.Server.Mcp;
using VueApp1.Server.UnitTests.Agent;
using Xunit;

namespace VueApp1.Server.IntegrationTests;

/// <summary>
/// The agent module proven end to end with ZERO secrets and ZERO provider
/// packages: a scripted <see cref="FakeChatClient"/> drives the loop, and the
/// tool calls dispatch IN-PROCESS to the SAME DI McpServerTool registry the
/// /mcp endpoint serves. Treat this suite like McpEndpointTests: it is the
/// loud-failure gate for Microsoft.Extensions.AI.* and ModelContextProtocol
/// dependency bumps.
/// </summary>
public class AgentEndpointTests(IntegrationTestWebApplicationFactory factory)
    : IClassFixture<IntegrationTestWebApplicationFactory>
{
    private static readonly TimeSpan _requestTimeout = TimeSpan.FromSeconds(30);

    private static CancellationTokenSource CreateRequestCts()
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(_requestTimeout);
        return cts;
    }

    private WebApplicationFactory<Program> CreateAgentFactory(
        FakeChatClient chatClient, Action<IWebHostBuilder>? configure = null) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Agent:Enabled", "true");
            // Exactly the McpEndpointTests UseSetting pattern, applied to the
            // provider seam: a pre-registered IChatClient satisfies the
            // fail-fast validator, so the module boots with no key and no
            // provider package.
            builder.ConfigureServices(services => services.AddSingleton<IChatClient>(chatClient));
            configure?.Invoke(builder);
        });

    // -- THE proving test ----------------------------------------------------

    [Fact]
    public async Task AgentLoop_DispatchesInProcess_ToTheSameRegistryMcpServes()
    {
        // Turn 1: the model calls the weather tool; turn 2: it answers.
        var chatClient = new FakeChatClient()
            .Enqueue(FakeChatClient.ToolCall("call-1", "get_weather_forecast"))
            .Enqueue(FakeChatClient.Text("Expect mild weather."), FakeChatClient.Usage(1_000, 50));
        using var agentFactory = CreateAgentFactory(chatClient);
        using var httpClient = agentFactory.CreateClient();
        using var cts = CreateRequestCts();

        using var response = await httpClient.PostAsJsonAsync(
            "/api/agent/conversations/conv-prove/turns", new AgentTurnRequest("What's the weather?"), cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var parts = await ReadSsePartsAsync(response, cts.Token);
        var types = parts.Select(part => part.GetProperty("type").GetString()).ToList();

        // tool-input → tool-output → text → usage → finish, fully offline.
        var toolInputIndex = types.IndexOf("tool-input-available");
        var toolOutputIndex = types.IndexOf("tool-output-available");
        var textDeltaIndex = types.IndexOf("text-delta");
        Assert.True(toolInputIndex >= 0 && toolOutputIndex > toolInputIndex && textDeltaIndex > toolOutputIndex);
        Assert.Equal("usage", types[^2]);
        Assert.Equal("finish", types[^1]);
        Assert.Equal("stop", parts[^1].GetProperty("reason").GetString());

        // The output came from the REAL WeatherForecastService through the
        // REAL DI McpServerTool registry — same wrapper shape /mcp emits.
        var toolOutput = parts[toolOutputIndex];
        Assert.False(toolOutput.GetProperty("isError").GetBoolean());
        using var result = JsonDocument.Parse(toolOutput.GetProperty("resultJson").GetString()!);
        var forecasts = result.RootElement.GetProperty("result");
        Assert.Equal(5, forecasts.GetArrayLength());
        Assert.True(forecasts[0].TryGetProperty("temperatureC", out _));

        // Usage reached the wire (the visible-spend contract).
        var usage = parts[^2];
        Assert.Equal(1_000, usage.GetProperty("inputTokens").GetInt64());
    }

    // -- Flag matrix ----------------------------------------------------------

    [Fact]
    public async Task FlagMatrix_AgentOnly_BootsWithNoMcpEndpointMapped()
    {
        var chatClient = new FakeChatClient();
        using var agentFactory = CreateAgentFactory(chatClient); // Mcp:Enabled stays false
        using var httpClient = agentFactory.CreateClient();
        using var cts = CreateRequestCts();

        // The agent surface is up...
        var usage = await httpClient.GetAsync("/api/agent/usage", cts.Token);
        Assert.Equal(HttpStatusCode.OK, usage.StatusCode);

        // ...but no MCP session can be established: tool REGISTRATION ran
        // (the loop needs the registry), endpoint mapping did not.
        var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var client = await ConnectMcpAsync(httpClient, cts.Token);
        });
        Assert.IsNotAssignableFrom<OperationCanceledException>(exception);
    }

    [Fact]
    public async Task FlagMatrix_BothFlags_ShareOneToolRegistry()
    {
        var chatClient = new FakeChatClient()
            .Enqueue(FakeChatClient.ToolCall("call-1", "get_weather_forecast"))
            .Enqueue(FakeChatClient.Text("Done."));
        using var bothFactory = CreateAgentFactory(
            chatClient, builder => builder.UseSetting("Mcp:Enabled", "true"));
        using var httpClient = bothFactory.CreateClient();
        using var cts = CreateRequestCts();

        // ONE registration: the weather tool exists exactly once in DI even
        // with both consumers enabled.
        var registry = bothFactory.Services.GetServices<McpServerTool>();
        Assert.Single(registry, tool => tool.ProtocolTool.Name == "get_weather_forecast");

        // Consumer 1: external MCP client over /mcp.
        await using (var mcpClient = await ConnectMcpAsync(httpClient, cts.Token))
        {
            var tools = await mcpClient.ListToolsAsync(cancellationToken: cts.Token);
            Assert.Single(tools, tool => tool.Name == "get_weather_forecast");
        }

        // Consumer 2: the in-process loop, same registry, same process.
        using var response = await httpClient.PostAsJsonAsync(
            "/api/agent/conversations/conv-both/turns", new AgentTurnRequest("weather?"), cts.Token);
        var parts = await ReadSsePartsAsync(response, cts.Token);
        Assert.Contains(parts, part => part.GetProperty("type").GetString() == "tool-output-available");
    }

    [Fact]
    public async Task FlagOn_OpenApiDocumentStillContainsNoAgentPaths()
    {
        // The committed OpenAPI contract is generated flag-off; this pins the
        // OTHER half of the byte-stability invariant: an Agent:Enabled boot
        // must not surface /api/agent/* either (the group-level
        // ExcludeFromDescription is load-bearing, not decorative).
        var chatClient = new FakeChatClient();
        using var agentFactory = CreateAgentFactory(
            chatClient, builder => builder.UseSetting("OpenApi:Enabled", "true"));
        using var httpClient = agentFactory.CreateClient();
        using var cts = CreateRequestCts();

        var payload = await httpClient.GetStringAsync("/openapi/v1.json", cts.Token);
        using var document = JsonDocument.Parse(payload);
        Assert.All(
            document.RootElement.GetProperty("paths").EnumerateObject(),
            path => Assert.False(
                path.Name.StartsWith("/api/agent", StringComparison.OrdinalIgnoreCase),
                $"'{path.Name}' leaked into the OpenAPI document from a flag-on boot."));
    }

    [Fact]
    public async Task FlagOff_AgentEndpointsAnswerWithTheApi404Contract()
    {
        // The default factory: Agent:Enabled=false from appsettings.json.
        using var httpClient = factory.CreateClient();
        using var cts = CreateRequestCts();

        var get = await httpClient.GetAsync("/api/agent/usage", cts.Token);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal("application/problem+json", get.Content.Headers.ContentType?.MediaType);

        var post = await httpClient.PostAsJsonAsync(
            "/api/agent/conversations/c1/turns", new AgentTurnRequest("hi"), cts.Token);
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        Assert.Equal("application/problem+json", post.Content.Headers.ContentType?.MediaType);
    }

    // -- Incident locks --------------------------------------------------------

    [Fact]
    public async Task AbortMidStream_StillWritesACostedLedgerEntry()
    {
        // Usage arrives, then the stream hangs; the client walks away.
        var chatClient = new FakeChatClient()
            .EnqueueHangingAfter(
                FakeChatClient.Usage(2_000, 100),
                FakeChatClient.Text("partial"));
        using var agentFactory = CreateAgentFactory(chatClient);
        using var httpClient = agentFactory.CreateClient();
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, "/api/agent/conversations/conv-abort/turns")
        {
            Content = JsonContent.Create(new AgentTurnRequest("never finishes")),
        };
        var response = await httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, requestCts.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(requestCts.Token));

        // Wait for the first streamed part, then abort like a closed tab.
        var firstDataLine = await ReadNextDataLineAsync(reader, requestCts.Token);
        Assert.NotNull(firstDataLine);
        await requestCts.CancelAsync();
        response.Dispose();

        // The $13k pin: the abort must still produce exactly one costed
        // entry, written by the loop's finally on the server.
        var ledger = agentFactory.Services.GetRequiredService<AgentUsageLedger>();
        var entry = await PollForSingleEntryAsync(ledger);
        Assert.Equal("cancelled", entry.Outcome);
        Assert.Equal(2_000, entry.InputTokens);
    }

    [Fact]
    public async Task ConcurrentTurn_SecondPostIs409TurnInProgress()
    {
        var chatClient = new FakeChatClient()
            .EnqueueHangingAfter(FakeChatClient.Text("streaming..."));
        using var agentFactory = CreateAgentFactory(chatClient);
        using var httpClient = agentFactory.CreateClient();
        using var cts = CreateRequestCts();

        using var first = new HttpRequestMessage(
            HttpMethod.Post, "/api/agent/conversations/conv-busy/turns")
        {
            Content = JsonContent.Create(new AgentTurnRequest("one")),
        };
        var firstResponse = await httpClient.SendAsync(
            first, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        using var reader = new StreamReader(await firstResponse.Content.ReadAsStreamAsync(cts.Token));
        Assert.NotNull(await ReadNextDataLineAsync(reader, cts.Token)); // turn is live

        var second = await httpClient.PostAsJsonAsync(
            "/api/agent/conversations/conv-busy/turns", new AgentTurnRequest("two"), cts.Token);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var problem = await ReadProblemAsync(second, cts.Token);
        Assert.Equal("/problems/agent-turn-in-progress", problem.GetProperty("type").GetString());

        firstResponse.Dispose(); // release the hanging stream
    }

    [Fact]
    public async Task DailyBudgetExhausted_Returns429WithResetAtUtc()
    {
        var chatClient = new FakeChatClient()
            .Enqueue(FakeChatClient.Text("pricey answer"), FakeChatClient.Usage(1_000_000, 0));
        using var agentFactory = CreateAgentFactory(chatClient, builder =>
            builder.UseSetting("Agent:DailyUsdBudget", "0.001"));
        using var httpClient = agentFactory.CreateClient();
        using var cts = CreateRequestCts();

        // First request starts under budget (spend is 0) and runs to
        // completion — ~3 USD against claude-sonnet-4-5's appsettings pricing.
        var first = await httpClient.PostAsJsonAsync(
            "/api/agent/conversations/conv-budget/turns", new AgentTurnRequest("q1"), cts.Token);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await first.Content.ReadAsStringAsync(cts.Token);

        // Second request hits the preflight gate.
        var second = await httpClient.PostAsJsonAsync(
            "/api/agent/conversations/conv-budget/turns", new AgentTurnRequest("q2"), cts.Token);

        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        // A budget is a quota, not a rate limit: resetAtUtc, no Retry-After.
        Assert.False(second.Headers.Contains("Retry-After"));
        var problem = await ReadProblemAsync(second, cts.Token);
        Assert.Equal("/problems/agent-budget-exceeded", problem.GetProperty("type").GetString());
        var resetAtUtc = problem.GetProperty("resetAtUtc").GetDateTimeOffset();
        Assert.True(resetAtUtc > TimeProvider.System.GetUtcNow().AddMinutes(-1));
        Assert.Equal(TimeSpan.Zero, resetAtUtc.Offset);
    }

    // -- Idempotency-Key on the turn POST (IdempotencyEndpointFilter) ------------
    //
    // The stream-aware billing guard: a network-level retry while the original
    // stream is running must get 409, never a second billable generation. The
    // filter deliberately NEVER commits (a stream cannot be replayed from a
    // cache record), so a completed or aborted key is fresh again — replay
    // protection for streams is the in-flight window only, by design. The
    // payload-mismatch (422) and cached-replay branches need a committed
    // record, which this endpoint never writes — they are pinned at the unit
    // level in IdempotencyEndpointFilterTests.

    [Fact]
    public async Task IdempotencyKey_RetryWhileStreamInFlight_Gets409NotASecondGeneration()
    {
        var chatClient = new FakeChatClient()
            .EnqueueHangingAfter(FakeChatClient.Text("streaming..."));
        using var agentFactory = CreateAgentFactory(chatClient);
        using var httpClient = agentFactory.CreateClient();
        using var cts = CreateRequestCts();

        using var first = CreateTurnRequest("conv-idem", "one", idempotencyKey: "key-1");
        var firstResponse = await httpClient.SendAsync(
            first, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        using var reader = new StreamReader(await firstResponse.Content.ReadAsStreamAsync(cts.Token));
        Assert.NotNull(await ReadNextDataLineAsync(reader, cts.Token)); // stream is live

        using var retry = CreateTurnRequest("conv-idem", "one", idempotencyKey: "key-1");
        var second = await httpClient.SendAsync(retry, cts.Token);

        // The filter answers BEFORE the handler: the idempotency type, not
        // agent-turn-in-progress — and the provider saw exactly one call.
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var problem = await ReadProblemAsync(second, cts.Token);
        Assert.Equal("/problems/idempotency-in-progress", problem.GetProperty("type").GetString());
        Assert.Single(chatClient.Calls);

        firstResponse.Dispose(); // release the hanging stream
    }

    [Fact]
    public async Task IdempotencyKey_IsFreshAgainAfterStreamCompletion_NeverCommits()
    {
        var chatClient = new FakeChatClient()
            .Enqueue(FakeChatClient.Text("first answer"))
            .Enqueue(FakeChatClient.Text("second answer"));
        using var agentFactory = CreateAgentFactory(chatClient);
        using var httpClient = agentFactory.CreateClient();
        using var cts = CreateRequestCts();

        using var first = CreateTurnRequest("conv-idem-fresh", "same body", idempotencyKey: "key-fresh");
        using var firstResponse = await httpClient.SendAsync(first, cts.Token);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        await firstResponse.Content.ReadAsStringAsync(cts.Token);

        // The reservation releases when the SERVER finishes the response;
        // the client observing completion can race that by a moment.
        using var second = await PostTurnUntilNotConflictAsync(
            httpClient, "conv-idem-fresh", "same body", "key-fresh", cts.Token);

        // Identical retry after completion EXECUTES again (a second billable
        // generation): dispose-without-commit left the key fresh.
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, chatClient.Calls.Count);
    }

    [Fact]
    public async Task IdempotencyKey_ReservationReleasesOnClientAbort_KeyNotWedged()
    {
        var chatClient = new FakeChatClient()
            .EnqueueHangingAfter(FakeChatClient.Text("partial"))
            .Enqueue(FakeChatClient.Text("retry answer"));
        using var agentFactory = CreateAgentFactory(chatClient);
        using var httpClient = agentFactory.CreateClient();
        using var abortCts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);

        using var first = CreateTurnRequest("conv-idem-abort", "q", idempotencyKey: "key-abort");
        var firstResponse = await httpClient.SendAsync(
            first, HttpCompletionOption.ResponseHeadersRead, abortCts.Token);
        using (var reader = new StreamReader(await firstResponse.Content.ReadAsStreamAsync(abortCts.Token)))
        {
            Assert.NotNull(await ReadNextDataLineAsync(reader, abortCts.Token));
        }

        await abortCts.CancelAsync();
        firstResponse.Dispose(); // the closed-tab abort

        // RegisterForDisposeAsync ties the reservation to RESPONSE disposal,
        // not handler return: the abort must release it, or this key answers
        // 409 until process restart.
        using var cts = CreateRequestCts();
        using var retry = await PostTurnUntilNotConflictAsync(
            httpClient, "conv-idem-abort", "q", "key-abort", cts.Token);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(2, chatClient.Calls.Count);
    }

    // -- Approval flow ----------------------------------------------------------

    [Fact]
    public async Task Approval_FreezesAcrossRequests_ApproveExecutesFrozenArgs()
    {
        List<string> executed = [];
        var chatClient = new FakeChatClient()
            .Enqueue(FakeChatClient.ToolCall(
                "call-9", "delete_item", new Dictionary<string, object?> { ["target"] = "x42" }))
            .Enqueue(FakeChatClient.Text("Deleted as approved."));
        using var agentFactory = CreateAgentFactory(chatClient, builder =>
            builder.ConfigureServices(services => services.AddSingleton(DestructiveTool(executed.Add))));
        using var httpClient = agentFactory.CreateClient();
        using var cts = CreateRequestCts();

        // Request 1: the loop freezes at the approval gate.
        using var first = await httpClient.PostAsJsonAsync(
            "/api/agent/conversations/conv-appr/turns", new AgentTurnRequest("delete x42"), cts.Token);
        var firstParts = await ReadSsePartsAsync(first, cts.Token);
        var approval = Assert.Single(
            firstParts, part => part.GetProperty("type").GetString() == "tool-approval-required");
        Assert.Equal("delete_item", approval.GetProperty("toolName").GetString());
        Assert.Equal("approval-required", firstParts[^1].GetProperty("reason").GetString());
        Assert.Empty(executed); // frozen, not executed

        // Request 2 (a different HTTP request): approve → the FROZEN args
        // run and the loop resumes as SSE on this response.
        using var second = await httpClient.PostAsJsonAsync(
            "/api/agent/conversations/conv-appr/approvals/call-9",
            new AgentApprovalRequest(Approved: true), cts.Token);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondParts = await ReadSsePartsAsync(second, cts.Token);

        Assert.Equal(["x42"], executed);
        var output = Assert.Single(
            secondParts, part => part.GetProperty("type").GetString() == "tool-output-available");
        Assert.False(output.GetProperty("isError").GetBoolean());
        Assert.Equal("stop", secondParts[^1].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Approval_RejectIsModelVisible()
    {
        List<string> executed = [];
        var chatClient = new FakeChatClient()
            .Enqueue(FakeChatClient.ToolCall(
                "call-9", "delete_item", new Dictionary<string, object?> { ["target"] = "x42" }))
            .Enqueue(FakeChatClient.Text("Understood — not deleting."));
        using var agentFactory = CreateAgentFactory(chatClient, builder =>
            builder.ConfigureServices(services => services.AddSingleton(DestructiveTool(executed.Add))));
        using var httpClient = agentFactory.CreateClient();
        using var cts = CreateRequestCts();

        using var first = await httpClient.PostAsJsonAsync(
            "/api/agent/conversations/conv-rej/turns", new AgentTurnRequest("delete x42"), cts.Token);
        await ReadSsePartsAsync(first, cts.Token);

        using var second = await httpClient.PostAsJsonAsync(
            "/api/agent/conversations/conv-rej/approvals/call-9",
            new AgentApprovalRequest(Approved: false, Reason: "wrong item"), cts.Token);
        var secondParts = await ReadSsePartsAsync(second, cts.Token);

        Assert.Empty(executed);
        var output = Assert.Single(
            secondParts, part => part.GetProperty("type").GetString() == "tool-output-available");
        Assert.True(output.GetProperty("isError").GetBoolean());
        Assert.Contains("approval_rejected", output.GetProperty("resultJson").GetString(), StringComparison.Ordinal);

        // The model SAW the rejection (and the reason) on the resumed call.
        var resumedCall = chatClient.Calls[^1];
        var toolMessage = resumedCall.Messages.Single(m => m.Role == ChatRole.Tool);
        var resultText = Assert.IsType<FunctionResultContent>(
            Assert.Single(toolMessage.Contents)).Result?.ToString();
        Assert.Contains("wrong item", resultText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approval_PolicySurfaceDivergence_FailsClosedWith409()
    {
        List<string> executed = [];
        var chatClient = new FakeChatClient()
            .Enqueue(FakeChatClient.ToolCall(
                "call-9", "delete_item", new Dictionary<string, object?> { ["target"] = "x42" }));
        using var agentFactory = CreateAgentFactory(chatClient, builder =>
            builder.ConfigureServices(services => services.AddSingleton(DestructiveTool(executed.Add))));
        using var httpClient = agentFactory.CreateClient();
        using var cts = CreateRequestCts();

        using var first = await httpClient.PostAsJsonAsync(
            "/api/agent/conversations/conv-drift/turns", new AgentTurnRequest("delete x42"), cts.Token);
        await ReadSsePartsAsync(first, cts.Token);

        // Simulate policy-surface drift between freeze and approval (in a
        // restartless in-memory world the surface can only change through
        // the store, so the test writes the divergence directly).
        var store = agentFactory.Services.GetRequiredService<IAgentConversationStore>();
        var pending = store.GetPendingApproval("conv-drift", "call-9")!;
        store.RemovePendingApproval("conv-drift", "call-9");
        store.AddPendingApproval("conv-drift", pending with { PolicySurfaceHash = "STALE" });

        var approve = await httpClient.PostAsJsonAsync(
            "/api/agent/conversations/conv-drift/approvals/call-9",
            new AgentApprovalRequest(Approved: true), cts.Token);

        Assert.Equal(HttpStatusCode.Conflict, approve.StatusCode);
        var problem = await ReadProblemAsync(approve, cts.Token);
        Assert.Equal("/problems/agent-approval-conflict", problem.GetProperty("type").GetString());
        Assert.Empty(executed); // fail closed: nothing ran
    }

    // -- Detached turns + replay + usage -----------------------------------------

    [Fact]
    public async Task DetachedTurn_RunsWithoutHttpContext_InUnattendedPosture()
    {
        List<string> executed = [];
        var chatClient = new FakeChatClient()
            .Enqueue(FakeChatClient.ToolCall(
                "call-1", "delete_item", new Dictionary<string, object?> { ["target"] = "x" }))
            .Enqueue(FakeChatClient.Text("Nothing was deleted."));
        using var agentFactory = CreateAgentFactory(chatClient, builder =>
            builder.ConfigureServices(services => services.AddSingleton(DestructiveTool(executed.Add))));
        using var cts = CreateRequestCts();

        // The scheduling seam: resolve the loop from a fresh scope and run a
        // turn with no HTTP request anywhere in sight.
        await using var scope = agentFactory.Services.CreateAsyncScope();
        var loop = scope.ServiceProvider.GetRequiredService<AgentLoopService>();
        var result = await loop.RunDetachedTurnAsync(
            "conv-detached", "tidy up", AgentToolPosture.Unattended, cts.Token);

        Assert.Equal("stop", result.FinishReason);
        Assert.Equal("Nothing was deleted.", result.Text);
        // Unattended posture: only the read tier was advertised, and the
        // approval-tier call was refused with the envelope, not parked.
        Assert.Equal(["get_weather_forecast"], chatClient.Calls[0].Tools.Select(t => t.Name));
        Assert.Empty(executed);
        var store = agentFactory.Services.GetRequiredService<IAgentConversationStore>();
        Assert.Empty(store.GetPendingApprovals("conv-detached"));
        Assert.NotEmpty(store.GetMessages("conv-detached"));
    }

    [Fact]
    public async Task Replay_DerivesTheConversationThroughTheSameMapper()
    {
        var chatClient = new FakeChatClient()
            .Enqueue(FakeChatClient.ToolCall("call-1", "get_weather_forecast"))
            .Enqueue(FakeChatClient.Text("Mild weather ahead."), FakeChatClient.Usage(100, 10));
        using var agentFactory = CreateAgentFactory(chatClient);
        using var httpClient = agentFactory.CreateClient();
        using var cts = CreateRequestCts();

        using var live = await httpClient.PostAsJsonAsync(
            "/api/agent/conversations/conv-replay/turns", new AgentTurnRequest("weather?"), cts.Token);
        var liveParts = await ReadSsePartsAsync(live, cts.Token);

        var replay = await httpClient.GetAsync("/api/agent/conversations/conv-replay", cts.Token);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var snapshot = JsonDocument.Parse(await replay.Content.ReadAsStringAsync(cts.Token));
        var messages = snapshot.RootElement.GetProperty("messages").EnumerateArray().ToList();

        // user → assistant(tool call) → tool(result) → assistant(text).
        Assert.Equal(
            ["user", "assistant", "tool", "assistant"],
            messages.Select(m => m.GetProperty("role").GetString()));

        // The replayed parts use the SAME vocabulary the live stream emitted
        // (one renderer client-side), and the text content is identical.
        var replayedTypes = messages
            .SelectMany(m => m.GetProperty("parts").EnumerateArray())
            .Select(p => p.GetProperty("type").GetString())
            .ToList();
        Assert.Contains("tool-input-available", replayedTypes);
        Assert.Contains("tool-output-available", replayedTypes);
        var liveText = string.Concat(liveParts
            .Where(p => p.GetProperty("type").GetString() == "text-delta")
            .Select(p => p.GetProperty("delta").GetString()));
        var replayedText = string.Concat(messages
            .SelectMany(m => m.GetProperty("parts").EnumerateArray())
            .Where(p => p.GetProperty("type").GetString() == "text-delta")
            .Select(p => p.GetProperty("delta").GetString()));
        Assert.Contains(liveText, replayedText, StringComparison.Ordinal);

        var missing = await httpClient.GetAsync("/api/agent/conversations/never-existed", cts.Token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task UsageEndpoint_MakesSpendVisible()
    {
        var chatClient = new FakeChatClient()
            .Enqueue(FakeChatClient.Text("answer"), FakeChatClient.Usage(10_000, 500));
        using var agentFactory = CreateAgentFactory(chatClient);
        using var httpClient = agentFactory.CreateClient();
        using var cts = CreateRequestCts();

        using var turn = await httpClient.PostAsJsonAsync(
            "/api/agent/conversations/conv-usage/turns", new AgentTurnRequest("q"), cts.Token);
        await turn.Content.ReadAsStringAsync(cts.Token);

        var usage = await httpClient.GetAsync("/api/agent/usage", cts.Token);
        Assert.Equal(HttpStatusCode.OK, usage.StatusCode);
        using var summary = JsonDocument.Parse(await usage.Content.ReadAsStringAsync(cts.Token));
        var root = summary.RootElement;

        Assert.Equal(1, root.GetProperty("entryCount").GetInt32());
        Assert.Equal(10_000, root.GetProperty("totalInputTokens").GetInt64());
        Assert.True(root.GetProperty("totalUsd").GetDecimal() > 0m); // priced via appsettings
        Assert.Equal(25m, root.GetProperty("dailyUsdBudget").GetDecimal());
        Assert.Equal(1, root.GetProperty("recentEntries").GetArrayLength());
    }

    // -- Helpers -----------------------------------------------------------------

    private static McpServerTool DestructiveTool(Action<string> onExecuted) =>
        McpServerTool.Create(
            (string target) =>
            {
                onExecuted(target);
                return McpToolResults.Success($"deleted {target}");
            },
            new McpServerToolCreateOptions
            {
                Name = "delete_item",
                Description = "Deletes an item (test-only destructive tool).",
                ReadOnly = false,
                Destructive = true,
            });

    private static async Task<McpClient> ConnectMcpAsync(HttpClient httpClient, CancellationToken cancellationToken)
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

    private static HttpRequestMessage CreateTurnRequest(
        string conversationId, string message, string idempotencyKey)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/agent/conversations/{conversationId}/turns")
        {
            Content = JsonContent.Create(new AgentTurnRequest(message)),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return request;
    }

    /// <summary>
    /// Bounded poll for "the key/turn is free again": the idempotency
    /// reservation and the turn lock release when the SERVER completes the
    /// first response, which can lag the client's view of it by a moment.
    /// </summary>
    private static async Task<HttpResponseMessage> PostTurnUntilNotConflictAsync(
        HttpClient httpClient,
        string conversationId,
        string message,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            using var request = CreateTurnRequest(conversationId, message, idempotencyKey);
            var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.Conflict)
            {
                return response;
            }

            response.Dispose();
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }
    }

    /// <summary>Reads a finished SSE response into its JSON data payloads.</summary>
    private static async Task<List<JsonElement>> ReadSsePartsAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        List<JsonElement> parts = [];
        foreach (var line in body.Split('\n'))
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                using var document = JsonDocument.Parse(line["data: ".Length..].TrimEnd('\r'));
                parts.Add(document.RootElement.Clone());
            }
        }

        Assert.NotEmpty(parts);
        return parts;
    }

    private static async Task<string?> ReadNextDataLineAsync(
        StreamReader reader, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                return line;
            }
        }

        return null;
    }

    private static async Task<JsonElement> ReadProblemAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.Clone();
    }

    /// <summary>
    /// The abort is observed server-side asynchronously; poll (bounded)
    /// instead of sleeping a fixed guess.
    /// </summary>
    private static async Task<AgentUsageEntry> PollForSingleEntryAsync(AgentUsageLedger ledger)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            var entries = ledger.GetEntries();
            if (entries.Count == 1)
            {
                return entries[0];
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cts.Token);
        }
    }
}
