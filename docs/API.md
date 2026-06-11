# API

ASP.NET Core (.NET 10) Web API. Architecture rules live in
[AGENTS.md](../AGENTS.md); this page is the deep dive.

## Error contract (RFC 9457 everywhere)

Every non-2xx response is `application/problem+json`:

- **Service layer**: return `ServiceResponse` / `ServiceResponse<T>` — never
  throw for expected failures. Helpers: `BadRequest`, `NotFound`, `Conflict`,
  `PreconditionFailed` (412), `UnprocessableEntity` (422). Give recurring,
  client-actionable errors a stable `ProblemDetailTypes` URI so frontends
  branch on `type`, not on detail strings.
- **Controllers**: `HandleServiceResponse(...)` maps results to HTTP,
  forwarding `Details.Status` so new statuses need no new enum members.
- **Unhandled exceptions**: `ApiProblemDetailsExceptionHandler` stamps
  `traceId` (and, in Development only, the message + exception type).
- **Status-code pages** (`UseStatusCodePages`) cover errors produced outside
  MVC, and the `/api` fallback 404 is a ProblemDetails too.

## OpenAPI contract gate

`docs/openapi/openapi.v1.json` is committed; CI regenerates the document by
booting the real server (Testing environment) and fails on drift. After any
API-surface change: `npm run openapi:sync` and commit the diff. The harvested
`servers` array is stripped (it embeds the ephemeral localhost port). XML doc
comments flow into the document (`GenerateDocumentationFile`) and render in
Scalar (`/scalar/v1`, Development). RFC 9727 catalog at
`/.well-known/api-catalog`.

## Pipeline order (and why)

1. Security headers (every response, including errors, gets them)
2. Exploit-probe denylist (scanner noise dies before routing/telemetry)
3. Exception handler + status-code pages
4. HTTPS redirect → compression → routing → Server-Timing → CORS →
   (request timeouts) → rate limiter → output cache → authorization
5. Static assets (`MapStaticAssets`, fingerprint-cached) → controllers →
   health probes → SPA fallback (no-cache `index.html`; ProblemDetails 404
   for `/api`)

## Health probes

The orchestrator pair, wired with the standard tag-filter idiom:

- `/health/live` — liveness: runs **no** checks (`Predicate = _ => false`),
  answers only "is the process up?". A dependency outage must never make an
  orchestrator restart a healthy process.
- `/health/ready` — readiness: runs the `"ready"`-tagged checks (empty by
  default; the `AddDbContextCheck` seam in [docs/DATA.md](DATA.md) plugs in
  here), so a failing dependency drains traffic instead of killing the pod.
- `/health` — readiness-filtered alias for single-path consumers (uptime
  monitors, platform defaults that probe a single path). Deliberately NOT
  unfiltered: an unfiltered catch-all probed as liveness would reintroduce
  the restart-on-dependency-blip foot-gun.

The service worker's `navigateFallbackDenylist` (`/^\/health/`) already
covers all three paths.

## Observability

OpenTelemetry is config-gated (`OpenTelemetry:Enabled`). Exporter endpoint:
`OpenTelemetry:Otlp:Endpoint` config key, or the standard
`OTEL_EXPORTER_OTLP_ENDPOINT` env var. Any `ActivitySource`/`Meter` under the
`VueApp1.*` namespace is collected automatically (wildcard registration) —
see `WeatherForecastService` for the span pattern. For a local traces UI with
zero code changes:

```bash
docker run --rm -p 18888:18888 -p 4317:18889 mcr.microsoft.com/dotnet/aspire-dashboard
OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317 npm run dev:server   # + OpenTelemetry__Enabled=true
```

`Server-Timing` response headers expose per-request duration in browser
DevTools; implement `IServerTimingMetrics` to append entries (EF wiring in
docs/PATTERNS.md).

## Hard-won notes

- `UseStatusCodePages()` is needed ALONGSIDE `UseExceptionHandler()` for
  ProblemDetails on all HTTP errors.
- Microsoft.OpenApi stays 2.x (ASP.NET Core 10 compiles against it) — see
  Directory.Packages.props.
- xUnit v3: package `xunit.v3`, namespace `using Xunit;` unchanged.
- OpenAPI.NET 2.0: `JsonNode` replaces `OpenApiAny`; `JsonSchemaType` is
  bitwise flags for nullable.
- Integration tests disable hosting startup
  (`PreventHostingStartupKey`) so SpaProxy never launches inside tests.
