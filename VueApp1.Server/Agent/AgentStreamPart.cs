using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using VueApp1.Server.Agent.Attachments;

namespace VueApp1.Server.Agent;

/// <summary>
/// The SSE wire vocabulary: a JSON-polymorphic part union adopting the
/// Vercel AI-SDK UI Message Stream part NAMES — vocabulary-aligned,
/// deliberately NOT useChat-protocol-compatible (the in-repo composable is
/// the only contracted consumer; wire-shape snapshot tests on both sides are
/// the contract, not a fast-moving JS SDK). Deliberately omitted:
/// <c>tool-input-start</c>/<c>tool-input-delta</c> — the provider adapters do
/// not reliably surface argument deltas, and a dead part type in the union is
/// comprehension tax. Every part carries <c>conversationId</c> + <c>turnId</c>
/// so late events from a dead run can never mutate current client state.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextStartPart), "text-start")]
[JsonDerivedType(typeof(TextDeltaPart), "text-delta")]
[JsonDerivedType(typeof(TextEndPart), "text-end")]
[JsonDerivedType(typeof(ReasoningStartPart), "reasoning-start")]
[JsonDerivedType(typeof(ReasoningDeltaPart), "reasoning-delta")]
[JsonDerivedType(typeof(ReasoningEndPart), "reasoning-end")]
[JsonDerivedType(typeof(ToolInputAvailablePart), "tool-input-available")]
[JsonDerivedType(typeof(ToolOutputAvailablePart), "tool-output-available")]
[JsonDerivedType(typeof(ToolApprovalRequiredPart), "tool-approval-required")]
[JsonDerivedType(typeof(FilePart), "file")]
[JsonDerivedType(typeof(UsagePart), "usage")]
[JsonDerivedType(typeof(ErrorPart), "error")]
[JsonDerivedType(typeof(FinishPart), "finish")]
public abstract record AgentStreamPart
{
    public required string ConversationId { get; init; }

    public required Guid TurnId { get; init; }
}

public sealed record TextStartPart : AgentStreamPart;

public sealed record TextDeltaPart : AgentStreamPart
{
    public required string Delta { get; init; }
}

public sealed record TextEndPart : AgentStreamPart;

public sealed record ReasoningStartPart : AgentStreamPart;

public sealed record ReasoningDeltaPart : AgentStreamPart
{
    public required string Delta { get; init; }
}

public sealed record ReasoningEndPart : AgentStreamPart;

public sealed record ToolInputAvailablePart : AgentStreamPart
{
    public required string ToolCallId { get; init; }
    public required string ToolName { get; init; }
    public required string ArgumentsJson { get; init; }
}

public sealed record ToolOutputAvailablePart : AgentStreamPart
{
    public required string ToolCallId { get; init; }
    public required string ResultJson { get; init; }
    public required bool IsError { get; init; }
}

public sealed record ToolApprovalRequiredPart : AgentStreamPart
{
    public required string ToolCallId { get; init; }
    public required string ToolName { get; init; }
    public required string ArgumentsJson { get; init; }
}

/// <summary>
/// An attachment REFERENCE on a user message (AI-SDK vocabulary name:
/// <c>file</c>). Replay-only today: the live stream never emits it because
/// the client renders its own uploads locally; the GET snapshot derives it
/// from the refs stamped on the stored message — never from bytes.
/// </summary>
public sealed record FilePart : AgentStreamPart
{
    public required string AttachmentId { get; init; }
    public required string FileName { get; init; }
    public required string MediaType { get; init; }
}

public sealed record UsagePart : AgentStreamPart
{
    public required long InputTokens { get; init; }
    public required long CachedInputTokens { get; init; }
    public required long OutputTokens { get; init; }
    public required long ReasoningTokens { get; init; }
    public required decimal EstimatedUsd { get; init; }
}

/// <summary>
/// Stream-level error. The nested <see cref="Problem"/> object is
/// RFC 9457-shaped (<c>type</c>/<c>title</c>/<c>status</c>/<c>detail</c>) —
/// nested rather than flattened because the part union already claims the
/// top-level <c>type</c> key as its discriminator.
/// </summary>
public sealed record ErrorPart : AgentStreamPart
{
    public required AgentProblem Problem { get; init; }
}

