import { describe, expect, it, vi } from 'vitest';
import { useWeatherApi } from '../weatherApi';

describe('weatherApi', () => {
  it('returns typed forecasts for a valid API payload', async () => {
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

    const api = useWeatherApi();
    const forecasts = await api.getForecasts();

    expect(forecasts).toHaveLength(1);
    expect(forecasts[0]?.temperatureF).toBe(68);
  });

  it('throws when payload violates the weather contract', async () => {
    globalThis.fetch = vi.fn(() =>
      Promise.resolve(
        new Response(JSON.stringify([{ date: '2026-02-09', temperatureC: '20' }]), { status: 200 }),
      ),
    );

    const api = useWeatherApi();

    await expect(api.getForecasts()).rejects.toThrow(
      'API contract mismatch: expected WeatherForecast[] response.',
    );
  });
});
