import { ref } from 'vue';
import { defineStore } from 'pinia';
import { useWeatherApi } from '@/services/weatherApi';
import { logger } from '@/utils/logger';
import type { WeatherForecast } from '@/contracts/weather';

export const useWeatherStore = defineStore('weather', () => {
  const { getForecasts, isLoading } = useWeatherApi();

  const forecasts = ref<WeatherForecast[]>([]);
  const error = ref<string>();

  async function load() {
    error.value = undefined;
    try {
      forecasts.value = await getForecasts();
    } catch (e) {
      error.value = e instanceof Error ? e.message : 'Failed to load forecasts';
      logger.error('Failed to load weather forecasts:', e);
    }
  }

  function clear() {
    forecasts.value = [];
    error.value = undefined;
  }

  return { forecasts, error, isLoading, load, clear };
});
