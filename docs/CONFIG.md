# Configuration

How configuration flows through both halves of the stack, and the workflow for
adding a new setting.

## Frontend (Vite environment variables)

Only variables prefixed with `VITE_` are exposed to client code — everything
else is stripped at build time. Values are static at build time; the built
`dist/` has them baked in.

Precedence (highest wins) for a given mode (`development` / `production`):

1. `.env.[mode].local` (gitignored)
2. `.env.[mode]`
3. `.env.local` (gitignored)
4. `.env`

### Adding a new frontend variable

1. Add it to `vueapp1.client/.env.example` (documents it for every clone).
2. Declare it in `vueapp1.client/env.d.ts` under `ImportMetaEnv`. This template
   enables `strictImportMetaEnv`, so **an undeclared variable is a type error**
   at the `import.meta.env.VITE_...` access site — the declaration is the
   contract.
3. Set a real value in `.env` (shared default) or `.env.local` (per-machine
   secret-ish values; gitignored — but remember anything `VITE_` ends up in the
   shipped bundle, so never put actual secrets in frontend env vars).

## Backend (ASP.NET Core configuration)

Layered, last-one-wins:

1. `appsettings.json` — committed defaults
2. `appsettings.{Environment}.json` — e.g. `appsettings.Development.json`
3. User secrets (`dotnet user-secrets`, Development only)
4. Environment variables — `Section__Key` maps to `Section:Key`
   (double underscore = section separator)
5. Command-line arguments

Production deployments override via environment variables, e.g.:

```bash
Performance__RateLimiting__PermitLimit=1000
OpenTelemetry__Otlp__Endpoint=http://collector:4317
```

### Test environment

The integration tests (`WebApplicationFactory`) run with the `Testing`
environment, so `appsettings.Testing.json` (if present) layers on top of
`appsettings.json` for test-only overrides. The template's design goal is that
the backend **boots with zero secrets** — placeholder/default config must be
enough to run and test it. Keep it that way when adding services.

## Ports

| What | Where | Default |
| --- | --- | --- |
| Vite dev server | `DEV_SERVER_PORT` env var (vite.config.ts) | 57292 |
| Backend HTTPS | `VueApp1.Server/Properties/launchSettings.json` | 7191 |
| Vite proxy target | `ASPNETCORE_HTTPS_PORT` / `ASPNETCORE_URLS` env vars | https://localhost:7191 |
