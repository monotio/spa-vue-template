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
3. User secrets (`dotnet user-secrets`, Development only — works out of the
   box: `VueApp1.Server.csproj` ships a stable `<UserSecretsId>`, which both
   the CLI and the default builder's secrets layer require)
4. Environment variables — `Section__Key` maps to `Section:Key`
   (double underscore = section separator)
5. Command-line arguments

Production deployments override via environment variables, e.g.:

```bash
Performance__RateLimiting__PermitLimit=1000
OpenTelemetry__Otlp__Endpoint=http://collector:4317
```

### Options are validated at boot (fail fast)

The backend twin of `strictImportMetaEnv` on the frontend: configuration is a
validated contract, not a best-effort guess. `Performance` and `PublicUri` are
bound with `AddOptions<T>().Bind(...).ValidateOnStart()` plus a hand-written
`IValidateOptions<T>` validator for range and cross-field invariants (see
`PerformanceTuningOptionsValidator`).

- **Unknown keys fail the bind** (`ErrorOnUnknownConfiguration`): a typo like
  `Performance__RateLimitting__PermitLimit` is a boot error naming the
  unrecognized property — not a silent fallback to defaults.
- **Invalid values fail at host start** (`ValidateOnStart`), pre-traffic, with
  every violation listed in one `OptionsValidationException` and each message
  naming the exact configuration key.
- `Performance` is also consumed *before* `Build()` (it configures Kestrel,
  caching and rate limiting), so Program.cs validates that eager instance
  directly — `ValidateOnStart` alone only guards the DI-resolved copy.

When adding an options class: bind it in `SetupOptionsValidation` the same
way, write a validator if any value has an invariant worth a precise boot
error, and keep the committed defaults valid — the zero-secrets boot below is
a design goal.

### Collections bind by MERGE — never pre-populate a list you want to narrow

Two binder behaviors that silently defeat "tighten via config":

- **The binder merges into code defaults.** Binding a collection ADDS the
  configured entries to whatever the property was initialized with; it never
  removes code-default entries. A property initialized to a 7-item list plus
  a 1-item config list binds to 8 items. For an allowlist this is a one-way
  ratchet: config can widen it but never narrow it. Rule: default such
  properties to `[]` in code, ship the real default list in
  `appsettings.json`, and let the validator reject an empty result
  (`AgentAttachmentOptions.AllowedContentTypes` is the executed example).
- **Overlays replace arrays index-by-index.** A shorter array in
  `appsettings.{Environment}.json` or env vars (`Section__List__0`) overrides
  only the indexes it provides; higher indexes survive from the base layer.
  Narrow a list by editing the base `appsettings.json`, not by overlaying a
  shorter one.

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
