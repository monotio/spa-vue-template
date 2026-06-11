# Documented patterns

Patterns this template deliberately ships as documentation instead of
always-on code. A starter template earns its keep by staying lean: most
entries are paste-when-needed recipes, and where correctness rules are
code-shaped a dormant seam ships instead (`Idempotency/`, `BackgroundWork/`)
— those sections carry the upgrade paths beyond the shipped seam.

## Minimal-API variant of the controller pattern

The template uses controllers (`ApiControllerBase` + `HandleServiceResponse` +
`ServiceResponse<T>`) because that pipeline is its teaching core. Microsoft
recommends minimal APIs for new projects; if you prefer them, the same
service layer plugs in with a small adapter:

```csharp
public static class ServiceResponseExtensions
{
    public static IResult ToTypedResult<T>(this ServiceResponse<T> response) =>
        response.Result switch
        {
            ServiceResult.Success => TypedResults.Ok(response.Value),
            ServiceResult.NotFound => TypedResults.NotFound(),
            ServiceResult.NotAuthorized => TypedResults.Forbid(),
            _ => TypedResults.Problem(
                detail: response.Details?.Detail,
                statusCode: response.Details?.Status ?? StatusCodes.Status400BadRequest,
                type: response.Details?.Type),
        };
}

var api = app.MapGroup("/api/weatherforecast");
api.MapGet("/", async (IWeatherForecastService service, CancellationToken ct) =>
    (await service.GetForecastsAsync(ct)).ToTypedResult());
```

Note: `builder.Services.AddValidation()` (Microsoft.Extensions.Validation)
applies **only to minimal API endpoints** — add it if you adopt this variant.
Controllers already get DataAnnotations validation through MVC; with zero
minimal endpoints it is dead code, which is why the template doesn't call it.

## Origin-header validation (DNS-rebinding / CSRF defense-in-depth)

Relevant the moment you add token-authenticated POST endpoints or an MCP
surface. Browsers always send `Origin` on cross-origin requests; DNS-rebinding
attacks reach `localhost`-bound services with an attacker-controlled Origin.
Pattern: an endpoint filter that

1. derives its allowlist from the existing `AllowedHosts` configuration
   (zero per-environment upkeep; `*` disables the check),
2. allows requests **without** an Origin header (non-browser clients),
3. rejects requests whose Origin is present but not allowlisted (403 with a
   ProblemDetails body),
4. honors subdomain wildcards with the same semantics as host filtering.

```csharp
public sealed class ValidateOriginFilter(IConfiguration config) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var origin = context.HttpContext.Request.Headers.Origin;
        if (StringValues.IsNullOrEmpty(origin) || IsAllowed(origin!, config["AllowedHosts"]))
        {
            return await next(context);
        }
        return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden,
            title: "Origin not allowed.");
    }
    // IsAllowed: '*' => true; otherwise compare origin host against the
    // semicolon-separated AllowedHosts list, honoring leading-dot wildcards.
}
```

## Idempotency-Key: cross-process upgrades

The seam itself ships as code (`Idempotency/`, demonstrated on
`FeedbackController`) because its correctness rules are code-shaped and prose
re-implementations lose them: payload-hash + response committed as ONE atomic
record; commit under `CancellationToken.None` (a client disconnect after the
mutation must not skip the commit and let the retry duplicate it);
dispose-without-commit stores nothing (failures don't poison the key);
fast-path read plus a double-check under the lock. The in-memory defaults are
**single-node**; behind a load balancer, upgrade both halves:

- **Storage**: register a real `IDistributedCache` (e.g.
  `AddStackExchangeRedisCache`) — `IdempotencyService` needs no changes.
  Note this also gives `HybridCache` a distributed L2 (it adopts any real
  `IDistributedCache` automatically; the in-memory default is special-cased
  and ignored) — one registration upgrades both consumers, by design.
- **Lock**: implement `IIdempotencyLock` cross-process. Two known-good shapes:
  - *SQL advisory lock* (when you've adopted a DB — docs/DATA.md): PostgreSQL
    `SELECT pg_try_advisory_lock(hashtext(@key))` on a connection you hold
    open until dispose (`pg_advisory_unlock` + return to pool), or SQL Server
    `sp_getapplock @Resource=@key, @LockOwner='Session', @LockTimeout=0`.
    Non-blocking acquire (`try`/timeout 0) is the point — a held key maps to
    409 InProgress, not a queue of waiting requests.
  - *Redis*: `SET key value NX PX <ttl>` as acquire; release with a
    compare-and-delete script on the stored value so an expired holder can't
    delete its successor's lock. The TTL bounds orphaned locks if a node dies
    mid-request.

## Scoped DI factory: HTTP vs background context

When a dependency needs different implementations inside and outside a
request (e.g. current-user accessor), register a factory that switches on
`IHttpContextAccessor.HttpContext`:

```csharp
builder.Services.AddScoped<ICurrentUser>(sp =>
    sp.GetRequiredService<IHttpContextAccessor>().HttpContext is { } http
        ? new HttpCurrentUser(http)
        : new SystemCurrentUser());
```

Scope this fallback narrowly: `SystemCurrentUser` is for work that genuinely
has no human cause (startup tasks, schedules). Work enqueued **on behalf of a
user** must carry the user's identity through an explicit envelope instead —
an ambient "system" default at that boundary silently misattributes
user-initiated work in logs, traces and audit trails. That is why the shipped
background queue (`BackgroundWork/`, [docs/BACKGROUND.md](BACKGROUND.md))
fails closed: `EnqueueAsync` requires an initiator stamp and throws rather
than guessing.

## Server-Timing entries from EF Core

`ServerTimingMiddleware` exposes `IServerTimingMetrics` (see the middleware's
comments). Wire a `DbCommandInterceptor` that accumulates command duration and
count into an `AsyncLocal` holder, and a scoped `IServerTimingMetrics` that
reads it: the browser DevTools network panel then shows
`db;dur=12.3;desc="4 queries"` per API call. The middleware snapshots inside
`Response.OnStarting`, which preserves the request's `ExecutionContext` —
required for `AsyncLocal` to resolve.

## Optimistic UI list updates

Shape that composes with `useAbortableRequest`: seed the list with an
optimistic item (client-generated id + timestamp injected as options for
testability), reconcile by identity when the server responds (replace the
optimistic item with the real one), and defensively clear/rollback on error.
Keep `generateId` and `now` injectable — the composable becomes trivially
unit-testable without fake timers.
