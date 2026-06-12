import { computed, ref, onScopeDispose } from 'vue';
import {
  AGENT_PROBLEM_TYPES,
  isAgentProblemType,
  isAgentStreamPart,
  type AgentApprovalRequest,
  type AgentFinishReason,
  type AgentStreamPart,
  type AgentTurnRequest,
} from '@/contracts/agent';
import { useAgentApi } from '@/services/agent';
import { ProblemError, StatusCodeError, createResponseError, isOfflineError } from '@/utils/errors';
import { logger } from '@/utils/logger';

/**
 * POST-initiated SSE for the agent surface — the gap docs/REALTIME.md names:
 * `EventSource` cannot POST a body and `useFetch` buffers one JSON body, so
 * this composable owns the template's ONE sanctioned `fetch`+`ReadableStream`
 * consumer (see the narrow allowlist entry in eslint.config.ts). Components
 * never touch the transport: they render the reduced message model and call
 * `sendMessage`/`approve`/`reject`/`abort`.
 *
 * Server contract: `VueApp1.Server/Agent/AgentStreamPart.cs` (mirrored in
 * `src/contracts/agent.ts`). Abort propagation is the cost story, not just
 * cleanup: AbortController → fetch abort → `HttpContext.RequestAborted` →
 * the provider call is cancelled and the ledger still bills tokens-to-date
 * (pinned server-side by the abort-mid-stream integration test).
 */

// ---------------------------------------------------------------------------
// SSE parsing — a small, spec-faithful line parser hardened against the
// chunk-boundary failure modes that silently corrupt naive split('\n\n')
// parsers: multi-byte UTF-8 split mid-character (TextDecoder in streaming
// mode), CRLF split across reads, frames split across reads, and the
// incomplete-event-at-EOF rule. Locked by adversarial fixtures in
// __tests__/useAgentStream.spec.ts.

/** Yields the `data` payload of each complete SSE event in the stream. */
export async function* parseSseStream(
  stream: ReadableStream<Uint8Array>,
): AsyncGenerator<string, void, undefined> {
  const reader = stream.getReader();
  // stream:true carries partial code points across read() boundaries — the
  // decoder, not the parser, owns UTF-8 reassembly.
  const decoder = new TextDecoder();
  let buffer = '';
  let skipLeadingLf = false;
  let dataLines: string[] = [];
  const ready: string[] = [];

  function processLine(line: string): void {
    if (line === '') {
      // Blank line dispatches the accumulated event (if any).
      if (dataLines.length > 0) {
        ready.push(dataLines.join('\n'));
        dataLines = [];
      }
      return;
    }
    if (line.startsWith(':')) {
      return; // comment (keep-alive)
    }
    const colon = line.indexOf(':');
    const field = colon === -1 ? line : line.slice(0, colon);
    if (field !== 'data') {
      return; // event:/id:/retry: — parts are self-describing JSON
    }
    let value = colon === -1 ? '' : line.slice(colon + 1);
    if (value.startsWith(' ')) {
      value = value.slice(1);
    }
    dataLines.push(value);
  }

  function drainCompleteLines(): void {
    let index = 0;
    while (index < buffer.length) {
      const cr = buffer.indexOf('\r', index);
      const lf = buffer.indexOf('\n', index);
      const at = cr === -1 ? lf : lf === -1 ? cr : Math.min(cr, lf);
      if (at === -1) {
        break; // incomplete line — wait for more bytes
      }
      processLine(buffer.slice(index, at));
      if (buffer[at] === '\r') {
        if (at + 1 < buffer.length) {
          index = buffer[at + 1] === '\n' ? at + 2 : at + 1;
        } else {
          // CR is the last byte read so far: the matching LF may arrive in
          // the next chunk and must NOT count as a second terminator.
          index = at + 1;
          skipLeadingLf = true;
        }
      } else {
        index = at + 1;
      }
    }
    buffer = buffer.slice(index);
  }

  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) {
        break;
      }
      let text = decoder.decode(value, { stream: true });
      if (skipLeadingLf && text.length > 0) {
        skipLeadingLf = false;
        if (text.startsWith('\n')) {
          text = text.slice(1);
        }
      }
      buffer += text;
      drainCompleteLines();
      while (ready.length > 0) {
        yield ready.shift()!;
      }
    }
    // Flush the decoder, then drain. Anything left after that — an
    // unterminated line or an event missing its blank line — is DISCARDED:
    // the SSE spec never dispatches an incomplete event at EOF, and half a
    // JSON payload must never reach JSON.parse.
    buffer += decoder.decode();
    drainCompleteLines();
    while (ready.length > 0) {
      yield ready.shift()!;
    }
  } finally {
    try {
      await reader.cancel();
    } catch {
      // Already errored (abort) or closed — releasing is all that matters.
    }
  }
}

