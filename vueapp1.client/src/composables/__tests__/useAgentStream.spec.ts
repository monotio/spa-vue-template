import { describe, it, expect, vi, beforeEach, onTestFinished } from 'vitest';
import { withSetup } from '@/test/withSetup';
import { useAgentStream, parseSseStream, applyAgentPart } from '../useAgentStream';
import type { AgentChatMessage, AgentToolItem } from '../useAgentStream';
import type { AgentStreamPart } from '@/contracts/agent';

// ---------------------------------------------------------------------------
// Helpers

const encoder = new TextEncoder();

/** A push-controlled SSE body, with fetch-faithful abort semantics. */
function createSseFeed() {
  let controller!: ReadableStreamDefaultController<Uint8Array>;
  const stream = new ReadableStream<Uint8Array>({
    start(c) {
      controller = c;
    },
  });
  return {
    stream,
    push: (text: string) => controller.enqueue(encoder.encode(text)),
    pushBytes: (bytes: Uint8Array) => controller.enqueue(bytes),
    close: () => controller.close(),
    error: (reason: unknown) => controller.error(reason),
  };
}

function streamOf(...chunks: (string | Uint8Array)[]): ReadableStream<Uint8Array> {
  return new ReadableStream<Uint8Array>({
    start(controller) {
      for (const chunk of chunks) {
        controller.enqueue(typeof chunk === 'string' ? encoder.encode(chunk) : chunk);
      }
      controller.close();
    },
  });
}

async function collect(stream: ReadableStream<Uint8Array>): Promise<string[]> {
  const events: string[] = [];
  for await (const data of parseSseStream(stream)) {
    events.push(data);
  }
  return events;
}

/** One macrotask turn: lets enqueued chunks flow through reader.read() loops. */
function tick(): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, 0));
}

const CONVERSATION_ID = 'conv-1';
const TURN_ID = '11111111-2222-3333-4444-555555555555';

/** Builds a server-shaped part with the identity fields every part carries. */
function part<T extends Record<string, unknown>>(body: T): T & Record<string, unknown> {
  return { conversationId: CONVERSATION_ID, turnId: TURN_ID, ...body };
}

function frame(body: Record<string, unknown>): string {
  return `data: ${JSON.stringify(body)}\n\n`;
}

interface FeedHandle {
  feed: ReturnType<typeof createSseFeed>;
  request: { url: string; init: RequestInit | undefined };
}

/**
 * Installs a fetch mock that answers each call with a fresh SSE feed
 * (fetch-faithful: aborting the request errors the body stream with an
 * AbortError, exactly like the real network stack).
 */
function mockSseFetch(): FeedHandle[] {
  const handles: FeedHandle[] = [];
  globalThis.fetch = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const feed = createSseFeed();
    init?.signal?.addEventListener('abort', () => {
      feed.error(new DOMException('The operation was aborted.', 'AbortError'));
    });
    const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
    handles.push({ feed, request: { url, init } });
    return Promise.resolve(
      new Response(feed.stream, {
        status: 200,
        headers: { 'Content-Type': 'text/event-stream' },
      }),
    );
  });
  return handles;
}

function mockProblemFetch(status: number, problem: Record<string, unknown>): void {
  globalThis.fetch = vi.fn(() =>
    Promise.resolve(
      new Response(JSON.stringify(problem), {
        status,
        headers: { 'Content-Type': 'application/problem+json' },
      }),
    ),
  );
}

function findTool(messages: AgentChatMessage[], toolCallId: string): AgentToolItem | undefined {
  for (const message of messages) {
    for (const item of message.items) {
      if (item.kind === 'tool' && item.toolCallId === toolCallId) {
        return item;
      }
    }
  }
  return undefined;
}

function setupAgent() {
  const setup = withSetup(() => useAgentStream());
  onTestFinished(setup.unmount);
  return setup.result;
}

beforeEach(() => {
  sessionStorage.clear();
  // Most tests pin the conversation id so part identity fields and URLs are
  // deterministic; the fresh-id test clears this again.
  sessionStorage.setItem('agent:conversationId', CONVERSATION_ID);
});

