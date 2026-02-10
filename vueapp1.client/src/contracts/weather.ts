export interface WeatherForecast {
  readonly date: string;
  readonly temperatureC: number;
  readonly temperatureF: number;
  readonly summary: string | null;
}

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

export function assertWeatherForecastList(value: unknown): asserts value is WeatherForecast[] {
  if (!Array.isArray(value) || !value.every(isWeatherForecast)) {
    throw new Error('API contract mismatch: expected WeatherForecast[] response.');
  }
}
