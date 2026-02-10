import { describe, it, expect } from 'vitest';
import { apiUrl } from '../api';

describe('apiUrl', () => {
  it('returns the path when no params given', () => {
    expect(apiUrl('/api/items')).toBe('/api/items');
  });

  it('appends query parameters', () => {
    const url = apiUrl('/api/items', { page: 1, search: 'hello' });
    expect(url).toBe('/api/items?page=1&search=hello');
  });

  it('omits null and undefined values', () => {
    const url = apiUrl('/api/items', { page: 1, search: null, filter: undefined });
    expect(url).toBe('/api/items?page=1');
  });

  it('handles boolean params', () => {
    const url = apiUrl('/api/items', { active: true });
    expect(url).toBe('/api/items?active=true');
  });
});
