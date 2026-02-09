<template>
  <div class="weather-component">
    <h1>Weather forecast</h1>
    <p>This component demonstrates fetching data from the server.</p>

    <div v-if="loading" class="loading">
      Loading... Please refresh once the ASP.NET backend has started. See
      <a href="https://aka.ms/jspsintegrationvue">https://aka.ms/jspsintegrationvue</a> for more
      details.
    </div>

    <div v-if="forecasts.length > 0" class="content">
      <table>
        <thead>
          <tr>
            <th>Date</th>
            <th>Temp. (C)</th>
            <th>Temp. (F)</th>
            <th>Summary</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="forecast in forecasts" :key="forecast.date">
            <td>{{ forecast.date }}</td>
            <td>{{ forecast.temperatureC }}</td>
            <td>{{ forecast.temperatureF }}</td>
            <td>{{ forecast.summary }}</td>
          </tr>
        </tbody>
      </table>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue';

interface WeatherForecast {
  date: string;
  temperatureC: number;
  temperatureF: number;
  summary: string | null;
}

const loading = ref(false);
const forecasts = ref<WeatherForecast[]>([]);

const fetchData = async () => {
  loading.value = true;
  forecasts.value = [];

  try {
    const response = await fetch('/api/weatherforecast');
    if (response.ok) {
      forecasts.value = await response.json();
    } else {
      console.error('Failed to fetch weather data:', response.statusText);
    }
  } catch (error) {
    console.error('Error fetching weather data:', error);
  } finally {
    loading.value = false;
  }
};

onMounted(() => {
  fetchData();
});
</script>

<style scoped>
th {
  font-weight: bold;
}

th,
td {
  padding-left: 0.5rem;
  padding-right: 0.5rem;
}

.weather-component {
  text-align: center;
}

table {
  margin-left: auto;
  margin-right: auto;
}
</style>
