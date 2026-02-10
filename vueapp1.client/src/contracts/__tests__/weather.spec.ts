import { describe, expect, it } from 'vitest';
import { assertWeatherForecastList } from '../weather';

describe('weather contract', () => {
  it('accepts valid weather forecasts', () => {
    const payload = [
      {
        date: '2026-02-09',
        temperatureC: 20,
        temperatureF: 68,
        summary: 'Warm',
      },
    ];

    expect(() => assertWeatherForecastList(payload)).not.toThrow();
  });

  it('throws when payload does not match contract', () => {
    const payload = [{ date: '2026-02-09', temperatureC: '20', summary: 'Warm' }];

    expect(() => assertWeatherForecastList(payload)).toThrow(
      'API contract mismatch: expected WeatherForecast[] response.',
    );
  });
});
