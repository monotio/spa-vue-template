# .NET 10 + Vue 3 SPA Template

Production-oriented full-stack template for SPAs in 2026+: Vue 3 + TypeScript frontend and ASP.NET Core backend, with strict lint/type/test gates.

## Tech Stack

- Frontend: Vue 3, TypeScript, Vite 7, Vitest, Pinia, Vue Router
- Backend: ASP.NET Core (.NET 10), C# latest, OpenAPI, ProblemDetails
- Quality: ESLint flat config, Prettier, strict TS config, xUnit v3, CI checks

## Prerequisites

- .NET SDK pinned by `global.json`
- Node.js 22.12+

## Quick Start

```bash
npm ci --prefix vueapp1.client
dotnet restore
npm run check
```

## Daily Commands

Run from repo root.

```bash
npm run dev:server      # Start backend (https://localhost:7191)
npm run dev:client      # Start Vite dev server (https://localhost:57292)

npm run check           # Full validation used before commit
npm run build           # Build backend + frontend
npm run test            # Frontend tests + backend tests
npm run test:load       # Local sustained-load smoke test

npm run openapi:sync    # Generate docs/openapi/openapi.v1.json baseline
npm run openapi:check   # Verify runtime contract matches baseline
```

## Project Layout

```text
VueApp1.Server/                    ASP.NET Core API
VueApp1.Server.UnitTests/          Fast unit tests
VueApp1.Server.IntegrationTests/   End-to-end API pipeline tests
vueapp1.client/                    Vue app
scripts/                           OpenAPI + load test tooling
```

## Backend Design

- Controller + service separation (`ServiceResponse<T>` for consistent outcomes)
- RFC 9457 ProblemDetails enabled globally
- Health endpoint at `/health`
- Server-Timing middleware for API request timing visibility
- Performance config in `appsettings.json` for:
  - output cache
  - rate limiting
  - request timeout policy toggle
- OpenAPI endpoint available in Development and when `OpenApi:Enabled=true`

## Frontend Design

- Strict TypeScript enabled (`exactOptionalPropertyTypes`, `noImplicitOverride`, `noUnused*`)
- API access centralized via composables/services (direct `fetch` restricted by lint rule)
- Feature-oriented structure (`pages`, `stores`, `services`, `composables`, `contracts`, `utils`)
- Coverage thresholds enforced in Vitest config

## OpenAPI Contract Workflow

`openapi-contract.mjs` starts the backend in `Testing`, fetches `/openapi/v1.json`, and compares it with `docs/openapi/openapi.v1.json`.

- Use `npm run openapi:sync` when API contracts intentionally change
- CI uses `npm run openapi:check` to fail on uncommitted contract drift

## CI Gates

CI runs:

- frontend lint + format + type-check + tests
- frontend/backend build
- OpenAPI contract drift check
- backend unit tests
- backend integration tests with a coverage floor on server code
