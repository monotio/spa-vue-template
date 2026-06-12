/**
 * Hand-written wire contract for the agent SSE surface.
 *
 * SYNC RULE — these types are NOT in `api.gen.ts`: every `/api/agent/*`
 * endpoint is `ExcludeFromDescription` (a flag-gated surface must never
 * drift the committed OpenAPI contract), so `npm run openapi:sync` will
 * never produce them. The source of truth is the server union in
 * `VueApp1.Server/Agent/AgentStreamPart.cs`; the contract is locked by
 * wire-shape snapshot tests on BOTH sides — `AgentStreamPartTests.cs` on
 * the server, `src/contracts/__tests__/agent.spec.ts` here. When you change
 * the server vocabulary, change this file and both snapshot suites in the
 * same commit.
 */

/** Identity carried by EVERY part: late events from a dead run are attributable and droppable. */
interface AgentStreamPartBase {
  readonly conversationId: string;
  /** Server-side GUID, serialized as a string. */
  readonly turnId: string;
}

export interface AgentTextStartPart extends AgentStreamPartBase {
  readonly type: 'text-start';
}

export interface AgentTextDeltaPart extends AgentStreamPartBase {
  readonly type: 'text-delta';
  readonly delta: string;
}

export interface AgentTextEndPart extends AgentStreamPartBase {
  readonly type: 'text-end';
}

export interface AgentReasoningStartPart extends AgentStreamPartBase {
  readonly type: 'reasoning-start';
}

export interface AgentReasoningDeltaPart extends AgentStreamPartBase {
  readonly type: 'reasoning-delta';
  readonly delta: string;
}

export interface AgentReasoningEndPart extends AgentStreamPartBase {
  readonly type: 'reasoning-end';
}

export interface AgentToolInputAvailablePart extends AgentStreamPartBase {
  readonly type: 'tool-input-available';
  readonly toolCallId: string;
  readonly toolName: string;
  /** Raw JSON string — never parse→re-serialize tool arguments. */
  readonly argumentsJson: string;
}

export interface AgentToolOutputAvailablePart extends AgentStreamPartBase {
  readonly type: 'tool-output-available';
  readonly toolCallId: string;
  /** Raw JSON string — render as-is; fidelity over prettiness. */
  readonly resultJson: string;
  readonly isError: boolean;
}

export interface AgentToolApprovalRequiredPart extends AgentStreamPartBase {
  readonly type: 'tool-approval-required';
  readonly toolCallId: string;
  readonly toolName: string;
  readonly argumentsJson: string;
}

/**
 * An attachment REFERENCE on a user message (AI-SDK vocabulary name: `file`).
 * Replay-only: the live stream never emits it — the composer renders its own
 * uploads locally; the GET snapshot derives these from the references stored
 * on the message (never from bytes).
 */
export interface AgentFilePart extends AgentStreamPartBase {
  readonly type: 'file';
  readonly attachmentId: string;
  readonly fileName: string;
  readonly mediaType: string;
}

export interface AgentUsagePart extends AgentStreamPartBase {
  readonly type: 'usage';
  readonly inputTokens: number;
  readonly cachedInputTokens: number;
  readonly outputTokens: number;
  readonly reasoningTokens: number;
  readonly estimatedUsd: number;
}

/**
 * RFC 9457-shaped, nested under `problem` because the part union already
 * claims the top-level `type` key as its discriminator.
 */
export interface AgentProblem {
  readonly type?: string | undefined;
  readonly title: string;
  readonly status: number;
  readonly detail?: string | undefined;
}

export interface AgentErrorPart extends AgentStreamPartBase {
  readonly type: 'error';
  readonly problem: AgentProblem;
}

export const AGENT_FINISH_REASONS = [
  'stop',
  'max-turns',
  'budget-exceeded',
  'approval-required',
  'cancelled',
] as const;

export type AgentFinishReason = (typeof AGENT_FINISH_REASONS)[number];

