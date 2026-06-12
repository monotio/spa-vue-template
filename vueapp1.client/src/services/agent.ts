import { useFetch } from '@/composables/useFetch';
import type { AgentConversationSnapshot } from '@/contracts/agent';
import { assertAgentConversationSnapshot } from '@/contracts/agent';

/**
 * JSON half of the agent surface: the replay snapshot GET. The streaming
 * POSTs (turns, approvals) answer with `text/event-stream` and therefore
 * live in `useAgentStream` — `useFetch` deliberately buffers one JSON body
 * and must never be pointed at a stream (docs/REALTIME.md).
 *
 * These endpoints are ExcludeFromDescription (never in `api.gen.ts`); the
 * hand-written wire types and their sync rule live in `contracts/agent.ts`.
 */
export function useAgentApi() {
  const { getJson, isGetting } = useFetch();

  async function getConversation(
    conversationId: string,
    signal?: AbortSignal,
  ): Promise<AgentConversationSnapshot> {
    const payload = await getJson<unknown>(
      `/api/agent/conversations/${encodeURIComponent(conversationId)}`,
      signal,
    );
    assertAgentConversationSnapshot(payload);
    return payload;
  }

  return {
    isLoading: isGetting,
    getConversation,
  };
}