// ---------------------------------------------------------------------------
// The SSE parser: adversarial byte-split fixtures. This is the
// silently-corrupts-in-production class — every fixture here reproduces a
// real chunk-boundary failure mode.

describe('parseSseStream', () => {
  it('parses one event per data frame', async () => {
    expect(await collect(streamOf('data: {"a":1}\n\n'))).toEqual(['{"a":1}']);
  });

  it('reassembles a frame split across reads', async () => {
    expect(await collect(streamOf('data: {"type":"te', 'xt-delta","delta":"Hi"}\n\n'))).toEqual([
      '{"type":"text-delta","delta":"Hi"}',
    ]);
  });

  it('decodes multi-byte UTF-8 split mid-character', async () => {
    // 'é' is 2 bytes, '✨' is 3, '🌧' is 4 — split inside the 4-byte emoji.
    const payload = 'data: {"delta":"é ✨ 🌧"}\n\n';
    const bytes = encoder.encode(payload);
    const splitAt = payload.indexOf('🌧') + 2; // byte offset lands inside the surrogate's bytes
    expect(await collect(streamOf(bytes.slice(0, splitAt), bytes.slice(splitAt)))).toEqual([
      '{"delta":"é ✨ 🌧"}',
    ]);
  });

  it('handles CRLF terminators split across reads without phantom events', async () => {
    // The \r\n of the first line is split between chunks: the lone trailing
    // \r must terminate the line WITHOUT the following \n spawning an empty
    // line (which would dispatch the event twice or dispatch an empty one).
    expect(await collect(streamOf('data: one\r', '\n\r\ndata: two\r\n\r\n'))).toEqual([
      'one',
      'two',
    ]);
  });

  it('accepts lone-CR line terminators', async () => {
    expect(await collect(streamOf('data: a\rdata: b\r\r'))).toEqual(['a\nb']);
  });

  it('parses multiple frames arriving in one read', async () => {
    expect(await collect(streamOf('data: one\n\ndata: two\n\n'))).toEqual(['one', 'two']);
  });

  it('ignores comments and non-data fields', async () => {
    expect(
      await collect(streamOf(': keep-alive\nevent: message\nid: 7\nretry: 100\ndata: x\n\n')),
    ).toEqual(['x']);
  });

  it('joins multi-line data with newlines per the SSE spec', async () => {
    expect(await collect(streamOf('data: a\ndata: b\n\n'))).toEqual(['a\nb']);
  });

  it('discards an unterminated trailing event at end of stream (spec rule)', async () => {
    // WHATWG: an incomplete event at EOF is never dispatched — half a JSON
    // payload must not reach JSON.parse.
    expect(await collect(streamOf('data: complete\n\ndata: {"half":'))).toEqual(['complete']);
  });
});

// ---------------------------------------------------------------------------
// The composable: status machine, reducer, abort, approvals, disabled-state.

