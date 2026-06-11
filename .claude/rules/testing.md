---
globs:
  - 'vueapp1.client/src/**/__tests__/**'
  - 'vueapp1.client/src/test/**'
  - 'VueApp1.Server.UnitTests/**'
  - 'VueApp1.Server.IntegrationTests/**'
---

# Testing rules (auto-loaded when touching test files)

- Full doctrine: docs/TESTING.md. Key points below.
- xUnit v3: the package is `xunit.v3`; `using Xunit;` is unchanged.
- Never widen a per-test timeout to fix a flake — replace the
  non-deterministic wait. Under fake timers use
  `await vi.advanceTimersByTimeAsync(ms)` + `await nextTick()`,
  never `vi.waitFor`.
- TZ is pinned to `Etc/GMT-5` (= UTC+5, POSIX inverted sign) — write
  assertions timezone-safely; don't "fix" the TZ.
- Backend test culture is pinned to `sv-SE` (`"culture"` in both
  `xunit.runner.json` files) — write culture-safe assertions
  (InvariantCulture for wire formats); don't "fix" the culture. Need a
  specific OTHER culture? Use `CultureSwitcher`
  (`VueApp1.Server.UnitTests/Infrastructure`).
- Composable tests: use `withSetup` (real effect scope) and
  `createMockedComposable` (cached instance) from `src/test/`.
- Integration tests: `IntegrationTestWebApplicationFactory` only; Testing
  environment; never depend on the SPA runtime or SpaProxy.
- When a test run fails and output scrolled: `tail -200` the disk log under
  `test-results/logs/` instead of re-running.
