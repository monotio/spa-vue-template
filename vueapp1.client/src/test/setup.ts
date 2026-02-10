const hasStorageApi =
  'localStorage' in globalThis && typeof globalThis.localStorage?.getItem === 'function';

if (!hasStorageApi) {
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
}
