import { describe, it, expect, vi } from 'vitest';
import { useFetch } from '../useFetch';
import { ProblemError, StatusCodeError } from '@/utils/errors';

describe('useFetch', () => {
  it('tracks loading state for GET requests', async () => {
    let resolveFetch!: (value: Response) => void;
    globalThis.fetch = vi.fn(() => new Promise<Response>((resolve) => (resolveFetch = resolve)));

    const { getJson, isGetting, isSending, isLoading } = useFetch();

    expect(isGetting.value).toBe(false);
    expect(isLoading.value).toBe(false);

    const promise = getJson('/api/test');

    expect(isGetting.value).toBe(true);
    expect(isSending.value).toBe(false);
    expect(isLoading.value).toBe(true);

    resolveFetch(new Response(JSON.stringify({ ok: true }), { status: 200 }));
    await promise;

    expect(isGetting.value).toBe(false);
    expect(isLoading.value).toBe(false);
  });

  it('tracks loading state for POST requests', async () => {
    globalThis.fetch = vi.fn(() =>
      Promise.resolve(new Response(JSON.stringify({ id: 1 }), { status: 200 })),
    );

    const { postJson, isSending, isGetting } = useFetch();

    const result = await postJson<{ id: number }>('/api/test', { name: 'test' });

    expect(result).toEqual({ id: 1 });
    expect(isSending.value).toBe(false);
    expect(isGetting.value).toBe(false);
  });

  it('returns parsed JSON on success', async () => {
    const data = [{ id: 1 }, { id: 2 }];
    globalThis.fetch = vi.fn(() =>
      Promise.resolve(new Response(JSON.stringify(data), { status: 200 })),
    );

    const { getJson } = useFetch();
    const result = await getJson<typeof data>('/api/items');

    expect(result).toEqual(data);
  });

  it('throws ProblemError for 400 with Problem Details body', async () => {
    const problem = { status: 400, title: 'Validation failed', detail: 'Name is required' };
    globalThis.fetch = vi.fn(() =>
      Promise.resolve(
        new Response(JSON.stringify(problem), {
          status: 400,
          headers: { 'Content-Type': 'application/problem+json' },
        }),
      ),
    );

    const { getJson } = useFetch();

    await expect(getJson('/api/test')).rejects.toThrow(ProblemError);
  });

  it('throws StatusCodeError for non-Problem Details errors', async () => {
    globalThis.fetch = vi.fn(() =>
      Promise.resolve(
        new Response('Server error', { status: 500, statusText: 'Internal Server Error' }),
      ),
    );

    const { getJson } = useFetch();

    await expect(getJson('/api/test')).rejects.toThrow(StatusCodeError);
  });

  it('throws Offline error for network failures', async () => {
    globalThis.fetch = vi.fn(() => Promise.reject(new TypeError('Failed to fetch')));

    const { getJson } = useFetch();

    await expect(getJson('/api/test')).rejects.toThrow('Offline');
  });

  it('handles 204 No Content', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(new Response(null, { status: 204 })));

    const { deleteJson } = useFetch();
    const result = await deleteJson('/api/items/1');

    expect(result).toBeUndefined();
  });
});
