import { describe, it, expect } from 'vitest';
import {
  AGENT_STREAM_PART_TYPES,
  isAgentProblemType,
  isAgentStreamPart,
  assertAgentConversationSnapshot,
} from '../agent';

/**
 * Frontend twin of the server's AgentStreamPartTests: the fixtures below are
 * server-shaped wire bytes (camelCase, kebab-case `type` discriminator,
 * identity fields on every part). If the server vocabulary changes without
 * this file changing, these fixtures rot loudly — that is the contract
 * (the agent surface is ExcludeFromDescription, so no generated types exist
 * to catch the drift).
 */

const ID = '"conversationId":"conv-1","turnId":"11111111-2222-3333-4444-555555555555"';

const SERVER_SHAPED_FIXTURES: readonly string[] = [
  `{"type":"text-start",${ID}}`,
  `{"type":"text-delta","delta":"Hi",${ID}}`,
  `{"type":"text-end",${ID}}`,
  `{"type":"reasoning-start",${ID}}`,
  `{"type":"reasoning-delta","delta":"mull",${ID}}`,
  `{"type":"reasoning-end",${ID}}`,
  `{"type":"tool-input-available","toolCallId":"call-1","toolName":"get_weather_forecast","argumentsJson":"{}",${ID}}`,
  `{"type":"tool-output-available","toolCallId":"call-1","resultJson":"{\\"result\\":[]}","isError":false,${ID}}`,
  `{"type":"tool-approval-required","toolCallId":"call-2","toolName":"delete_item","argumentsJson":"{\\"id\\":7}",${ID}}`,
  `{"type":"usage","inputTokens":100,"cachedInputTokens":40,"outputTokens":20,"reasoningTokens":5,"estimatedUsd":0.0123,${ID}}`,
  `{"type":"error","problem":{"title":"Boom","status":502,"detail":"It broke."},${ID}}`,
  `{"type":"finish","reason":"stop",${ID}}`,
];

describe('agent wire contract', () => {
  it('accepts every server-shaped part fixture', () => {
    for (const fixture of SERVER_SHAPED_FIXTURES) {
      const parsed: unknown = JSON.parse(fixture);
      expect(isAgentStreamPart(parsed), fixture).toBe(true);
    }
  });

  it('covers the exact server vocabulary — no extras, no omissions', () => {
    const fixtureTypes = SERVER_SHAPED_FIXTURES.map(
      (fixture) => (JSON.parse(fixture) as { type: string }).type,
    );
    expect(fixtureTypes).toEqual([...AGENT_STREAM_PART_TYPES]);
  });

  it('rejects the deliberately-omitted AI-SDK part types', () => {
    // tool-input-start/-delta do NOT exist in this vocabulary (the adapters
    // never surface argument deltas; a dead part type is comprehension tax).
    for (const type of ['tool-input-start', 'tool-input-delta']) {
      expect(isAgentStreamPart(JSON.parse(`{"type":"${type}","toolCallId":"call-1",${ID}}`))).toBe(
        false,
      );
    }
  });

  it('rejects finish parts whose reason is outside the closed union', () => {
    // `lastFinishReason` is typed as the CLOSED AgentFinishReason union and
    // consumers exhaustively switch on it — an unvalidated server string must
    // not flow through under that type. A future server-added reason drops
    // the part (the same forward tolerance as an unknown part type).
    for (const reason of ['turn-in-progress', 'new-server-reason']) {
      expect(isAgentStreamPart(JSON.parse(`{"type":"finish","reason":"${reason}",${ID}}`))).toBe(
        false,
      );
    }
  });

  it('rejects parts missing the identity fields every part must carry', () => {
    expect(isAgentStreamPart(JSON.parse('{"type":"text-start"}'))).toBe(false);
    expect(
      isAgentStreamPart(JSON.parse('{"type":"text-delta","delta":"x","conversationId":"c1"}')),
    ).toBe(false);
  });

  it('rejects malformed payloads per discriminant', () => {
    expect(isAgentStreamPart(JSON.parse(`{"type":"text-delta",${ID}}`))).toBe(false);
    expect(
      isAgentStreamPart(
        JSON.parse(`{"type":"tool-output-available","toolCallId":"c","resultJson":"{}",${ID}}`),
      ),
    ).toBe(false);
    expect(isAgentStreamPart(JSON.parse(`{"type":"error","problem":{},${ID}}`))).toBe(false);
    expect(isAgentStreamPart(null)).toBe(false);
    expect(isAgentStreamPart([])).toBe(false);
  });

  it('classifies agent-owned ProblemDetails types', () => {
    expect(isAgentProblemType('/problems/agent-turn-in-progress')).toBe(true);
    expect(isAgentProblemType('/problems/agent-conversation-not-found')).toBe(true);
    // The generic /api 404 (module disabled) carries an RFC link or nothing.
    expect(isAgentProblemType('https://tools.ietf.org/html/rfc9110#section-15.5.5')).toBe(false);
    expect(isAgentProblemType(undefined)).toBe(false);
    expect(isAgentProblemType('/problems/idempotency-key-conflict')).toBe(false);
  });

  it('asserts the snapshot shell and rejects non-snapshots', () => {
    const snapshot: unknown = JSON.parse(
      `{"conversationId":"conv-1","messages":[{"role":"user","parts":[{"type":"text-start",${ID}}]}],"pendingApprovals":[]}`,
    );
    expect(() => assertAgentConversationSnapshot(snapshot)).not.toThrow();
    expect(() => assertAgentConversationSnapshot({ conversationId: 'x' })).toThrow(
      'API contract mismatch',
    );
    expect(() =>
      assertAgentConversationSnapshot({
        conversationId: 'x',
        messages: [{}],
        pendingApprovals: [],
      }),
    ).toThrow('API contract mismatch');
  });
});
