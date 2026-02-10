import { ref, computed } from 'vue';
import { ProblemError, StatusCodeError, isOfflineError } from '@/utils/errors';

type JsonPrimitive = string | number | boolean | null;
export type JsonValue = JsonPrimitive | JsonValue[] | { [key: string]: JsonValue };

/**
 * Composable providing type-safe fetch wrappers with automatic loading state tracking.
 *
 * - `isGetting` / `isSending` track GET vs mutating requests separately
 * - `isLoading` is true when any request is in flight
 * - All methods handle Problem Details (RFC 9457) responses automatically
 */
export function useFetch() {
  const getCounter = ref(0);
  const sendCounter = ref(0);

  const isGetting = computed(() => getCounter.value > 0);
  const isSending = computed(() => sendCounter.value > 0);
  const isLoading = computed(() => isGetting.value || isSending.value);

  async function handleResponse<TResponse>(response: Response): Promise<TResponse> {
    if (response.ok) {
      if (response.status === 204) return undefined as TResponse;
      return (await response.json()) as TResponse;
    }

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

  function wrapFetchError(error: unknown): never {
    if (error instanceof ProblemError || error instanceof StatusCodeError) throw error;
    if (isOfflineError(error)) throw new Error('Offline');
    throw error;
  }

  async function getJson<TResponse>(url: string, signal?: AbortSignal): Promise<TResponse> {
    getCounter.value++;
    try {
      const request: RequestInit = { credentials: 'same-origin' };
      if (signal !== undefined) {
        request.signal = signal;
      }

      const response = await fetch(url, request);
      return await handleResponse<TResponse>(response);
    } catch (error) {
      wrapFetchError(error);
    } finally {
      getCounter.value--;
    }
  }

  async function sendJson<TResponse>(
    url: string,
    method: 'POST' | 'PUT' | 'PATCH' | 'DELETE',
    body?: JsonValue,
    signal?: AbortSignal,
  ): Promise<TResponse> {
    sendCounter.value++;
    try {
      const request: RequestInit = {
        method,
        credentials: 'same-origin',
      };

      if (body !== undefined) {
        request.headers = { 'Content-Type': 'application/json' };
        request.body = JSON.stringify(body);
      }

      if (signal !== undefined) {
        request.signal = signal;
      }

      const response = await fetch(url, request);
      return await handleResponse<TResponse>(response);
    } catch (error) {
      wrapFetchError(error);
    } finally {
      sendCounter.value--;
    }
  }

  async function postJson<TResponse>(
    url: string,
    body?: JsonValue,
    signal?: AbortSignal,
  ): Promise<TResponse> {
    return sendJson<TResponse>(url, 'POST', body, signal);
  }

  async function putJson<TResponse>(
    url: string,
    body?: JsonValue,
    signal?: AbortSignal,
  ): Promise<TResponse> {
    return sendJson<TResponse>(url, 'PUT', body, signal);
  }

  async function patchJson<TResponse>(
    url: string,
    body?: JsonValue,
    signal?: AbortSignal,
  ): Promise<TResponse> {
    return sendJson<TResponse>(url, 'PATCH', body, signal);
  }

  async function deleteJson<TResponse>(url: string, signal?: AbortSignal): Promise<TResponse> {
    return sendJson<TResponse>(url, 'DELETE', undefined, signal);
  }

  return {
    isGetting,
    isSending,
    isLoading,
    getJson,
    postJson,
    putJson,
    patchJson,
    deleteJson,
  };
}
