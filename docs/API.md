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
API-surface change: `npm run openapi:sync` and commit the diff. The sync also
regenerates the frontend's compile-time view of the contract
(`vueapp1.client/src/contracts/api.gen.ts`, see docs/FRONTEND.md "Generated
API types"), and `openapi:check` gates both artifacts. The harvested
`servers` array is stripped (it embeds the ephemeral localhost port). XML doc
comments flow into the document (`GenerateDocumentationFile`) and render in
Scalar (`/scalar/v1`, Development). RFC 9727 catalog at
`/.well-known/api-catalog`.

## Error contract in the OpenAPI document

A committed contract is only as good as its truthfulness, and ASP.NET Core
does not document error responses on its own — without help the contract
would show a 200-only API while the runtime answers RFC 9457 on every error.
The transformer pack in `VueApp1.Server/OpenApi/` closes that gap
mechanically, for current and future endpoints (no per-action
`ProducesResponseType` discipline needed):

- `RateLimitResponseTransformer` — the rate limiter is a `GlobalLimiter`, so
  every operation documents a 429 with a `Retry-After` header (delta-seconds,
  marked `required`: `OnRejected` sets it unconditionally, so documenting it
  as optional would make generated clients null-check a guarantee) and a
  problem+json body. A per-action 429 declaration wins — the transformer only
  fills the gap the global limiter would leave undocumented. `RateLimit-*`
  headers are deliberately NOT documented: the runtime never emits them and
  the IETF draft defining them is still unstable — documenting them would be
  a new contract lie.
- `ProblemDetailsContentTypeTransformer` — ApiExplorer describes declared
  4xx/5xx responses differently per declaration shape (verified empirically;
  the test-assembly probe controller in `OpenApiDocumentContractTests` pins
  each one): a typed `ProducesResponseType` as content-negotiated
  `application/json` + `text/plain`/`text/json`, a bodiless declaration as no
  content at all — EXCEPT on a `[Produces]`-annotated action, where it is an
  EMPTY `application/json` media type (content present, no schema) whose
  naive relabel would ship an untyped error body. This pass rewrites every
  error response to a single `application/problem+json` entry, preserving a
  declared schema and backfilling `ProblemDetails` when none was declared.
- `CanonicalJsonContentTransformer` — there is deliberately NO class-level
  `[Produces("application/json")]` on `ApiControllerBase`: `ProducesAttribute`
  is a result filter that REPLACES the content types on every `ObjectResult`,
  which relabels RFC 9457 error bodies (the automatic 400
  `ValidationProblemDetails`, filter-produced problems) as plain
  `application/json` **on the wire** — found when the Idempotency-Key filter's
  422 came back mislabeled. Without `[Produces]`, though, ApiExplorer
  describes every typed 2xx as `text/plain` + `application/json` + `text/json`
  and JSON request bodies as three alias types; this pass collapses success
  responses and request bodies to the canonical `application/json` entry
  (`text/plain` is a lie for object results — no registered formatter writes
  objects as plain text).
- `ComputedPropertySchemaTransformer` — get-only computed properties (e.g.
  `WeatherForecast.TemperatureF`) are serialized on every response, but the
  schema exporter only marks deserialization-required members as `required`;
  this marks them `required` + `readOnly` so generated clients don't treat
  them as optional.
- JSON number handling is `Strict` (Program.cs, both MVC and minimal-API
  options): the Web default `AllowReadingFromString` makes the exporter
  document every int as an `["integer","string"]` union — which generated
  TypeScript clients inherit as `number | string`.

The runtime side of the 429 lives in the rate limiter's `OnRejected`: it
writes through `IProblemDetailsService.TryWriteAsync` (a bare
`WriteAsJsonAsync` mislabels the body as plain `application/json`; the `Try`
variant degrades to a bodiless 429 — status and `Retry-After` already set —
when no writer satisfies an exotic `Accept` header, instead of throwing into
the exception handler) and derives `Retry-After` from the rejected lease's
`MetadataName.RetryAfter` (time until the window resets), falling back to
the configured window length.
`OpenApiDocumentContractTests` pins all of these invariants.

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
- Registering a schema component from a transformer: ASP.NET Core's
  `AddOpenApiSchemaByReference` extension is internal — use
  `document.AddComponent<IOpenApiSchema>(id, schema)` plus
  `new OpenApiSchemaReference(id, document)` (both public OpenAPI.NET 2.x).
- Integration tests disable hosting startup
  (`PreventHostingStartupKey`) so SpaProxy never launches inside tests.
