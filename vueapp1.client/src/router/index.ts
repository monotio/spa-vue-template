import { createRouter, createWebHistory } from 'vue-router';

const router = createRouter({
  history: createWebHistory(import.meta.env.BASE_URL),
  routes: [
    {
      path: '/',
      name: 'home',
      component: () => import('@/pages/HomePage.vue'),
    },
    {
      path: '/weather',
      name: 'weather',
      component: () => import('@/pages/WeatherPage.vue'),
    },
  ],
});

export default router;
