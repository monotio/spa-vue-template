# Testing

## Running tests

| What | Command (from repo root) |
| --- | --- |
| Everything (the gate) | `npm run check` |
| All tests only | `npm run test` |
| Frontend, watch mode | `npm --prefix vueapp1.client run test:watch` |
| Frontend, single file | `npm --prefix vueapp1.client run test -- HelloWorld` |
| Backend (wrapper) | `npm run test:backend` |
| Backend, single test | `npm run test:backend -- --filter "FullyQualifiedName~TestMethodName"` |
| Backend, with coverage | `npm run test:backend -- --coverage` |

The backend wrapper (`scripts/run-dotnet-test.mjs`) tees ANSI-stripped output
to `test-results/logs/` and prints the log path first and last — **when a run
fails and your terminal scrolled, tail the log instead of re-running the
suite.** It also maps signal-killed runs to a failing exit code and adds blame
dumps on CI so hung tests can't stall runners silently.

## Determinism choices (and why)

- **TZ is pinned to `Etc/GMT-5`** (vitest.global-setup.ts) — deliberately
  non-UTC so timezone bugs surface in tests. POSIX sign inversion:
  `Etc/GMT-5` means **UTC+5**.
- **Backend test culture is pinned to `sv-SE`** (`"culture"` in both
  `xunit.runner.json` files) — the culture sibling of the TZ pin: a
  deliberately hostile non-invariant culture (decimal comma, different date
  order) so culture-formatting bugs surface in `npm run check` instead of on
  the first non-English server. Don't "fix" the culture — write culture-safe
  code (`InvariantCulture` for wire formats). A test that needs a specific
  OTHER culture uses the disposable `CultureSwitcher` helper
  (`VueApp1.Server.UnitTests/Infrastructure`).
- **Timeouts are CI-aware** (vite.config.ts): CI runners are typically 3–5x
  slower than dev machines, so CI gets 15s while local runs fail fast at 5s.
  Never widen a *per-test* timeout to fix a flake — fix the
  non-deterministic wait (see below).
- **window.location is stable** (`http://localhost:3000` via
  environmentOptions) for URL-relative assertions.
- **localStorage is a Map-based shim** installed unconditionally
  (src/test/setup.ts) so storage behavior is identical on every Node version.
- **Router composables are mocked globally** (src/test/setup.ts) with safe
  defaults; components stay real — stub `RouterLink` per test when needed.

## Anti-flake doctrine

- **Class T failures** (test-body timeout): the test awaits something
  non-deterministic. Replace polling with deterministic settles:
  under fake timers use `await vi.advanceTimersByTimeAsync(ms)` +
  `await nextTick()` — never `vi.waitFor`, which polls ~40x and re-runs timer
  cascades.
- **Class H failures** (hook/import timeout): a heavy static import. Mock
  heavy child components with `vi.mock`.
- Under any `--kill-others-on-fail` runner, one root failure makes siblings
  report collateral kills (`MSB4166` on the .NET side, `code=null` on Node).
  Chase the FIRST failure; never debug the collateral ones.

## Helpers

- `src/test/withSetup.ts` — run a composable in a real effect scope so
  watchers and `onScopeDispose` behave like in a component (and can be
  asserted via `unmount()`).
- `src/test/mockComposable.ts` — `createMockedComposable` caches the mock
  instance so the component under test and your assertions see the same one.

## Coverage

- **Frontend**: thresholds live in vite.config.ts (85% lines/functions/
  statements, 80% branches) over the logic directories (composables,
  contracts, services, stores, utils). SFCs are deliberately excluded from
  the include list — component templates are better covered by behavioral
  tests than line metrics.
- **Backend**: CI gates the *integration* suite at 20% line coverage
  (`/p:Threshold=20`, coverlet.msbuild path). It's a tripwire against
  accidentally disabling the pipeline test surface, not a quality bar —
  raise it as your real suite grows.
- Merged FE+BE reports: both sides emit cobertura; `dotnet tool restore`
  then `dotnet reportgenerator` merges them (tool pinned in
  `.config/dotnet-tools.json`).

## Test platform: VSTest now, MTP documented

The template stays on the VSTest runner deliberately: coverlet is not
supported under Microsoft.Testing.Platform, and MTP changes every documented
`--filter` invocation. When you switch (sensible once xUnit v4 stabilizes):
set `"test": { "runner": "Microsoft.Testing.Platform" }` in global.json
(the older dotnet.config mechanism was removed at .NET 10 RC2), add
`<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>`
to the test csproj files, replace coverlet with
Microsoft.Testing.Extensions.CodeCoverage, and update filter syntax
everywhere it's documented. Half-migrating is the one wrong answer.

## Patterns to adopt as the app grows

- **Authorization-matrix integration tests**: once auth lands (docs/auth.md),
  add one parameterized test per endpoint asserting the role × verb matrix —
  it catches missing `[Authorize]` attributes mechanically.
- **OpenAPI schema assertions**: the contract gate (`openapi:check`) already
  catches accidental contract drift; for schema-shape rules (e.g. "no
  anonymous inline enums"), assert directly on the document in an
  integration test via `/openapi/v1.json`.
- **Test performance baselines**: when the suite gets big enough that slow
  tests matter, parse TRX timings from the wrapper's results directory
  (`test-results/dotnet`), keep a top-slow-tests list with per-test budgets,
  and treat regressions as review items (not CI gates, at first).

## E2E / browser testing

jsdom-first is deliberate: component logic is unit-tested, and
`WebApplicationFactory` covers the API end-to-end in-process. When you need
real-browser coverage, add a Vitest browser-mode project
(`@vitest/browser-playwright` + vitest-browser-vue) for `*.browser.test.ts`
files, or a minimal Playwright smoke that boots the real server via
`scripts/server-process.mjs`. Neither ships by default — they double CI time
for value that only materializes once you have real UI complexity.

For **on-demand interactive verification** (zero CI cost), use the committed
`verify-in-browser` skill (`.claude/skills/verify-in-browser/SKILL.md`): it
boots both dev servers, drives the SPA via browser MCP tools (Playwright MCP
is the documented default wiring), and asserts console/network cleanliness,
header contracts, and SW activation — with a documented curl-only fallback
when no browser tools are available.
