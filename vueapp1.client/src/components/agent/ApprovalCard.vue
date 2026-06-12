<script setup lang="ts">
import { ref } from 'vue';
import type { AgentPendingApproval } from '@/composables/useAgentStream';

// The human gate for approval-tier tool calls. What you see here is what
// runs: the server executes the FROZEN arguments shown below (and fails
// closed with a 409 if the tool-policy surface changed since the freeze).
const { approval, busy = false } = defineProps<{
  approval: AgentPendingApproval;
  busy?: boolean;
}>();

const emit = defineEmits<{
  approve: [toolCallId: string, reason?: string];
  reject: [toolCallId: string, reason?: string];
}>();

const reason = ref('');

function respond(approved: boolean): void {
  const trimmed = reason.value.trim();
  const optionalReason = trimmed === '' ? undefined : trimmed;
  if (approved) {
    emit('approve', approval.toolCallId, optionalReason);
  } else {
    emit('reject', approval.toolCallId, optionalReason);
  }
}
</script>

<template>
  <div class="approval-card" role="group" aria-label="Tool approval request">
    <p class="approval-title">
      The agent wants to run <code>{{ approval.toolName }}</code>
    </p>
    <pre v-if="approval.argumentsJson && approval.argumentsJson !== '{}'">{{
      approval.argumentsJson
    }}</pre>
    <label class="reason-label" for="approval-reason">
      Reason (optional, shown to the agent)
      <input
        id="approval-reason"
        v-model="reason"
        type="text"
        autocomplete="off"
        :disabled="busy"
      />
    </label>
    <div class="actions">
      <button type="button" class="approve" :disabled="busy" @click="respond(true)">Approve</button>
      <button type="button" class="reject" :disabled="busy" @click="respond(false)">Reject</button>
    </div>
  </div>
</template>

<style scoped>
.approval-card {
  border: 1px solid #d69e2e;
  border-radius: 4px;
  padding: 0.75rem 1rem;
  margin: 1rem 0;
  text-align: left;
}

.approval-title {
  margin: 0 0 0.5rem;
  font-weight: 600;
}

pre {
  margin: 0 0 0.5rem;
  padding: 0.4rem 0.5rem;
  background-color: var(--color-background-soft, rgba(128, 128, 128, 0.08));
  border-radius: 4px;
  font-size: 0.8rem;
  overflow-x: auto;
  white-space: pre-wrap;
  word-break: break-word;
}

.reason-label {
  display: block;
  font-size: 0.85rem;
  margin-bottom: 0.25rem;
}

input {
  width: 100%;
  padding: 0.35rem 0.5rem;
  border: 1px solid var(--color-border);
  border-radius: 4px;
  background-color: var(--color-background);
  color: var(--color-text);
}

.actions {
  display: flex;
  gap: 0.75rem;
  margin-top: 0.6rem;
}

button {
  padding: 0.35rem 1rem;
  border-radius: 4px;
  border: 1px solid var(--color-border);
  background-color: var(--color-background);
  color: var(--color-text);
  cursor: pointer;
}

button:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.approve {
  border-color: #3ba55d;
}

.reject {
  border-color: var(--vt-c-red, #e53e3e);
}
</style>
