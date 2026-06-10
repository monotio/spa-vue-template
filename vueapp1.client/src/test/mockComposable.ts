import { vi, type Mock } from 'vitest';

export interface MockedComposable<T> {
  /** Pass as the composable in a vi.mock factory. */
  mock: Mock<() => T>;
  /** Clears the cached instance and call history (use in beforeEach). */
  reset: () => void;
}

/**
 * Caches a mock factory so every invocation of the mocked composable returns
 * the SAME instance — without caching, the component under test and your
 * assertions each get a fresh object and the assertions see zero calls.
 *
 * ```ts
 * const fetchMock = createMockedComposable(() => ({
 *   data: ref(null),
 *   isLoading: ref(false),
 *   execute: vi.fn(),
 * }));
 * vi.mock('@/composables/useFetch', () => ({ useFetch: fetchMock.mock }));
 * beforeEach(fetchMock.reset);
 * ```
 */
export function createMockedComposable<T>(factory: () => T): MockedComposable<T> {
  let instance: T | undefined;
  const mock = vi.fn(() => (instance ??= factory()));
  return {
    mock,
    reset: () => {
      instance = undefined;
      mock.mockClear();
    },
  };
}
