<script setup lang="ts">
// One chip per attachment, shared by the composer (removable, pre-send) and
// the transcript (display-only, fed by local sends and replayed `file`
// parts). Pure renderer — uploads and references are owned by useAgentStream.
const {
  fileName,
  mediaType,
  removable = false,
} = defineProps<{
  fileName: string;
  mediaType: string;
  removable?: boolean;
}>();

const emit = defineEmits<{ remove: [] }>();
</script>

<template>
  <span class="attachment-chip">
    <span class="chip-name">{{ fileName }}</span>
    <span class="chip-type">{{ mediaType }}</span>
    <button
      v-if="removable"
      type="button"
      class="chip-remove"
      :aria-label="`Remove attachment ${fileName}`"
      @click="emit('remove')"
    >
      &times;
    </button>
  </span>
</template>

<style scoped>
.attachment-chip {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
  max-width: 100%;
  border: 1px solid var(--color-border);
  border-radius: 999px;
  padding: 0.15rem 0.6rem;
  font-size: 0.8rem;
}

.chip-name {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  max-width: 14rem;
}

.chip-type {
  color: var(--color-text);
  opacity: 0.7;
  white-space: nowrap;
}

.chip-remove {
  border: none;
  background: none;
  color: var(--color-text);
  cursor: pointer;
  font-size: 1rem;
  line-height: 1;
  padding: 0 0.1rem;
}
</style>
