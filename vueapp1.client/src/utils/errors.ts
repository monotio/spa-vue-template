export interface ProblemDetails {
  readonly type?: string;
  readonly title?: string;
  readonly status?: number;
  readonly detail?: string;
  readonly instance?: string;
  readonly errors?: Readonly<Record<string, readonly string[]>>;
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
