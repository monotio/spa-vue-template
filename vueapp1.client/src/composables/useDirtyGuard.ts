import { toValue, type MaybeRefOrGetter } from 'vue';
import { onBeforeRouteLeave, useRouter } from 'vue-router';
import { useEventListener } from '@vueuse/core';
import { logger } from '@/utils/logger';

export interface UseDirtyGuardOptions {
  /**
   * Raise the confirm-discard affordance (open a dialog, etc.). The intended
   * navigation has been stashed: call `confirmLeave()` to discard the changes
   * and replay it, or `cancelLeave()` to stay on the page.
   */
  onNavigationBlocked: () => void;
}

export interface UseDirtyGuardReturn {
  /** Discard changes: replays the stashed navigation past the guard. */
  confirmLeave: () => void;
  /** Keep editing: drops the stashed navigation. */
  cancelLeave: () => void;
}

/**
 * Blocks navigation away from unsaved changes — the dialog UI stays yours.
 *
 * - In-app navigation: an `onBeforeRouteLeave` guard stashes the intended
 *   destination, raises `onNavigationBlocked`, and cancels. `confirmLeave()`
 *   replays the stashed navigation (the guard lets the replay through);
 *   `cancelLeave()` drops it. The replay is always a `push` of the stashed
 *   `fullPath` — `replace` semantics and history `state` of the original
 *   navigation are not preserved.
 * - Hard navigation (tab close, reload, external link): a `beforeunload`
 *   listener triggers the browser's native leave prompt. It is registered
 *   ONLY while dirty — a page with a `beforeunload` listener is ineligible
 *   for the back/forward cache, so an always-on listener would slow every
 *   back/forward navigation even with nothing to save.
 *
 * Must be called during the setup of a route-level component (it registers a
 * leave guard for the current route). See docs/FRONTEND.md for wiring.
 */
export function useDirtyGuard(
  isDirty: MaybeRefOrGetter<boolean>,
  { onNavigationBlocked }: UseDirtyGuardOptions,
): UseDirtyGuardReturn {
  const router = useRouter();
  let pendingNavigation: string | undefined;
  let bypassGuard = false;

  onBeforeRouteLeave((to) => {
    if (bypassGuard) {
      bypassGuard = false;
      return true;
    }
    if (!toValue(isDirty)) {
      return true;
    }
    pendingNavigation = to.fullPath;
    onNavigationBlocked();
    return false;
  });

  // Reactive target: while clean the getter yields undefined and no listener
  // exists (bfcache stays eligible); flipping dirty registers it, and scope
  // disposal removes it automatically.
  useEventListener(
    () => (toValue(isDirty) ? window : undefined),
    'beforeunload',
    (event) => {
      // preventDefault is the standard signal for the native leave prompt;
      // the legacy returnValue-string mechanism is deprecated.
      event.preventDefault();
    },
  );

  function confirmLeave(): void {
    const target = pendingNavigation;
    pendingNavigation = undefined;
    if (target === undefined) {
      return;
    }
    bypassGuard = true;
    router
      .push(target)
      .finally(() => {
        bypassGuard = false;
      })
      // push() resolves with a NavigationFailure for ordinary aborts but
      // REJECTS when another guard throws — never discard that rejection.
      .catch((error: unknown) => {
        logger.error('useDirtyGuard: replaying the blocked navigation failed', error);
      });
  }

  function cancelLeave(): void {
    pendingNavigation = undefined;
  }

  return { confirmLeave, cancelLeave };
}