public sealed record AgentProblem(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Type,
    string Title,
    int Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detail);

public sealed record FinishPart : AgentStreamPart
{
    /// <summary>stop | max-turns | budget-exceeded | approval-required | cancelled</summary>
    public required string Reason { get; init; }
}

public static class AgentFinishReasons
{
    public const string Stop = "stop";
    public const string MaxTurns = "max-turns";
    public const string BudgetExceeded = "budget-exceeded";
    public const string ApprovalRequired = "approval-required";
    public const string Cancelled = "cancelled";

    /// <summary>
    /// Detached-only outcome of <c>RunDetachedTurnAsync</c>: the conversation
    /// already had an active turn, so nothing ran and nothing was billed.
    /// Never emitted on the SSE wire (the HTTP path surfaces the same
    /// condition as a 409 before any stream starts) — a scheduler/sweeper
    /// branches on it to retry later.
    /// </summary>
    public const string TurnInProgress = "turn-in-progress";
}

// ---------------------------------------------------------------------------
// Request/response DTOs for the agent endpoints (all ExcludeFromDescription —
// this surface never enters the committed OpenAPI contract).

/// <summary>
/// <paramref name="AttachmentIds"/> reference prior uploads to
/// <c>POST /api/agent/attachments</c> (upload-then-reference; the JSON turn
/// body never carries bytes). Count/existence are validated before the turn
/// starts.
/// </summary>
public sealed record AgentTurnRequest(string Message, IReadOnlyList<string>? AttachmentIds = null);

/// <summary>Response of the multipart upload endpoint.</summary>
public sealed record AgentAttachmentUploadResponse(string AttachmentId, string MediaType, string FileName);

/// <summary>
/// Mirrors the shape of MEAI's <c>ToolApprovalResponseContent</c>
/// (<c>{approved, reason}</c>) so a later migration onto the middleware's
/// approval contents is mechanical. We do not consume those types in code —
/// the hand-rolled loop owns the dispatch point.
/// </summary>
public sealed record AgentApprovalRequest(bool Approved, string? Reason = null);

public sealed record AgentConversationSnapshot(
    string ConversationId,
    IReadOnlyList<AgentMessageSnapshot> Messages,
    IReadOnlyList<ToolApprovalRequiredPart> PendingApprovals);

public sealed record AgentMessageSnapshot(string Role, IReadOnlyList<AgentStreamPart> Parts);

// ---------------------------------------------------------------------------

/// <summary>
/// The ONE transcript→parts mapper. The replay endpoint derives completed
/// parts from stored <see cref="ChatMessage"/>s through exactly the part
/// constructors the live stream emits, so a re-rendered conversation and a
/// live one are the same UI by construction — no second rendering path to
/// drift. (The live loop additionally chunks text/reasoning as deltas while
/// streaming; completed messages collapse to start → one delta → end.)
/// </summary>
public static class AgentUiParts
{
    /// <summary>Stamp key for the provider that produced an assistant message (drives the provider-switch reasoning strip).</summary>
    public const string ProviderStampKey = "agent.provider";

    /// <summary>Stamp key for the request (turn) a message belongs to.</summary>
    public const string TurnStampKey = "agent.turnId";

    /// <summary>Stamp key marking a tool-result content as the error envelope.</summary>
    public const string ToolErrorStampKey = "agent.isError";

    /// <summary>
    /// Stamp key carrying a user message's attachment REFERENCES
    /// (<see cref="AgentAttachmentRef"/> list) — app-level state in
    /// AdditionalProperties, never bytes, never provider file ids (P1).
    /// </summary>
    public const string AttachmentsStampKey = "agent.attachments";

    public static AgentMessageSnapshot ToSnapshot(ChatMessage message, string conversationId)
    {
        ArgumentNullException.ThrowIfNull(message);
        var turnId = message.AdditionalProperties?.TryGetValue(TurnStampKey, out var value) == true
            && value is Guid stamped ? stamped : Guid.Empty;
        return new AgentMessageSnapshot(
            message.Role.Value,
            [.. FromMessage(message, conversationId, turnId)]);
    }

