import { describe, it, expect, vi } from 'vitest';
import { ref, nextTick } from 'vue';
import type { NavigationGuard, RouteLocationNormalized } from 'vue-router';
import { withSetup } from '@/test/withSetup';
import { useDirtyGuard } from '../useDirtyGuard';

// Replace the global setup.ts router mock entirely for this file: the guard
// registered via onBeforeRouteLeave is captured so tests can invoke it like
// the router would.
const { onBeforeRouteLeaveMock, pushMock } = vi.hoisted(() => ({
  onBeforeRouteLeaveMock: vi.fn(),
  pushMock: vi.fn(),
}));

vi.mock('vue-router', () => ({
  onBeforeRouteLeave: onBeforeRouteLeaveMock,
  useRouter: () => ({ push: pushMock }),
}));

function makeTo(fullPath: string): RouteLocationNormalized {
  return { fullPath } as RouteLocationNormalized;
}

function setup(isDirty: () => boolean) {
  pushMock.mockResolvedValue(undefined);
  const onNavigationBlocked = vi.fn();
  const { result, unmount } = withSetup(() => useDirtyGuard(isDirty, { onNavigationBlocked }));
  const lastCall = onBeforeRouteLeaveMock.mock.calls.at(-1) as [NavigationGuard];
  const guard = (to: RouteLocationNormalized) =>
    (lastCall[0] as (to: RouteLocationNormalized) => boolean)(to);
  return { result, unmount, guard, onNavigationBlocked };
}

describe('useDirtyGuard', () => {
  it('allows navigation when there are no unsaved changes', () => {
    const { guard, onNavigationBlocked, unmount } = setup(() => false);

    expect(guard(makeTo('/elsewhere'))).toBe(true);
    expect(onNavigationBlocked).not.toHaveBeenCalled();
    unmount();
  });

  it('blocks navigation while dirty and raises the confirm affordance', () => {
    const { guard, onNavigationBlocked, unmount } = setup(() => true);

    expect(guard(makeTo('/elsewhere'))).toBe(false);
    expect(onNavigationBlocked).toHaveBeenCalledTimes(1);
    expect(pushMock).not.toHaveBeenCalled();
    unmount();
  });

  it('replays the stashed navigation on confirmLeave, past the still-dirty guard', async () => {
    const { result, guard, unmount } = setup(() => true);

    guard(makeTo('/elsewhere?tab=2'));
    result.confirmLeave();

    expect(pushMock).toHaveBeenCalledWith('/elsewhere?tab=2');
    // The replayed navigation passes even though the state is still dirty...
    expect(guard(makeTo('/elsewhere?tab=2'))).toBe(true);
    await Promise.resolve(); // let the replay's finally settle
    // ...but the bypass is single-use: the next navigation blocks again.
    expect(guard(makeTo('/other'))).toBe(false);
    unmount();
  });

  it('drops the stashed navigation on cancelLeave', () => {
    const { result, guard, unmount } = setup(() => true);

    guard(makeTo('/elsewhere'));
    result.cancelLeave();
    result.confirmLeave();

    expect(pushMock).not.toHaveBeenCalled();
    unmount();
  });

  it('confirmLeave without a stashed navigation is a no-op', () => {
    const { result, unmount } = setup(() => true);

    result.confirmLeave();

    expect(pushMock).not.toHaveBeenCalled();
    unmount();
  });

  it('registers the beforeunload listener only while dirty (bfcache)', async () => {
    const isDirty = ref(false);
    const { unmount } = setup(() => isDirty.value);

    const dispatch = () => {
      const event = new Event('beforeunload', { cancelable: true });
      window.dispatchEvent(event);
      return event.defaultPrevented;
    };

    expect(dispatch()).toBe(false); // clean: no listener registered

    isDirty.value = true;
    await nextTick();
    expect(dispatch()).toBe(true); // dirty: native leave prompt triggered

    isDirty.value = false;
    await nextTick();
    expect(dispatch()).toBe(false); // clean again: listener removed

    unmount();
  });

  it('removes the beforeunload listener on scope dispose', async () => {
    const isDirty = ref(true);
    const { unmount } = setup(() => isDirty.value);
    await nextTick();

    unmount();

    const event = new Event('beforeunload', { cancelable: true });
    window.dispatchEvent(event);
    expect(event.defaultPrevented).toBe(false);
  });
});
