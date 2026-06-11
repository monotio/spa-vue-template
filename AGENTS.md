# VueApp1 Agent Playbook

Guidance for coding agents (and humans) working in this repository.

## Commands

**Always use `npm run` wrappers; never bypass them with the underlying tool.**
Wrappers encode invariants the raw tool doesn't know about — environment
selection, build ordering, OpenAPI generation, results directories, disk
logging. Run `npm run` to list all scripts.

```bash
npm run check          # THE gate: lint + format + type-check + FE tests + build + OpenAPI contract + BE tests (serial, deterministic)
npm run check:fast     # iteration variant: parallelizes the frontend static checks; check stays canonical
npm run build          # backend + frontend production build
npm run test           # all tests (FE + BE wrappers)
npm run test:backend -- --filter "FullyQualifiedName~Name"   # filtered BE tests
npm --prefix vueapp1.client run test -- HelloWorld           # filtered FE tests
npm run openapi:sync   # regenerate the committed OpenAPI contract after API changes
npm run dev:server     # ASP.NET Core API (https://localhost:7191)
npm run dev:client     # Vite dev server (https://localhost:57292)
npm run setup          # one-command onboarding: githooks + npm ci + rebuild-trusted + locked restore
```

**Test-cost discipline.** Subagents should avoid the full `npm run test`; use
filtered variants scoped to the files you touched and pass ALL patterns to ONE
invocation (each separate run pays MSBuild/fixture startup tax). Prefer the
full suite only when you've touched ~10+ unrelated test files — it is the
pre-commit gate, not the iteration tool.

**Read disk logs instead of re-running.** The test wrappers tee every local
run to `test-results/logs/` and print the path as the first and last line.
When a run fails and stdout was truncated, `tail -200` the log — it has every
per-test line and the assertion diffs.

**Stale build server.** If MSBuild starts failing inexplicably in files you
never touched (and it survives a clean rebuild), recycle the build servers
instead of deleting `obj`/`bin`: `npm run build-server-shutdown`, then rebuild.

## Documentation

Deep dives: [Testing](docs/TESTING.md), [Frontend](docs/FRONTEND.md),
[API](docs/API.md), [Configuration](docs/CONFIG.md),
[Patterns](docs/PATTERNS.md), [MCP server](docs/MCP.md),
[GitHub workflow](docs/GITHUB.md),
[ast-grep guardrails](docs/AST_GREP_GUIDE.md).

**Capture learnings into these docs** (or this file) when you fix something
non-obvious — committed docs are the project's memory. Do not use
agent-runtime-local memory for project knowledge.

## Project Overview

Production-grade SPA template:

- **Frontend** (`vueapp1.client/`): Vue 3 Composition API (`<script setup lang="ts">`),
  TypeScript 6, Vite 8 (Rolldown), Pinia, Vue Router, PWA (vite-plugin-pwa), Vitest 4.
