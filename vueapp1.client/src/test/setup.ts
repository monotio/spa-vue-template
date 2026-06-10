import { vi } from 'vitest';

// Safe defaults for router composables so component/composable tests don't
// need a real router. Components (RouterLink/RouterView) stay real — stub
// them per test via mount options when needed:
//   mount(Comp, { global: { stubs: { RouterLink: true } } })
vi.mock('vue-router', async (importOriginal) => {
  const actual = await importOriginal<typeof import('vue-router')>();
  return {
    ...actual,
    useRouter: vi.fn(() => ({
      push: vi.fn(),
      replace: vi.fn(),
      back: vi.fn(),
      afterEach: vi.fn(),
      beforeEach: vi.fn(),
    })),
    useRoute: vi.fn(() => ({
      path: '/',
      name: undefined,
      params: {},
      query: {},
      hash: '',
      fullPath: '/',
      matched: [],
      meta: {},
      redirectedFrom: undefined,
    })),
  };
});

// Filter known-noisy console output. Keep the allowlist EXPLICIT — adding a
// pattern is a conscious decision, never silent. Empty by default.
const silencedConsolePatterns: RegExp[] = [
  // example: /Download the Vue Devtools extension/,
];
for (const method of ['warn', 'error'] as const) {
  const original = console[method].bind(console);
  console[method] = (...args: unknown[]) => {
    const first = typeof args[0] === 'string' ? args[0] : '';
    if (silencedConsolePatterns.some((pattern) => pattern.test(first))) {
      return;
    }
    original(...args);
  };
}

// Deterministic in-memory Web Storage for tests, installed unconditionally.
// Depending on the runtime, `globalThis.localStorage` is provided by jsdom or by
// Node's built-in Web Storage (Node >= 22), which logs "--localstorage-file"
// warnings when touched without a backing file. Always overriding with a
// Map-based shim keeps storage behavior identical across Node versions and
// guarantees a clean slate per test process.
const data = new Map<string, string>();

const storage: Storage = {
  get length() {
    return data.size;
  },
  clear() {
    data.clear();
  },
  getItem(key: string) {
    return data.get(key) ?? null;
  },
  key(index: number) {
    return Array.from(data.keys())[index] ?? null;
  },
  removeItem(key: string) {
    data.delete(key);
  },
  setItem(key: string, value: string) {
    data.set(key, value);
  },
};

Object.defineProperty(globalThis, 'localStorage', {
  value: storage,
  configurable: true,
  writable: false,
});
