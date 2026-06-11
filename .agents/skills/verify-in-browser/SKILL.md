---
name: verify-in-browser
description: Live-verify the running app in a real browser — boot both dev servers, drive the SPA, assert clean console/network, Server-Timing and caching headers, and service-worker activation. Use when asked to verify in browser, live-verify a change, smoke-test the dev environment, or validate headers/SW behavior end to end.
license: MIT
argument-hint: '[dev|prod]'
allowed-tools: Bash(npm:*) Bash(curl:*) Bash(pkill:*) Bash(dotnet:*) Read Grep mcp__playwright
---

# Verify in Browser

Live verification of the actually-running app. This loop exists because it
catches what the entire `npm run check` gate structurally cannot: it once
surfaced three real bugs in one pass (a dev-server launch-profile mismatch,
a locale-dependent `Server-Timing` header, and lint noise from the generated
dev service worker) that every unit, integration, and contract test missed.

**Arguments:** $ARGUMENTS — empty/`dev` = dev-environment loop (default);
`prod` = also run the production caching-contract tier (Phase 4).

## Phase 1: Boot

Run each server as a separate background process (never chained in one
foreground shell):

1. `npm run dev:server` — backend on **https://localhost:7191**. The wrapper
   pins `--launch-profile https`; never bypass it with a bare `dotnet run`,
   which silently picks the FIRST launchSettings profile (http, port 5128)
   and breaks the Vite proxy with 502s.
2. `npm run dev:client` — Vite on **https://localhost:57292** (override:
   `DEV_SERVER_PORT`), proxying `^/api` to the backend.
3. Wait for readiness by polling (the `-k` is required — dev servers use the
   self-signed ASP.NET dev certificate):

   ```bash
   curl -sk --max-time 2 https://localhost:7191/health/live   # until "Healthy"
   curl -sk --max-time 2 -o /dev/null -w '%{http_code}' https://localhost:57292/   # until 200
   ```

   Allow up to ~90s for the first poll to succeed: a cold `dotnet run`
   compiles first. If a port is wedged from a dropped session, kill
   path-scoped (`pkill -f 'VueApp1.Server'`, `pkill -f 'vueapp1.client.*vite'`)
   — never a bare `pkill node`/`pkill dotnet`.

## Phase 2: Browser tier (preferred)

Use whatever browser MCP tools the current runtime exposes — the assertions
below are tool-agnostic. The recommended default is Playwright MCP, wired
through a root `.mcp.json` (npx-based, no secrets; `--ignore-https-errors`
is required because the dev servers use the self-signed ASP.NET dev
certificate):

```json
{
  "mcpServers": {
    "playwright": {
      "command": "npx",
      "args": ["-y", "@playwright/mcp@latest", "--ignore-https-errors"]
    }
  }
}
```

One-time setup: if Playwright has no browser installed yet, run
`npx playwright install chromium`.

**If NO browser tools are available, skip to Phase 3 and SAY SO explicitly
in your report — the curl tier covers wiring and headers but cannot observe
console, rendering, or service-worker state.**

Drive https://localhost:57292/ and assert:

1. **App shell renders** — the home page heading and nav are visible.
2. **Console is clean** — zero errors and zero warnings after load and after
   each navigation. Quote any finding verbatim; "probably harmless" is a
   finding, not a pass.
3. **Data flows through the proxy** — navigate to `/weather`; the table
   renders forecast rows (the SPA → Vite proxy → API round trip works).
4. **Network is clean** — no failed (4xx/5xx) requests in the network log.
   The `/api/weatherforecast` response must carry
   `Server-Timing: total;dur=<number>` with a decimal POINT — a comma means
   a culture-sensitive formatting regression (the spec requires
   invariant-culture decimals; this exact bug shipped once).
5. **PWA surface** — `manifest.webmanifest` and the icons load with 200; the
   service worker reaches `activated`
   (`await navigator.serviceWorker.ready` resolves; the dev SW is enabled
   via `devOptions` in vite.config.ts).

## Phase 3: curl tier (always run; sole tier without browser tools)

```bash
curl -sk https://localhost:7191/health/live                               # Healthy
curl -skD - -o /dev/null https://localhost:57292/api/weatherforecast      # 200 via proxy
#   → server-timing header present; verify the decimal point:
#     grep -iE 'server-timing: .*dur=[0-9]+\.[0-9]'
curl -sk -o /dev/null -w '%{http_code}' https://localhost:57292/manifest.webmanifest  # 200
curl -skD - -o /dev/null 'https://localhost:57292/dev-sw.js?dev-sw' | grep -i 'content-type'
#   → text/javascript = the dev SW script is served (devOptions in
#     vite.config.ts); text/html means the SPA fallback answered and the
#     SW was NOT generated. Activation itself still needs the browser tier.
curl -skD - -o /dev/null https://localhost:7191/api/nosuchendpoint        # 404 + application/problem+json
```

## Phase 4: Production caching contract (`prod` argument only)

The caching contract (docs/FRONTEND.md, "Loading performance") only
manifests when the backend serves the built SPA — the dev loop cannot test
it. Publish and boot a production instance, then assert with curl:

```bash
dotnet publish VueApp1.Server -c Release -o /tmp/verify-publish
ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS=http://127.0.0.1:8080 \
  dotnet /tmp/verify-publish/VueApp1.Server.dll   # background process
```

| Request | Expected |
| --- | --- |
| `/assets/<any-built>.js` | 200, `cache-control: public, max-age=31536000, immutable` |
| `/` and `/sw.js` | 200, `cache-control: no-cache`, an `ETag` |
| repeat with `If-None-Match: <etag>` | 304 |
| `/api/nosuchendpoint` | 404, `application/problem+json` (not the SPA shell) |
| `/weather` (deep link) | 200, the SPA shell (fallback works) |

Get a real asset name from `/tmp/verify-publish/wwwroot/assets/`.

## Phase 5: Teardown

- Stop every process you started (background task stop, or path-scoped
  `pkill` as in Phase 1). Verify both ports are released.
- Remove `/tmp/verify-publish` if Phase 4 ran. A `dev-dist/` directory may
  have appeared under `vueapp1.client/` — it is git- and lint-ignored
  generated SW output; leave it.

## Report

State which tier(s) actually ran (explicitly flag a curl-only run), then
each assertion as pass/fail with evidence: header lines verbatim, console
messages verbatim, forecast row count. Three historical catches set the bar:
wrong launch profile (502 through the proxy), `dur=0,9` under a
comma-decimal OS locale, and generated-file lint noise — look for that class
of environment-dependent bug, not just "the page loads".

## Gotchas

- **Browser shows a certificate interstitial**: stale cached dev-cert export
  — see SETUP.md ("Troubleshooting: browser says the dev cert is invalid").
  On Linux, `dotnet dev-certs https` cannot `--trust`; expect the warning or
  use the Playwright `--ignore-https-errors` wiring.
- **Weather page 502s**: the backend is not on 7191 — someone bypassed
  `npm run dev:server` (see Phase 1).
- **`npm run check` fails after this loop** on files under `dev-dist/`:
  generated SW output leaked into linting — it must stay ignored, do not
  "fix" the generated code.
