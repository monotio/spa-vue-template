<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { useAgentStream } from '@/composables/useAgentStream';
import ToolCallCard from '@/components/agent/ToolCallCard.vue';
import ApprovalCard from '@/components/agent/ApprovalCard.vue';

// The composable is the single transport owner (docs/REALTIME.md): this page
// only renders the reduced message model and forwards user intent. Route
// focus is owned by App.vue's afterEach machinery — the page never steals
// focus on mount; in-stream updates are announced via the role="status"
// region below instead of an aria-live transcript (delta spam).
const {
  status,
  messages,
  pendingApproval,
  errorMessage,
  agentDisabled,
  budgetResetAtUtc,
  lastFinishReason,
  usage,
  isRestoring,
  sendMessage,
  approve,
  reject,
  abort,
  restore,
} = useAgentStream();

const draft = ref('');

// Replay-on-mount: a revisit within the session re-renders the transcript
// through the same reducer the live stream uses.
onMounted(() => {
  void restore();
});

// The composer locks while an approval is pending, not just while
// streaming: sending a new message then would hand the provider a
// transcript with an unanswered tool call (see the wedge guard in
// useAgentStream.sendMessage) — the approval must be resolved or rejected
// first, and the UI must not offer the wedging action.
const composerLocked = computed(
  () =>
    status.value === 'streaming' ||
    status.value === 'awaiting-approval' ||
    pendingApproval.value !== undefined,
);

function onSubmit(): void {
  const text = draft.value;
  if (text.trim() === '' || composerLocked.value) {
    return;
  }
  draft.value = '';
  void sendMessage(text);
}

const hasUsage = computed(() => usage.value.inputTokens > 0 || usage.value.outputTokens > 0);

const statusNote = computed(() => {
  if (status.value === 'streaming') {
    return 'The agent is responding…';
  }
  if (status.value === 'awaiting-approval') {
    return 'The agent is waiting for your approval. Approve or reject the pending tool call to continue.';
  }
  switch (lastFinishReason.value) {
    case 'max-turns':
      return 'Turn limit reached — the agent stopped gracefully.';
    case 'budget-exceeded':
      return 'Per-request budget reached — the agent stopped between turns.';
    case 'cancelled':
      return 'Generation stopped. Tokens streamed so far are still billed.';
    default:
      return '';
  }
});
</script>

<template>
  <div class="agent-page">
    <h1>Agent chat</h1>
    <p class="intro">
      Streaming agent turns over SSE: text and reasoning deltas, live tool calls against the
      in-process MCP registry, human approval for write-tier tools, and visible spend.
    </p>

    <div v-if="agentDisabled" class="callout" data-testid="agent-disabled">
      <h2>The agent module is switched off</h2>
      <p>
        This showcase needs the opt-in agent loop, which ships disabled (<code
          >Agent:Enabled=false</code
        >
        — no provider calls, no spend, zero OpenAPI surface by default). To try it locally: set
        <code>Agent:Enabled</code> to <code>true</code> in
        <code>VueApp1.Server/appsettings.Development.json</code>, provide
        <code>ANTHROPIC_API_KEY</code> (or switch <code>Agent:Provider</code> and use
        <code>OPENAI_API_KEY</code>), and restart the backend. Guards, budgets, and the full setup
        guide live in <code>docs/AGENT.md</code>.
      </p>
    </div>

    <template v-else>
      <p v-if="isRestoring" class="muted">Restoring conversation…</p>

      <section class="transcript" aria-label="Conversation">
        <article
          v-for="(message, messageIndex) in messages"
          :key="messageIndex"
          class="message"
          :class="message.role"
        >
          <p class="message-author">{{ message.role === 'user' ? 'You' : 'Agent' }}</p>
          <template v-for="(item, itemIndex) in message.items" :key="itemIndex">
            <p v-if="item.kind === 'text'" class="message-text">
              {{ item.text }}<span v-if="item.streaming" class="cursor" aria-hidden="true">▍</span>
            </p>
            <details v-else-if="item.kind === 'reasoning'" class="reasoning">
              <summary>
                Reasoning<span v-if="item.streaming" class="muted"> (thinking…)</span>
              </summary>
              <p class="message-text reasoning-text">{{ item.text }}</p>
            </details>
            <ToolCallCard v-else :tool="item" />
          </template>
        </article>
      </section>

      <ApprovalCard
        v-if="pendingApproval"
        :approval="pendingApproval"
        :busy="status === 'streaming'"
        @approve="(toolCallId, reason) => void approve(toolCallId, reason)"
        @reject="(toolCallId, reason) => void reject(toolCallId, reason)"
      />

      <p v-if="errorMessage" class="error" role="alert">
        {{ errorMessage }}
        <template v-if="budgetResetAtUtc"> The budget resets at {{ budgetResetAtUtc }}.</template>
      </p>

      <p class="status-note" role="status">{{ statusNote }}</p>

      <form class="composer" @submit.prevent="onSubmit">
        <input
          id="agent-message"
          v-model="draft"
          type="text"
          autocomplete="off"
          aria-label="Message to the agent"
          placeholder="Ask the agent…"
          :disabled="composerLocked"
        />
        <button v-if="status === 'streaming'" type="button" class="stop" @click="abort">
          Stop
        </button>
        <button v-else type="submit" :disabled="draft.trim() === '' || composerLocked">Send</button>
      </form>

      <footer v-if="hasUsage" class="usage" data-testid="usage-footer">
        <span>
          {{ usage.inputTokens }} in ({{ usage.cachedInputTokens }} cached) ·
          {{ usage.outputTokens }} out · {{ usage.reasoningTokens }} reasoning
        </span>
        <span>≈ ${{ usage.estimatedUsd.toFixed(4) }} this session</span>
      </footer>
    </template>
  </div>