// ---------------------------------------------------------------------------
// The part reducer: AgentStreamParts → a renderable message model. The SAME
// reducer serves the live stream and the GET replay snapshot, so a replayed
// conversation and a live one are one renderer by construction.

export type AgentStreamStatus = 'idle' | 'streaming' | 'awaiting-approval' | 'error';

export type AgentToolStatus = 'running' | 'awaiting-approval' | 'ok' | 'error' | 'rejected';

export interface AgentTextItem {
  kind: 'text';
  text: string;
  streaming: boolean;
}

export interface AgentReasoningItem {
  kind: 'reasoning';
  text: string;
  streaming: boolean;
}

export interface AgentToolItem {
  kind: 'tool';
  toolCallId: string;
  toolName: string;
  /** Raw JSON strings from the wire — rendered as-is, never re-serialized. */
  argumentsJson: string;
  status: AgentToolStatus;
  resultJson?: string | undefined;
}

export type AgentChatItem = AgentTextItem | AgentReasoningItem | AgentToolItem;

export type AgentChatRole = 'user' | 'assistant';

export interface AgentChatMessage {
  role: AgentChatRole;
  items: AgentChatItem[];
}

export interface AgentPendingApproval {
  toolCallId: string;
  toolName: string;
  argumentsJson: string;
}

export interface AgentUsageTotals {
  inputTokens: number;
  cachedInputTokens: number;
  outputTokens: number;
  reasoningTokens: number;
  estimatedUsd: number;
}

/**
 * Status-rank idempotence: stream-driven updates may only move a
 * tool card FORWARD — duplicated or re-ordered parts (network retries,
 * replay-after-live) can never regress a completed card to "running" or
 * flip a human "rejected" verdict into a generic error.
 */
const TOOL_STATUS_RANK: Record<AgentToolStatus, number> = {
  running: 0,
  'awaiting-approval': 1,
  ok: 2,
  error: 2,
  rejected: 2,
};

function findToolItem(messages: AgentChatMessage[], toolCallId: string): AgentToolItem | undefined {
  for (const message of messages) {
    for (const item of message.items) {
      if (item.kind === 'tool' && item.toolCallId === toolCallId) {
        return item;
      }
    }
  }
  return undefined;
}

function ensureBucket(messages: AgentChatMessage[], role: AgentChatRole): AgentChatMessage {
  const last = messages[messages.length - 1];
  if (last !== undefined && last.role === role) {
    return last;
  }
  const bucket: AgentChatMessage = { role, items: [] };
  messages.push(bucket);
  return bucket;
}

function appendDelta(
  messages: AgentChatMessage[],
  role: AgentChatRole,
  kind: 'text' | 'reasoning',
  delta: string,
): void {
  const bucket = ensureBucket(messages, role);
  for (let i = bucket.items.length - 1; i >= 0; i--) {
    const item = bucket.items[i];
    if (item.kind === kind && item.streaming) {
      item.text += delta;
      return;
    }
  }
  // Delta without a start part — tolerate (create the item) rather than drop content.
  bucket.items.push({ kind, text: delta, streaming: true });
}

function closeStreamingItem(messages: AgentChatMessage[], kind: 'text' | 'reasoning'): void {
  for (let m = messages.length - 1; m >= 0; m--) {
    const items = messages[m].items;
    for (let i = items.length - 1; i >= 0; i--) {
      const item = items[i];
      if (item.kind === kind && item.streaming) {
        item.streaming = false;
        return;
      }
    }
  }
}

function upgradeToolStatus(item: AgentToolItem, next: AgentToolStatus): void {
  if (TOOL_STATUS_RANK[next] > TOOL_STATUS_RANK[item.status]) {
    item.status = next;
  }
}

