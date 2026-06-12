using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using VueApp1.Server.Mcp;

namespace VueApp1.Server.Agent;

/// <summary>
/// The agent loop, hand-rolled and visible on purpose: every production
/// non-negotiable — the turn cap with graceful exhaustion, the error-spiral
/// cap, the between-turns budget check, the approval freeze, the
/// finally-block accounting that survives a client abort — is a readable
/// line here instead of a hidden middleware knob. One HTTP POST = one agent
/// request = up to <c>Agent:MaxTurnsPerRequest</c> provider calls ("turns").
/// The 20-line <c>UseFunctionInvocation</c> alternative, and when to prefer
/// it, lives in docs/AGENT.md.
///
/// Cancellation: browser <c>AbortController</c> → <c>RequestAborted</c> → the
/// provider call. Disconnect CANCELS generation — stopping billing beats
/// resumability in the no-database default (the Redis-tee resumable upgrade
/// is documented, not shipped).
/// </summary>
public sealed partial class AgentLoopService(
    IChatClient chatClient,
    AgentToolPolicy toolPolicy,
    IAgentConversationStore store,
    AgentUsageLedger ledger,
    IOptions<AgentOptions> options,
    IServiceProvider services,
    ILogger<AgentLoopService> logger)
{
    private const int MaxUserMessageLength = 32_000;

    // Collected by the "VueApp1.*" wildcard in SetupTelemetry. The gen_ai.*
    // attribute names follow the OTel GenAI semconv, which is still
    // experimental — prompt text is never recorded either way.
    private static readonly ActivitySource _activitySource = new("VueApp1.Agent");

    private static readonly JsonSerializerOptions _envelopeJson = new(JsonSerializerDefaults.Web);

    public static bool IsValidUserMessage(string? message) =>
        !string.IsNullOrWhiteSpace(message) && message.Length <= MaxUserMessageLength;

    /// <summary>
    /// A <see cref="AgentTurnStartStatus.Started"/> result HOLDS the
    /// conversation's one-active-turn lock; the matching release lives in the
    /// returned stream's <c>finally</c>. The caller must enumerate
    /// <see cref="AgentTurnStart.Stream"/> exactly once — see the contract on
    /// <see cref="AgentTurnStart"/>.
    /// </summary>
    public AgentTurnStart TryStartTurn(
        string conversationId,
        AgentTurnRequest request,
        ClaimsPrincipal? user,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!store.TryBeginTurn(conversationId))
        {
            return new AgentTurnStart(AgentTurnStartStatus.TurnInProgress);
        }

        // Daily budget is a PREFLIGHT gate at turn 0, by design never a
        // mid-flight kill: a request that begins under budget runs to
        // completion even if it crosses the line (bounded overrun beats
        // partial output).
        if (ledger.IsDailyBudgetExhausted(out var resetAtUtc))
        {
            store.EndTurn(conversationId);
            return new AgentTurnStart(AgentTurnStartStatus.BudgetExceeded, ResetAtUtc: resetAtUtc);
        }

        var turnId = Guid.NewGuid();
        var userMessage = new ChatMessage(ChatRole.User, request.Message)
        {
            AdditionalProperties = new() { [AgentUiParts.TurnStampKey] = turnId },
        };
        return new AgentTurnStart(
            AgentTurnStartStatus.Started,
            RunTurnsAsync(
                conversationId, turnId, userMessage,
                approvalAction: null, AgentToolPosture.Interactive, user,
                new SingleEnumerationGuard(), cancellationToken));
    }

    /// <inheritdoc cref="TryStartTurn"/>
    public AgentTurnStart TryStartApprovalTurn(
        string conversationId,
        string toolCallId,
        AgentApprovalRequest request,
        ClaimsPrincipal? user,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Cheap pre-lock rejection (and 404-over-409 precedence while another
        // turn streams) — NOT the authoritative gate; that re-check is below,
        // under the turn lock.
        var pending = store.GetPendingApproval(conversationId, toolCallId);
        if (pending is null)
        {
            return new AgentTurnStart(AgentTurnStartStatus.ApprovalNotFound);
        }

        if (!string.Equals(pending.PolicySurfaceHash, toolPolicy.PolicySurfaceHash, StringComparison.Ordinal))
        {
            return new AgentTurnStart(AgentTurnStartStatus.ApprovalConflict);
        }

        if (!store.TryBeginTurn(conversationId))
        {
            return new AgentTurnStart(AgentTurnStartStatus.TurnInProgress);
        }

        // Re-validate UNDER the one-active-turn lock: between the pre-checks
        // above and the lock acquisition, a concurrent resume for the same
        // toolCallId can run to completion (its whole remove → execute → park
        // sequence takes milliseconds). While we hold the lock nothing else
        // can consume the pending, so this read is authoritative.
        pending = store.GetPendingApproval(conversationId, toolCallId);
        if (pending is null)
        {
            store.EndTurn(conversationId);
            return new AgentTurnStart(AgentTurnStartStatus.ApprovalNotFound);
        }

        // Fail closed: the approval was granted against a snapshot of the
        // policy surface (tool names + schemas + tiers + knobs). If that
        // surface changed since the freeze, the user approved something the
        // system no longer means — refuse, never execute.
        if (!string.Equals(pending.PolicySurfaceHash, toolPolicy.PolicySurfaceHash, StringComparison.Ordinal))
        {
            store.EndTurn(conversationId);
            return new AgentTurnStart(AgentTurnStartStatus.ApprovalConflict);
        }

        if (ledger.IsDailyBudgetExhausted(out var resetAtUtc))
        {
            store.EndTurn(conversationId);
            return new AgentTurnStart(AgentTurnStartStatus.BudgetExceeded, ResetAtUtc: resetAtUtc);
        }

        var turnId = Guid.NewGuid();
        return new AgentTurnStart(
            AgentTurnStartStatus.Started,
            RunTurnsAsync(
                conversationId, turnId, userMessage: null,
                new ApprovalAction(pending, request.Approved, request.Reason),
                AgentToolPosture.Interactive, user,
                new SingleEnumerationGuard(), cancellationToken));
    }

    /// <summary>
    /// The HttpContext-free entry point — the scheduling seam. Same loop, no
    /// SSE writer: parts are drained internally and the result lives in the
    /// store. A future scheduler/sweeper or <c>BackgroundWorkQueue</c>
    /// consumer calls exactly this (resolve <see cref="AgentLoopService"/>
    /// from a fresh DI scope per run); the DB-sweep doctrine targeting it is
    /// in docs/AGENT.md.
    /// </summary>
    public async Task<AgentDetachedTurnResult> RunDetachedTurnAsync(
        string conversationId,
        string prompt,
        AgentToolPosture posture,
        CancellationToken cancellationToken)
    {
        if (!store.TryBeginTurn(conversationId))
        {
            return new AgentDetachedTurnResult(AgentFinishReasons.TurnInProgress, null);
        }

        if (ledger.IsDailyBudgetExhausted(out _))
        {
            store.EndTurn(conversationId);
            return new AgentDetachedTurnResult(AgentFinishReasons.BudgetExceeded, null);
        }

        var turnId = Guid.NewGuid();
        var userMessage = new ChatMessage(ChatRole.User, prompt)
        {
            AdditionalProperties = new() { [AgentUiParts.TurnStampKey] = turnId },
        };

        var finishReason = AgentFinishReasons.Cancelled;
        var text = new StringBuilder();
        await foreach (var part in RunTurnsAsync(
            conversationId, turnId, userMessage, approvalAction: null, posture,
            user: null, new SingleEnumerationGuard(), cancellationToken).ConfigureAwait(false))
        {
            switch (part)
            {
                case TextDeltaPart delta:
                    text.Append(delta.Delta);
                    break;
                case FinishPart finish:
                    finishReason = finish.Reason;
                    break;
                default:
                    break;
            }
        }

        return new AgentDetachedTurnResult(finishReason, text.Length > 0 ? text.ToString() : null);
    }

    // -----------------------------------------------------------------------

    private async IAsyncEnumerable<AgentStreamPart> RunTurnsAsync(
        string conversationId,
        Guid turnId,
        ChatMessage? userMessage,
        ApprovalAction? approvalAction,
        AgentToolPosture posture,
        ClaimsPrincipal? user,
        SingleEnumerationGuard enumerationGuard,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // BEFORE the try: a second enumeration must throw without running
        // the finally — its EndTurn would release a lock this enumeration
        // does not own.
        enumerationGuard.OnEnumerationStarted();

        var agentOptions = options.Value;
        try
        {
            var provider = agentOptions.Provider;
            var model = agentOptions.SelectedModel;
            var messages = BuildRequestMessages(conversationId, provider);

            if (userMessage is not null)
            {
                store.AppendMessages(conversationId, [userMessage]);
                messages.Add(userMessage);
            }

            if (approvalAction is not null)
            {
                // The ATOMIC consume gate: removal either succeeds exactly
                // once or the pending was already consumed by another path —
                // in which case the frozen args must never run again
                // (defense in depth behind the under-lock re-validation in
                // TryStartApprovalTurn).
                if (!store.RemovePendingApproval(conversationId, approvalAction.Pending.ToolCallId))
                {
                    yield return new ErrorPart
                    {
                        ConversationId = conversationId,
                        TurnId = turnId,
                        Problem = new AgentProblem(
                            Type: null,
                            Title: "Approval already resolved",
                            Status: StatusCodes.Status409Conflict,
                            Detail: "This tool call's approval was already consumed by another request; "
                                + "the frozen call was not executed again."),
                    };
                    yield return Finish(AgentFinishReasons.Stop);
                    yield break;
                }

                var (resultMessage, outputPart) = await ExecuteApprovalActionAsync(
                    conversationId, turnId, approvalAction, user, cancellationToken).ConfigureAwait(false);
                messages.Add(resultMessage);
                yield return outputPart;

                if (store.GetPendingApprovals(conversationId).Count > 0)
                {
                    // Other calls from the same response are still frozen; the
                    // transcript still holds unanswered tool calls, so the
                    // model cannot resume yet. Park again.
                    yield return Finish(AgentFinishReasons.ApprovalRequired);
                    yield break;
                }
            }

            // Posture is fixed for the whole request, and the list instance
            // is reused across turns — tools[] is NEVER mutated
            // mid-conversation (mutating it busts the provider's prompt-cache
            // prefix; narrowing happens via ToolMode below instead).
            var tools = posture == AgentToolPosture.Unattended ? toolPolicy.UnattendedTools : toolPolicy.Tools;
            var requestSpentUsd = 0m;
            var consecutiveToolErrors = 0;
            var anyToolCalls = false;
            string? finishReason = null;

            for (var turn = 0; turn < agentOptions.MaxTurnsPerRequest && finishReason is null; turn++)
            {
                // Per-request budget: checked BETWEEN turns, never mid-stream.
                if (requestSpentUsd >= agentOptions.MaxRequestUsd)
                {
                    finishReason = AgentFinishReasons.BudgetExceeded;
                    break;
                }

                // Graceful exhaustion (cache-safe narrowing): the FINAL turn
                // keeps the full Tools list intact and narrows via
                // ChatToolMode.None — the model must answer in text, and the
                // byte-stable tool prefix stays byte-stable.
                var finalTurn = turn == agentOptions.MaxTurnsPerRequest - 1;
                var chatOptions = new ChatOptions
                {
                    Tools = tools,
                    ToolMode = finalTurn ? ChatToolMode.None : ChatToolMode.Auto,
                };

                List<ChatResponseUpdate> updates = [];
                UsageDetails? usage = null;
                var callOutcome = "completed";
                Exception? providerFailure = null;
                var cancelled = false;
                AgentUsageEntry? entry = null;
                var emitter = new StreamingPartEmitter(conversationId, turnId);

                using var activity = _activitySource.StartActivity("agent.provider_call");
                activity?.SetTag("gen_ai.provider.name", provider);
                activity?.SetTag("gen_ai.request.model", model);

                var enumerator = chatClient
                    .GetStreamingResponseAsync(messages, chatOptions, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);
                try
                {
                    while (true)
                    {
                        ChatResponseUpdate update;
                        try
                        {
                            if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                            {
                                break;
                            }

                            update = enumerator.Current;
                        }
                        catch (OperationCanceledException)
                        {
                            cancelled = true;
                            callOutcome = "cancelled";
                            break;
                        }
                        catch (Exception exception)
                        {
                            providerFailure = exception;
                            callOutcome = "error";
                            break;
                        }

                        updates.Add(update);
                        foreach (var content in update.Contents)
                        {
                            if (content is UsageContent usageContent)
                            {
                                (usage ??= new UsageDetails()).Add(usageContent.Details);
                                continue;
                            }

                            foreach (var part in emitter.Map(content))
                            {
                                yield return part;
                            }
                        }
                    }
                }
                finally
                {
                    // THE $13k line: exactly one costed ledger entry per
                    // provider call, written in a finally — a client abort
                    // disposes this iterator at its current yield point and
                    // STILL lands here. The provider billed for what
                    // streamed; so do we.
                    entry = ledger.Record(provider, model, conversationId, turnId, usage, callOutcome);
                    try
                    {
                        await enumerator.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Disposal of a cancelled provider stream may itself
                        // observe the cancellation; the entry above is
                        // already safe.
                    }
                }

                requestSpentUsd += entry!.EstimatedUsd;

                if (cancelled)
                {
                    // Browser abort or detached-caller cancellation: there is
                    // nobody to stream to. Billing is recorded; the outer
                    // finally releases the lock; completed prior messages are
                    // already persisted.
                    LogTurnCancelled(logger, conversationId, turn);
                    yield break;
                }

                foreach (var part in emitter.CloseOpenBlocks())
                {
                    yield return part;
                }

                if (providerFailure is not null)
                {
                    LogProviderCallFailed(logger, providerFailure, conversationId, turn);
                    // The IncludeDetailedErrors=false posture, hand-enforced:
                    // raw exception text reaches the log, never the wire and
                    // never the transcript.
                    yield return new ErrorPart
                    {
                        ConversationId = conversationId,
                        TurnId = turnId,
                        Problem = new AgentProblem(
                            Type: null,
                            Title: "Provider call failed",
                            Status: StatusCodes.Status502BadGateway,
                            Detail: "The model provider call failed; the turn was abandoned. See server logs."),
                    };
                    yield return Finish(AgentFinishReasons.Stop);
                    yield break;
                }

                var responseMessages = updates.ToChatResponse().Messages;
                StampAssistantMessages(responseMessages, provider, turnId);
                store.AppendMessages(conversationId, [.. responseMessages]);
                messages.AddRange(responseMessages);

                yield return new UsagePart
                {
                    ConversationId = conversationId,
                    TurnId = turnId,
                    InputTokens = entry.InputTokens,
                    CachedInputTokens = entry.CachedInputTokens,
                    OutputTokens = entry.OutputTokens,
                    ReasoningTokens = entry.ReasoningTokens,
                    EstimatedUsd = entry.EstimatedUsd,
                };

                var toolCalls = responseMessages
                    .SelectMany(message => message.Contents)
                    .OfType<FunctionCallContent>()
                    .ToList();

                if (toolCalls.Count == 0)
                {
                    finishReason = finalTurn && anyToolCalls ? AgentFinishReasons.MaxTurns : AgentFinishReasons.Stop;
                    break;
                }

                anyToolCalls = true;
                foreach (var call in toolCalls)
                {
                    if (!toolPolicy.TryGet(call.Name, out var registration))
                    {
                        var unknown = Envelope("not_found", $"No tool named '{call.Name}' is registered.");
                        var (message, part) = AppendToolResult(conversationId, turnId, call, unknown);
                        messages.Add(message);
                        yield return part;
                        consecutiveToolErrors++;
                        continue;
                    }

                    if (posture == AgentToolPosture.Unattended && !toolPolicy.IsAllowedUnattended(call.Name))
                    {
                        var refused = Envelope(
                            "not_authorized",
                            "This tool requires human approval and the run is unattended.");
                        var (message, part) = AppendToolResult(conversationId, turnId, call, refused);
                        messages.Add(message);
                        yield return part;
                        consecutiveToolErrors++;
                        continue;
                    }

                    if (registration.RequiresApproval)
                    {
                        var pending = new PendingApproval(
                            call.CallId,
                            call.Name,
                            call,
                            AgentUiParts.SerializeArguments(call),
                            toolPolicy.PolicySurfaceHash,
                            turnId);
                        store.AddPendingApproval(conversationId, pending);
                        yield return AgentUiParts.ApprovalRequired(pending, conversationId);
                        finishReason = AgentFinishReasons.ApprovalRequired;
                        continue;
                    }

                    var outcome = await InvokeBridgedToolAsync(registration.Function, call, user, cancellationToken)
                        .ConfigureAwait(false);
                    var (resultMessage, outputPart) = AppendToolResult(conversationId, turnId, call, outcome);
                    messages.Add(resultMessage);
                    yield return outputPart;
                    consecutiveToolErrors = outcome.IsError ? consecutiveToolErrors + 1 : 0;
                }

                if (finishReason is null && consecutiveToolErrors >= agentOptions.MaxConsecutiveToolErrors)
                {
                    // Terminate the error spiral instead of paying for more
                    // turns of the model retrying a broken tool.
                    LogErrorSpiralTerminated(logger, conversationId, consecutiveToolErrors);
                    yield return new ErrorPart
                    {
                        ConversationId = conversationId,
                        TurnId = turnId,
                        Problem = new AgentProblem(
                            Type: null,
                            Title: "Tool error limit reached",
                            Status: StatusCodes.Status502BadGateway,
                            Detail: $"{consecutiveToolErrors} consecutive tool calls failed; the request was stopped."),
                    };
                    finishReason = AgentFinishReasons.Stop;
                }
            }

            yield return Finish(finishReason ?? AgentFinishReasons.MaxTurns);
        }
        finally
        {
            store.EndTurn(conversationId);
        }

        FinishPart Finish(string reason) =>
            new() { ConversationId = conversationId, TurnId = turnId, Reason = reason };
    }

    private async ValueTask<(ChatMessage Message, ToolOutputAvailablePart Part)> ExecuteApprovalActionAsync(
        string conversationId,
        Guid turnId,
        ApprovalAction action,
        ClaimsPrincipal? user,
        CancellationToken cancellationToken)
    {
        // The caller (RunTurnsAsync) already consumed the pending via the
        // atomic RemovePendingApproval gate; this method only executes.
        var pending = action.Pending;

        McpToolCallOutcome outcome;
        if (!action.Approved)
        {
            // The rejection is MODEL-VISIBLE: the model learns the human said
            // no (and why), instead of seeing an unanswered call.
            outcome = Envelope(
                "approval_rejected",
                string.IsNullOrWhiteSpace(action.Reason)
                    ? "The user rejected this tool call."
                    : $"The user rejected this tool call: {action.Reason}");
        }
        else if (toolPolicy.TryGet(pending.ToolName, out var registration))
        {
            // Executes the FROZEN arguments — what the user saw is what runs.
            outcome = await InvokeBridgedToolAsync(registration.Function, pending.Call, user, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            outcome = Envelope("not_found", $"No tool named '{pending.ToolName}' is registered.");
        }

        var (message, part) = AppendToolResult(conversationId, turnId, pending.Call, outcome);
        return (message, part);
    }

    private async ValueTask<McpToolCallOutcome> InvokeBridgedToolAsync(
        McpServerToolAIFunction function,
        FunctionCallContent call,
        ClaimsPrincipal? user,
        CancellationToken cancellationToken)
    {
        var arguments = call.Arguments is null
            ? new AIFunctionArguments()
            : new AIFunctionArguments(call.Arguments);
        // The per-call dispatch context: the CURRENT scope's services (tool
        // constructor dependencies resolve alongside the rest of the turn)
        // and the caller's principal.
        arguments.Services = services;
        if (user is not null)
        {
            arguments.Context = new Dictionary<object, object?>
            {
                [McpServerToolAIFunction.UserContextKey] = user,
            };
        }

        using var activity = _activitySource.StartActivity("agent.execute_tool");
        activity?.SetTag("gen_ai.tool.name", function.Name);
        try
        {
            return await function.InvokeToolAsync(arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Tool failures fail SOFT (the loop continues, the model adapts);
            // raw exception text is withheld from the transcript.
            LogToolDispatchFailed(logger, exception, function.Name);
            return Envelope("internal_error", "The tool failed unexpectedly. See server logs.");
        }
    }

    private (ChatMessage Message, ToolOutputAvailablePart Part) AppendToolResult(
        string conversationId,
        Guid turnId,
        FunctionCallContent call,
        McpToolCallOutcome outcome)
    {
        var resultContent = new FunctionResultContent(call.CallId, ParseResultJson(outcome.ResultJson));
        if (outcome.IsError)
        {
            resultContent.AdditionalProperties = new() { [AgentUiParts.ToolErrorStampKey] = true };
        }

        var message = new ChatMessage(ChatRole.Tool, [resultContent])
        {
            AdditionalProperties = new() { [AgentUiParts.TurnStampKey] = turnId },
        };
        store.AppendMessages(conversationId, [message]);

        var part = new ToolOutputAvailablePart
        {
            ConversationId = conversationId,
            TurnId = turnId,
            ToolCallId = call.CallId,
            ResultJson = outcome.ResultJson,
            IsError = outcome.IsError,
        };
        return (message, part);
    }

    /// <summary>
    /// JSON results ride as <see cref="JsonElement"/> so the provider
    /// adapters serialize them as JSON, not as a double-encoded string —
    /// <c>GetRawText()</c> round-trips the exact original bytes, so this is
    /// not a parse→re-serialize fidelity loss. Non-JSON tool text stays a
    /// plain string.
    /// </summary>
    private static object ParseResultJson(string resultJson)
    {
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return resultJson;
        }
    }

    private List<ChatMessage> BuildRequestMessages(string conversationId, string provider)
    {
        // Byte-stable prefix first; dynamic content only ever APPENDS.
        List<ChatMessage> messages = [AgentPrompts.CreateSystemMessage()];
        foreach (var message in store.GetMessages(conversationId))
        {
            messages.Add(StripForeignReasoning(message, provider));
        }

        return messages;
    }

    /// <summary>
    /// Provider-switch reasoning strip: reasoning content is provider-private
    /// (thinking signatures, encrypted reasoning in <c>ProtectedData</c>) —
    /// replaying one provider's blobs to another corrupts the exchange. A
    /// provider switch closes the reasoning-replay window: assistant messages
    /// stamped with a DIFFERENT provider lose their reasoning content on the
    /// way into the request (the store keeps the original untouched).
    /// </summary>
    private static ChatMessage StripForeignReasoning(ChatMessage message, string provider)
    {
        if (message.Role != ChatRole.Assistant
            || !message.Contents.Any(content => content is TextReasoningContent))
        {
            return message;
        }

        var stamp = message.AdditionalProperties?.TryGetValue(AgentUiParts.ProviderStampKey, out var value) == true
            ? value as string
            : null;
        if (string.Equals(stamp, provider, StringComparison.OrdinalIgnoreCase))
        {
            return message;
        }

        var clone = message.Clone();
        clone.Contents = [.. message.Contents.Where(content => content is not TextReasoningContent)];
        return clone;
    }

    private static void StampAssistantMessages(IList<ChatMessage> messages, string provider, Guid turnId)
    {
        foreach (var message in messages)
        {
            var properties = message.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            properties[AgentUiParts.ProviderStampKey] = provider;
            properties[AgentUiParts.TurnStampKey] = turnId;
        }
    }

    private static McpToolCallOutcome Envelope(string code, string detail) =>
        new(JsonSerializer.Serialize(new McpToolError(code, Detail: detail), _envelopeJson), IsError: true);

    private sealed record ApprovalAction(PendingApproval Pending, bool Approved, string? Reason);

    /// <summary>
    /// Enforces the single-enumeration contract of a turn stream (see
    /// <see cref="AgentTurnStart"/>): the conversation's turn lock is taken
    /// when the stream is CREATED and released by the iterator's
    /// <c>finally</c>, so a second enumeration would re-run the whole turn
    /// without the lock — and its <c>finally</c> would release a lock owned
    /// by someone else. One guard instance is shared by every enumerator the
    /// returned <c>IAsyncEnumerable</c> produces.
    /// </summary>
    private sealed class SingleEnumerationGuard
    {
        private int _enumerated;

        public void OnEnumerationStarted()
        {
            if (Interlocked.Exchange(ref _enumerated, 1) != 0)
            {
                throw new InvalidOperationException(
                    "An agent turn stream can be enumerated only once: it owns the conversation's "
                    + "turn lock, and re-running it would execute provider/tool calls outside that lock.");
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Agent provider call failed (conversation {ConversationId}, turn {Turn}); turn abandoned")]
    private static partial void LogProviderCallFailed(
        ILogger logger, Exception exception, string conversationId, int turn);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Agent turn cancelled by caller (conversation {ConversationId}, turn {Turn}); usage was still ledgered")]
    private static partial void LogTurnCancelled(ILogger logger, string conversationId, int turn);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Agent tool dispatch failed for '{ToolName}'; returned internal_error envelope to the model")]
    private static partial void LogToolDispatchFailed(ILogger logger, Exception exception, string toolName);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Agent error spiral terminated (conversation {ConversationId}) after {ConsecutiveErrors} consecutive tool failures")]
    private static partial void LogErrorSpiralTerminated(
        ILogger logger, string conversationId, int consecutiveErrors);
}
