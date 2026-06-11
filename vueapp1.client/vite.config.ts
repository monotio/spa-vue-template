/// <reference types="vitest/config" />

import { fileURLToPath, URL } from 'node:url';

import { defineConfig } from 'vite';
import plugin from '@vitejs/plugin-vue';
import vueDevTools from 'vite-plugin-vue-devtools';
import { VitePWA } from 'vite-plugin-pwa';
import fs from 'fs';
import path from 'path';
import child_process from 'child_process';
import { env } from 'process';

// Dev-server HTTPS uses the ASP.NET Core dev cert. Only invoked for
// `vite` (serve) — production builds (incl. Docker, where no dotnet
// exists) must never require a cert or the SDK.
function ensureDevCertificate(): { certFilePath: string; keyFilePath: string } {
  const baseFolder =
    env['APPDATA'] !== undefined && env['APPDATA'] !== ''
      ? `${env['APPDATA']}/ASP.NET/https`
      : `${env['HOME']}/.aspnet/https`;

  const certificateName = 'vueapp1.client';
  const certFilePath = path.join(baseFolder, `${certificateName}.pem`);
  const keyFilePath = path.join(baseFolder, `${certificateName}.key`);

  if (!fs.existsSync(baseFolder)) {
    fs.mkdirSync(baseFolder, { recursive: true });
  }

  if (!fs.existsSync(certFilePath) || !fs.existsSync(keyFilePath)) {
    if (
      0 !==
      child_process.spawnSync(
        'dotnet',
        ['dev-certs', 'https', '--export-path', certFilePath, '--format', 'Pem', '--no-password'],
        { stdio: 'inherit' },
      ).status
    ) {
      throw new Error('Could not create certificate.');
    }
  }

  return { certFilePath, keyFilePath };
}

const isCI = env['CI'] !== undefined && env['CI'] !== '' && env['CI'] !== 'false';

const target = env['ASPNETCORE_HTTPS_PORT']
  ? `https://localhost:${env['ASPNETCORE_HTTPS_PORT']}`
  : env['ASPNETCORE_URLS']
    ? env['ASPNETCORE_URLS'].split(';')[0]
    : 'https://localhost:7191';