/**
 * Reduces one wire part into the message model, mutating `messages` in
 * place (callers hold it in a deep ref). `role` only matters when a part
 * must open a new bucket — replay passes the snapshot message's role, the
 * live stream always reduces into an assistant bucket.
 */
export function applyAgentPart(
  messages: AgentChatMessage[],
  part: AgentStreamPart,
  role: AgentChatRole = 'assistant',
): void {
  switch (part.type) {
    case 'text-start':
      ensureBucket(messages, role).items.push({ kind: 'text', text: '', streaming: true });
      break;
    case 'text-delta':
      appendDelta(messages, role, 'text', part.delta);
      break;
    case 'text-end':
      closeStreamingItem(messages, 'text');
      break;
    case 'reasoning-start':
      ensureBucket(messages, role).items.push({ kind: 'reasoning', text: '', streaming: true });
      break;
    case 'reasoning-delta':
      appendDelta(messages, role, 'reasoning', part.delta);
      break;
    case 'reasoning-end':
      closeStreamingItem(messages, 'reasoning');
      break;
    case 'tool-input-available': {
      const existing = findToolItem(messages, part.toolCallId);
      if (existing !== undefined) {
        // Out-of-order tolerance: a card created by an early output part
        // gains its name/args here; a duplicate input never resets status.
        if (existing.toolName === '') {
          existing.toolName = part.toolName;
        }
        if (existing.argumentsJson === '') {
          existing.argumentsJson = part.argumentsJson;
        }
      } else {
        ensureBucket(messages, role).items.push({
          kind: 'tool',
          toolCallId: part.toolCallId,
          toolName: part.toolName,
          argumentsJson: part.argumentsJson,
          status: 'running',
        });
      }
      break;
    }
    case 'tool-approval-required': {
      const existing = findToolItem(messages, part.toolCallId);
      if (existing !== undefined) {
        upgradeToolStatus(existing, 'awaiting-approval');
        if (existing.toolName === '') {
          existing.toolName = part.toolName;
        }
        if (existing.argumentsJson === '') {
          existing.argumentsJson = part.argumentsJson;
        }
      } else {
        ensureBucket(messages, role).items.push({
          kind: 'tool',
          toolCallId: part.toolCallId,
          toolName: part.toolName,
          argumentsJson: part.argumentsJson,
          status: 'awaiting-approval',
        });
      }
      break;
    }
    case 'tool-output-available': {
      const existing = findToolItem(messages, part.toolCallId);
      const next: AgentToolStatus = part.isError ? 'error' : 'ok';
      if (existing !== undefined) {
        upgradeToolStatus(existing, next);
        if (existing.resultJson === undefined) {
          existing.resultJson = part.resultJson;
        }
      } else {
        ensureBucket(messages, role).items.push({
          kind: 'tool',
          toolCallId: part.toolCallId,
          toolName: '',
          argumentsJson: '',
          status: next,
          resultJson: part.resultJson,
        });
      }
      break;
    }
    case 'usage':
    case 'error':
    case 'finish':
      // Stream-level signals, not transcript items — the composable handles
      // them as status/ref side effects.
      break;
  }
}

// ---------------------------------------------------------------------------

const CONVERSATION_ID_STORAGE_KEY = 'agent:conversationId';

function readPersistedConversationId(): string | null {
  try {
    return sessionStorage.getItem(CONVERSATION_ID_STORAGE_KEY);
  } catch {
    return null; // storage blocked (privacy mode) — run without persistence
  }
}

function writePersistedConversationId(id: string | null): void {
  try {
    if (id === null) {
      sessionStorage.removeItem(CONVERSATION_ID_STORAGE_KEY);
    } else {
      sessionStorage.setItem(CONVERSATION_ID_STORAGE_KEY, id);
    }
  } catch {
    // Best effort — a non-persisted conversation still streams fine.
  }
}

