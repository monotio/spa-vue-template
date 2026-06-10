import { effectScope, type EffectScope } from 'vue';

export interface WithSetupResult<T> {
  result: T;
  scope: EffectScope;
  /** Stops the scope — runs onScopeDispose handlers, kills watchers. */
  unmount: () => void;
}

/**
 * Runs a composable inside a real effect scope, so lifecycle behavior
 * (watchers, computed, onScopeDispose) works exactly as it would inside a
 * component — and can be asserted by calling unmount().
 *
 * ```ts
 * const { result, unmount } = withSetup(() => useFetch<Data>('/api/data'));
 * onTestFinished(unmount);
 * ```
 */
export function withSetup<T>(composable: () => T): WithSetupResult<T> {
  const scope = effectScope();
  const result = scope.run(composable) as T;
  return { result, scope, unmount: () => scope.stop() };
}