    public static IEnumerable<AgentStreamPart> FromMessage(ChatMessage message, string conversationId, Guid turnId)
    {
        ArgumentNullException.ThrowIfNull(message);
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent { Text.Length: > 0 } text:
                    yield return new TextStartPart { ConversationId = conversationId, TurnId = turnId };
                    yield return new TextDeltaPart { ConversationId = conversationId, TurnId = turnId, Delta = text.Text };
                    yield return new TextEndPart { ConversationId = conversationId, TurnId = turnId };
                    break;

                case TextReasoningContent reasoning:
                    yield return new ReasoningStartPart { ConversationId = conversationId, TurnId = turnId };
                    if (reasoning.Text is { Length: > 0 })
                    {
                        yield return new ReasoningDeltaPart
                        {
                            ConversationId = conversationId,
                            TurnId = turnId,
                            Delta = reasoning.Text,
                        };
                    }

                    yield return new ReasoningEndPart { ConversationId = conversationId, TurnId = turnId };
                    break;

                case FunctionCallContent call:
                    yield return ToolInput(call, conversationId, turnId);
                    break;

                case FunctionResultContent result:
                    yield return new ToolOutputAvailablePart
                    {
                        ConversationId = conversationId,
                        TurnId = turnId,
                        ToolCallId = result.CallId,
                        ResultJson = result.Result switch
                        {
                            JsonElement json => json.GetRawText(),
                            string raw => raw,
                            null => "null",
                            var other => JsonSerializer.Serialize(other, AIJsonUtilities.DefaultOptions),
                        },
                        IsError = result.AdditionalProperties?.TryGetValue(ToolErrorStampKey, out var isError) == true
                            && isError is true,
                    };
                    break;

                default:
                    // UsageContent is ledger input, not UI; unknown content
                    // types are dropped rather than guessed at.
                    break;
            }
        }

        // Attachment chips derive from the REFERENCES stamped on the stored
        // message — the snapshot never touches the blob store, and a message
        // whose bytes were evicted still replays its chips.
        if (message.AdditionalProperties?.TryGetValue(AttachmentsStampKey, out var stamped) == true
            && stamped is IReadOnlyList<AgentAttachmentRef> refs)
        {
            foreach (var reference in refs)
            {
                yield return new FilePart
                {
                    ConversationId = conversationId,
                    TurnId = turnId,
                    AttachmentId = reference.AttachmentId,
                    FileName = reference.FileName,
                    MediaType = reference.MediaType,
                };
            }
        }
    }

    public static ToolInputAvailablePart ToolInput(FunctionCallContent call, string conversationId, Guid turnId)
    {
        ArgumentNullException.ThrowIfNull(call);
        return new ToolInputAvailablePart
        {
            ConversationId = conversationId,
            TurnId = turnId,
            ToolCallId = call.CallId,
            ToolName = call.Name,
            ArgumentsJson = SerializeArguments(call),
        };
    }

    public static ToolApprovalRequiredPart ApprovalRequired(PendingApproval approval, string conversationId)
    {
        ArgumentNullException.ThrowIfNull(approval);
        return new ToolApprovalRequiredPart
        {
            ConversationId = conversationId,
            TurnId = approval.TurnId,
            ToolCallId = approval.ToolCallId,
            ToolName = approval.ToolName,
            ArgumentsJson = approval.ArgumentsJson,
        };
    }

    public static string SerializeArguments(FunctionCallContent call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return call.Arguments is null
            ? "{}"
            : JsonSerializer.Serialize(call.Arguments, AIJsonUtilities.DefaultOptions);
    }
}

/// <summary>
/// Source-generated serialization for everything the agent surface writes:
/// deterministic camelCase bytes for the SSE stream, the replay snapshot and
/// the wire-shape snapshot tests, independent of whatever the host's
/// reflection-based JSON options evolve into.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AgentStreamPart))]
[JsonSerializable(typeof(AgentTurnRequest))]
[JsonSerializable(typeof(AgentApprovalRequest))]
[JsonSerializable(typeof(AgentConversationSnapshot))]
[JsonSerializable(typeof(AgentUsageSummary))]
[JsonSerializable(typeof(AgentAttachmentUploadResponse))]
public sealed partial class AgentJsonContext : JsonSerializerContext;
