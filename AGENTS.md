# AGENTS.md

Guidance for coding agents working in this repository.

## Project Overview

Template for a production-grade SPA stack:

- Frontend: Vue 3 + TypeScript + Vite + Vitest + Pinia + Vue Router
- Backend: ASP.NET Core (.NET 10) Web API
- Shared quality gates: strict linting, strict TS, tests, OpenAPI contract check

## Core Commands

Run from repo root.

```bash
npm run check
npm run build
npm run test
npm run openapi:sync
npm run openapi:check
npm run test:load
npm run dev:server
npm run dev:client
```

Frontend-only:

```bash
npm --prefix vueapp1.client run lint:check
npm --prefix vueapp1.client run format:check
npm --prefix vueapp1.client run type-check
npm --prefix vueapp1.client run test:coverage
```

Backend-only:

```bash
dotnet restore
dotnet build
dotnet test VueApp1.Server.UnitTests/VueApp1.Server.UnitTests.csproj
dotnet test VueApp1.Server.IntegrationTests/VueApp1.Server.IntegrationTests.csproj
```

## Codex Sandbox: Required Elevated Permissions

In this repo, MSBuild/NPM operations can hang in sandboxed mode if not elevated.

- For Codex CLI `shell` tool calls, use `with_escalated_permissions: true`.
- In this environment (`exec_command`), use `sandbox_permissions: "require_escalated"`.
- Always include a one-sentence justification (example: `Need network access for dotnet restore/build/test in sandbox`).

Apply elevation for any command that can restore/build/test/install/fetch:

```bash
npm ci --prefix vueapp1.client
npm --prefix vueapp1.client install
dotnet restore
dotnet build
dotnet test
npm run build
npm run test
npm run check
npm run openapi:sync
npm run openapi:check
dotnet test --no-build /p:CollectCoverage=true /p:CoverletOutputFormat=opencover
```

Treat any MSBuild-backed command as network-sensitive unless proven otherwise.

## Findings From Recent Debugging

1. Integration tests should explicitly disable hosting startup to avoid SpaProxy startup side effects.
2. Use `WebApplicationFactory` with `Testing` environment and:
   - `WebHostDefaults.PreventHostingStartupKey=true`
   - `WebHostDefaults.HostingStartupAssembliesKey=""`
3. Prefer sequential build/test execution when diagnosing hangs; parallel mixed startup commands increased flakiness.
4. Keep OpenAPI generation deterministic by running backend in `Testing` environment in scripts.
5. Avoid hidden global test environment initializers; keep startup behavior explicit in the test factory and scripts.

## Integration Testing Rules

- Keep integration tests API-focused and deterministic.
- Do not depend on SPA runtime for API integration tests.
- Use `IntegrationTestWebApplicationFactory` for all HTTP pipeline tests.

## Architecture Notes

- Backend serves SPA static assets in normal runtime.
- Dev proxy behavior is configured in Vite and SpaProxy settings.
- Integration tests intentionally bypass hosting startup hooks; this is expected and should stay.
