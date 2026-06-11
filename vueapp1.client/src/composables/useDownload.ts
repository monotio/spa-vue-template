import { ref, computed } from 'vue';
import { ProblemError, StatusCodeError, isOfflineError } from '@/utils/errors';

/**
 * Extracts the server-suggested filename from a Content-Disposition header.
 *
 * Per RFC 6266 §4.3 the extended `filename*` parameter (RFC 5987 encoding,
 * e.g. `filename*=UTF-8''na%C3%AFve.csv`) takes precedence over the plain
 * `filename` parameter — it is the only interoperable way to transport
 * non-ASCII filenames. Malformed percent-encoding falls back gracefully to
 * the plain parameter instead of throwing.
 */
export function parseContentDispositionFilename(header: string | null): string | undefined {
  if (header === null || header === '') {
    return undefined;
  }

  // filename*=charset'language'percent-encoded — only UTF-8 is interoperable
  // (and the only charset RFC 5987 requires); anything else falls through.
  const extended = /filename\*\s*=\s*([^';]+)'[^']*'([^;]+)/i.exec(header);
  const charset = extended?.[1]?.trim();
  const encodedValue = extended?.[2]?.trim();
  if (charset !== undefined && encodedValue !== undefined && /^utf-8$/i.test(charset)) {
    try {
      return decodeURIComponent(encodedValue);
    } catch {
      // Malformed percent-encoding — fall back to the plain parameter.
    }
  }

  // filename="quoted string" (backslash-escaped quotes per RFC 7230 §3.2.6).
  const quoted = /filename\s*=\s*"((?:[^"\\]|\\.)*)"/i.exec(header);
  if (quoted?.[1] !== undefined) {
    return quoted[1].replace(/\\(.)/g, '$1');
  }

  // filename=unquoted-token
  return /filename\s*=\s*([^;\s]+)/i.exec(header)?.[1];
}

export interface DownloadOptions {
  /** Used when the response carries no parsable Content-Disposition filename. */
  fallbackFilename?: string;
  signal?: AbortSignal;
  /** Request overrides for non-GET exports (method, headers, body). */
  init?: RequestInit;
}

/**
 * Fetches a file and hands it to the browser's download UI, honoring the
 * server-suggested filename from Content-Disposition.
 *
 * For plain same-origin GET exports you don't need this — a simple
 * `<a href="/api/report" download>` lets the browser parse the header itself
 * (see docs/FRONTEND.md). Reach for the composable when the export is a
 * POST, needs auth headers, or should surface ProblemDetails errors in-app
 * instead of navigating to an error body.
 */
export function useDownload() {
  const activeCount = ref(0);
  const isDownloading = computed(() => activeCount.value > 0);

  async function download(url: string, options: DownloadOptions = {}): Promise<void> {
    activeCount.value++;
    try {
      const request: RequestInit = { credentials: 'same-origin', ...options.init };
      if (options.signal !== undefined) {
        request.signal = options.signal;
      }

      let response: Response;
      try {
        response = await fetch(url, request);
      } catch (error) {
        if (isOfflineError(error)) {
          throw new Error('Offline');
        }
        throw error;
      }

      if (!response.ok) {
        return await throwResponseError(response);
      }

      const blob = await response.blob();
      const filename =
        parseContentDispositionFilename(response.headers.get('content-disposition')) ??
        options.fallbackFilename ??
        'download';
      triggerAnchorDownload(blob, filename);
    } finally {
      activeCount.value--;
    }
  }

  return { isDownloading, download };
}

// Failed exports come back as ProblemDetails JSON, not a downloadable body —
// mirrors the error path of useFetch's handleResponse (which stays private:
// useFetch is deliberately JSON-only).
async function throwResponseError(response: Response): Promise<never> {
  const contentType = response.headers.get('content-type');
  if (
    contentType?.includes('application/problem+json') ||
    contentType?.includes('application/json')
  ) {
    const text = await response.text();
    try {
      throw new ProblemError(JSON.parse(text));
    } catch (e) {
      if (e instanceof ProblemError) throw e;
      throw new StatusCodeError(response.status, text, response.headers);
    }
  }

  throw new StatusCodeError(
    response.status,
    `${response.status} ${response.statusText}`,
    response.headers,
  );
}

// Anchor + temporary object URL is the portable save mechanism: the File
// System Access API save picker is still Chromium-only.
function triggerAnchorDownload(blob: Blob, filename: string): void {
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = filename;
  anchor.rel = 'noopener';
  document.body.appendChild(anchor); // Firefox requires the anchor to be in the DOM
  anchor.click();
  anchor.remove();
  // Revoke OUTSIDE the click task: modern browsers capture the blob at
  // navigation start, but some older Firefox/Safari releases aborted the
  // save when the URL was revoked before the download dereferenced it.
  // Deferring costs nothing and removes the compat question mark.
  setTimeout(() => URL.revokeObjectURL(url), 0);
}
