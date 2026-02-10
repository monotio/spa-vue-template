import { useFetch } from '@/composables/useFetch';
import type { WeatherForecast } from '@/contracts/weather';
import { assertWeatherForecastList } from '@/contracts/weather';

export function useWeatherApi() {
  const { getJson, isGetting } = useFetch();

  async function getForecasts(signal?: AbortSignal): Promise<WeatherForecast[]> {
    const payload = await getJson<unknown>('/api/weatherforecast', signal);
    assertWeatherForecastList(payload);
    return payload;
  }

  return {
    isLoading: isGetting,
    getForecasts,
  };
}
