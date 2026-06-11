# Documented patterns

Patterns this template deliberately ships as documentation instead of code.
A starter template earns its keep by staying lean: these become relevant when
your app grows a particular feature, and pasting a documented pattern beats
deleting shipped abstractions you never needed.

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

**Now shipped as code**: the opt-in MCP module brought
`VueApp1.Server/Mcp/ValidateOriginFilter.cs` into the tree (an
`IEndpointFilter`; semantics in [MCP.md](MCP.md), matching tests in
`ValidateOriginFilterTests`). Reuse it beyond `/mcp` the moment you add
token-authenticated POST endpoints — browsers always send `Origin` on
cross-origin requests, and DNS-rebinding attacks reach `localhost`-bound
services with an attacker-controlled Origin. The contract:

1. allowlist derives from the existing `AllowedHosts` configuration
   (zero per-environment upkeep; `*` disables the check),
2. requests **without** an Origin header pass (non-browser clients),
3. requests whose Origin is present but not allowlisted get a 403 with a
   ProblemDetails body,
4. subdomain wildcards follow host-filtering semantics (`*.example.com`
   matches subdomains, never the apex).

**Scope caveat**: an `IEndpointFilter` runs only for minimal-API endpoints
mapped onto the filtered builder — it never executes for the template's
attribute-routed controller actions (`MapControllers` sits outside any
group), the same minimal-API-only scoping as the `AddValidation()` note
above. For controller endpoints, port the same checks into middleware or an
MVC action filter instead.

```csharp
var admin = app.MapGroup("/api/admin")
    .AddEndpointFilter(new ValidateOriginFilter(app.Configuration));
// The filter guards endpoints mapped onto THIS group builder:
admin.MapPost("/reindex", () => Results.Accepted());
```

## Idempotency-Key convention

For unsafe endpoints that clients may retry (mobile networks, agent tools):
accept an `Idempotency-Key` header, store `(key, response)` for a TTL window
(HybridCache fits), and replay the stored response on key reuse. Combine with
an `AsyncLocal`-backed request-context accessor when services deep in the
stack need the key without parameter-threading.

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