export interface AgentFinishPart extends AgentStreamPartBase {
  readonly type: 'finish';
  readonly reason: AgentFinishReason;
}

export type AgentStreamPart =
  | AgentTextStartPart
  | AgentTextDeltaPart
  | AgentTextEndPart
  | AgentReasoningStartPart
  | AgentReasoningDeltaPart
  | AgentReasoningEndPart
  | AgentToolInputAvailablePart
  | AgentToolOutputAvailablePart
  | AgentToolApprovalRequiredPart
  | AgentFilePart
  | AgentUsagePart
  | AgentErrorPart
  | AgentFinishPart;

/** The full server vocabulary — `tool-input-start`/`tool-input-delta` deliberately do not exist. */
export const AGENT_STREAM_PART_TYPES = [
  'text-start',
  'text-delta',
  'text-end',
  'reasoning-start',
  'reasoning-delta',
  'reasoning-end',
  'tool-input-available',
  'tool-output-available',
  'tool-approval-required',
  'file',
  'usage',
  'error',
  'finish',
] as const;

export type AgentStreamPartType = (typeof AGENT_STREAM_PART_TYPES)[number];

// ---------------------------------------------------------------------------
// Request/response DTOs (mirroring AgentStreamPart.cs).

export interface AgentTurnRequest {
  readonly message: string;
  /** Ids from prior `POST /api/agent/attachments` uploads (upload-then-reference). */
  readonly attachmentIds?: readonly string[] | undefined;
}

/** Response of the multipart upload endpoint. */
export interface AgentAttachmentUploadResponse {
  readonly attachmentId: string;
  readonly mediaType: string;
  readonly fileName: string;
}

/**
 * Client-side mirror of the SERVER DEFAULTS in `Agent:Attachments`
 * (appsettings.json). Mirrored for instant composer feedback; the server
 * remains the authority — its typed ProblemDetails (413/415/400) surface
 * through the normal error path when a deployment tightens the limits.
 */
export const AGENT_ATTACHMENT_LIMITS = {
  maxBytes: 5_242_880,
  maxPerMessage: 4,
  allowedContentTypes: [
    'image/png',
    'image/jpeg',
    'image/webp',
    'image/gif',
    'application/pdf',
    'text/plain',
    'text/markdown',
  ],
} as const;

/** Mirrors the shape of MEAI's `ToolApprovalResponseContent` (`{approved, reason}`). */
export interface AgentApprovalRequest {
  readonly approved: boolean;
  readonly reason?: string | undefined;
}

export interface AgentMessageSnapshot {
  readonly role: string;
  readonly parts: readonly AgentStreamPart[];
}

export interface AgentConversationSnapshot {
  readonly conversationId: string;
  readonly messages: readonly AgentMessageSnapshot[];
  readonly pendingApprovals: readonly AgentToolApprovalRequiredPart[];
}

// ---------------------------------------------------------------------------
// ProblemDetails types the agent endpoints emit (ProblemDetailTypes.cs).

export const AGENT_PROBLEM_TYPES = {
  turnInProgress: '/problems/agent-turn-in-progress',
  budgetExceeded: '/problems/agent-budget-exceeded',
  approvalConflict: '/problems/agent-approval-conflict',
  approvalNotFound: '/problems/agent-approval-not-found',
  conversationNotFound: '/problems/agent-conversation-not-found',
  attachmentTooLarge: '/problems/agent-attachment-too-large',
  attachmentTypeNotAllowed: '/problems/agent-attachment-type-not-allowed',
} as const;

/**
 * True when a ProblemDetails `type` belongs to the agent module itself.
 * The disabled-module detection hinges on this: with `Agent:Enabled=false`
 * the endpoints are never mapped, so a turn POST falls through to the
 * generic `/api` 404 ProblemDetails (no `/problems/agent-*` type) — whereas
 * any agent-typed problem proves the module is alive.
 */
export function isAgentProblemType(type: string | undefined): boolean {
  return typeof type === 'string' && type.startsWith('/problems/agent-');
}

