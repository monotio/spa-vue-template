# Frontend

Vue 3 + TypeScript + Vite 8 (Rolldown) + Pinia + Vue Router + PWA. This page
collects the deep-dive knowledge; style rules live in [AGENTS.md](../AGENTS.md).

## Layout

- `src/pages/` — routed pages (lazy-loaded from `src/router/index.ts`)
- `src/components/` — shared components (incl. `ReloadPrompt.vue` for PWA updates)
- `src/composables/` — `useFetch` (ProblemDetails-aware), `useAbortableRequest`,
  `useDirtyGuard` (unsaved-changes guard), `useDownload` (file exports)
- `src/services/` — typed API clients built on `useFetch`
- `src/stores/` — Pinia composition stores
- `src/contracts/` — wire types (generated from the OpenAPI contract) +
  runtime validators for API responses
- `src/utils/logger.ts` — the logging seam (`console.*` is banned in src)

## Routing

The explicit route table in `src/router/index.ts` is the default — greppable,
codegen-free, agent-friendly. `scrollBehavior` restores position on
back/forward; `App.vue` moves focus to the main landmark after navigation
(a11y — SPA navigations otherwise strand keyboard/screen-reader focus).

**404s are a three-leg story** in this dual-stack template: the server's
`MapFallbackToFile` and the service worker's `navigateFallback` both answer
unknown URLs with the SPA shell (a 200, deliberately — see docs/API.md), so
the client's `/:pathMatch(.*)*` catch-all route is the leg that turns that
shell into visible feedback (`NotFoundPage.vue`). Remove any leg and typo
URLs render a silent blank page. Runtime *errors*, by contrast, deliberately
route to the logger seam (`main.ts` errorHandler), not to an error page: Vue
has no first-class route-level error boundary, and a generic "something went
wrong" page is an app-level UX decision — add one consciously by navigating
from your error handler if your app wants it.

