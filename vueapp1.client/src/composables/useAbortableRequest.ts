import { ref, computed, onScopeDispose } from 'vue';

/**
 * Prevents race conditions by aborting the previous request when a new one starts.
 * Useful for search-as-you-type, filter changes, or any async operation where
 * only the latest result matters.
 *
 * @example
 * ```ts
 * const { execute } = useAbortableRequest();
 *
 * async function onSearchInput(query: string) {
 *   const results = await execute((signal) => api.search(query, signal));
 *   if (results !== undefined) {
 *     items.value = results;
 *   }
 * }
 * ```
 */
export function useAbortableRequest() {
  let controller: AbortController | null = null;
  const activeCount = ref(0);
  const isActive = computed(() => activeCount.value > 0);

  function abort() {
    if (controller) {
      controller.abort();
      controller = null;
    }
  }

  async function execute<T>(fn: (signal: AbortSignal) => Promise<T>): Promise<T | undefined> {
    abort();
    controller = new AbortController();
    const { signal } = controller;

    activeCount.value++;
    try {
      const result = await fn(signal);
      if (!signal.aborted) return result;
      return undefined;
    } catch (err) {
      if (signal.aborted) return undefined;
      if (err instanceof Error && err.name === 'AbortError') return undefined;
      throw err;
    } finally {
      activeCount.value--;
    }
  }

  onScopeDispose(() => abort());

  return { execute, abort, isActive };
}
