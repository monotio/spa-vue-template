<script setup lang="ts">
import { onMounted } from 'vue';
import { useWeatherStore } from '@/stores/weather';

const weather = useWeatherStore();

onMounted(() => {
  void weather.load();
});
</script>

<template>
  <div class="weather-page">
    <h1>Weather forecast</h1>
    <p>This page demonstrates the API layer, Pinia store, and loading state patterns.</p>

    <div v-if="weather.isLoading" class="loading">Loading...</div>

    <div v-if="weather.error" class="error">
      {{ weather.error }}
    </div>

    <table v-if="weather.forecasts.length > 0">
      <thead>
        <tr>
          <th>Date</th>
          <th>Temp. (C)</th>
          <th>Temp. (F)</th>
          <th>Summary</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="forecast in weather.forecasts" :key="forecast.date">
          <td>{{ forecast.date }}</td>
          <td>{{ forecast.temperatureC }}</td>
          <td>{{ forecast.temperatureF }}</td>
          <td>{{ forecast.summary }}</td>
        </tr>
      </tbody>
    </table>
  </div>
</template>

<style scoped>
.weather-page {
  text-align: center;
}

table {
  margin: 1.5rem auto 0;
  border-collapse: collapse;
}

th {
  font-weight: 600;
}

th,
td {
  padding: 0.4rem 0.75rem;
}

.loading {
  margin-top: 1rem;
}

.error {
  color: var(--vt-c-red, #e53e3e);
  margin-top: 1rem;
}
</style>