</template>

<style scoped>
.agent-page {
  max-width: 720px;
  margin: 0 auto;
  text-align: left;
}

.intro {
  margin-bottom: 1.5rem;
}

.callout {
  border: 1px solid var(--color-border);
  border-left: 3px solid #d69e2e;
  border-radius: 4px;
  padding: 0.75rem 1rem;
}

.callout h2 {
  margin: 0 0 0.5rem;
  font-size: 1.05rem;
}

.transcript {
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.message-author {
  margin: 0 0 0.15rem;
  font-size: 0.8rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--color-heading);
}

.message.user .message-text {
  background-color: var(--color-background-soft, rgba(128, 128, 128, 0.08));
  border-radius: 6px;
  padding: 0.5rem 0.75rem;
}

.message-text {
  margin: 0;
  white-space: pre-wrap;
  word-break: break-word;
}

.reasoning {
  margin: 0.35rem 0;
}

.reasoning summary {
  cursor: pointer;
  font-size: 0.85rem;
}

.reasoning-text {
  font-size: 0.85rem;
  color: var(--color-text);
  border-left: 2px solid var(--color-border);
  padding-left: 0.6rem;
  margin-top: 0.3rem;
}

.cursor {
  animation: blink 1s steps(2) infinite;
}

@keyframes blink {
  50% {
    opacity: 0;
  }
}

.muted {
  color: var(--color-text);
  font-size: 0.85rem;
}

.error {
  color: var(--vt-c-red, #e53e3e);
  margin: 0.75rem 0 0;
}

.status-note {
  min-height: 1.25rem;
  margin: 0.5rem 0 0;
  font-size: 0.85rem;
  color: var(--color-text);
}

.composer {
  display: flex;
  gap: 0.5rem;
  margin-top: 0.75rem;
}

.composer input {
  flex: 1;
  padding: 0.5rem 0.75rem;
  border: 1px solid var(--color-border);
  border-radius: 4px;
  background-color: var(--color-background);
  color: var(--color-text);
}

.composer button {
  padding: 0.5rem 1.25rem;
  border-radius: 4px;
  border: 1px solid var(--color-border);
  background-color: var(--color-background);
  color: var(--color-text);
  cursor: pointer;
}

.composer button:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.composer .stop {
  border-color: var(--vt-c-red, #e53e3e);
}

.usage {
  display: flex;
  justify-content: space-between;
  gap: 1rem;
  flex-wrap: wrap;
  margin-top: 1rem;
  padding-top: 0.5rem;
  border-top: 1px solid var(--color-border);
  font-size: 0.85rem;
  color: var(--color-text);
}
</style>
