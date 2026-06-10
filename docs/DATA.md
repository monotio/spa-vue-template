# Database — decision guide

The template deliberately ships **no database**: a starter that picks your
database picks wrong for half its users, and every seam a DbContext needs is
already in place. This page maps where one slots in.

## Recommended default: EF Core

- **SQLite** for the smallest start (file-based, zero infra) — swap the
  provider later; EF Core makes that mostly config.
- **PostgreSQL** (Npgsql) as the boring-and-right production default.
- Add `Microsoft.EntityFrameworkCore.<Provider>` versions to
  `Directory.Packages.props` (central package management — no versions in
  csproj).

## Where a DbContext plugs into the existing seams

| Seam | What to do |
| --- | --- |
| Service layer | Inject the DbContext into services; keep returning `ServiceResponse<T>` — error mapping stays unchanged. |
| Health checks | Uncomment the `AddDbContextCheck<AppDbContext>()` line already stubbed in Program.cs's health-check setup. |
| Server-Timing | Implement `IServerTimingMetrics` with an EF `DbCommandInterceptor` — per-request `db;dur=…` in browser DevTools ([docs/PATTERNS.md](PATTERNS.md)). |
| OpenTelemetry | Add the EF Core instrumentation package; spans join the existing `VueApp1.*` pipeline. |
| Config | Connection string via the standard appsettings layering ([docs/CONFIG.md](CONFIG.md)); keep the zero-secrets-boot rule — local dev should work with a default/localdb string. |

## Testing strategy

- **SQLite in-memory** keeps integration tests fast and infra-free, at the
  cost of provider-behavior gaps (no real concurrency semantics, different
  SQL dialect edge cases).
- **Testcontainers** (real PostgreSQL in Docker per test run) closes those
  gaps at the cost of Docker-in-CI; the existing
  `IntegrationTestWebApplicationFactory` is the place to wire either.
- Whichever you choose: migrations are part of the contract — run them in
  the factory so tests fail when a migration is missing.

## Rules that activate once EF lands

The ast-grep cookbook (docs/AST_GREP_GUIDE.md) lists EF-contingent guardrails
deliberately left out of the starter set: no sync `SaveChanges()`,
`AsNoTracking()` for read-only queries, ChangeTracker clearing in batch loops.
Adopt them with the layer.
