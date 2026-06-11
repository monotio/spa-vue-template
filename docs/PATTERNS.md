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

## Outbound HTTP: typed clients, idempotent-only retries

The template makes no outbound HTTP calls; the commented seam in Program.cs
(next to `AddHybridCache`) is where the first external API lands. The
discipline for when it does:

- **One typed client per upstream** (`AddHttpClient<TClient>`): base address
  and default headers set at registration, settings bound and validated at
  construction — the same fail-fast posture as [docs/CONFIG.md](CONFIG.md).
- **Identifying `User-Agent` on every request** — upstream operators
  throttle, allowlist, and debug by it; anonymous traffic gets the blunt
  treatment.
- **Retries apply ONLY to idempotent methods.** `AddStandardResilienceHandler()`
  retries unsafe methods by default (still true through
  Microsoft.Extensions.Http.Resilience 10.x — the maintainers kept the
  default for compatibility and shipped the opt-out instead): an auto-retried
  POST is a duplicate payment, a double webhook delivery, a second email.

```csharp
builder.Services.AddHttpClient<GeocodingClient>(client =>
    {
        client.BaseAddress = new Uri("https://geocoding.example.com/");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("vueapp1/1.0 (+https://your-app.example)");
    })
    // Retry GET/HEAD/... only; failed POST/PATCH/DELETE surface to the caller.
    .AddStandardResilienceHandler(options => options.Retry.DisableForUnsafeHttpMethods());
```

Retrying a write safely requires the upstream to honor an idempotency key —
send one per logical operation and make that client's retry policy explicit,
never inherited. (The inbound twin of this rule is the shipped `Idempotency/`
seam below.)

## SSRF-safe outbound requests (user-supplied URLs)

The first outbound feature most apps grow — webhooks, link previews, "import
from URL" — fetches a **user-supplied URL**, and naive `HttpClient` use
against one is a cloud-credential-theft vector: on IMDS-style clouds,
`http://169.254.169.254/` answers with role credentials (the breach class
behind several headline cloud incidents). Two things make string-level URL
validation insufficient:

1. **Hostname checks race DNS** (TOCTOU / DNS rebinding): the attacker
   registers `hooks.attacker.example`, points it at a harmless IP while you
   validate, then re-points it at `169.254.169.254` before you connect.
   Resolve-validate-connect as ONE step is the OWASP-aligned fix.
2. **Each redirect is a fresh SSRF vector**: the validated URL can answer
   `302 Location: http://10.0.0.5/admin` — a target no validator ever saw.

`SocketsHttpHandler.ConnectCallback` runs after the handler knows the target
host but **before any TCP connect** — the one seam where you can resolve DNS
yourself, validate every resolved address, and connect only to what you
validated. As of .NET 10 the BCL has no built-in private-range guard, so this
is the pattern to copy (reference: OWASP SSRF Prevention Cheat Sheet):

```csharp
using System.Net;
using System.Net.Sockets;

/// <summary>
/// Handler factory for fetching user-supplied URLs: resolves DNS itself,
/// rejects private/reserved/cloud-metadata addresses, connects only to the
/// addresses it validated, and never follows redirects.
/// </summary>
public static class SsrfSafeHandlerFactory
{
    // Named constants so a security review can diff them against the IANA
    // special-use registries. Keep the per-range comments when you copy.
    private static readonly IPNetwork[] BlockedIPv4Ranges =
    [
        IPNetwork.Parse("0.0.0.0/8"),      // "this network"
        IPNetwork.Parse("10.0.0.0/8"),     // RFC 1918 private
        IPNetwork.Parse("100.64.0.0/10"),  // CGNAT — cloud-internal (some metadata services live here)
        IPNetwork.Parse("127.0.0.0/8"),    // loopback
        IPNetwork.Parse("169.254.0.0/16"), // link-local — incl. 169.254.169.254 (cloud metadata)
        IPNetwork.Parse("172.16.0.0/12"),  // RFC 1918 private
        IPNetwork.Parse("192.0.0.0/24"),   // IETF protocol assignments
        IPNetwork.Parse("192.168.0.0/16"), // RFC 1918 private
        IPNetwork.Parse("198.18.0.0/15"),  // benchmarking
        IPNetwork.Parse("224.0.0.0/4"),    // multicast
        IPNetwork.Parse("240.0.0.0/4"),    // reserved + broadcast
    ];

    // Metadata hostnames that DNS may resolve to non-blocked relays.
    private static readonly string[] BlockedHosts = ["metadata.google.internal"];

    public static SocketsHttpHandler Create() => new()
    {
        // Redirects off: surface 3xx to the caller; follow manually only if
        // you re-validate every hop with this same handler.
        AllowAutoRedirect = false,
        ConnectCallback = static async (context, cancellationToken) =>
        {
            // Resolve HERE, validate ALL addresses, connect only to them.
            // Anything that re-resolves later (or trusts an earlier
            // validation) reopens the rebinding window.
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
            if (addresses.Length == 0 || addresses.Any(IsBlocked))
            {
                throw new HttpRequestException(
                    $"Refusing to connect to '{context.DnsEndPoint.Host}': resolves to a reserved address.");
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    public static bool IsBlocked(IPAddress address)
    {
        // Normalize first: ::ffff:169.254.169.254 must hit the IPv4 table.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6
            ? IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal
                || address.IsIPv6UniqueLocal || address.IsIPv6SiteLocal
                || address.IsIPv6Multicast
            : BlockedIPv4Ranges.Any(range => range.Contains(address));
    }

    /// <summary>
    /// Registration-time validation — fast feedback for the user; the
    /// ConnectCallback above remains the security boundary.
    /// </summary>
    public static bool IsAcceptableUserUrl(string input)
    {
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            return false;
        }

        // IdnHost: punycoded, bracket-free. Trailing dot ("example.com.")
        // resolves identically but dodges string comparisons — normalize it.
        var host = uri.IdnHost.TrimEnd('.');
        return uri.Scheme == Uri.UriSchemeHttps      // no plaintext, no scheme games
            && uri.UserInfo.Length == 0              // "https://trusted@evil" confusion class
            && !BlockedHosts.Contains(host, StringComparer.OrdinalIgnoreCase)
            && !(IPAddress.TryParse(host, out var literal) && IsBlocked(literal));
    }
}
```

Wire-up:
`AddHttpClient<LinkPreviewClient>().ConfigurePrimaryHttpMessageHandler(SsrfSafeHandlerFactory.Create)`.
If local development needs to hit a loopback receiver, make that an explicit
constructor/factory parameter — never a mutable static toggle, and never the
default. The handler composes with the resilience/retry discipline above:
validation happens per connect, so retries and redirects you follow manually
are re-validated automatically.

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
