import { createRouter, createWebHistory } from 'vue-router';
import HomePage from '@/pages/HomePage.vue';

const router = createRouter({
  history: createWebHistory(import.meta.env.BASE_URL),
  scrollBehavior(_to, _from, savedPosition) {
    // Restore scroll on back/forward; scroll to top on new navigations
    // (focus is moved to the main landmark in App.vue).
    return savedPosition ?? { top: 0 };
  },
  routes: [
    {
      // The default route is imported eagerly: lazy-loading it costs a cold
      // visitor one extra sequential round trip (download+parse the main
      // bundle before even DISCOVERING the home chunk). Keep rarely-visited
      // pages lazy; keep the landing page in the main bundle.
      path: '/',
      name: 'home',
      component: HomePage,
    },
    {
      path: '/weather',
      name: 'weather',
      component: () => import('@/pages/WeatherPage.vue'),
    },
  ],
});

export default router;
