# ast-grep guardrails (opt-in)

[ast-grep](https://ast-grep.github.io/) enforces structural rules ESLint and
analyzers can't express cheaply — and doubles as machine-enforced AGENTS.md:
**when you fix a recurring agent (or human) mistake, encode it as a rule** so
the lesson never depends on anyone re-reading a doc.

The repo ships `sgconfig.yml` + 8 starter rules in `.ast-grep/rules/`, each
enforcing a convention the template documents. The layer is **opt-in** — it is
NOT wired into `npm run check` or CI by default.

## Adopting it (one-time)

```bash
cd vueapp1.client
npm install -D @ast-grep/cli@0.43.0
# its postinstall places the platform binary; ignore-scripts is on, so:
#   1. add @ast-grep/cli to the rebuild-trusted script in package.json
#   2. npm run rebuild-trusted
```

Then add scripts at the repo root and (optionally) wire them into `check`/CI:

```json
"lint:ast-grep": "vueapp1.client/node_modules/.bin/ast-grep scan -U",
"lint:ast-grep:ci": "vueapp1.client/node_modules/.bin/ast-grep scan --format github"
```

Measure the wall-time impact on `npm run check` before making it mandatory.

## The starter rules

| Rule | Enforces |
| --- | --- |
| `cs-no-direct-utcnow` | inject `TimeProvider`, never `DateTime.UtcNow` (now also build-enforced, see below) |
| `cs-if-without-braces` | braces on every control block |
| `vue-no-withdefaults` | reactive props destructure (Vue 3.5+) |
| `vue-ref-null-redundant` | `ref<T>()` over `ref<T \| null>(null)` (autofix) |
| `vue-no-usetemplateref-generic` | let `useTemplateRef` infer (autofix) |
| `ts-no-console-log` | route logging through `src/utils/logger.ts` |
| `ts-no-per-test-timeout` | fix flakes, don't widen timeouts (docs/TESTING.md) |
| `ts-no-waitfor-under-fake-timers` | deterministic settles under fake timers |

The starter set found (and fixed) a real violation in the template's own
weather store on first run — that's the bar for a good rule: it catches
things, not hypotheticals.

> **`cs-no-direct-utcnow` is now build-enforced.** The repo-root
> `BannedSymbols.txt` (Microsoft.CodeAnalysis.BannedApiAnalyzers, wired via
> `Directory.Packages.props`/`Directory.Build.props`) turns
> `DateTime`/`DateTimeOffset` `.Now`/`.UtcNow` and sync-over-async
> (`Task<T>.Result`, `Task.Wait`) into RS0030 compile errors in every csproj —
> wider coverage than this rule (which matches `UtcNow` only, in
> `VueApp1.Server/**`) and impossible to skip because it rides on
> `dotnet build` inside `npm run check`. The ast-grep rule stays for cheap
> in-editor feedback; the banned list is the gate.

## Writing rules

- Simple shapes: `pattern:` with metavariables (`$NAME`, `$$$ARGS`).
- What patterns can't match (e.g. "an if statement whose body is NOT a
  block"): use the YAML rule object with `kind:` + `not:`/`has:` — see
  `cs-if-without-braces.yml`.
- **Gotcha**: in composite rules, `any:`/`all:` ordering matters for
  metavariable capture — put the capturing alternative first.
- Reaching inside `.vue` SFCs: `sgconfig.yml` maps `*.vue` to HTML and
  injects TS for `<script setup lang="ts">` blocks — TS rules then apply
  inside components. Both attribute orders are mapped; if you use another
  (`<script setup generic="..." lang="ts">`), add an injection.
- Give every rule a `message` (what) and a `note` with BAD/GOOD examples
  (why + how) — agents read these verbatim when a scan fails.
- Autofixes must be semantics-preserving: `vue-no-withdefaults` deliberately
  has NO fix because dropping the defaults object would change behavior.

## Rule ideas for grown-up codebases

Worth adding once the corresponding subsystem exists (kept out of the starter
set to avoid dead rules — sync-over-async moved to the build-enforced banned
list above): no `SaveChanges()` (sync) once EF lands,
ProblemDetails-shape enforcement in
services (`ServiceResponse` factories over inline `new ProblemDetails`),
banning `as unknown as T` assertion chains, `no-force-update`.
