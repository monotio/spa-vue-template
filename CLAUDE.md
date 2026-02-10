# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Vue 3 + .NET 10 SPA template. Frontend uses Composition API with `<script setup>`, TypeScript, Vite 7, and Pinia. Backend is ASP.NET Core Web API with C# 14. SDK version pinned in `global.json`.

## Commands

All commands run from the repo root. The root `package.json` delegates to `vueapp1.client/` via `--prefix`.

### Validate (run before committing)

```bash
npm run check            # Full stack: lint + format + type-check + test (FE) + build + test (BE)
```

### Root scripts (repo root)

```bash
npm run check            # Full stack validation (single command, exits cleanly)
npm run build            # Build backend + frontend
npm run test             # Run all tests (frontend + backend)
npm run lint             # ESLint auto-fix
npm run lint:check       # ESLint check only (no fix)
npm run format           # Prettier auto-fix
npm run format:check     # Prettier check only (no write)
npm run type-check       # TypeScript checking
npm run dev:client       # Start Vite dev server (https://localhost:57292)
npm run dev:server       # Start .NET API server (https://localhost:7191)
```

### Frontend-specific (from `vueapp1.client/`)

```bash
npm run test -- HelloWorld              # Run single test file by name
npm run test:watch       # Watch mode (interactive)
```

### Backend-specific (from repo root)

```bash
dotnet test --filter "FullyQualifiedName~TestMethodName"   # Single test
dotnet test --filter "ClassName=WeatherForecastControllerTests"  # Test class
dotnet test --project VueApp1.Server.UnitTests              # Unit tests only
dotnet test --project VueApp1.Server.IntegrationTests       # Integration tests only
```

## Architecture

### Dev Setup

Backend serves as host and proxies to the Vite dev server. Both use HTTPS with auto-generated dev certs (`dotnet dev-certs`). The Vite config proxies `/api/*` requests to the backend at `https://localhost:7191`.

In production, the backend serves the built frontend static files with a fallback to `index.html`.

### Frontend (`vueapp1.client/`)

- **Vue 3 Composition API** with `<script setup lang="ts">` syntax
- **Vue Router** for client-side routing with lazy-loaded pages
- **Pinia** for state management (composition API stores in `src/stores/`)
- **API layer**: `useFetch` composable with loading states, Problem Details handling, and `useAbortableRequest` for race condition prevention
- **Vitest 4** with jsdom environment
- **Path alias**: `@` maps to `./src`
- Pages in `src/pages/`, components in `src/components/`, composables in `src/composables/`, stores in `src/stores/`

### Backend (`VueApp1.Server/`)

- Controller-based API inheriting from `ApiControllerBase` with `HandleServiceResponse` pattern
- Service layer returning `ServiceResponse<T>` for consistent success/error handling
- `AddProblemDetails()` + `UseExceptionHandler()` + `UseStatusCodePages()` for RFC 9457 responses on all errors
- `ServerTimingMiddleware` exposes request duration in browser DevTools
- Health check endpoint at `/health`
- Modern C# patterns: primary constructors, records, collection expressions
- Native OpenAPI 3.1 with Scalar API docs at `/scalar/v1` in development
- Structured `Program.cs` with method-per-concern setup

### Backend Tests

- **`VueApp1.Server.UnitTests/`**: Fast, isolated tests with mocked dependencies (xUnit v3 + Moq). Mirrors source folder structure (`Controllers/`, `Services/`).
- **`VueApp1.Server.IntegrationTests/`**: Full pipeline tests via `WebApplicationFactory<Program>`. Tests real HTTP requests, middleware, DI, and serialization.

### Code Style

- **Frontend**: ESLint 9 flat config with type-checked rules + Prettier (semicolons, single quotes, trailing commas, 100 char width, 2-space indent, LF line endings)
- **Frontend TypeScript**: `strict` + `noUncheckedIndexedAccess` + `noPropertyAccessFromIndexSignature` + `verbatimModuleSyntax` + `erasableSyntaxOnly` via `@vue/tsconfig` 0.8 base. `strictImportMetaEnv` enforced in `env.d.ts`
- **Frontend ESLint enforces**: `<script setup lang="ts">` only, type-based `defineProps`/`defineEmits`, `<style scoped>` required, no undefined components, `require-typed-ref`, `no-ref-object-reactivity-loss`, `prefer-use-template-ref`
- **Backend**: `Directory.Build.props` centralizes `TreatWarningsAsErrors`, `EnforceCodeStyleInBuild`, and `AnalysisLevel: latest-recommended` across all projects
- **Backend**: `.editorconfig` enforces C# naming conventions (`_camelCase` private fields, `PascalCase` types, `I`-prefixed interfaces) and code style preferences
