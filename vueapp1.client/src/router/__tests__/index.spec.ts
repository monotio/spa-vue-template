import { describe, it, expect, vi, afterEach } from 'vitest';
import type appRouter from '../index';

// jsdom doesn't implement scrolling, and scrollBehavior calls window.scrollTo
// after each navigation — stub it to keep test output clean.
vi.stubGlobal('scrollTo', vi.fn());

// The router module captures the app name at import time — reset modules so
// each test's stubbed VITE_APP_TITLE is observed.
async function loadRouter(): Promise<typeof appRouter> {
  vi.resetModules();
  return (await import('../index')).default;
}

describe('router', () => {
  afterEach(() => {
    vi.unstubAllEnvs();
  });

  it('renders the catch-all not-found route for unknown URLs', async () => {
    const router = await loadRouter();

    await router.push('/no/such/page');

    expect(router.currentRoute.value.name).toBe('not-found');
  });

  it('suffixes the per-route title with the app name', async () => {
    vi.stubEnv('VITE_APP_TITLE', 'TestApp');
    const router = await loadRouter();

    await router.push('/weather');

    expect(document.title).toBe('Weather forecast · TestApp');
  });

  it('uses the bare app name on routes without a meta title', async () => {
    vi.stubEnv('VITE_APP_TITLE', 'TestApp');
    const router = await loadRouter();

    await router.push('/');

    expect(document.title).toBe('TestApp');
  });

  it('titles the not-found page', async () => {
    vi.stubEnv('VITE_APP_TITLE', 'TestApp');
    const router = await loadRouter();

    await router.push('/missing');

    expect(document.title).toBe('Page not found · TestApp');
  });
});