describe('useAgentStream', () => {
  it('streams a turn: POST, text deltas, usage, finish → idle', async () => {
    const handles = mockSseFetch();
    const agent = setupAgent();

    expect(agent.status.value).toBe('idle');
    const send = agent.sendMessage('What is the weather?');
    await tick();

    expect(agent.status.value).toBe('streaming');
    const handle = handles[0];
    expect(handle.request.url).toBe(`/api/agent/conversations/${CONVERSATION_ID}/turns`);
    expect(handle.request.init?.method).toBe('POST');
    expect(handle.request.init?.body).toBe(JSON.stringify({ message: 'What is the weather?' }));

    handle.feed.push(frame(part({ type: 'text-start' })));
    handle.feed.push(frame(part({ type: 'text-delta', delta: 'Hello ' })));
    handle.feed.push(frame(part({ type: 'text-delta', delta: 'world' })));
    handle.feed.push(frame(part({ type: 'text-end' })));
    handle.feed.push(
      frame(
        part({
          type: 'usage',
          inputTokens: 100,
          cachedInputTokens: 40,
          outputTokens: 20,
          reasoningTokens: 5,
          estimatedUsd: 0.0123,
        }),
      ),
    );
    handle.feed.push(frame(part({ type: 'finish', reason: 'stop' })));
    handle.feed.close();
    await send;

    expect(agent.status.value).toBe('idle');
    expect(agent.lastFinishReason.value).toBe('stop');
    expect(agent.messages.value).toEqual([
      { role: 'user', items: [{ kind: 'text', text: 'What is the weather?', streaming: false }] },
      { role: 'assistant', items: [{ kind: 'text', text: 'Hello world', streaming: false }] },
    ]);
    expect(agent.usage.value).toEqual({
      inputTokens: 100,
      cachedInputTokens: 40,
      outputTokens: 20,
      reasoningTokens: 5,
      estimatedUsd: 0.0123,
    });
  });

  it('generates and persists a conversation id when none is stored', async () => {
    sessionStorage.clear();
    const handles = mockSseFetch();
    const agent = setupAgent();

    const send = agent.sendMessage('hi');
    await tick();

    const url = handles[0].request.url;
    const match = /^\/api\/agent\/conversations\/([A-Za-z0-9_-]{1,64})\/turns$/.exec(url);
    expect(match).not.toBeNull();
    expect(sessionStorage.getItem('agent:conversationId')).toBe(match![1]);

    handles[0].feed.close();
    await send;
  });

  it('reduces reasoning parts into a reasoning item', async () => {
    const handles = mockSseFetch();
    const agent = setupAgent();

    const send = agent.sendMessage('think');
    await tick();
    const { feed } = handles[0];
    feed.push(frame(part({ type: 'reasoning-start' })));
    feed.push(frame(part({ type: 'reasoning-delta', delta: 'mulling' })));
    await tick();

    const assistant = agent.messages.value[1];
    expect(assistant.items).toEqual([{ kind: 'reasoning', text: 'mulling', streaming: true }]);

    feed.push(frame(part({ type: 'reasoning-end' })));
    feed.push(frame(part({ type: 'finish', reason: 'stop' })));
    feed.close();
    await send;

    expect(assistant.items).toEqual([{ kind: 'reasoning', text: 'mulling', streaming: false }]);
  });

  it('keeps tool cards idempotent under duplicate and out-of-order parts', async () => {
    const handles = mockSseFetch();
    const agent = setupAgent();

    const send = agent.sendMessage('weather please');
    await tick();
    const { feed } = handles[0];

    // Out of order: output for call-2 arrives before its input part.
    feed.push(
      frame(
        part({
          type: 'tool-output-available',
          toolCallId: 'call-2',
          resultJson: '{"t":7}',
          isError: false,
        }),
      ),
    );
    feed.push(
      frame(
        part({
          type: 'tool-input-available',
          toolCallId: 'call-2',
          toolName: 'get_weather_forecast',
          argumentsJson: '{}',
        }),
      ),
    );
    // Duplicate output must not duplicate the card nor regress its status.
    feed.push(
      frame(
        part({
          type: 'tool-output-available',
          toolCallId: 'call-2',
          resultJson: '{"t":7}',
          isError: false,
        }),
      ),
    );
    // Duplicate input must not reset a completed card to running.
    feed.push(
      frame(
        part({
          type: 'tool-input-available',
          toolCallId: 'call-2',
          toolName: 'get_weather_forecast',
          argumentsJson: '{}',
        }),
      ),
    );
    feed.push(frame(part({ type: 'finish', reason: 'stop' })));
    feed.close();
    await send;

    const cards = agent.messages.value
      .flatMap((m) => m.items)
      .filter((item) => item.kind === 'tool');
    expect(cards).toHaveLength(1);
    expect(cards[0]).toMatchObject({
      toolCallId: 'call-2',
      toolName: 'get_weather_forecast',
      status: 'ok',
      resultJson: '{"t":7}',
    });
  });

  it('drops parts from another conversation (dead-run guard)', async () => {
    const handles = mockSseFetch();
    const agent = setupAgent();

    const send = agent.sendMessage('hello');
    await tick();
    const { feed } = handles[0];
    feed.push(
      `data: ${JSON.stringify({ type: 'text-delta', delta: 'ghost', conversationId: 'other-conv', turnId: TURN_ID })}\n\n`,
    );
    feed.push(frame(part({ type: 'text-delta', delta: 'real' })));
    feed.push(frame(part({ type: 'finish', reason: 'stop' })));
    feed.close();
    await send;

    expect(agent.messages.value[1].items).toEqual([
      { kind: 'text', text: 'real', streaming: false },
    ]);
  });

  it('drops unknown part types without corrupting state (forward tolerance)', async () => {
    const handles = mockSseFetch();
    const agent = setupAgent();

    const send = agent.sendMessage('hello');
    await tick();
    const { feed } = handles[0];
    feed.push(frame(part({ type: 'tool-input-start', toolCallId: 'x' })));
    feed.push('data: not even json\n\n');
    feed.push(frame(part({ type: 'text-delta', delta: 'still fine' })));
    feed.push(frame(part({ type: 'finish', reason: 'stop' })));
    feed.close();
    await send;

    expect(agent.status.value).toBe('idle');
    expect(agent.messages.value[1].items).toEqual([
      { kind: 'text', text: 'still fine', streaming: false },
    ]);
  });

  it('enters awaiting-approval and resumes through approve()', async () => {
    const handles = mockSseFetch();
    const agent = setupAgent();

    const send = agent.sendMessage('delete it');
    await tick();
    handles[0].feed.push(
      frame(
        part({
          type: 'tool-approval-required',
          toolCallId: 'call-9',
          toolName: 'delete_item',
          argumentsJson: '{"id":7}',
        }),
      ),
    );
    handles[0].feed.push(frame(part({ type: 'finish', reason: 'approval-required' })));
    handles[0].feed.close();
    await send;

    expect(agent.status.value).toBe('awaiting-approval');
    expect(agent.pendingApproval.value).toEqual({
      toolCallId: 'call-9',
      toolName: 'delete_item',
      argumentsJson: '{"id":7}',
    });
    expect(findTool(agent.messages.value, 'call-9')?.status).toBe('awaiting-approval');

    const approval = agent.approve('call-9');
    await tick();

    expect(agent.status.value).toBe('streaming');
    expect(agent.pendingApproval.value).toBeUndefined();
    expect(findTool(agent.messages.value, 'call-9')?.status).toBe('running');
    const second = handles[1];
    expect(second.request.url).toBe(`/api/agent/conversations/${CONVERSATION_ID}/approvals/call-9`);
    expect(second.request.init?.body).toBe(JSON.stringify({ approved: true }));

    second.feed.push(
      frame(
        part({
          type: 'tool-output-available',
          toolCallId: 'call-9',
          resultJson: '{"ok":true}',
          isError: false,
        }),
      ),
    );
    second.feed.push(frame(part({ type: 'text-delta', delta: 'Done.' })));
    second.feed.push(frame(part({ type: 'finish', reason: 'stop' })));
    second.feed.close();
    await approval;

    expect(agent.status.value).toBe('idle');
    expect(findTool(agent.messages.value, 'call-9')).toMatchObject({
      status: 'ok',
      resultJson: '{"ok":true}',
    });
  });

  it('reject() posts approved:false and the card stays rejected', async () => {
    const handles = mockSseFetch();
    const agent = setupAgent();

    const send = agent.sendMessage('delete it');
    await tick();
    handles[0].feed.push(
      frame(
        part({
          type: 'tool-approval-required',
          toolCallId: 'call-9',
          toolName: 'delete_item',
          argumentsJson: '{"id":7}',
        }),
      ),
    );
    handles[0].feed.push(frame(part({ type: 'finish', reason: 'approval-required' })));
    handles[0].feed.close();
    await send;

    const rejection = agent.reject('call-9', 'too risky');
    await tick();

    const second = handles[1];
    expect(second.request.init?.body).toBe(
      JSON.stringify({ approved: false, reason: 'too risky' }),
    );
    expect(findTool(agent.messages.value, 'call-9')?.status).toBe('rejected');

    // The server appends a model-visible rejection envelope (isError: true);
    // the card must STAY rejected — not flip to a generic error.
    second.feed.push(
      frame(
        part({
          type: 'tool-output-available',
          toolCallId: 'call-9',
          resultJson: '{"error":{"code":"approval_rejected"}}',
          isError: true,
        }),
      ),
    );
    second.feed.push(frame(part({ type: 'finish', reason: 'stop' })));
    second.feed.close();
    await rejection;

    expect(findTool(agent.messages.value, 'call-9')?.status).toBe('rejected');
    expect(agent.status.value).toBe('idle');
  });

  it('abort() propagates to the fetch signal and settles as cancelled', async () => {
    const handles = mockSseFetch();
    const agent = setupAgent();

    const send = agent.sendMessage('long task');
    await tick();
    handles[0].feed.push(frame(part({ type: 'text-delta', delta: 'partial' })));
    await tick();
    expect(agent.status.value).toBe('streaming');

    agent.abort();
    await send;

    // AbortController → fetch signal: the server's RequestAborted fires and
    // it cancels the provider call + writes the bills-to-date ledger entry
    // (pinned server-side in AgentEndpointTests); the client's only job is
    // to actually abort the request.
    expect(handles[0].request.init?.signal?.aborted).toBe(true);
    expect(agent.status.value).toBe('idle');
    expect(agent.lastFinishReason.value).toBe('cancelled');
    // Bills-to-date text stays visible — aborting must not eat the partial output.
    expect(agent.messages.value[1].items).toEqual([
      { kind: 'text', text: 'partial', streaming: false },
    ]);
  });

  it('latest-wins: a new sendMessage aborts the previous stream', async () => {
    const handles = mockSseFetch();
    const agent = setupAgent();

    const first = agent.sendMessage('first');
    await tick();
    const second = agent.sendMessage('second');
    await tick();

    expect(handles[0].request.init?.signal?.aborted).toBe(true);
    expect(agent.status.value).toBe('streaming');

    handles[1].feed.push(frame(part({ type: 'finish', reason: 'stop' })));
    handles[1].feed.close();
    await Promise.all([first, second]);
    expect(agent.status.value).toBe('idle');
  });

  it('disposing the scope aborts the in-flight stream', async () => {
    const handles = mockSseFetch();
    const { result, unmount } = withSetup(() => useAgentStream());

    const send = result.sendMessage('hello');
    await tick();
    unmount();
    await send;

    expect(handles[0].request.init?.signal?.aborted).toBe(true);
  });

  // THE disabled-state mapping (pinned): with Agent:Enabled=false the agent
  // endpoints are never mapped, so the turn POST falls through to the
  // generic /api 404 ProblemDetails contract — no /problems/agent-* type.
  it('maps the generic API 404 to the disabled-module state', async () => {
    mockProblemFetch(404, {
      type: 'https://tools.ietf.org/html/rfc9110#section-15.5.5',
      title: 'Not Found',
      status: 404,
      detail: "No API endpoint matches path '/api/agent/conversations/conv-1/turns'.",
    });
    const agent = setupAgent();

    await agent.sendMessage('hello?');

    expect(agent.agentDisabled.value).toBe(true);
    expect(agent.status.value).toBe('idle');
    expect(agent.errorMessage.value).toBeUndefined();
  });

  it('does NOT treat agent-typed problems as the disabled state', async () => {
    mockProblemFetch(409, {
      type: '/problems/agent-turn-in-progress',
      title: 'Agent turn already in progress',
      status: 409,
    });
    const agent = setupAgent();

    await agent.sendMessage('hello?');

    expect(agent.agentDisabled.value).toBe(false);
    expect(agent.status.value).toBe('error');
    expect(agent.errorMessage.value).toBe('Agent turn already in progress');
  });

  it('surfaces budget exhaustion with resetAtUtc', async () => {
    mockProblemFetch(429, {
      type: '/problems/agent-budget-exceeded',
      title: 'Daily agent budget exhausted',
      status: 429,
      resetAtUtc: '2026-06-13T00:00:00+00:00',
    });
    const agent = setupAgent();

    await agent.sendMessage('expensive question');

    expect(agent.status.value).toBe('error');
    expect(agent.budgetResetAtUtc.value).toBe('2026-06-13T00:00:00+00:00');
  });

  it('turns a stream error part into the error state', async () => {
    const handles = mockSseFetch();
    const agent = setupAgent();

    const send = agent.sendMessage('boom');
    await tick();
    const { feed } = handles[0];
    feed.push(
      frame(
        part({
          type: 'error',
          problem: {
            title: 'Tool error limit reached',
            status: 502,
            detail: '3 consecutive tool calls failed.',
          },
        }),
      ),
    );
    feed.push(frame(part({ type: 'finish', reason: 'stop' })));
    feed.close();
    await send;

    expect(agent.status.value).toBe('error');
    expect(agent.errorMessage.value).toBe('3 consecutive tool calls failed.');
  });

  it('restore() replays the snapshot through the same reducer as live parts', async () => {
    const snapshot = {
      conversationId: CONVERSATION_ID,
      messages: [
        {
          role: 'user',
          parts: [
            part({ type: 'text-start' }),
            part({ type: 'text-delta', delta: 'Forecast?' }),
            part({ type: 'text-end' }),
          ],
        },
        {
          role: 'assistant',
          parts: [
            part({
              type: 'tool-input-available',
              toolCallId: 'call-1',
              toolName: 'get_weather_forecast',
              argumentsJson: '{}',
            }),
          ],
        },
        {
          role: 'tool',
          parts: [
            part({
              type: 'tool-output-available',
              toolCallId: 'call-1',
              resultJson: '{"t":21}',
              isError: false,
            }),
          ],
        },
        {
          role: 'assistant',
          parts: [
            part({ type: 'text-start' }),
            part({ type: 'text-delta', delta: 'Sunny, 21.' }),
            part({ type: 'text-end' }),
          ],
        },
      ],
      pendingApprovals: [
        part({
          type: 'tool-approval-required',
          toolCallId: 'call-3',
          toolName: 'delete_item',
          argumentsJson: '{"id":1}',
        }),
      ],
    };
    globalThis.fetch = vi.fn(() =>
      Promise.resolve(
        new Response(JSON.stringify(snapshot), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      ),
    );
    const agent = setupAgent();

    await agent.restore();

    expect(vi.mocked(globalThis.fetch)).toHaveBeenCalledWith(
      `/api/agent/conversations/${CONVERSATION_ID}`,
      expect.anything(),
    );

    // Replay parity: the snapshot reduces to the same item structures a live
    // stream of the same parts produces — one renderer, by construction.
    const live: AgentChatMessage[] = [];
    applyAgentPart(live, part({ type: 'text-start' }) as unknown as AgentStreamPart, 'user');
    applyAgentPart(
      live,
      part({ type: 'text-delta', delta: 'Forecast?' }) as unknown as AgentStreamPart,
      'user',
    );
    applyAgentPart(live, part({ type: 'text-end' }) as unknown as AgentStreamPart, 'user');
    for (const livePart of [
      part({
        type: 'tool-input-available',
        toolCallId: 'call-1',
        toolName: 'get_weather_forecast',
        argumentsJson: '{}',
      }),
      part({
        type: 'tool-output-available',
        toolCallId: 'call-1',
        resultJson: '{"t":21}',
        isError: false,
      }),
      part({ type: 'text-start' }),
      part({ type: 'text-delta', delta: 'Sunny, 21.' }),
      part({ type: 'text-end' }),
      part({
        type: 'tool-approval-required',
        toolCallId: 'call-3',
        toolName: 'delete_item',
        argumentsJson: '{"id":1}',
      }),
    ]) {
      applyAgentPart(live, livePart as unknown as AgentStreamPart);
    }
    expect(agent.messages.value).toEqual(live);

    expect(agent.status.value).toBe('awaiting-approval');
    expect(agent.pendingApproval.value).toEqual({
      toolCallId: 'call-3',
      toolName: 'delete_item',
      argumentsJson: '{"id":1}',
    });
  });

  it('restore() forgets a conversation the server no longer knows', async () => {
    mockProblemFetch(404, {
      type: '/problems/agent-conversation-not-found',
      title: 'Not Found',
      status: 404,
    });
    const agent = setupAgent();

    await agent.restore();

    // An agent-typed 404 proves the module is ALIVE — this must clear the
    // stale id, not flip the disabled callout.
    expect(agent.agentDisabled.value).toBe(false);
    expect(agent.status.value).toBe('idle');
    expect(sessionStorage.getItem('agent:conversationId')).toBeNull();
  });
});
