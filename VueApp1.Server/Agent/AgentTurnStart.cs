namespace VueApp1.Server.Agent;

public enum AgentToolPosture
{
    /// <summary>A human is on the other end: approval-tier calls freeze and wait.</summary>
    Interactive,

    /// <summary>
    /// Nobody can click Approve: only read-tier tools are advertised, and an
    /// approval-tier call gets the <c>not_authorized</c> envelope instead of
    /// parking the run.
    /// </summary>
    Unattended,
}

public enum AgentTurnStartStatus
{
    Started,
    TurnInProgress,
    BudgetExceeded,
    ApprovalNotFound,
    ApprovalConflict,
}

/// <summary>
/// Outcome of trying to start a turn. Guards that must surface as HTTP
/// statuses (409/429/404) are decided HERE, before any SSE bytes go out — a
/// stream that starts cannot change its status code.
///
/// CONTRACT for a <see cref="AgentTurnStartStatus.Started"/> result: it
/// already holds the conversation's one-active-turn lock, and the lock is
/// released only by the <see cref="Stream"/> iterator's <c>finally</c>. The
/// caller MUST enumerate <see cref="Stream"/> exactly once (to completion or
/// via early disposal). A stream that is never enumerated leaks the lock —
/// the conversation answers 409 until process restart; a second enumeration
/// throws instead of re-running the turn without the lock.
/// </summary>
public sealed record AgentTurnStart(
    AgentTurnStartStatus Status,
    IAsyncEnumerable<AgentStreamPart>? Stream = null,
    DateTimeOffset ResetAtUtc = default);

/// <summary>
/// Result of <see cref="AgentLoopService.RunDetachedTurnAsync"/>.
/// <see cref="FinishReason"/> uses the <see cref="AgentFinishReasons"/>
/// vocabulary; <see cref="AgentFinishReasons.TurnInProgress"/> is the one
/// detached-only value (the conversation was busy — nothing ran, nothing was
/// billed) and never appears on the SSE wire.
/// </summary>
public sealed record AgentDetachedTurnResult(string FinishReason, string? Text);
