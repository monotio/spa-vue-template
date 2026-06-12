<script setup lang="ts">
import { computed } from 'vue';
import type { AgentToolItem, AgentToolStatus } from '@/composables/useAgentStream';

// One card per toolCallId. The card is a pure renderer: idempotent
// status-rank updates happen in the useAgentStream reducer, so duplicated
// or re-ordered stream parts never regress what this card shows.
const { tool } = defineProps<{ tool: AgentToolItem }>();

const STATUS_LABELS: Record<AgentToolStatus, string> = {
  running: 'Running…',
  'awaiting-approval': 'Awaiting approval',
  ok: 'Completed',
  error: 'Failed',
  rejected: 'Rejected',
};

const statusLabel = computed(() => STATUS_LABELS[tool.status]);
const hasArguments = computed(() => tool.argumentsJson !== '' && tool.argumentsJson !== '{}');
</script>

<template>
  <div class="tool-card" :class="`status-${tool.status}`">
    <p class="tool-card-header">
      <span class="tool-name">{{ tool.toolName || tool.toolCallId }}</span>
      <span class="tool-status">{{ statusLabel }}</span>
    </p>
    <details v-if="hasArguments">
      <summary>Arguments</summary>
      <!-- Raw wire JSON, rendered verbatim: fidelity over prettiness. -->
      <pre>{{ tool.argumentsJson }}</pre>
    </details>
    <details v-if="tool.resultJson !== undefined">
      <summary>Result</summary>
      <pre>{{ tool.resultJson }}</pre>
    </details>
  </div>
</template>

<style scoped>
.tool-card {
  border: 1px solid var(--color-border);
  border-left-width: 3px;
  border-radius: 4px;
  padding: 0.5rem 0.75rem;
  margin: 0.5rem 0;
  text-align: left;
}

.status-ok {
  border-left-color: #3ba55d;
}

.status-error,
.status-rejected {
  border-left-color: var(--vt-c-red, #e53e3e);
}

.status-awaiting-approval {
  border-left-color: #d69e2e;
}

.tool-card-header {
  display: flex;
  justify-content: space-between;
  gap: 1rem;
  margin: 0;
}

.tool-name {
  font-family: ui-monospace, monospace;
  font-weight: 600;
}

.tool-status {
  color: var(--color-text);
  font-size: 0.85rem;
}

details {
  margin-top: 0.35rem;
}

summary {
  cursor: pointer;
  font-size: 0.85rem;
}

pre {
  margin: 0.25rem 0 0;
  padding: 0.4rem 0.5rem;
  background-color: var(--color-background-soft, rgba(128, 128, 128, 0.08));
  border-radius: 4px;
  font-size: 0.8rem;
  overflow-x: auto;
  white-space: pre-wrap;
  word-break: break-word;
}
</style>
