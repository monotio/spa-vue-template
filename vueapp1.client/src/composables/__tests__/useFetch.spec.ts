import { describe, it, expect, vi } from 'vitest';
import { useFetch } from '../useFetch';
import { ProblemError, StatusCodeError, mapValidationErrors } from '@/utils/errors';

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

  // RFC 9457 classification at the fetch boundary: problem+json is
  // authoritative; plain application/json must EARN ProblemDetails treatment
  // by carrying at least one descriptive field — every JSON-speaking gateway
  // and proxy in front of the API also sends `{status, message}`-style
  // envelopes with a status code.
  describe('ProblemDetails classification', () => {
    it('surfaces a gateway error envelope message instead of misreading it as ProblemDetails', async () => {
      // Typical LB/proxy envelope: carries `status`, but none of the RFC 9457
      // descriptive fields — its message must reach the user, not vanish
      // behind a generic "Request failed".
      globalThis.fetch = vi.fn(() =>
        Promise.resolve(
          new Response(JSON.stringify({ status: 502, message: 'upstream connect error' }), {
            status: 502,
            statusText: 'Bad Gateway',
            headers: { 'Content-Type': 'application/json' },
          }),
        ),
      );

      const { getJson } = useFetch();
      const error = await getJson('/api/test').catch((e: unknown) => e);

      expect(error).toBeInstanceOf(StatusCodeError);
      expect((error as StatusCodeError).statusCode).toBe(502);
      expect((error as StatusCodeError).message).toBe('upstream connect error');
    });

    it('accepts plain application/json that carries RFC 9457 fields', async () => {
      // Some backends forget the problem+json media type but send a real
      // problem body — the descriptive fields are the tell.
      globalThis.fetch = vi.fn(() =>
        Promise.resolve(
          new Response(JSON.stringify({ status: 409, title: 'Edit conflict' }), {
            status: 409,
            headers: { 'Content-Type': 'application/json' },
          }),
        ),
      );

      const { getJson } = useFetch();
      const error = await getJson('/api/test').catch((e: unknown) => e);

      expect(error).toBeInstanceOf(ProblemError);
      expect((error as ProblemError).problem.title).toBe('Edit conflict');
    });

    it('treats application/problem+json as authoritative even without descriptive fields', async () => {
      globalThis.fetch = vi.fn(() =>
        Promise.resolve(
          new Response('{}', {
            status: 503,
            statusText: 'Service Unavailable',
            headers: { 'Content-Type': 'application/problem+json' },
          }),
        ),
      );

      const { getJson } = useFetch();
      const error = await getJson('/api/test').catch((e: unknown) => e);

      expect(error).toBeInstanceOf(ProblemError);
      // Normalization fills the gaps: status from HTTP, title from the
      // status text (both empty in the body).
      expect((error as ProblemError).problem.status).toBe(503);
      expect((error as ProblemError).problem.title).toBe('Service Unavailable');
      expect((error as ProblemError).message).toBe('Service Unavailable');
    });

    it('prefers the HTTP status over a contradicting body status', async () => {
      // RFC 9457 §3.1: the body's `status` is advisory; the HTTP status
      // is what actually happened (proxies rewrite codes, bodies go stale).
      globalThis.fetch = vi.fn(() =>
        Promise.resolve(
          new Response(JSON.stringify({ status: 500, title: 'Stale status' }), {
            status: 503,
            headers: { 'Content-Type': 'application/problem+json' },
          }),
        ),
      );

      const { getJson } = useFetch();
      const error = await getJson('/api/test').catch((e: unknown) => e);

      expect((error as ProblemError).problem.status).toBe(503);
    });

    it('preserves RFC 9457 extension members', async () => {
      globalThis.fetch = vi.fn(() =>
        Promise.resolve(
          new Response(JSON.stringify({ title: 'Boom', traceId: '00-abc-01', balance: 30 }), {
            status: 500,
            headers: { 'Content-Type': 'application/problem+json' },
          }),
        ),
      );

      const { getJson } = useFetch();
      const error = await getJson('/api/test').catch((e: unknown) => e);

      expect((error as ProblemError).problem['traceId']).toBe('00-abc-01');
      expect((error as ProblemError).problem['balance']).toBe(30);
    });

    it('does not backfill title from statusText when the body has a detail', async () => {
      // A real detail must keep its spotlight: backfilling "Service
      // Unavailable" over it would bury the actionable text.
      globalThis.fetch = vi.fn(() =>
        Promise.resolve(
          new Response(JSON.stringify({ detail: 'Database connection pool exhausted' }), {
            status: 503,
            statusText: 'Service Unavailable',
            headers: { 'Content-Type': 'application/problem+json' },
          }),
        ),
      );

      const { getJson } = useFetch();
      const error = await getJson('/api/test').catch((e: unknown) => e);

      expect((error as ProblemError).problem.title).toBeUndefined();
      expect((error as ProblemError).problem.detail).toBe('Database connection pool exhausted');
    });

    it('keeps validation problems mappable to form fields', async () => {
      globalThis.fetch = vi.fn(() =>
        Promise.resolve(
          new Response(
            JSON.stringify({
              status: 400,
              title: 'Validation failed',
              errors: { Name: ['Name is required'] },
            }),
            { status: 400, headers: { 'Content-Type': 'application/problem+json' } },
          ),
        ),
      );

      const { postJson } = useFetch();
      const error = await postJson('/api/test', {}).catch((e: unknown) => e);

      const setFieldError = vi.fn();
      expect(mapValidationErrors(error, { Name: 'name' }, setFieldError)).toBe(true);
      expect(setFieldError).toHaveBeenCalledWith('name', ['Name is required']);
    });
  });
});
