using System.Collections.Concurrent;
using Microsoft.Extensions.AI;

namespace VueApp1.Server.Agent;

/// <summary>
/// A tool call frozen at the approval gate. Keeps the ORIGINAL
/// <see cref="FunctionCallContent"/> (raw parsed arguments — what the model
/// actually asked for) plus the policy-surface hash it was frozen under;
/// approval executes the frozen arguments or nothing.
/// </summary>
public sealed record PendingApproval(
    string ToolCallId,
    string ToolName,
    FunctionCallContent Call,
    string ArgumentsJson,
    string PolicySurfaceHash,
    Guid TurnId);

/// <summary>
/// The transcript seam. The transcript IS MEAI's block model — a
/// <see cref="ChatMessage"/> list whose <c>Contents</c> interleave text,
/// reasoning, tool calls and tool results. No bespoke block hierarchy, no
/// intermediate event layer: both re-introduce the mapping-bug class the
/// "use the entity directly" rule exists to kill.
///
/// DURABLE UPGRADE (docs/AGENT.md): one row per message, parts serialized
/// with <c>AIJsonUtilities</c>. The fidelity boundary to know:
/// <see cref="TextReasoningContent.ProtectedData"/> — the opaque
/// provider-roundtrip slot carrying thinking signatures / encrypted
/// reasoning — SERIALIZES and round-trips fine; what does NOT serialize is
/// <c>RawRepresentation</c> (adapter-internal extras). A durable store
/// therefore keeps reasoning replay intact; only RawRepresentation-level
/// extras are lossy.
/// </summary>
public interface IAgentConversationStore
{
    bool Exists(string conversationId);

    IReadOnlyList<ChatMessage> GetMessages(string conversationId);

    void AppendMessages(string conversationId, IReadOnlyList<ChatMessage> messages);

    /// <summary>One active turn per conversation; false = a turn is already streaming (409).</summary>
    bool TryBeginTurn(string conversationId);

    void EndTurn(string conversationId);

    void AddPendingApproval(string conversationId, PendingApproval approval);

    PendingApproval? GetPendingApproval(string conversationId, string toolCallId);

    bool RemovePendingApproval(string conversationId, string toolCallId);

    IReadOnlyList<PendingApproval> GetPendingApprovals(string conversationId);
}

/// <summary>
/// In-memory default — same honest banner as Idempotency/BackgroundWork:
/// transcripts, pending approvals and the turn lock are process-local and
/// lost on restart, and the lock is single-node. Production: implement
/// <see cref="IAgentConversationStore"/> over a database (the interface is
/// shaped so the swap changes this file's registration, not call sites) and
/// replace the bool lock with a lease (TTL + renewal). Live object graphs
/// are stored as-is — no serialization happens here at all.
/// </summary>
public sealed class InMemoryAgentConversationStore : IAgentConversationStore
{
    private readonly ConcurrentDictionary<string, Conversation> _conversations = new(StringComparer.Ordinal);

    public bool Exists(string conversationId) => _conversations.ContainsKey(conversationId);

    public IReadOnlyList<ChatMessage> GetMessages(string conversationId)
    {
        if (!_conversations.TryGetValue(conversationId, out var conversation))
        {
            return [];
        }

        lock (conversation.Gate)
        {
            return [.. conversation.Messages];
        }
    }

    public void AppendMessages(string conversationId, IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var conversation = GetOrCreate(conversationId);
        lock (conversation.Gate)
        {
            conversation.Messages.AddRange(messages);
        }
    }

    public bool TryBeginTurn(string conversationId)
    {
        var conversation = GetOrCreate(conversationId);
        lock (conversation.Gate)
        {
            if (conversation.TurnActive)
            {
                return false;
            }

            conversation.TurnActive = true;
            return true;
        }
    }

    public void EndTurn(string conversationId)
    {
        if (_conversations.TryGetValue(conversationId, out var conversation))
        {
            lock (conversation.Gate)
            {
                conversation.TurnActive = false;
            }
        }
    }

    public void AddPendingApproval(string conversationId, PendingApproval approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        var conversation = GetOrCreate(conversationId);
        lock (conversation.Gate)
        {
            conversation.PendingApprovals[approval.ToolCallId] = approval;
        }
    }

    public PendingApproval? GetPendingApproval(string conversationId, string toolCallId)
    {
        if (!_conversations.TryGetValue(conversationId, out var conversation))
        {
            return null;
        }

        lock (conversation.Gate)
        {
            return conversation.PendingApprovals.GetValueOrDefault(toolCallId);
        }
    }

    public bool RemovePendingApproval(string conversationId, string toolCallId)
    {
        if (!_conversations.TryGetValue(conversationId, out var conversation))
        {
            return false;
        }

        lock (conversation.Gate)
        {
            return conversation.PendingApprovals.Remove(toolCallId);
        }
    }

    public IReadOnlyList<PendingApproval> GetPendingApprovals(string conversationId)
    {
        if (!_conversations.TryGetValue(conversationId, out var conversation))
        {
            return [];
        }

        lock (conversation.Gate)
        {
            return [.. conversation.PendingApprovals.Values];
        }
    }

    private Conversation GetOrCreate(string conversationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        return _conversations.GetOrAdd(conversationId, static _ => new Conversation());
    }

    private sealed class Conversation
    {
        public object Gate { get; } = new();
        public List<ChatMessage> Messages { get; } = [];
        public Dictionary<string, PendingApproval> PendingApprovals { get; } = new(StringComparer.Ordinal);
        public bool TurnActive { get; set; }
    }
}