function newConversationId(): string {
  // Server constraint: 1-64 chars of [A-Za-z0-9_-]; a UUID fits.
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }
  return `conv-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
}

export function useAgentStream() {
  const api = useAgentApi();

  const status = ref<AgentStreamStatus>('idle');
  const messages = ref<AgentChatMessage[]>([]);
  // Pending approvals are a QUEUE, not a single slot: one model response can
  // freeze SEVERAL approval-tier calls — the server emits
  // tool-approval-required for each, and on resume it parks again WITHOUT
  // re-emitting the remainder. The surfaced card is always the head;
  // resolving one surfaces the next.
  const pendingApprovals = ref<AgentPendingApproval[]>([]);
  const pendingApproval = computed(() => pendingApprovals.value[0]);
  const errorMessage = ref<string>();
  const agentDisabled = ref(false);
  const budgetResetAtUtc = ref<string>();
  const lastFinishReason = ref<AgentFinishReason>();
  const isRestoring = ref(false);
  const usage = ref<AgentUsageTotals>({
    inputTokens: 0,
    cachedInputTokens: 0,
    outputTokens: 0,
    reasoningTokens: 0,
    estimatedUsd: 0,
  });

  const isStreaming = computed(() => status.value === 'streaming');

  let conversationId: string | null = readPersistedConversationId();
  let activeController: AbortController | null = null;

  function ensureConversationId(): string {
    if (conversationId === null) {
      conversationId = newConversationId();
      writePersistedConversationId(conversationId);
    }
    return conversationId;
  }

  function abort(): void {
    activeController?.abort();
  }

  onScopeDispose(abort);

  function closeAllStreamingItems(): void {
    for (const message of messages.value) {
      for (const item of message.items) {
        if (item.kind !== 'tool' && item.streaming) {
          item.streaming = false;
        }
      }
    }
  }

  function enqueuePendingApproval(approval: AgentPendingApproval): void {
    if (!pendingApprovals.value.some((p) => p.toolCallId === approval.toolCallId)) {
      pendingApprovals.value.push(approval);
    }
  }

  function applyStreamSideEffects(part: AgentStreamPart): void {
    switch (part.type) {
      case 'tool-approval-required':
        enqueuePendingApproval({
          toolCallId: part.toolCallId,
          toolName: part.toolName,
          argumentsJson: part.argumentsJson,
        });
        break;
      case 'usage':
        usage.value = {
          inputTokens: usage.value.inputTokens + part.inputTokens,
          cachedInputTokens: usage.value.cachedInputTokens + part.cachedInputTokens,
          outputTokens: usage.value.outputTokens + part.outputTokens,
          reasoningTokens: usage.value.reasoningTokens + part.reasoningTokens,
          estimatedUsd: usage.value.estimatedUsd + part.estimatedUsd,
        };
        break;
      case 'error':
        errorMessage.value = part.problem.detail ?? part.problem.title;
        status.value = 'error';
        break;
      case 'finish':
        lastFinishReason.value = part.reason;
        if (part.reason === 'approval-required') {
          status.value = 'awaiting-approval';
        } else if (status.value === 'streaming') {
          status.value = 'idle';
        }
        break;
      default:
        break;
    }
  }

  async function consumeStream(
    body: ReadableStream<Uint8Array>,
    expectedConversationId: string,
    controller: AbortController,
    progress: { parts: number },
  ): Promise<void> {
    for await (const data of parseSseStream(body)) {
      if (activeController !== controller) {
        // Latest-wins enforcement at the ONLY place state is mutated: a
        // read that resolved just before a supersession still delivers its
        // chunk afterwards, and a superseded run shares the conversationId —
        // only ownership can stop its parts from mutating current state.
        return;
      }
      let parsed: unknown;
      try {
        parsed = JSON.parse(data);
      } catch {
        logger.debug('useAgentStream: dropping non-JSON SSE payload', data);
        continue;
      }
      if (!isAgentStreamPart(parsed)) {
        // Forward tolerance: a newer server vocabulary degrades to missing
        // parts, never to corrupted client state.
        logger.debug('useAgentStream: dropping unknown stream part', parsed);
        continue;
      }
      if (parsed.conversationId !== expectedConversationId) {
        continue; // late event from a dead run — never mutate current state
      }
      applyAgentPart(messages.value, parsed);
      applyStreamSideEffects(parsed);
      progress.parts += 1;
    }
  }

  function applyRequestError(error: unknown): void {
    if (error instanceof ProblemError) {
      const problem = error.problem;
      // THE disabled-module mapping: with Agent:Enabled=false the endpoints
      // are never mapped, so the POST falls through to the generic /api 404
      // ProblemDetails (no /problems/agent-* type). An agent-typed problem
      // proves the module is alive and is NEVER the disabled state.
      if (problem.status === 404 && !isAgentProblemType(problem.type)) {
        agentDisabled.value = true;
        status.value = 'idle';
        return;
      }
      if (problem.type === AGENT_PROBLEM_TYPES.budgetExceeded) {
        const reset = problem['resetAtUtc'];
        budgetResetAtUtc.value = typeof reset === 'string' ? reset : undefined;
      }
      errorMessage.value = error.message;
      status.value = 'error';
      return;
    }
    if (error instanceof StatusCodeError) {
      errorMessage.value = error.message;
      status.value = 'error';
      return;
    }
    if (isOfflineError(error)) {
      errorMessage.value = 'Offline';
      status.value = 'error';
      return;
    }
    logger.error('useAgentStream: stream failed', error);
    errorMessage.value = error instanceof Error ? error.message : 'Request failed';
    status.value = 'error';
  }

  /**
   * Shared stream runner with latest-wins ownership: starting a new stream
   * aborts the previous one, and a superseded stream may no longer touch
   * shared state — enforced where parts are applied (consumeStream) AND in
   * the error/cleanup paths below (its abort fallout belongs to a dead run).
   *
   * `failedBeforeFirstPart` is true only when the request failed before ANY
   * part was applied: the server never started the continuation, so a
   * caller's optimistic mutations can be rolled back safely.
   */
  async function runStream(
    expectedConversationId: string,
    start: (signal: AbortSignal) => Promise<Response>,
  ): Promise<{ failedBeforeFirstPart: boolean }> {
    abort();
    const controller = new AbortController();
    activeController = controller;
    const progress = { parts: 0 };
    try {
      const response = await start(controller.signal);
      if (!response.ok) {
        throw await createResponseError(response);
      }
      if (response.body === null) {
        throw new Error('Agent stream response carried no body.');
      }
      await consumeStream(response.body, expectedConversationId, controller, progress);
      if (activeController === controller && status.value === 'streaming') {
        status.value = 'idle'; // stream ended without a finish part
      }
    } catch (error) {
      if (activeController !== controller) {
        return { failedBeforeFirstPart: false }; // superseded — the newer stream owns the state
      }
      if (controller.signal.aborted || (error instanceof Error && error.name === 'AbortError')) {
        // Deliberate cancel: keep the partial output (the server billed for
        // it), settle the machine.
        lastFinishReason.value = 'cancelled';
        if (status.value === 'streaming') {
          status.value = 'idle';
        }
        return { failedBeforeFirstPart: false };
      }
      applyRequestError(error);
      return { failedBeforeFirstPart: progress.parts === 0 };
    } finally {
      if (activeController === controller) {
        activeController = null;
        closeAllStreamingItems();
      }
    }
    return { failedBeforeFirstPart: false };
  }

  async function sendMessage(text: string): Promise<void> {
    const message = text.trim();
    if (message === '' || agentDisabled.value) {
      return;
    }
    if (status.value === 'awaiting-approval' || pendingApprovals.value.length > 0) {
      // WEDGE GUARD: a frozen approval means the transcript holds a tool
      // call with no result yet. Appending a user message now produces a
      // transcript real providers reject (tool_use without tool_result →
      // every subsequent call 400s), and the approval result would land out
      // of position afterwards. Resolve or reject the pending approval
      // first — the page's composer mirrors this rule.
      return;
    }
    const id = ensureConversationId();
    messages.value.push({
      role: 'user',
      items: [{ kind: 'text', text: message, streaming: false }],
    });
    errorMessage.value = undefined;
    lastFinishReason.value = undefined;
    status.value = 'streaming';
    const request: AgentTurnRequest = { message };
    await runStream(id, (signal) =>
      fetch(`/api/agent/conversations/${id}/turns`, {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json', Accept: 'text/event-stream' },
        body: JSON.stringify(request),
        signal,
      }),
    );
  }

  async function respondToApproval(
    toolCallId: string,
    approved: boolean,
    reason?: string,
  ): Promise<void> {
    if (conversationId === null || agentDisabled.value) {
      return;
    }
    const id = conversationId;
    // Local, user-driven transition (not status-ranked: the human verdict
    // outranks stream ordering rules).
    const item = findToolItem(messages.value, toolCallId);
    const previousItemStatus = item?.status;
    if (item !== undefined) {
      item.status = approved ? 'running' : 'rejected';
    }
    const queueIndex = pendingApprovals.value.findIndex((p) => p.toolCallId === toolCallId);
    const dequeued = queueIndex === -1 ? undefined : pendingApprovals.value[queueIndex];
    if (queueIndex !== -1) {
      pendingApprovals.value.splice(queueIndex, 1);
    }
    errorMessage.value = undefined;
    status.value = 'streaming';
    const request: AgentApprovalRequest = { approved, reason };
    const { failedBeforeFirstPart } = await runStream(id, (signal) =>
      fetch(`/api/agent/conversations/${id}/approvals/${encodeURIComponent(toolCallId)}`, {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json', Accept: 'text/event-stream' },
        // JSON.stringify drops an undefined reason — {approved} on the wire.
        body: JSON.stringify(request),
        signal,
      }),
    );
    if (failedBeforeFirstPart) {
      // The POST itself was refused (409 approval/turn conflict, network
      // failure): the server consumes a pending only inside a STARTED
      // stream, so the frozen approval is still actionable. Roll the
      // optimistic mutations back so the card returns instead of leaving a
      // tool stuck at 'running' with no control — the error message stays
      // visible alongside the restored card.
      if (item !== undefined && previousItemStatus !== undefined) {
        item.status = previousItemStatus;
      }
      if (
        dequeued !== undefined &&
        !pendingApprovals.value.some((p) => p.toolCallId === toolCallId)
      ) {
        pendingApprovals.value.splice(
          Math.min(queueIndex, pendingApprovals.value.length),
          0,
          dequeued,
        );
      }
      if (!agentDisabled.value && pendingApprovals.value.length > 0) {
        status.value = 'awaiting-approval';
      }
    }
  }

  function approve(toolCallId: string, reason?: string): Promise<void> {
    return respondToApproval(toolCallId, true, reason);
  }

  function reject(toolCallId: string, reason?: string): Promise<void> {
    return respondToApproval(toolCallId, false, reason);
  }

  /**
   * Replay-on-mount: rebuilds the message model from the GET snapshot via
   * the SAME applyAgentPart reducer the live stream uses — one renderer for
   * live and replayed conversations.
   */
  async function restore(): Promise<void> {
    if (conversationId === null || agentDisabled.value) {
      return;
    }
    const id = conversationId;
    isRestoring.value = true;
    try {
      const snapshot = await api.getConversation(id);
      const next: AgentChatMessage[] = [];
      for (const message of snapshot.messages) {
        const role: AgentChatRole = message.role === 'user' ? 'user' : 'assistant';
        for (const part of message.parts) {
          if (isAgentStreamPart(part)) {
            applyAgentPart(next, part, role);
          }
        }
      }
      const restoredApprovals: AgentPendingApproval[] = [];
      for (const pending of snapshot.pendingApprovals) {
        if (!isAgentStreamPart(pending) || pending.type !== 'tool-approval-required') {
          continue;
        }
        applyAgentPart(next, pending);
        restoredApprovals.push({
          toolCallId: pending.toolCallId,
          toolName: pending.toolName,
          argumentsJson: pending.argumentsJson,
        });
      }
      messages.value = next;
      pendingApprovals.value = restoredApprovals;
      if (restoredApprovals.length > 0) {
        status.value = 'awaiting-approval';
      }
    } catch (error) {
      if (
        error instanceof ProblemError &&
        error.problem.type === AGENT_PROBLEM_TYPES.conversationNotFound
      ) {
        // The server forgot us (in-memory store, process restart) — start
        // fresh, including any stale approvals that would otherwise lock the
        // composer against a conversation the server no longer knows.
        conversationId = null;
        writePersistedConversationId(null);
        pendingApprovals.value = [];
        return;
      }
      applyRequestError(error);
    } finally {
      isRestoring.value = false;
    }
  }

  return {
    status,
    messages,
    pendingApproval,
    errorMessage,
    agentDisabled,
    budgetResetAtUtc,
    lastFinishReason,
    usage,
    isRestoring,
    isStreaming,
    sendMessage,
    approve,
    reject,
    abort,
    restore,
  };
}