// ---------------------------------------------------------------------------
// Runtime guards: the types above are compile-time promises about the wire;
// a version-skewed server breaks them at runtime. Guards turn that breakage
// into a loud (or deliberately tolerant) signal next to its cause.

const FINISH_REASON_SET: ReadonlySet<string> = new Set(AGENT_FINISH_REASONS);

function isIdentity(value: Record<string, unknown>): boolean {
  return typeof value['conversationId'] === 'string' && typeof value['turnId'] === 'string';
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/**
 * Validates one parsed SSE payload against the union. Unknown `type` values
 * return false — the stream consumer DROPS them (forward-tolerant: a newer
 * server vocabulary degrades to missing parts, never to corrupted state).
 */
export function isAgentStreamPart(value: unknown): value is AgentStreamPart {
  if (!isObject(value) || !isIdentity(value)) {
    return false;
  }

  switch (value['type']) {
    case 'text-start':
    case 'text-end':
    case 'reasoning-start':
    case 'reasoning-end':
      return true;
    case 'text-delta':
    case 'reasoning-delta':
      return typeof value['delta'] === 'string';
    case 'tool-input-available':
    case 'tool-approval-required':
      return (
        typeof value['toolCallId'] === 'string' &&
        typeof value['toolName'] === 'string' &&
        typeof value['argumentsJson'] === 'string'
      );
    case 'tool-output-available':
      return (
        typeof value['toolCallId'] === 'string' &&
        typeof value['resultJson'] === 'string' &&
        typeof value['isError'] === 'boolean'
      );
    case 'file':
      return (
        typeof value['attachmentId'] === 'string' &&
        typeof value['fileName'] === 'string' &&
        typeof value['mediaType'] === 'string'
      );
    case 'usage':
      return (
        typeof value['inputTokens'] === 'number' &&
        typeof value['cachedInputTokens'] === 'number' &&
        typeof value['outputTokens'] === 'number' &&
        typeof value['reasoningTokens'] === 'number' &&
        typeof value['estimatedUsd'] === 'number'
      );
    case 'error': {
      const problem = value['problem'];
      return isObject(problem) && typeof problem['title'] === 'string';
    }
    case 'finish':
      // The reason set is validated, not just typeof-checked: `finish` is
      // the one part whose VALUE flows onward under a closed union type
      // (`lastFinishReason`), and consumers exhaustively switch on it. An
      // unknown reason drops the part — the same forward tolerance as an
      // unknown part type, instead of an out-of-union string in disguise.
      return typeof value['reason'] === 'string' && FINISH_REASON_SET.has(value['reason']);
    default:
      return false;
  }
}

/** Loud assertion for the upload response — a malformed shape is a contract mismatch. */
export function assertAgentAttachmentUploadResponse(
  value: unknown,
): asserts value is AgentAttachmentUploadResponse {
  if (
    !isObject(value) ||
    typeof value['attachmentId'] !== 'string' ||
    typeof value['mediaType'] !== 'string' ||
    typeof value['fileName'] !== 'string'
  ) {
    throw new Error('API contract mismatch: expected an agent attachment upload response.');
  }
}

/**
 * Shell assertion for the replay snapshot. Individual parts inside messages
 * are FILTERED through {@link isAgentStreamPart} by the consumer (same
 * forward-tolerance as the live stream) — but a response that is not even
 * snapshot-shaped is a contract mismatch and must fail loudly.
 */
export function assertAgentConversationSnapshot(
  value: unknown,
): asserts value is AgentConversationSnapshot {
  if (
    !isObject(value) ||
    typeof value['conversationId'] !== 'string' ||
    !Array.isArray(value['messages']) ||
    !value['messages'].every(
      (message: unknown) =>
        isObject(message) && typeof message['role'] === 'string' && Array.isArray(message['parts']),
    ) ||
    !Array.isArray(value['pendingApprovals'])
  ) {
    throw new Error('API contract mismatch: expected an agent conversation snapshot.');
  }
}
