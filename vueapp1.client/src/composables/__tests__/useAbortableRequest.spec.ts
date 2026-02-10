import { describe, it, expect } from 'vitest';
import { effectScope } from 'vue';
import { useAbortableRequest } from '../useAbortableRequest';

describe('useAbortableRequest', () => {
  it('returns the result of a successful request', async () => {
    const scope = effectScope();
    const { execute } = scope.run(() => useAbortableRequest())!;

    const result = await execute(() => Promise.resolve('data'));
    expect(result).toBe('data');

    scope.stop();
  });

  it('aborts previous request when a new one starts', async () => {
    const scope = effectScope();
    const { execute } = scope.run(() => useAbortableRequest())!;

    const slow = execute(
      (signal) =>
        new Promise<string>((resolve, reject) => {
          signal.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')));
          setTimeout(() => resolve('slow'), 1000);
        }),
    );

    const fast = execute(() => Promise.resolve('fast'));

    const [slowResult, fastResult] = await Promise.all([slow, fast]);
    expect(slowResult).toBeUndefined();
    expect(fastResult).toBe('fast');

    scope.stop();
  });

  it('re-throws non-abort errors', async () => {
    const scope = effectScope();
    const { execute } = scope.run(() => useAbortableRequest())!;

    await expect(execute(() => Promise.reject(new Error('network error')))).rejects.toThrow(
      'network error',
    );

    scope.stop();
  });

  it('tracks active state', async () => {
    const scope = effectScope();
    const composable = scope.run(() => useAbortableRequest())!;

    expect(composable.isActive.value).toBe(false);

    let resolveFn!: (v: string) => void;
    const promise = composable.execute(() => new Promise<string>((r) => (resolveFn = r)));

    expect(composable.isActive.value).toBe(true);

    resolveFn('done');
    await promise;

    expect(composable.isActive.value).toBe(false);
    scope.stop();
  });

  it('aborts on scope dispose', () => {
    const scope = effectScope();
    let capturedSignal: AbortSignal | undefined;

    scope.run(() => {
      const { execute } = useAbortableRequest();
      void execute(
        (signal) =>
          new Promise(() => {
            capturedSignal = signal;
          }),
      );
    });

    expect(capturedSignal!.aborted).toBe(false);
    scope.stop();
    expect(capturedSignal!.aborted).toBe(true);
  });
});
