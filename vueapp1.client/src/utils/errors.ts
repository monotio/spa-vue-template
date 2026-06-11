export interface ProblemDetails {
  // `| undefined` is explicit (exactOptionalPropertyTypes): normalization in
  // createResponseError assigns these fields even when absent from the wire.
  readonly type?: string | undefined;
  readonly title?: string | undefined;
  readonly status?: number | undefined;
  readonly detail?: string | undefined;
  readonly instance?: string | undefined;
  readonly errors?: Readonly<Record<string, readonly string[]>> | undefined;
  /** RFC 9457 §3.2: extension members (traceId, domain data) travel along. */
  readonly [extension: string]: unknown;
}

export class ProblemError extends Error {
  override readonly name = 'ProblemError';
  readonly problem: ProblemDetails;

  constructor(problem: ProblemDetails, message?: string) {
    super(message ?? problem.title ?? 'Request failed');
    this.problem = problem;
  }
}

export class StatusCodeError extends Error {
  override readonly name = 'StatusCodeError';
  readonly statusCode: number;
  readonly headers: Headers;

  constructor(statusCode: number, message: string, headers: Headers) {
    super(message);
    this.statusCode = statusCode;
    this.headers = headers;
  }
}

export function isOfflineError(error: unknown): boolean {
  return (
    error instanceof TypeError &&
    (error.message.includes('Failed to fetch') || error.message.includes('NetworkError'))
  );
}

const PROBLEM_DETAIL_FIELDS = ['type', 'title', 'detail', 'instance'] as const;

function asString(value: unknown): string | undefined {
  return typeof value === 'string' ? value : undefined;
}

function parseJsonObject(text: string): Record<string, unknown> | undefined {
  try {
    const parsed: unknown = JSON.parse(text);
    if (typeof parsed === 'object' && parsed !== null && !Array.isArray(parsed)) {
      return parsed as Record<string, unknown>;
    }
  } catch {
    // Not JSON despite the content type — the caller falls back to the text.
  }
  return undefined;
}

function hasProblemDetailField(body: Record<string, unknown>): boolean {
  return PROBLEM_DETAIL_FIELDS.some((field) => typeof body[field] === 'string');
}

function normalizeProblem(body: Record<string, unknown>, response: Response): ProblemDetails {
  const detail = asString(body['detail']);
  // statusText backfills the title ONLY when the body offers neither title
  // nor detail — the generic reason phrase must never outshine real text.
  const title =
    asString(body['title']) ??
    (detail === undefined && response.statusText !== '' ? response.statusText : undefined);

  return {
    // RFC 9457 §3.2: extension members travel with the problem.
    ...body,
    type: asString(body['type']),
    title,
    // RFC 9457 §3.1: the body's status is advisory — the HTTP status is what
    // actually happened (proxies rewrite codes, bodies go stale).
    status: response.status,
    detail,
    instance: asString(body['instance']),
    // Same trust level as the consumer had before: mapValidationErrors
    // guards status and presence before iterating.
    errors: body['errors'] as ProblemDetails['errors'],
  };
}

/**
 * Classifies a non-OK Response into the template's typed errors per RFC 9457:
 *
 * - `application/problem+json` is authoritative — always a `ProblemError`.
 * - Plain `application/json` must carry at least one RFC 9457 descriptive
 *   field (type/title/detail/instance) to count: gateways and proxies send
 *   `{status, message}` envelopes too, and a status code alone proves
 *   nothing.
 * - Everything else is a `StatusCodeError` — surfacing a JSON envelope's
 *   `message` rather than discarding it.
 */
export async function createResponseError(
  response: Response,
): Promise<ProblemError | StatusCodeError> {
  const contentType = response.headers.get('content-type') ?? '';
  const isProblemJson = contentType.includes('application/problem+json');

  if (isProblemJson || contentType.includes('application/json')) {
    const text = await response.text();
    const body = parseJsonObject(text);
    if (body !== undefined && (isProblemJson || hasProblemDetailField(body))) {
      return new ProblemError(normalizeProblem(body, response));
    }

    const envelopeMessage = body === undefined ? undefined : asString(body['message']);
    const message =
      envelopeMessage ?? (text !== '' ? text : `${response.status} ${response.statusText}`);
    return new StatusCodeError(response.status, message, response.headers);
  }

  return new StatusCodeError(
    response.status,
    `${response.status} ${response.statusText}`,
    response.headers,
  );
}

/**
 * Maps validation errors from a ProblemDetails response to form fields.
 * Returns true if errors were mapped, false if the error wasn't a validation error.
 */
export function mapValidationErrors<TFieldMap extends Record<string, string>>(
  error: unknown,
  fieldMap: TFieldMap,
  setFieldError: (field: TFieldMap[keyof TFieldMap], messages: readonly string[]) => void,
): boolean {
  if (!(error instanceof ProblemError) || error.problem.status !== 400 || !error.problem.errors) {
    return false;
  }

  for (const [serverField, messages] of Object.entries(error.problem.errors)) {
    if (serverField in fieldMap) {
      const localField = fieldMap[serverField as keyof TFieldMap];
      setFieldError(localField, messages);
    }
  }

  return true;
}
