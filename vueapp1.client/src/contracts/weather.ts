import type { components } from './api.gen';

/**
 * Wire type re-exported from the generated OpenAPI types (`api.gen.ts`,
 * produced by `npm run openapi:sync`): when the backend changes the
 * WeatherForecast shape, the regenerated types ripple here and `tsc` flags
 * every stale consumer at compile time.
 */
export type WeatherForecast = components['schemas']['WeatherForecast'];

function isWeatherForecast(value: unknown): value is WeatherForecast {
  if (!value || typeof value !== 'object') {
    return false;
  }

  const candidate = value as Partial<WeatherForecast>;

  return (
    typeof candidate.date === 'string' &&
    typeof candidate.temperatureC === 'number' &&
    typeof candidate.temperatureF === 'number' &&
    (typeof candidate.summary === 'string' || candidate.summary === null)
  );
}

/**
 * The runtime guard stays alongside the generated types deliberately:
 * generated types are compile-time promises about the wire, and a
 * version-skewed server or misbehaving proxy breaks them at runtime. This
 * assertion turns that breakage into a loud error next to its cause.
 */
export function assertWeatherForecastList(value: unknown): asserts value is WeatherForecast[] {
  if (!Array.isArray(value) || !value.every(isWeatherForecast)) {
    throw new Error('API contract mismatch: expected WeatherForecast[] response.');
  }
}
