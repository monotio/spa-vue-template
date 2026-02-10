/**
 * Build an API URL with optional query parameters.
 * Null/undefined values are omitted from the query string.
 */
export function apiUrl(
  path: string,
  params?: Record<string, string | number | boolean | undefined | null>,
): string {
  const url = new URL(path, window.location.origin);
  if (params) {
    for (const [key, value] of Object.entries(params)) {
      if (value != null) {
        url.searchParams.append(key, String(value));
      }
    }
  }
  return url.pathname + url.search;
}
