<script setup lang="ts">
import { nextTick, useTemplateRef } from 'vue';
import { useRouter } from 'vue-router';
import ReloadPrompt from '@/components/ReloadPrompt.vue';

const main = useTemplateRef('main');
const router = useRouter();

// SPA navigations don't reload the page, so without this, keyboard and
// screen-reader focus silently stays on the old page's content. Moving focus
// to the main landmark after each route change restores document-like behavior.
router.afterEach(async (to, from) => {
  if (from.matched.length === 0 || to.path === from.path) {
    return; // initial navigation or same-page change: don't steal focus
  }
  await nextTick();
  main.value?.focus();
});
</script>

<template>
  <a class="skip-link" href="#main-content">Skip to content</a>

  <header>
    <nav>
      <RouterLink to="/"> Home </RouterLink>
      <RouterLink to="/weather"> Weather </RouterLink>
      <RouterLink to="/agent"> Agent </RouterLink>
    </nav>
  </header>

  <main id="main-content" ref="main" tabindex="-1">
    <RouterView />
  </main>

  <ReloadPrompt />
</template>

<style scoped>
.skip-link {
  position: absolute;
  left: -9999px;
  top: 0;
  padding: 0.5rem 1rem;
  background-color: var(--color-background);
  z-index: 200;
}

.skip-link:focus {
  left: 0;
}

main:focus {
  outline: none;
}

header {
  border-bottom: 1px solid var(--color-border);
  margin-bottom: 2rem;
  padding-bottom: 1rem;
}

nav {
  display: flex;
  gap: 1.5rem;
}

nav a {
  font-weight: 500;
}

nav a.router-link-active {
  color: var(--color-heading);
}
</style>
