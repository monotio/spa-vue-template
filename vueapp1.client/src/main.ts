import './assets/main.css';

import { createApp } from 'vue';
import { createPinia } from 'pinia';
import App from './App.vue';
import router from './router';
import { logger } from '@/utils/logger';

const app = createApp(App);

// Route runtime errors through the logger seam: logger.ts is the single place
// to plug in an error tracker or OTLP browser exporter later (docs/CONFIG.md).
app.config.errorHandler = (err, _instance, info) => {
  logger.error(`Unhandled Vue error (${info}):`, err);
};
window.addEventListener('unhandledrejection', (event) => {
  logger.error('Unhandled promise rejection:', event.reason);
});

app.use(createPinia());
app.use(router);
app.mount('#app');
