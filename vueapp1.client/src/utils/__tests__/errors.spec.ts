import { describe, it, expect } from 'vitest';
import { ProblemError, StatusCodeError, isOfflineError, mapValidationErrors } from '../errors';

describe('ProblemError', () => {
  it('uses title as default message', () => {
    const error = new ProblemError({ title: 'Bad Request', status: 400 });
    expect(error.message).toBe('Bad Request');
    expect(error.name).toBe('ProblemError');
    expect(error.problem.status).toBe(400);
  });

  it('accepts a custom message', () => {
    const error = new ProblemError({ title: 'Bad Request' }, 'custom');
    expect(error.message).toBe('custom');
  });
});

describe('StatusCodeError', () => {
  it('stores status code and headers', () => {
    const headers = new Headers({ 'x-custom': 'value' });
    const error = new StatusCodeError(500, 'Server Error', headers);
    expect(error.statusCode).toBe(500);
    expect(error.headers.get('x-custom')).toBe('value');
  });
});

describe('isOfflineError', () => {
  it('returns true for Failed to fetch', () => {
    expect(isOfflineError(new TypeError('Failed to fetch'))).toBe(true);
  });

  it('returns false for other errors', () => {
    expect(isOfflineError(new Error('something else'))).toBe(false);
  });
});

describe('mapValidationErrors', () => {
  it('maps server field names to local field names', () => {
    const error = new ProblemError({
      status: 400,
      errors: { Name: ['Name is required'], Email: ['Invalid email'] },
    });

    const mapped: Record<string, readonly string[]> = {};
    const result = mapValidationErrors(error, { Name: 'name', Email: 'email' }, (field, msgs) => {
      mapped[field] = msgs;
    });

    expect(result).toBe(true);
    expect(mapped).toEqual({
      name: ['Name is required'],
      email: ['Invalid email'],
    });
  });

  it('returns false for non-validation errors', () => {
    const result = mapValidationErrors(new Error('other'), {}, () => {});
    expect(result).toBe(false);
  });
});
