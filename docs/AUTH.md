# Authentication — decision guide

The template deliberately ships **no auth**: baking in ASP.NET Core Identity
forces a database and doubles the template's surface for everyone who wanted
something else. Pick the lane that matches your app:

## Same-origin SPA (this template's shape) → cookies + Identity API endpoints

The backend serves the SPA, so cookie auth is the simplest correct choice
(no token storage in JS, browser handles everything):

- `AddIdentityApiEndpoints<TUser>()` + `MapIdentityApi<TUser>()` gives
  register/login/refresh/2FA endpoints out of the box (requires an EF Core
  store — see [docs/DATA.md](DATA.md)).
- **.NET 10 niceties**: passkey support (`AddPasskeys()`) plugs into
  Identity, and cookie auth now returns **401/403 for API endpoints instead
  of login-page redirects** — which means the template's `useFetch`
  ProblemDetails handling works unchanged with cookie auth.
- CSRF: same-origin + SameSite cookies cover the basics; add antiforgery on
  state-changing endpoints if you relax SameSite. The documented
  origin-validation pattern in [docs/PATTERNS.md](PATTERNS.md) is
  defense-in-depth here.

## External IdP (Entra, Auth0, Keycloak) → BFF pattern

Keep tokens out of the browser: the backend acts as the OAuth client
(Backend-for-Frontend), stores tokens server-side, and gives the SPA a
session cookie. Duende.BFF or a small YARP-based BFF are the established
shapes. Avoid implicit/auth-code-in-SPA flows — token-in-JS is the thing the
BFF exists to prevent.

## Machine-to-machine / public API → JWT bearer

`AddAuthentication().AddJwtBearer()` against your IdP; keep the SPA on
cookies and accept both schemes if you need both audiences.

## Return-URL redirects: validate before you navigate

Every login flow grows a `?returnUrl=` redirect, and the naive
`startsWith('/')` check is an open redirect: `//evil.example`
(protocol-relative), `/\evil.example` (backslash normalization), and
`/%0d%0a/evil.example` (URLSearchParams decodes the CRLF; the browser then
strips raw control characters at navigation time, leaving `//evil.example`)
all pass it. Validate with the SAME parser the browser navigates with, then
navigate a **recomposed** local path — never the raw input:

```ts
function safeLocalPath(input: string | null, fallback = '/'): string {
  const url = input ? URL.parse(input, window.location.origin) : null;
  if (!url || url.origin !== window.location.origin) return fallback;
  return url.pathname + url.search + url.hash; // recomposed: origin-free by construction
}

void router.push(safeLocalPath(new URLSearchParams(window.location.search).get('returnUrl')));
```

Why this shape holds: `URL.parse` (Baseline 2024; returns `null` instead of
throwing) resolves relative inputs against your origin, while every hostile
form — protocol-relative, backslash, userinfo `@`, `javascript:` — resolves
to a foreign (or `"null"`) origin and fails the comparison. Do **not**
`decodeURIComponent` the whole input before checking: that over-rejects
legitimate `%2F` segments and multi-byte UTF-8 — the parser already handles
encoding correctly.

## When you add any of these

- Add the authorization-matrix integration tests described in
  [docs/TESTING.md](TESTING.md) — they mechanically catch missing
  `[Authorize]` attributes.
- Wire the `enduser.id` OpenTelemetry enricher (commented pattern in
  Program.cs's telemetry setup region) so traces correlate to users.
- Revisit the CSP in `ConfigureSecurityHeaders` if your IdP needs redirects
  or frames.
