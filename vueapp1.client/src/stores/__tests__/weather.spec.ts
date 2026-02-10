import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { useWeatherStore } from '../weather';
import { logger } from '@/utils/logger';

describe('weather store', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    vi.spyOn(logger, 'error').mockImplementation(() => undefined);
  });

  it('loads forecasts from API', async () => {
    globalThis.fetch = vi.fn(() =>
      Promise.resolve(
        new Response(
          JSON.stringify([
            {
              date: '2026-02-09',
              temperatureC: 20,
              temperatureF: 68,
              summary: 'Warm',
            },
          ]),
          { status: 200 },
        ),
      ),
    );

    const store = useWeatherStore();
    await store.load();

    expect(store.error).toBeNull();
    expect(store.forecasts).toHaveLength(1);
    expect(store.forecasts[0]?.summary).toBe('Warm');
  });

  it('surfaces contract errors as user-facing messages', async () => {
    globalThis.fetch = vi.fn(() =>
      Promise.resolve(new Response(JSON.stringify([{ invalid: true }]), { status: 200 })),
    );

    const store = useWeatherStore();
    await store.load();

    expect(store.forecasts).toHaveLength(0);
    expect(store.error).toBe('API contract mismatch: expected WeatherForecast[] response.');
  });

  it('falls back to default message for unknown errors and supports clearing state', async () => {
    // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors
    globalThis.fetch = vi.fn(() => Promise.reject('boom'));

    const store = useWeatherStore();
    await store.load();

    expect(store.error).toBe('Failed to load forecasts');

    store.clear();
    expect(store.forecasts).toHaveLength(0);
    expect(store.error).toBeNull();
  });
});
