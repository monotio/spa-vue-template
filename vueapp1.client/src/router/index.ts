import { createRouter, createWebHistory } from 'vue-router';

const router = createRouter({
  history: createWebHistory(import.meta.env.BASE_URL),
  scrollBehavior(_to, _from, savedPosition) {
    // Restore scroll on back/forward; scroll to top on new navigations
    // (focus is moved to the main landmark in App.vue).
    return savedPosition ?? { top: 0 };
  },
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
