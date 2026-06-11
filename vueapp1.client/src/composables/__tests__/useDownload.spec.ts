import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { ProblemError, StatusCodeError } from '@/utils/errors';
import { parseContentDispositionFilename, useDownload } from '../useDownload';

describe('parseContentDispositionFilename', () => {
  it.each([
    ['attachment', undefined],
    [`attachment; filename="report.csv"`, 'report.csv'],
    [`attachment; filename=report.csv`, 'report.csv'],
    [`attachment; filename="with \\"quotes\\".csv"`, 'with "quotes".csv'],
    // RFC 5987 extended parameter wins over the plain fallback
    [`attachment; filename="fallback.csv"; filename*=UTF-8''na%C3%AFve.csv`, 'naïve.csv'],
    [`attachment; filename*=utf-8'en'rapport%20%E2%82%AC.pdf; filename="r.pdf"`, 'rapport €.pdf'],
    // Malformed percent-encoding degrades gracefully to the plain parameter
    [`attachment; filename*=UTF-8''bad%E0%A4%A; filename="safe.csv"`, 'safe.csv'],
    // Non-UTF-8 charsets are not interoperable; use the plain parameter
    [`attachment; filename*=ISO-8859-1''caf%E9.txt; filename="cafe.txt"`, 'cafe.txt'],
  ])('parses %s -> %s', (header, expected) => {
    expect(parseContentDispositionFilename(header)).toBe(expected);
  });

  it('returns undefined for a missing header', () => {
    expect(parseContentDispositionFilename(null)).toBeUndefined();
    expect(parseContentDispositionFilename('')).toBeUndefined();
  });
});

describe('useDownload', () => {
  let clickedAnchor: HTMLAnchorElement | undefined;
  const revokeObjectURL = vi.fn();

  beforeEach(() => {
    clickedAnchor = undefined;
    // jsdom implements neither object URLs nor real downloads — stub the seam
    // and capture the anchor the composable creates and clicks.
    URL.createObjectURL = vi.fn(() => 'blob:vitest');
    URL.revokeObjectURL = revokeObjectURL;
    const createElement = document.createElement.bind(document);
    vi.spyOn(document, 'createElement').mockImplementation((tagName: string) => {
      const element = createElement(tagName);
      if (element instanceof HTMLAnchorElement) {
        vi.spyOn(element, 'click').mockImplementation(() => {
          clickedAnchor = element;
        });
      }
      return element;
    });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('downloads a blob using the Content-Disposition filename', async () => {
    vi.useFakeTimers();
    globalThis.fetch = vi.fn(() =>
      Promise.resolve(
        new Response(new Blob(['a;b;c']), {
          status: 200,
          headers: { 'Content-Disposition': `attachment; filename*=UTF-8''m%C3%A4tdata.csv` },
        }),
      ),
    );

    const { download } = useDownload();
    await download('/api/export');

    expect(clickedAnchor?.download).toBe('mätdata.csv');
    expect(clickedAnchor?.href).toBe('blob:vitest');
    expect(document.querySelector('a[download]')).toBeNull(); // anchor cleaned up
    // The object URL is revoked OUTSIDE the click task (browser-compat
    // safety) — not yet at this point, then on the next timer tick.
    expect(revokeObjectURL).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(0);
    expect(revokeObjectURL).toHaveBeenCalledWith('blob:vitest');
  });

  it('falls back to the provided filename, then to "download"', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(new Response(new Blob(['x']))));

    const { download } = useDownload();
    await download('/api/export', { fallbackFilename: 'export.csv' });
    expect(clickedAnchor?.download).toBe('export.csv');

    await download('/api/export');
    expect(clickedAnchor?.download).toBe('download');
  });

  it('throws ProblemError for a failed export with a ProblemDetails body', async () => {
    const problem = { status: 403, title: 'Export not allowed' };
    globalThis.fetch = vi.fn(() =>
      Promise.resolve(
        new Response(JSON.stringify(problem), {
          status: 403,
          headers: { 'Content-Type': 'application/problem+json' },
        }),
      ),
    );

    const { download } = useDownload();
    const error = await download('/api/export').catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ProblemError);
    expect((error as ProblemError).problem).toEqual(problem);
    expect(clickedAnchor).toBeUndefined();
  });

  it('surfaces a gateway error envelope message instead of misreading it as ProblemDetails', async () => {
    // Mirrors useFetch's classification: `{status, message}` envelopes from
    // gateways/proxies are NOT ProblemDetails — their message must surface.
    globalThis.fetch = vi.fn(() =>
      Promise.resolve(
        new Response(JSON.stringify({ status: 502, message: 'upstream connect error' }), {
          status: 502,
          headers: { 'Content-Type': 'application/json' },
        }),
      ),
    );

    const { download } = useDownload();
    const error = await download('/api/export').catch((e: unknown) => e);

    expect(error).toBeInstanceOf(StatusCodeError);
    expect((error as StatusCodeError).message).toBe('upstream connect error');
  });

  it('throws StatusCodeError for a non-JSON failure', async () => {
    globalThis.fetch = vi.fn(() =>
      Promise.resolve(new Response('gateway timeout', { status: 504 })),
    );

    const { download } = useDownload();
    const error = await download('/api/export').catch((e: unknown) => e);

    expect(error).toBeInstanceOf(StatusCodeError);
    expect((error as StatusCodeError).statusCode).toBe(504);
  });

  it('maps connectivity failures to the Offline error', async () => {
    globalThis.fetch = vi.fn(() => Promise.reject(new TypeError('Failed to fetch')));

    const { download } = useDownload();
    await expect(download('/api/export')).rejects.toThrow('Offline');
  });

  it('passes through abort signals and request overrides', async () => {
    const fetchMock = vi.fn(() => Promise.resolve(new Response(new Blob(['x']))));
    globalThis.fetch = fetchMock;
    const controller = new AbortController();

    const { download } = useDownload();
    await download('/api/export', {
      signal: controller.signal,
      init: { method: 'POST', body: JSON.stringify({ year: 2026 }) },
    });

    const [, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(init.method).toBe('POST');
    expect(init.signal).toBe(controller.signal);
    expect(init.credentials).toBe('same-origin');
  });

  it('tracks isDownloading while the request is in flight', async () => {
    let resolveFetch!: (value: Response) => void;
    globalThis.fetch = vi.fn(() => new Promise<Response>((resolve) => (resolveFetch = resolve)));

    const { download, isDownloading } = useDownload();
    expect(isDownloading.value).toBe(false);

    const promise = download('/api/export');
    expect(isDownloading.value).toBe(true);

    resolveFetch(new Response(new Blob(['x'])));
    await promise;
    expect(isDownloading.value).toBe(false);
  });
});