// https://vitejs.dev/config/
export default defineConfig(({ command, mode }) => ({
  plugins: [
    plugin(),
    // In-browser Vue DevTools overlay (component tree, Pinia stores, router
    // state, timeline) — no browser extension needed. Dev-server only; Vitest
    // also resolves this config with command === 'serve', so gate on mode too.
    ...(command === 'serve' && mode !== 'test' ? [vueDevTools()] : []),
    VitePWA({
      // 'prompt' + ReloadPrompt.vue gives users an explicit "reload to update"
      // affordance; switch to 'autoUpdate' for silent updates.
      // NOTE: clientsClaim is deliberately ABSENT — claiming clients evicts
      // bfcached pages in Chrome, hurting back/forward navigation. Its absence
      // is load-bearing; don't "fix" it.
      registerType: 'prompt',
      devOptions: { enabled: true },
      includeAssets: ['favicon.ico', 'logo.svg', 'apple-touch-icon-180x180.png'],
      manifest: {
        name: 'VueApp1',
        short_name: 'VueApp1',
        description: 'Vue 3 + ASP.NET Core SPA',
        theme_color: '#35495e',
        background_color: '#ffffff',
        display: 'standalone',
        icons: [
          { src: 'pwa-64x64.png', sizes: '64x64', type: 'image/png' },
          { src: 'pwa-192x192.png', sizes: '192x192', type: 'image/png' },
          { src: 'pwa-512x512.png', sizes: '512x512', type: 'image/png' },
          {
            src: 'maskable-icon-512x512.png',
            sizes: '512x512',
            type: 'image/png',
            purpose: 'maskable',
          },
        ],
      },
      workbox: {
        navigateFallback: 'index.html',
        // The crux of hosting a PWA on a .NET backend: the service worker must
        // never answer navigations to backend routes with the SPA shell.
        // Mirrors the server-side MapFallback exclusion for /api.
        navigateFallbackDenylist: [/^\/api/, /^\/health/, /^\/scalar/, /^\/openapi/],
        runtimeCaching: [
          {
            // Hashed chunks NOT in the precache manifest (lazy pages excluded
            // via globIgnores, or anything over maximumFileSizeToCacheInBytes
            // as the app grows) self-cache on first use. Safe because /assets
            // contains only content-hashed, immutable files.
            // Deliberately NO rule for /api: caching API responses by default
            // causes stale-data bugs and caches authenticated payloads — if
            // you want offline reads, add a GET-only NetworkFirst route
            // consciously (see docs/FRONTEND.md).
            urlPattern: ({ url, sameOrigin }) => sameOrigin && url.pathname.startsWith('/assets/'),
            handler: 'CacheFirst',
            options: {
              cacheName: 'hashed-assets',
              expiration: { maxEntries: 200, maxAgeSeconds: 31536000 },
              cacheableResponse: { statuses: [200] },
            },
          },
        ],
      },
    }),
  ],
  resolve: {
    // Must agree with `paths` in tsconfig.app.json. Vite 8's native
    // resolve.tsconfigPaths would make tsconfig the single source of truth,
    // but it currently breaks dependency pre-bundling here — see the watch
    // list in docs/FRONTEND.md before switching.
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  build: {
    rolldownOptions: {
      output: {
        // Long-term-caching split: framework code changes far less often than
        // app code, so warm browsers (and the service worker precache) keep
        // the ~85 KB vendor chunk across app deploys and re-fetch only the
        // few-KB app chunk. One group only — over-splitting hurts.
        codeSplitting: {
          groups: [
            { name: 'vue-vendor', test: /node_modules[\\/](?:@vue|vue|vue-router|pinia)[\\/]/ },
          ],
        },
      },
    },
  },
  test: {
    environment: 'jsdom',
    clearMocks: true,
    restoreMocks: true,
    setupFiles: ['./src/test/setup.ts'],
    // Pins TZ to a non-UTC zone so timezone bugs surface in tests.
    globalSetup: './vitest.global-setup.ts',
    // CI runners are typically 3-5x slower than dev machines; default
    // timeouts commonly flake there. Locally, fail fast.
    testTimeout: isCI ? 15_000 : 5_000,
    hookTimeout: isCI ? 15_000 : 10_000,
    environmentOptions: {
      jsdom: {
        // Stable window.location for assertions and relative-URL resolution.
        url: 'http://localhost:3000',
      },
    },
    coverage: {
      provider: 'v8',
      // json-summary feeds badges/size-delta tooling; cobertura merges with the
      // .NET side via reportgenerator (see .config/dotnet-tools.json).
      reporter: ['text', 'lcov', 'json-summary', 'cobertura'],
      include: [
        'src/composables/**/*.ts',
        'src/contracts/**/*.ts',
        'src/services/**/*.ts',
        'src/stores/**/*.ts',
        'src/utils/**/*.ts',
      ],
      exclude: ['src/**/__tests__/**'],
      thresholds: {
        lines: 85,
        functions: 85,
        statements: 85,
        branches: 80,
      },
    },
  },
  server: {
    proxy: {
      '^/api': {
        target,
        secure: false,
      },
    },
    port: parseInt(env['DEV_SERVER_PORT'] ?? '57292'),
    // The cert is only ensured/read when actually serving — builds (incl.
    // Docker) and vitest never touch dotnet dev-certs.
    ...(command === 'serve'
      ? {
          https: toHttpsOptions(ensureDevCertificate()),
          // Forward browser console output and unhandled errors/rejections to
          // dev-server stdout — the stream terminal-bound coding agents read.
          // Vite auto-enables this only when it detects an agent; explicit
          // config makes it deterministic for human-started servers too.
          // NOTE: the object form defaults logLevels to [] — list them.
          // error/warn suffices: logger.ts logs through console.warn/error
          // and console.log is lint-banned in src, so wider levels would only
          // surface third-party noise.
          forwardConsole: { unhandledErrors: true, logLevels: ['error', 'warn'] },
        }
      : {}),
  },
}));

function toHttpsOptions(certificate: { certFilePath: string; keyFilePath: string }) {
  return {
    key: fs.readFileSync(certificate.keyFilePath),
    cert: fs.readFileSync(certificate.certFilePath),
  };
}
