# Frontend

Vue 3 + TypeScript + Vite 8 (Rolldown) + Pinia + Vue Router + PWA. This page
collects the deep-dive knowledge; style rules live in [AGENTS.md](../AGENTS.md).

## Layout

- `src/pages/` — routed pages (lazy-loaded from `src/router/index.ts`)
- `src/components/` — shared components (incl. `ReloadPrompt.vue` for PWA updates)
- `src/composables/` — `useFetch` (ProblemDetails-aware), `useAbortableRequest`
- `src/services/` — typed API clients built on `useFetch`
- `src/stores/` — Pinia composition stores
- `src/contracts/` — wire types + runtime validators for API responses
- `src/utils/logger.ts` — the logging seam (`console.*` is banned in src)

## Routing

The explicit route table in `src/router/index.ts` is the default — greppable,
codegen-free, agent-friendly. `scrollBehavior` restores position on
back/forward; `App.vue` moves focus to the main landmark after navigation
(a11y — SPA navigations otherwise strand keyboard/screen-reader focus).

**Typed file-based routing variant**: vue-router 5.1 absorbs unplugin-vue-router;
if you prefer routes derived from `src/pages/` with typed names, know the
trade-offs before switching: `typed-router.d.ts` is generated only when the
Vite plugin runs, but CI type-checks BEFORE building — so either commit the
generated file or add a codegen step before `vue-tsc` in CI and the root
`type-check` script. Deleting `src/router/index.ts` ripples into `main.ts`
and any tests referencing named routes. The template doesn't choose this for
you; file-based typing is real value if your app grows many routes.

## Data fetching

`useFetch` is deliberately hand-rolled: it teaches the RFC 9457 ProblemDetails
contract end-to-end (typed errors, loading states); `useAbortableRequest`
prevents race conditions on rapid re-requests. For server-state caching,
dedup, and optimistic updates, [Pinia Colada](https://pinia-colada.esm.dev/)
is the officially recommended layer — wire its query functions through the
same typed fetch + ProblemDetails parser to keep error handling uniform.

## PWA

`vite-plugin-pwa` (generateSW) precaches the app shell;
`navigateFallbackDenylist` keeps the service worker away from `/api`,
`/health`, `/scalar`, `/openapi` — the crux of hosting a PWA on a .NET
backend. `ReloadPrompt.vue` surfaces updates (hourly check + prompt).
Regenerate icons from `public/logo.svg`: `npm run generate-pwa-assets`.
Verify installability with a Chrome DevTools Lighthouse audit after changing
any of this.

## Strict TS gotchas (hard-won)

- `exactOptionalPropertyTypes` is NOT compatible with Vue 3 (vuejs/core#12859) — don't enable it.
- `noPropertyAccessFromIndexSignature` requires bracket access on `process.env`: `env['CI']`.
- `strictImportMetaEnv`: every `VITE_` var must be declared in `env.d.ts` (see docs/CONFIG.md).
- Use `globalThis.fetch` (not `global.fetch`) in tests for DOM-tsconfig compatibility.
- `vue/no-undef-components` needs `ignorePatterns: ['RouterLink', 'RouterView']`.
- ESLint type-checked rules require test files to be included in a tsconfig — don't exclude `__tests__`.

## Bundle analysis

Take a `dist/` size snapshot (`du -sk dist` + the per-chunk table Vite
prints) before/after dependency bumps. Rolldown has built-in build analysis
options; verify rollup-plugin-visualizer compatibility before reaching for it
(it targets Rollup's bundle format).

## Watch list (deliberate holds)

- **Vue Vapor Mode**: opt-in per-component when stable; not template material yet.
- **oxlint pre-pass**: create-vue ships it by default now; it cannot replace
  eslint-plugin-vue's template-aware or type-checked rules — adopt only as a
  speed pre-pass in front of ESLint, never instead of it.
- **tsgo (TypeScript native)**: vue-tsc can't run on it yet
  (vuejs/language-tools#5381); revisit for an order-of-magnitude CI
  type-check speedup when language-tools ships support.
- **VueUse**: fine to add; the bespoke composables here stay because they
  teach the ProblemDetails contract.