**Per-page document titles** (WCAG 2.4.2): routes declare `meta.title` and a
single `router.afterEach` suffixes it with the app name
(`VITE_APP_TITLE`, falling back to the product name — both rewritten by the
rename script). The `RouteMeta` augmentation in `src/router/index.ts` types
the field. For titles derived from page data (entity names, unread counts),
call VueUse's `useTitle` inside the page; for SSR or social/meta tags reach
for [@unhead/vue](https://unhead.unjs.io/) (v3) instead — neither replaces
the static per-route baseline here.

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

## Generated API types

`src/contracts/api.gen.ts` is generated from the committed OpenAPI contract
by `npm run openapi:sync` ([openapi-typescript](https://openapi-ts.dev/),
devDependency, zero runtime cost) and joins the same drift gate: `npm run
openapi:check` — part of `npm run check` and CI — fails when it's stale.
Change the API surface, run `openapi:sync`, and `vue-tsc` flags every stale
frontend consumer at compile time. Conventions:

- **Re-export, don't scatter**: consume schema types through a hand-named
  contract module (`src/contracts/weather.ts` re-exports
  `components['schemas']['WeatherForecast']`) instead of spreading
  `components[...]` lookups through the app.
- **Keep the runtime guards** (`assertWeatherForecastList`): generated types
  are compile-time promises about the wire — a version-skewed server or a
  misbehaving proxy breaks them at runtime, and the guard turns that into a
  loud error next to its cause.
- The file carries a do-not-edit header and is ESLint- and Prettier-ignored:
  regenerate it, never edit or reformat it.
- **Types only, deliberately no generated client**: `useFetch` stays the
  single fetch boundary (lint-enforced). If your app later wants a full SDK,
  openapi-fetch / Hey API are the upgrade path — wire them through the same
  ProblemDetails handling.
- openapi-typescript declares a `typescript ^5.x` peer while the template
  runs TS 6; the root `package.json` `overrides` entry widens that edge.
  Drop the override once upstream catches up. A Dependabot bump of
  openapi-typescript can legitimately change the emitted output — run
  `npm run openapi:sync` and commit the regenerated file in the same PR.

## VueUse

`@vueuse/core` ships with the template — **check it before writing a bespoke
composable**; lifecycle-safe wrappers for browser APIs are exactly what it
exists for. Pointers:

- `useEventListener` — auto-removes on unmount; supports reactive targets
  (that's how `useDirtyGuard` registers `beforeunload` only while dirty).
- `useTitle` — reactive document title for data-derived page titles.
- `useLocalStorage` / `useStorage` — persisted reactive state without the
  serialize/parse/SSR-guard boilerplate.
- `watchDebounced` / `useDebounceFn` — debounced search-as-you-type; pairs
  with `useAbortableRequest` for cancelling the superseded request.

The bespoke `useFetch`/`useAbortableRequest` stay: they teach this template's
RFC 9457 ProblemDetails contract, which VueUse's `useFetch` knows nothing
about — don't swap them for the generic version.

## Unsaved-changes guard

`useDirtyGuard` blocks both navigation legs when a form has unsaved changes:
in-app via an `onBeforeRouteLeave` guard (stash the intended destination,
raise your confirm affordance, replay on confirm) and hard navigation via
`beforeunload` (browser-native prompt). The dialog UI is deliberately YOURS —
the composable only exposes the seam:

```vue
<script setup lang="ts">
const draft = ref(initialValue);
const saved = ref(initialValue);
const showConfirm = ref(false);

const { confirmLeave, cancelLeave } = useDirtyGuard(() => draft.value !== saved.value, {
  onNavigationBlocked: () => (showConfirm.value = true),
});
// Dialog buttons: "Discard" -> { showConfirm = false; confirmLeave(); }
//                 "Keep editing" -> { showConfirm = false; cancelLeave(); }
</script>
```

Call it during the setup of a route-level component (the leave guard binds to
the current route). One known limit: the replay is always a `push` of the
stashed `fullPath` — `replace` semantics and history `state` of the blocked
navigation are not preserved (stash more than the path if you need that
fidelity). The hard-won subtlety it encodes: the `beforeunload`
listener registers **only while dirty** — its mere presence makes the page
ineligible for the back/forward cache, so an always-on listener taxes every
back/forward navigation. Browsers ignore custom messages; `preventDefault()`
is the whole API.

## File downloads

For a plain same-origin GET export, skip JavaScript entirely:
`<a href="/api/report" download>` — the browser parses Content-Disposition
itself. Reach for `useDownload` when the export is a POST, needs auth
headers, or should surface failures in-app: it fetches the blob, picks the
filename from Content-Disposition (preferring RFC 5987 `filename*=UTF-8''…`,
the only interoperable transport for non-ASCII filenames — the fiddly parsing
is the composable's whole value), and triggers the save via a temporary
object URL. Failed exports come back as ProblemDetails JSON, not a
downloadable body: `download()` throws the same `ProblemError` /
`StatusCodeError` as `useFetch`, so error handling stays uniform.

```ts
const { download, isDownloading } = useDownload();
await download('/api/reports/export', {
  fallbackFilename: 'report.csv',
  init: { method: 'POST', body: JSON.stringify(filter) },
});
```

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

## Loading performance & the caching contract

The template targets zero-request warm loads and minimal cold loads. The
contract (enforced in `Program.cs` and `vite.config.ts` — keep them in sync):

| Resource | Policy | Why |
| --- | --- | --- |
| `/assets/*`, `workbox-*.js` | `public, max-age=31536000, immutable` | Vite emits only content-hashed names there; a new build is a new URL |
| `index.html`, `sw.js`, `manifest.webmanifest` | `no-cache` (+ ETag → 304) | deployments and SW updates must propagate on next navigation |
| `/api/*` | server-side output cache only | client caching of API data is an app decision, not a template default |

Build-time pieces that make cold loads fast:

- **Vendor split** (`codeSplitting` in vite.config.ts): the ~90 KB framework
  chunk (`vue-vendor`) survives app deploys in warm caches and the SW
  precache; app changes re-ship only a ~6 KB chunk. One group only —
  over-splitting hurts. Vite auto-emits `modulepreload` for it.
- **Eager default route**: the landing page is statically imported — lazy
  routes cost cold visitors a sequential round trip. Keep rarely-visited
  pages lazy.
- **Publish-time precompression**: the static-web-assets pipeline emits
  max-quality `.br`/`.gz` with per-encoding ETags at `dotnet publish`
  (the Docker build feeds dist in before publish for the same treatment).

Service-worker caching tiers: the app shell is precached; other `/assets`
files self-cache on first use (CacheFirst — safe, they're immutable). As the
app grows, keep the precache app-shell-sized with `globIgnores` (the 2 MiB
`maximumFileSizeToCacheInBytes` default also guards against accidentally
precaching large media). **No `/api` caching by default** — if you want
offline reads for specific GET endpoints, add a conscious rule:

```ts
// GET-only, network-first with a timeout; never cache authenticated writes.
{
  urlPattern: ({ url, sameOrigin, request }) =>
    sameOrigin && request.method === 'GET' && url.pathname.startsWith('/api/weatherforecast'),
  handler: 'NetworkFirst',
  options: { cacheName: 'api-weather', networkTimeoutSeconds: 3 },
}
```

Deliberate omissions (researched, rejected for a lean template): critical-CSS
inlining (FCP is JS-gated on an empty `#app`), `preconnect` (no cross-origin
hosts), 103 Early Hints (no Kestrel 1xx API until .NET 11), HTTP/3 by default
(needs libmsquic + usually terminates at the proxy), SRI on same-origin
assets (wrong threat model), Speculation Rules (excludes SPA soft
navigations). Source maps: set `build.sourcemap: 'hidden'` if you want
stack traces without shipping sources.

## Agent observability: forwarded browser console

`server.forwardConsole` (explicit in `vite.config.ts`) forwards browser
`console.error`/`console.warn` plus uncaught exceptions and unhandled
promise rejections to dev-server stdout — the stream a terminal-bound agent
already reads. Vite would auto-enable it only when it detects an agent
launched the server; the explicit config makes the behavior deterministic
for human-started servers too. Two gotchas: the object form defaults
`logLevels` to `[]` (silently nothing) so the levels are spelled out, and
forwarding needs a connected browser session — it complements browser
tooling, it doesn't replace it.

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
- **`resolve.tsconfigPaths`**: Vite 8's native option would make
  tsconfig.app.json the single source of truth for the `@` alias (deleting
  the duplicated `resolve.alias`), and vue-tsc, Vitest, and `vite build` all
  resolve correctly with it. HELD because the dev-server dependency scanner
  does not: it resolves SFC script-block imports with importer
  `Page.vue?id=0`, which fails the tsconfig include match, so every
  cold-cache dev boot prints `(!) Failed to run dependency scan` and skips
  pre-bundling — a warm `node_modules/.vite` cache boots clean, but the
  failure returns on any cache invalidation (fresh clone, dependency bump).
  Reproduced on rolldown-vite 8.0.16 with `@/stores/weather` imported from
  `WeatherPage.vue`. Re-test on Vite bumps; switch when a cold boot is clean.
