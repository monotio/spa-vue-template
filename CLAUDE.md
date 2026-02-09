# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Vue 3 + .NET 10 SPA template. Frontend uses Composition API with `<script setup>`, TypeScript, Vite 7, and Pinia. Backend is ASP.NET Core Web API with C# 13. SDK version pinned in `global.json`.

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
```

## Architecture

### Dev Setup

Backend serves as host and proxies to the Vite dev server. Both use HTTPS with auto-generated dev certs (`dotnet dev-certs`). The Vite config proxies `/api/*` requests to the backend at `https://localhost:7191`.

In production, the backend serves the built frontend static files with a fallback to `index.html`.

### Frontend (`vueapp1.client/`)

- **Vue 3 Composition API** with `<script setup lang="ts">` syntax
- **Pinia** for state management (integrated in `main.ts`)
- **Vitest 4** with jsdom environment and globals enabled
- **Path alias**: `@` maps to `./src`
- API calls use native `fetch()` against `/api/...` paths (proxied to backend)
- Components in `src/components/`, tests in `src/components/__tests__/*.spec.ts`

### Backend (`VueApp1.Server/`)

- Controller-based API with route pattern `/api/[controller]`
- Modern C# patterns: primary constructors, records, collection expressions
- Native OpenAPI with Scalar API docs at `/scalar/v1` in development
- CORS allows frontend origin in development
- Tests in `VueApp1.Server.Tests/` using xUnit v3 + Moq

### Code Style

- **Frontend**: ESLint 9 flat config + Prettier (semicolons, single quotes, trailing commas, 100 char width, 2-space indent, LF line endings)
- **Backend**: Nullable reference types enabled, implicit usings, latest C# language version