- **Backend** (`VueApp1.Server/`): ASP.NET Core (.NET 10, C# 14) Web API;
  controllers over a `ServiceResponse<T>` service layer; RFC 9457 problem
  details everywhere; OpenAPI 3.1 + Scalar at `/scalar/v1` (Development).
- **Solution**: `VueApp1.slnx`; central package versions in `Directory.Packages.props`;
  NuGet lockfiles restored with locked-mode in CI.
- **npm workspaces**: `vueapp1.client` is a workspace of the root manifest —
  ONE root `package-lock.json`, one `npm ci` at the root, dependencies
  hoisted to the root `node_modules`. The `npm --prefix` wrappers still work.
- Dev: backend hosts and proxies to Vite; HTTPS via auto-generated dev certs.
  Prod: backend serves the built SPA with an `index.html` fallback (PWA-aware,
  no-cache) and ProblemDetails 404s for unmatched `/api` routes.

## Architecture Rules

- **API layer**: controllers inherit `ApiControllerBase`, return
  `HandleServiceResponse(...)`; services return `ServiceResponse<T>`. Give
  recurring client-actionable errors a stable `ProblemDetailTypes` constant.
- **Frontend API access**: `useFetch` composable (ProblemDetails-aware) or a
  service in `src/services/`; direct `fetch` is lint-blocked outside them.
- **OpenAPI contract**: `docs/openapi/openapi.v1.json` is a committed
  artifact; CI fails on drift. After changing the API surface, run
  `npm run openapi:sync` and commit the diff.
- **Integration tests** use `IntegrationTestWebApplicationFactory` (Testing
  environment, hosting startup disabled — SpaProxy must not start). Keep them
  API-focused and deterministic.
- **No database and no auth by default** — deliberate; see
  [docs/PATTERNS.md](docs/PATTERNS.md) and the README decision guides before
  adding either.

## Code Style

### Frontend

- ESLint 10 flat config (type-checked rules) + Prettier; strict TS
  (`strict`, `noUncheckedIndexedAccess`, `noPropertyAccessFromIndexSignature`,
  `verbatimModuleSyntax`, `erasableSyntaxOnly`, `strictImportMetaEnv`).
- Vue 3.5+ idioms (lint-enforced where possible):
  - reactive props destructure `const { x = 1 } = defineProps<{...}>()`, NOT `withDefaults`
  - `useTemplateRef('name')` with NO generic (3.5 infers; explicit generics go stale)
  - `ref<T>()` over `ref<T | null>(null)`
  - `<script setup lang="ts">` only; type-based `defineProps`/`defineEmits`; `<style scoped>`
- Every `VITE_` env var must be declared in `env.d.ts` (it's a type error
  otherwise) — see [docs/CONFIG.md](docs/CONFIG.md).
- No `console.log` in src — route through `src/utils/logger.ts`.

### Backend

- `TreatWarningsAsErrors` + `AnalysisLevel latest-recommended` solution-wide —
  zero warnings, and **never dismiss a warning as pre-existing**; the build was
  clean before your change.
- Modern C#: primary constructors, records, collection expressions `[1, 2]`,
  file-scoped namespaces, braces on every control block.
- **Never `DateTime.UtcNow`** — inject `TimeProvider` (registered as singleton).
  Build-enforced: `BannedSymbols.txt` (BannedApiAnalyzers) makes wall-clock
  reads and sync-over-async (`Task<T>.Result`, `Task.Wait`) compile errors.
- Prefer `[LoggerMessage]` source-generated logging (see the middleware and
  exception handler for the pattern); pass `PathString`/lazy values, not
  eagerly formatted strings (CA1873).
- Naming: `_camelCase` private fields, `I`-prefixed interfaces
  (`.editorconfig` enforces).

## Dependency Security

- npm: **carets + committed lockfile**; `npm ci` gives deterministic installs.
  (`save-exact` is a valid stricter posture — if you adopt it, beware `ncu -u`
  reintroduces carets; grep for `"^` after bulk updates.)
- npm install scripts are disabled (`.npmrc ignore-scripts=true`). A new
  dependency that needs its postinstall must be added to `rebuild-trusted` in
  `vueapp1.client/package.json` — and that's a code-review event.
- NuGet: central package management (`Directory.Packages.props`), lockfiles +
  locked-mode in CI. **Microsoft.OpenApi must stay 2.x** (ASP.NET Core 10
  compiles against 2.x; tooling will suggest 3.x — it's a trap).
- New packages: check publisher, repo activity, and typosquats before adding.

## Agent Workflow

1. **Read the full issue/PR conversation first** — comments often contain the
   actual spec, constraints, and previously rejected approaches.
2. **TDD when fixing bugs**: write the failing test, RUN it to see it fail,
   then fix. A test you never saw fail proves nothing.
3. **Before committing**: `npm run check` green, zero warnings. A lint rule is
   a PROXY for an intent — never reshape code just to slip past it; satisfy
   the intent or discuss the rule.
4. **Never commit to main.** Feature branch + PR, Conventional Commit
   messages and PR titles (CI enforces the title).
5. **Don't re-run expensive commands when nothing changed** — read the
   existing output (disk logs, CI artifacts) instead.
6. **Bot/AI review comments are leads, not verdicts** — validate each against
   the current code before acting (see `.claude/agents/comment-reviewer.md`).
7. **Existing patterns aren't automatically correct.** If the codebase
   contradicts a better approach, escalate with a recommendation instead of
   silently copying or silently "fixing".
8. **Capture learnings**: non-obvious fixes go into `docs/*.md` in the same PR.
9. **Scripts budget**: `scripts/` may not grow — prefer npm one-liners and
   documented patterns over new script files (current budget:
   openapi-contract, server-process, load-test, rename, run-dotnet-test, run-vitest).

## Other Agent Runtimes

The agentic layer is built to work from first clone in any runtime, not just
Claude Code:

- **This file (AGENTS.md)** is the cross-runtime entry point — Copilot,
  Codex, and most agent CLIs read it natively.
- **Skills** use the [agentskills.io](https://agentskills.io) SKILL.md
  format. The source of truth is `.claude/skills/`; `.agents/skills/` is a
  real-copy mirror for runtimes that only scan that path (e.g. Codex CLI).
  Edit the `.claude` copy, then run `npm run skills:sync` — CI fails on
  drift. (`.claude/agents/` subagents are Claude Code-specific; the skills
  are the portable surface.)
- **Browser tooling** for live verification is documented in
  `.claude/skills/verify-in-browser/SKILL.md` (Playwright MCP, npx-based,
  no secrets); runtimes that ship their own browser tools can use those
  instead.
- **Copilot coding agent** gets its environment from
  `.github/workflows/copilot-setup-steps.yml` — see
  [docs/GITHUB.md](docs/GITHUB.md) for the job-name contract and firewall
  model.

## Agent Environment Notes

- The repo must build and test with **zero secrets** — placeholder config
  boots the backend (design goal; keep it true).
- **Runtime client errors appear in dev-server stdout**: Vite's
  `server.forwardConsole` (explicit in vite.config.ts) forwards browser
  `console.error`/`warn` and unhandled errors/rejections to the terminal
  running `dev:client`/`dev:server` — read them there before reaching for
  browser tooling (needs a connected browser session; see docs/FRONTEND.md).
- Sandboxed runtimes (Codex-class): MSBuild/npm need network + elevated
  permissions for restore/build/test — request escalation with a one-line
  justification for any command that restores or installs.
- `dotnet list package --outdated` exits 1 on this solution (the esproj
  doesn't support it) — iterate the three csproj files instead.
- Read-only subagent types cannot Edit/Write files — dispatch file-modifying
  tasks to a general-purpose agent type.
