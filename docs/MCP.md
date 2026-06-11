# MCP server (opt-in)

The template can host a [Model Context Protocol](https://modelcontextprotocol.io)
server **inside the existing Web API** — no second project, no parallel
codebase. Tools live in `VueApp1.Server/Mcp/Tools/` and delegate to the same
`ServiceResponse<T>` service layer the REST controllers use, so the REST API
and the agent surface can never drift apart. The transport is **stateless
Streamable HTTP** at `/mcp` (POST-only): every request is self-contained, no
session affinity, scales horizontally.

Off by default (`Mcp:Enabled: false` in `appsettings.json`) — a fresh clone
pays zero cost until you flip the flag. (This page is about serving tools
*to* agents; the reverse direction — your app calling models — has its own
discipline guide: [docs/AI.md](AI.md).)

## Enabling

```jsonc
// appsettings.Development.json
{ "Mcp": { "Enabled": true } }
```

or per environment: `Mcp__Enabled=true`. Then `npm run dev:server` — the
endpoint is `https://localhost:7191/mcp`.

## Connecting a client

```bash
# Claude Code
claude mcp add --transport http vueapp1 https://localhost:7191/mcp
```

```jsonc
// VS Code / GitHub Copilot: .vscode/mcp.json
{ "servers": { "vueapp1": { "type": "http", "url": "https://localhost:7191/mcp" } } }
```

Hermes — like any other Streamable-HTTP-capable client (custom agents via an
MCP SDK included) — needs only the URL: add `https://localhost:7191/mcp` as a
Streamable HTTP server in its MCP server settings; there is nothing else to
configure. Verify with a `tools/list` round trip — you should see
`get_weather_forecast`.

## Security posture

**There is no authentication by default** — that is the template-wide stance
(see [AUTH.md](AUTH.md)), and an MCP endpoint is *remote tool execution*. Do
not expose `/mcp` beyond localhost or a trusted network without adding auth
first. What the template does ship:

- **Origin validation** (`Mcp/ValidateOriginFilter.cs`): rejects browser-borne
  requests (DNS rebinding, CSRF) whose `Origin` host isn't in the existing
  `AllowedHosts` config — zero extra configuration, `*` disables the check,
  and non-browser MCP clients (which send no Origin) pass untouched.
- **Rate limiting**: the global IP-partitioned limiter covers `/mcp` like
  every other endpoint.
- **405 instead of the SPA shell**: GET/HEAD/DELETE on `/mcp` answer with
  ProblemDetails, mirroring the `/api` fallback philosophy; the service
  worker's `navigateFallbackDenylist` excludes `/mcp` for the same reason.
- **Contract isolation**: the MCP endpoints are `ExcludeFromDescription()`-ed,
  and the OpenAPI harvest runs with the flag off — `/mcp` is never part of
  `docs/openapi/openapi.v1.json`.

## Error envelope contract

**The #1 first-time MCP-author mistake:** returning a failure as a successful
JSON string (`"{ \"error\": ... }"` with no `isError`). The protocol cannot
see it — runtimes branch on `isError`, agents treat the result as success,
mis-read it, and spiral into retry loops.

`McpToolResults` (in `Mcp/`) makes tools correct by construction — it is the
MCP twin of `ApiControllerBase.HandleServiceResponse`:

- **Success** → the value serialized into `structuredContent` *and* mirrored
  as a raw-JSON text block (some runtimes only surface text). The spec
  requires `structuredContent` to be a JSON **object** (SEP-2106 proposes
  allowing any JSON value), so non-object values — arrays, primitives — are
  wrapped as `{ "result": ... }`: the same wrapper the SDK emits for its own
  structured content and bakes into advertised output schemas, so the two
  never drift. Tools that return `CallToolResult` (because they route
  failures through the envelope) advertise their success shape with
  `OutputSchemaType` + `UseStructuredContent = true` on the attribute (see
  `WeatherTools`). For tools that return a POCO directly (no envelope
  needed), `UseStructuredContent = true` alone generates both the
  `outputSchema` and the wrapped structured content.
- **Failure** → `isError: true` with a JSON envelope as text content:

```json
{ "code": "not_found", "type": "/problems/...", "title": "...", "detail": "...", "status": 404 }
```

`code` is a finite, stable vocabulary (agents branch on it; never parse
messages), mapped 1:1 from `ServiceResult` exactly like the controller path:

| `ServiceResult`                      | `code`                |
| ------------------------------------ | --------------------- |
| `BadRequest` (incl. 400/422 details) | `invalid_parameter`   |
| `BadRequest` with 412 details        | `precondition_failed` |
| `NotFound`                           | `not_found`           |
| `Conflict`                           | `conflict`            |
| `NotAuthorized`                      | `not_authorized`      |
| `TooManyRequests`                    | `rate_limited`        |

`type`/`title`/`detail` carry the same RFC 9457 fields the HTTP response for
the same failure would — one error story, two protocols. Errors deliberately
carry **no** `structuredContent`: that slot belongs to the tool's declared
output shape, and an envelope there would violate a declared `outputSchema`.

## Tool-design doctrine

Mistakes in this section fail **silently** — the tool works in your manual
test and misbehaves only when a real agent runtime interprets it.

### Set all five annotations, explicitly

The MCP spec defaults `destructiveHint` and `openWorldHint` to **true**. An
unannotated read-only tool therefore presents to runtimes as *dangerous and
unpredictable* — some will demand human confirmation for every call. Every
tool sets `Title`, `ReadOnly`, `Destructive`, `Idempotent`, `OpenWorld`
(see `WeatherTools` for the shape).

### Descriptions: four parts

Tool-description quality measurably drives agent tool-selection accuracy.
Write every description with: **purpose** (one sentence, starts with a verb),
**limitations** (what it can't do — prevents misuse more than purpose
prevents disuse), **usage guidance** (when to pick this tool over siblings),
**parameter/return formats** (exact shapes; list enum values literally —
agents cannot guess your casing).

### Parameter traps (verified on SDK 1.4.0)

- A nullable C# parameter **without an explicit `= null` default** is marked
  *required* in the generated input schema. `string? filter` is required;
  `string? filter = null` is optional. Re-verify on SDK bumps — the
  maintainers have signaled intent to change this.
- Unhandled tool exceptions are swallowed into a generic
  "An error occurred invoking …" — the agent learns nothing. Expected
  failures must come back as envelope errors (`McpToolResults.Error`);
  reserve exceptions for genuine bugs.

### Conventions that scale past one tool

- **Few, narrow discovery tools** beat an endpoint-per-tool mirror of your
  API: one semantic `search`, one field-filtered `find`, simple `list_*` —
  each with a description that says when to prefer it over the others.
- **Smart identifiers**: accept an ID *or* a natural key (email, name) in the
  same parameter; on ambiguity return an `invalid_parameter` error whose
  `detail` lists the top candidates with their IDs so the agent can retry
  specifically.
- **`idempotencyKey` parameter** on create-shaped tools: agents retry on
  timeouts; key reuse must not duplicate (pairs with the Idempotency-Key
  convention in [PATTERNS.md](PATTERNS.md)).
- **`preview: true` dry-run** on destructive tools: returns an impact summary
  without executing, giving runtimes (and humans) a confirmation step.

### Idempotency preflight for write tools

Agents retry on timeouts, and the MCP spec defines no idempotency semantics
— server-side dedup is your job. The shipped `Idempotency/` services are the
natural backing store ([PATTERNS.md](PATTERNS.md) has the cross-process
upgrades); the parts a textbook implementation misses:

- **Scope the key as `(user, tool-name, key)`, hashed.** Without the tool
  name, a `create_x`/`update_x` pair sharing an agent-derived key collide.
  Add the tenant to the scope the day you have one.
- **Hash the payload too**: agents derive low-entropy keys, so same key +
  same payload within the window replays the stored result, while same key +
  **different** payload is a conflict — never a silent replay of the wrong
  result.
- **Write the conflict/in-progress error copy FOR the model**: state that
  the intended result probably already exists, how to verify it, how to
  retry with a new key — and explicitly that *only this call needs
  adjusting; do not switch to other tools*. Opaque conflicts send agents
  probing unrelated write tools with placeholder payloads, creating junk
  entities.

### Tool budget

Every tool's name + description + schema is in the context window of every
conversation. Past roughly **30 tools**, selection accuracy and token cost
degrade (clients with server-side tool search are relaxing this — treat it
as guidance, not law). Three gate questions before adding a tool:

1. Can an existing tool absorb this as a parameter?
2. Does a real agent workflow need it, or is it API-surface completeness?
3. Is its description distinct enough that a model reliably picks it over
   its closest sibling?

## Spec and SDK currency

`ModelContextProtocol.AspNetCore` is **pinned** in `Directory.Packages.props`:
the MCP spec is still revising its transport layer, and those wire changes
arrive through this package. Treat its version bumps as protocol upgrades —
run `McpEndpointTests` (which exercise the endpoint through the official SDK
client precisely so wire-format changes ride on the bump) and re-skim this
doc's SDK-version-tagged claims.
