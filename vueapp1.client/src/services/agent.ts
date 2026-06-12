import { useFetch } from '@/composables/useFetch';
import type { AgentAttachmentUploadResponse, AgentConversationSnapshot } from '@/contracts/agent';
import {
  assertAgentAttachmentUploadResponse,
  assertAgentConversationSnapshot,
} from '@/contracts/agent';

/**
 * JSON half of the agent surface: the replay snapshot GET and the attachment
 * upload POST (upload-then-reference: the multipart upload returns an id the
 * turn POST references; bytes never ride the JSON turn body). The streaming
 * POSTs (turns, approvals) answer with `text/event-stream` and therefore
 * live in `useAgentStream` — `useFetch` deliberately buffers one JSON body
 * and must never be pointed at a stream (docs/REALTIME.md).
 *
 * These endpoints are ExcludeFromDescription (never in `api.gen.ts`); the
 * hand-written wire types and their sync rule live in `contracts/agent.ts`.
 */
export function useAgentApi() {
  const { getJson, postForm, isGetting, isSending } = useFetch();

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

  async function uploadAttachment(
    file: File,
    signal?: AbortSignal,
  ): Promise<AgentAttachmentUploadResponse> {
    const form = new FormData();
    form.append('file', file, file.name);
    const payload = await postForm<unknown>('/api/agent/attachments', form, signal);
    assertAgentAttachmentUploadResponse(payload);
    return payload;
  }

  return {
    isLoading: isGetting,
    isUploading: isSending,
    getConversation,
    uploadAttachment,
  };
}
