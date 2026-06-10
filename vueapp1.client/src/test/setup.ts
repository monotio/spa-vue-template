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
