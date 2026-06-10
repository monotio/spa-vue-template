<script setup lang="ts">
import { useRegisterSW } from 'virtual:pwa-register/vue';

// Re-check for a new service worker once an hour, so long-lived tabs learn
// about deployments without a manual reload.
const UPDATE_INTERVAL_MS = 60 * 60 * 1000;

const { offlineReady, needRefresh, updateServiceWorker } = useRegisterSW({
  onRegisteredSW(swUrl, registration) {
    if (!registration) {
      return;
    }
    setInterval(() => {
      void (async () => {
        if (registration.installing || !navigator.onLine) {
          return;
        }
        // Bypass the HTTP cache so a stale sw.js never masks an update.
        const response = await fetch(swUrl, {
          cache: 'no-store',
          headers: { 'cache-control': 'no-cache' },
        });
        if (response.status === 200) {
          await registration.update();
        }
      })();
    }, UPDATE_INTERVAL_MS);
  },
});

function close(): void {
  offlineReady.value = false;
  needRefresh.value = false;
}
</script>

<template>
  <div v-if="offlineReady || needRefresh" class="pwa-toast" role="alert" aria-live="assertive">
    <span v-if="offlineReady">Ready to work offline.</span>
    <span v-else>A new version is available.</span>
    <button v-if="needRefresh" type="button" @click="updateServiceWorker()">Reload</button>
    <button type="button" @click="close">Close</button>
  </div>
</template>

<style scoped>
.pwa-toast {
  position: fixed;
  right: 1rem;
  bottom: 1rem;
  display: flex;
  align-items: center;
  gap: 0.75rem;
  padding: 0.75rem 1rem;
  border: 1px solid var(--color-border);
  border-radius: 0.5rem;
  background-color: var(--color-background);
  box-shadow: 0 2px 8px rgb(0 0 0 / 15%);
  z-index: 100;
}

.pwa-toast button {
  cursor: pointer;
}
</style>
