namespace VueApp1.Server;

/// <summary>
/// Stable URI identifiers for the <c>type</c> member of RFC 9457 problem
/// responses. Clients branch on these instead of parsing human-readable
/// detail strings (which can change or be localized). The URIs are
/// identifiers, not links — they don't have to resolve, though pointing them
/// at real help pages is a nice upgrade later.
/// </summary>
public static class ProblemDetailTypes
{
    /// <summary>The request was well-formed but failed a domain rule.</summary>
    public const string ValidationFailed = "/problems/validation-failed";

    /// <summary>The resource exists but the operation conflicts with its current state.</summary>
    public const string ConflictingState = "/problems/conflicting-state";

    /// <summary>An Idempotency-Key was reused with a different request payload (422).</summary>
    public const string IdempotencyPayloadMismatch = "/problems/idempotency-payload-mismatch";

    /// <summary>The original request carrying this Idempotency-Key is still in flight (409).</summary>
    public const string IdempotencyInProgress = "/problems/idempotency-in-progress";

    /// <summary>Another agent turn is already streaming for this conversation (409).</summary>
    public const string AgentTurnInProgress = "/problems/agent-turn-in-progress";

    /// <summary>
    /// The soft daily agent budget is exhausted (429). Carries a
    /// <c>resetAtUtc</c> extension (next UTC midnight) instead of
    /// <c>Retry-After</c>: budget exhaustion is a quota, not a rate limit —
    /// retrying sooner can never succeed.
    /// </summary>
    public const string AgentBudgetExceeded = "/problems/agent-budget-exceeded";

    /// <summary>
    /// The tool-policy surface changed between approval freeze and execution;
    /// the frozen call must never run under a surface the user did not see (409).
    /// </summary>
    public const string AgentApprovalConflict = "/problems/agent-approval-conflict";

    /// <summary>No pending approval matches this tool call id (404).</summary>
    public const string AgentApprovalNotFound = "/problems/agent-approval-not-found";
}
