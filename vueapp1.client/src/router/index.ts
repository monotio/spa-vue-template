import { createRouter, createWebHistory } from 'vue-router';
import HomePage from '@/pages/HomePage.vue';

// Typed per-route metadata: `meta.title` feeds the afterEach below, so a typo
// in a route definition is a type error, not a silently wrong tab title.
declare module 'vue-router' {
  interface RouteMeta {
    /** Per-page document title; suffixed with the app name on navigation. */
    title?: string;
  }
}

// Falls back to the product name when no .env overrides VITE_APP_TITLE —
// including a blank value (`VITE_APP_TITLE=` in a copied .env), which would
// otherwise produce titles like "Weather forecast · " and silently break the
// WCAG 2.4.2 story below (see .env.example and docs/CONFIG.md).
const appName = import.meta.env.VITE_APP_TITLE?.trim() || 'VueApp1';

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
      meta: { title: 'Weather forecast' },
    },
    {
      // Lazy on purpose: the agent showcase (and its SSE composable) costs
      // visitors nothing until they open it — and the whole feature deletes
      // cleanly with this route + the agent folders.
      path: '/agent',
      name: 'agent',
      component: () => import('@/pages/AgentPage.vue'),
      meta: { title: 'Agent chat' },
    },
    {
      // The backend's index.html fallback and the service worker's
      // navigateFallback both answer unknown URLs with the SPA shell — this
      // catch-all is the third leg of that story. Without it, a typo URL
      // renders an empty <RouterView> (and App.vue's focus reset would focus
      // an empty main landmark).
      path: '/:pathMatch(.*)*',
      name: 'not-found',
      component: () => import('@/pages/NotFoundPage.vue'),
      meta: { title: 'Page not found' },
    },
  ],
});

// Per-page document titles (WCAG 2.4.2): without this, every route shows the
// same tab title — and the focus reset in App.vue re-announces that stale
// title to screen readers on each navigation. For titles derived from page
// data (e.g. an entity name), use VueUse's useTitle inside the page instead;
// this hook only covers the static per-route baseline.
router.afterEach((to) => {
  document.title = to.meta.title ? `${to.meta.title} · ${appName}` : appName;
});

export default router;
