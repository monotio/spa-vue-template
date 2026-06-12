using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VueApp1.Server.Agent;
using VueApp1.Server.Agent.Skills;
using VueApp1.Server.Mcp;
using Xunit;

namespace VueApp1.Server.UnitTests.Agent;

/// <summary>
/// Pins the loop guards offline against the scripted client: graceful turn
/// exhaustion (ChatToolMode.None with Tools INTACT), the error-spiral cap,
/// fail-soft envelopes, the between-turns budget exit, abort-still-ledgers,
/// approval freeze/resume, the unattended posture, and the no-exception-text
/// rule.
/// </summary>
public class AgentLoopServiceTests
{
    private const string ConversationId = "conv-1";

    [Fact]
    public async Task TurnCap_FinalTurnIsForcedToTextWithFullToolsIntact()
    {
        // The model would loop forever; the cap must end it GRACEFULLY: the
        // last call narrows via ToolMode=None while Tools stays byte-stable
        // (mutating tools[] would bust the provider prompt cache).
        using var harness = new Harness(
            new AgentOptions { MaxTurnsPerRequest = 3 },
            ReadTool("fake_read"));
        harness.Client
            .Enqueue(FakeChatClient.ToolCall("call-1", "fake_read"))
            .Enqueue(FakeChatClient.ToolCall("call-2", "fake_read"))
            .Enqueue(FakeChatClient.Text("Final answer."));

        var parts = await DrainAsync(harness.StartTurn("loop forever", TestContext.Current.CancellationToken));

        Assert.Equal(3, harness.Client.Calls.Count);
        Assert.All(harness.Client.Calls.Take(2), call => Assert.IsType<AutoChatToolMode>(call.ToolMode));
        var finalCall = harness.Client.Calls[^1];
        Assert.IsType<NoneChatToolMode>(finalCall.ToolMode);
        Assert.Equal(["fake_read"], finalCall.Tools.Select(t => t.Name));
        Assert.Equal(AgentFinishReasons.MaxTurns, Assert.IsType<FinishPart>(parts[^1]).Reason);
    }

    [Fact]
    public async Task ErrorSpiral_TerminatesAfterMaxConsecutiveToolErrors()
    {
        using var harness = new Harness(
            new AgentOptions { MaxConsecutiveToolErrors = 2, MaxTurnsPerRequest = 10 },
            FailingTool("broken_tool"));
        harness.Client
            .Enqueue(
                FakeChatClient.ToolCall("call-1", "broken_tool"),
                FakeChatClient.ToolCall("call-2", "broken_tool"));

        var parts = await DrainAsync(harness.StartTurn("spiral", TestContext.Current.CancellationToken));

        // Two consecutive failures hit the cap: no further provider calls.
        Assert.Single(harness.Client.Calls);
        Assert.Equal(2, parts.OfType<ToolOutputAvailablePart>().Count(p => p.IsError));
        Assert.Contains(parts, p => p is ErrorPart { Problem.Title: "Tool error limit reached" });
        Assert.Equal(AgentFinishReasons.Stop, Assert.IsType<FinishPart>(parts[^1]).Reason);
    }

    [Fact]
    public async Task ToolFailure_FailsSoft_ModelSeesEnvelopeAndLoopContinues()
    {
        using var harness = new Harness(
            new AgentOptions(),
            FailingTool("broken_tool"));
        harness.Client
            .Enqueue(FakeChatClient.ToolCall("call-1", "broken_tool"))
            .Enqueue(FakeChatClient.Text("I hit an error; here is what I know."));

        var parts = await DrainAsync(harness.StartTurn("try the tool", TestContext.Current.CancellationToken));

        var output = Assert.Single(parts.OfType<ToolOutputAvailablePart>());
        Assert.True(output.IsError);
        Assert.Contains("not_found", output.ResultJson, StringComparison.Ordinal);
        // The loop CONTINUED: the model got the envelope and answered.
        Assert.Equal(2, harness.Client.Calls.Count);
        Assert.Equal(AgentFinishReasons.Stop, Assert.IsType<FinishPart>(parts[^1]).Reason);
        // The envelope is model-visible in the second call's input.
        var toolMessage = harness.Client.Calls[1].Messages.Single(m => m.Role == ChatRole.Tool);
        var result = Assert.IsType<FunctionResultContent>(Assert.Single(toolMessage.Contents));
        Assert.Contains("not_found", result.Result?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestBudget_ExitsBetweenTurns_NeverMidStream()
    {
        using var harness = new Harness(
            new AgentOptions
            {
                MaxRequestUsd = 0.01m,
                MaxTurnsPerRequest = 10,
                Pricing = PricedDefaultModel(),
            },
            ReadTool("fake_read"));
        // Turn 0 costs ~3 USD — far over the cap — but it STARTED, so it
        // finishes (tool dispatch included); the exit happens before turn 1.
        harness.Client.Enqueue(
            FakeChatClient.ToolCall("call-1", "fake_read"),
            FakeChatClient.Usage(1_000_000, 0));

        var parts = await DrainAsync(harness.StartTurn("expensive question", TestContext.Current.CancellationToken));

        Assert.Single(harness.Client.Calls);
        Assert.Single(parts.OfType<ToolOutputAvailablePart>());
        Assert.Equal(AgentFinishReasons.BudgetExceeded, Assert.IsType<FinishPart>(parts[^1]).Reason);
    }

    [Fact]
    public async Task CancelledTurn_StillWritesACostedLedgerEntry_AndReleasesTheLock()
    {
        using var harness = new Harness(new AgentOptions { Pricing = PricedDefaultModel() });
        // Usage is scripted BEFORE the text that triggers the abort: the pin
        // is that usage which arrived before the abort is still billed.
        harness.Client.EnqueueHangingAfter(
            FakeChatClient.Usage(1_000, 500),
            FakeChatClient.Text("partial answer"));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var stream = harness.StartTurn("will be aborted", cts.Token);

        List<AgentStreamPart> received = [];
        await foreach (var part in stream)
        {
            received.Add(part);
            if (part is TextDeltaPart)
            {
                // Simulates the browser abort: cancel while the provider
                // stream is mid-flight.
                cts.Cancel();
            }
        }

        // The $13k pin: the abort still produced exactly one COSTED entry.
        var entry = Assert.Single(harness.Ledger.GetEntries());
        Assert.Equal("cancelled", entry.Outcome);
        Assert.Equal(1_000, entry.InputTokens);
        Assert.True(entry.EstimatedUsd > 0m);
        // No finish part reached the (gone) client...
        Assert.DoesNotContain(received, part => part is FinishPart);
        // ...and the turn lock was released for the next request.
        Assert.True(harness.Store.TryBeginTurn(ConversationId));
    }

    [Fact]
    public async Task ProviderFailure_RawExceptionTextNeverReachesWireOrTranscript()
    {
        using var harness = new Harness(new AgentOptions());
        harness.Client.EnqueueFailureAfter(
            new InvalidOperationException("SECRET-INTERNAL-DETAIL"),
            FakeChatClient.Text("partial"));

        var parts = await DrainAsync(harness.StartTurn("hello", TestContext.Current.CancellationToken));

        var error = Assert.Single(parts.OfType<ErrorPart>());
        Assert.DoesNotContain("SECRET-INTERNAL-DETAIL", error.Problem.Detail, StringComparison.Ordinal);
        Assert.Equal(AgentFinishReasons.Stop, Assert.IsType<FinishPart>(parts[^1]).Reason);
        Assert.All(
            harness.Store.GetMessages(ConversationId),
            message => Assert.DoesNotContain("SECRET-INTERNAL-DETAIL", message.Text, StringComparison.Ordinal));
        Assert.Equal("error", Assert.Single(harness.Ledger.GetEntries()).Outcome);
    }

    [Fact]
    public async Task ConcurrentTurn_SecondStartIsRejectedWithTurnInProgress()
    {
        using var harness = new Harness(new AgentOptions());
        harness.Client.Enqueue(FakeChatClient.Text("hi"));

        var first = harness.Loop.TryStartTurn(
            ConversationId, new AgentTurnRequest("one"), user: null, TestContext.Current.CancellationToken);
        var second = harness.Loop.TryStartTurn(
            ConversationId, new AgentTurnRequest("two"), user: null, TestContext.Current.CancellationToken);

        Assert.Equal(AgentTurnStartStatus.Started, first.Status);
        Assert.Equal(AgentTurnStartStatus.TurnInProgress, second.Status);

        await DrainAsync(first.Stream!);
        // After the first finishes, the conversation is free again.
        Assert.True(harness.Store.TryBeginTurn(ConversationId));
    }

    [Fact]
    public async Task Approval_FreezesThenExecutesTheFrozenArguments()
    {
        List<string> executed = [];
        using var harness = new Harness(
            new AgentOptions(),
            DestructiveTool("delete_item", executed.Add));
        harness.Client
            .Enqueue(FakeChatClient.ToolCall(
                "call-9", "delete_item", new Dictionary<string, object?> { ["target"] = "x42" }))
            .Enqueue(FakeChatClient.Text("Deleted."));

        var firstParts = await DrainAsync(harness.StartTurn("delete x42", TestContext.Current.CancellationToken));

        // Frozen, not executed; the stream parked at approval-required.
        Assert.Empty(executed);
        var approvalPart = Assert.Single(firstParts.OfType<ToolApprovalRequiredPart>());
        Assert.Equal("delete_item", approvalPart.ToolName);
        Assert.Equal(
            AgentFinishReasons.ApprovalRequired, Assert.IsType<FinishPart>(firstParts[^1]).Reason);
        Assert.Single(harness.Store.GetPendingApprovals(ConversationId));

        // Approve on a SECOND request: the frozen args run, the loop resumes.
        var resume = harness.Loop.TryStartApprovalTurn(
            ConversationId, "call-9", new AgentApprovalRequest(Approved: true),
            user: null, TestContext.Current.CancellationToken);
        Assert.Equal(AgentTurnStartStatus.Started, resume.Status);
        var resumeParts = await DrainAsync(resume.Stream!);

        Assert.Equal(["x42"], executed);
        var output = Assert.Single(resumeParts.OfType<ToolOutputAvailablePart>());
        Assert.False(output.IsError);
        Assert.Equal(AgentFinishReasons.Stop, Assert.IsType<FinishPart>(resumeParts[^1]).Reason);
        Assert.Empty(harness.Store.GetPendingApprovals(ConversationId));
    }

    [Fact]
    public async Task Approval_RejectAppendsAModelVisibleRejection()
    {
        List<string> executed = [];
        using var harness = new Harness(
            new AgentOptions(),
            DestructiveTool("delete_item", executed.Add));
        harness.Client
            .Enqueue(FakeChatClient.ToolCall(
                "call-9", "delete_item", new Dictionary<string, object?> { ["target"] = "x42" }))
            .Enqueue(FakeChatClient.Text("Understood, leaving it alone."));

        await DrainAsync(harness.StartTurn("delete x42", TestContext.Current.CancellationToken));
        var resume = harness.Loop.TryStartApprovalTurn(
            ConversationId, "call-9", new AgentApprovalRequest(Approved: false, Reason: "too risky"),
            user: null, TestContext.Current.CancellationToken);
        var resumeParts = await DrainAsync(resume.Stream!);

        Assert.Empty(executed);
        var output = Assert.Single(resumeParts.OfType<ToolOutputAvailablePart>());
        Assert.True(output.IsError);
        Assert.Contains("approval_rejected", output.ResultJson, StringComparison.Ordinal);
        Assert.Contains("too risky", output.ResultJson, StringComparison.Ordinal);
        // The model SAW the rejection on the resumed call.
        var toolMessage = harness.Client.Calls[1].Messages.Single(m => m.Role == ChatRole.Tool);
        var result = Assert.IsType<FunctionResultContent>(Assert.Single(toolMessage.Contents));
        Assert.Contains("approval_rejected", result.Result?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approval_PendingConsumedBeforeEnumeration_NeverExecutesTwice()
    {
        // The TOCTOU window: TryStartApprovalTurn validated the pending, but
        // by the time the resume stream actually runs, another request (a
        // double-click, a proxy retry) already consumed it. The store
        // removal is the atomic consume gate — losing it must mean the
        // frozen args do NOT run a second time.
        List<string> executed = [];
        using var harness = new Harness(
            new AgentOptions(),
            DestructiveTool("delete_item", executed.Add));
        harness.Client.Enqueue(FakeChatClient.ToolCall(
            "call-9", "delete_item", new Dictionary<string, object?> { ["target"] = "x42" }));

        await DrainAsync(harness.StartTurn("delete x42", TestContext.Current.CancellationToken));
        var resume = harness.Loop.TryStartApprovalTurn(
            ConversationId, "call-9", new AgentApprovalRequest(Approved: true),
            user: null, TestContext.Current.CancellationToken);
        Assert.Equal(AgentTurnStartStatus.Started, resume.Status);

        // A competing path consumes the pending before this stream runs.
        harness.Store.RemovePendingApproval(ConversationId, "call-9");

        var parts = await DrainAsync(resume.Stream!);

        Assert.Empty(executed); // the frozen call must not run again
        Assert.DoesNotContain(parts, part => part is ToolOutputAvailablePart);
        Assert.Contains(parts, part => part is ErrorPart);
        // The refusal still released the turn lock.
        Assert.True(harness.Store.TryBeginTurn(ConversationId));
    }

    [Fact]
    public void Approval_PendingConsumedBetweenValidationAndLock_IsNotFound()
    {
        // The cross-request race: request B passes the pre-lock validation,
        // request A's whole resume completes, THEN B acquires the turn lock.
        // The post-lock re-validation must observe the consumed pending and
        // refuse — never hand B a runnable stream for already-executed args.
        List<string> executed = [];
        var store = new VanishingPendingStore();
        using var harness = new Harness(
            new AgentOptions(), store,
            DestructiveTool("delete_item", executed.Add));
        store.AddPendingApproval(ConversationId, new PendingApproval(
            "call-1", "delete_item",
            new FunctionCallContent("call-1", "delete_item", new Dictionary<string, object?> { ["target"] = "x" }),
            "{}",
            harness.Policy.PolicySurfaceHash,
            Guid.NewGuid()));
        store.VanishOnNextBeginTurn = "call-1";

        var result = harness.Loop.TryStartApprovalTurn(
            ConversationId, "call-1", new AgentApprovalRequest(Approved: true),
            user: null, TestContext.Current.CancellationToken);

        Assert.Equal(AgentTurnStartStatus.ApprovalNotFound, result.Status);
        Assert.Empty(executed);
        // The refusal path released the lock it briefly held.
        Assert.True(store.TryBeginTurn(ConversationId));
    }

    [Fact]
    public async Task TurnStream_SecondEnumerationThrows_InsteadOfRunningUnlocked()
    {
        using var harness = new Harness(new AgentOptions());
        harness.Client.Enqueue(FakeChatClient.Text("hi"));

        var start = harness.Loop.TryStartTurn(
            ConversationId, new AgentTurnRequest("one"), user: null, TestContext.Current.CancellationToken);
        await DrainAsync(start.Stream!);

        // A second enumeration would re-run the whole turn WITHOUT the
        // conversation lock (and its finally would release a lock another
        // request may hold) — it must throw, not run.
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DrainAsync(start.Stream!));
        Assert.Contains("once", exception.Message, StringComparison.OrdinalIgnoreCase);
        // The refusal did not corrupt the lock state.
        Assert.True(harness.Store.TryBeginTurn(ConversationId));
    }

    [Fact]
    public void Approval_PolicySurfaceDivergence_FailsClosed()
    {
        using var harness = new Harness(
            new AgentOptions(),
            DestructiveTool("delete_item", _ => { }));
        harness.Store.AddPendingApproval(ConversationId, new PendingApproval(
            "call-1", "delete_item",
            new FunctionCallContent("call-1", "delete_item", new Dictionary<string, object?>()),
            "{}",
            PolicySurfaceHash: "STALE-HASH-FROM-ANOTHER-SURFACE",
            Guid.NewGuid()));

        var result = harness.Loop.TryStartApprovalTurn(
            ConversationId, "call-1", new AgentApprovalRequest(Approved: true),
            user: null, TestContext.Current.CancellationToken);

        Assert.Equal(AgentTurnStartStatus.ApprovalConflict, result.Status);
        // Fail closed: nothing executed, nothing silently discarded.
        Assert.Single(harness.Store.GetPendingApprovals(ConversationId));
    }

    [Fact]
    public async Task DetachedUnattendedTurn_ExcludesApprovalTier_AndRefusesHallucinatedCalls()
    {
        List<string> executed = [];
        using var harness = new Harness(
            new AgentOptions(),
            ReadTool("fake_read"),
            DestructiveTool("delete_item", executed.Add));
        harness.Client
            // The model hallucinates a call to a tool it was never shown.
            .Enqueue(FakeChatClient.ToolCall(
                "call-1", "delete_item", new Dictionary<string, object?> { ["target"] = "x" }))
            .Enqueue(FakeChatClient.Text("Could not do that; nothing was deleted."));

        var result = await harness.Loop.RunDetachedTurnAsync(
            ConversationId, "tidy up", AgentToolPosture.Unattended,
            TestContext.Current.CancellationToken);

        // Unattended posture: only the read tier was ever advertised...
        Assert.Equal(["fake_read"], harness.Client.Calls[0].Tools.Select(t => t.Name));
        // ...the hallucinated call got the envelope instead of parking...
        Assert.Empty(executed);
        Assert.Empty(harness.Store.GetPendingApprovals(ConversationId));
        var toolMessage = harness.Client.Calls[1].Messages.Single(m => m.Role == ChatRole.Tool);
        Assert.Contains(
            "not_authorized",
            Assert.IsType<FunctionResultContent>(Assert.Single(toolMessage.Contents)).Result?.ToString(),
            StringComparison.Ordinal);
        // ...and the detached result landed without any HttpContext.
        Assert.Equal(AgentFinishReasons.Stop, result.FinishReason);
        Assert.Equal("Could not do that; nothing was deleted.", result.Text);
        Assert.NotEmpty(harness.Store.GetMessages(ConversationId));
    }

    // -----------------------------------------------------------------------

    private static Dictionary<string, AgentModelPricing> PricedDefaultModel() => new()
    {
        // SelectedModel for the default provider (anthropic).
        ["claude-sonnet-4-5"] = new AgentModelPricing
        {
            InputPerMTokUsd = 3.0m,
            CachedInputPerMTokUsd = 0.3m,
            OutputPerMTokUsd = 15.0m,
        },
    };

    private static readonly int[] _sampleReadResult = [1, 2, 3];

    private static McpServerTool ReadTool(string name) =>
        McpServerTool.Create(
            () => McpToolResults.Success(_sampleReadResult),
            new McpServerToolCreateOptions
            {
                Name = name,
                Description = "test read tool",
                ReadOnly = true,
                Destructive = false,
            });

    private static McpServerTool FailingTool(string name) =>
        McpServerTool.Create(
            () => McpToolResults.FromServiceResponse(ServiceResponse<string>.NotFound()),
            new McpServerToolCreateOptions
            {
                Name = name,
                Description = "always fails",
                ReadOnly = true,
                Destructive = false,
            });

    private static McpServerTool DestructiveTool(string name, Action<string> onExecuted) =>
        McpServerTool.Create(
            (string target) =>
            {
                onExecuted(target);
                return McpToolResults.Success($"deleted {target}");
            },
            new McpServerToolCreateOptions
            {
                Name = name,
                Description = "test destructive tool",
                ReadOnly = false,
                Destructive = true,
            });

    private static async Task<List<AgentStreamPart>> DrainAsync(IAsyncEnumerable<AgentStreamPart> stream)
    {
        List<AgentStreamPart> parts = [];
        await foreach (var part in stream)
        {
            parts.Add(part);
        }

        return parts;
    }

    /// <summary>
    /// Deterministic stand-in for the race window: the pending approval
    /// "vanishes" exactly when the turn lock is acquired — as if a competing
    /// resume consumed it between this request's pre-lock validation and its
    /// lock acquisition.
    /// </summary>
    private sealed class VanishingPendingStore : IAgentConversationStore
    {
        private readonly InMemoryAgentConversationStore _inner = new();

        public string? VanishOnNextBeginTurn { get; set; }

        public bool TryBeginTurn(string conversationId)
        {
            if (!_inner.TryBeginTurn(conversationId))
            {
                return false;
            }

            if (VanishOnNextBeginTurn is { } toolCallId)
            {
                VanishOnNextBeginTurn = null;
                _inner.RemovePendingApproval(conversationId, toolCallId);
            }

            return true;
        }

        public bool Exists(string conversationId) => _inner.Exists(conversationId);
        public IReadOnlyList<ChatMessage> GetMessages(string conversationId) => _inner.GetMessages(conversationId);
        public void AppendMessages(string conversationId, IReadOnlyList<ChatMessage> messages) =>
            _inner.AppendMessages(conversationId, messages);
        public void EndTurn(string conversationId) => _inner.EndTurn(conversationId);
        public void AddPendingApproval(string conversationId, PendingApproval approval) =>
            _inner.AddPendingApproval(conversationId, approval);
        public PendingApproval? GetPendingApproval(string conversationId, string toolCallId) =>
            _inner.GetPendingApproval(conversationId, toolCallId);
        public bool RemovePendingApproval(string conversationId, string toolCallId) =>
            _inner.RemovePendingApproval(conversationId, toolCallId);
        public IReadOnlyList<PendingApproval> GetPendingApprovals(string conversationId) =>
            _inner.GetPendingApprovals(conversationId);
    }

    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider _provider;

        public Harness(AgentOptions options, params McpServerTool[] tools)
            : this(options, new InMemoryAgentConversationStore(), tools)
        {
        }

        public Harness(AgentOptions options, IAgentConversationStore store, params McpServerTool[] tools)
        {
            Store = store;
            _provider = new ServiceCollection().BuildServiceProvider();
            var wrapped = Options.Create(options);
            var adapter = new McpToolAdapter(_provider, NullLoggerFactory.Instance);
            Policy = new AgentToolPolicy(tools, adapter, wrapped);
            Ledger = new AgentUsageLedger(wrapped, TimeProvider.System);
            // An EMPTY skill catalog (nonexistent directory): these tests pin
            // the loop guards; the skills-flavored loop tests live in
            // SkillCatalogTests with a populated catalog.
            var skills = new FileSystemSkillCatalog(
                Path.Combine(Path.GetTempPath(), "vueapp1-no-skills", Guid.NewGuid().ToString("N")));
            Loop = new AgentLoopService(
                Client, Policy, skills, Store, Ledger, wrapped, _provider,
                NullLogger<AgentLoopService>.Instance);
        }

        public FakeChatClient Client { get; } = new();

        public IAgentConversationStore Store { get; }

        public AgentToolPolicy Policy { get; }

        public AgentUsageLedger Ledger { get; }

        public AgentLoopService Loop { get; }

        public IAsyncEnumerable<AgentStreamPart> StartTurn(string message, CancellationToken cancellationToken)
        {
            var start = Loop.TryStartTurn(
                ConversationId, new AgentTurnRequest(message), user: null, cancellationToken);
            Assert.Equal(AgentTurnStartStatus.Started, start.Status);
            return start.Stream!;
        }

        public void Dispose()
        {
            Ledger.Dispose();
            _provider.Dispose();
        }
    }
}
