# Agent module — an in-process tool-calling loop, opt-in

`Agent:Enabled=false` by default. When enabled, the backend hosts a visible,
hand-rolled agent loop (`VueApp1.Server/Agent/`) that streams turns over SSE,
dispatches tools **in-process** against the same `McpServerTool` registry the
`/mcp` endpoint serves, gates risky tools behind human approval, and accounts
every provider call in a finally-block ledger. This page is the module's
contract; the code is deliberately small enough to read end to end.

This PR slice is **provider-free**: the loop codes against
`Microsoft.Extensions.AI.Abstractions.IChatClient` only, and the test suites
drive it with a scripted client (`FakeChatClient`) — zero secrets, zero
provider packages. The provider factory (Anthropic/OpenAI, "clone + one env
var = working agent") lands as its own pins-isolated PR; until then, enabling
the flag requires registering an `IChatClient` in DI yourself (boot fails
fast with a message saying exactly that).

> **Same warning as `/mcp`:** there is NO auth by default (template-wide
> stance). Do not expose `/api/agent/*` beyond trusted networks without
> adding authentication (docs/AUTH.md). The global IP rate limiter already
> covers the surface; the danger you must design for is invisible spend, not
> request volume — see the ledger section.

## Surface

| Endpoint | Behavior |
| --- | --- |
| `POST /api/agent/conversations/{id}/turns` | one agent request, streamed as SSE parts; `Idempotency-Key` honored (in-flight duplicate suppression); `long-running` timeout policy |
| `POST /api/agent/conversations/{id}/approvals/{toolCallId}` | `{approved, reason?}` → executes/rejects the frozen call and resumes the loop as SSE on this response |
| `GET /api/agent/conversations/{id}` | canonical replay — completed parts derived from the transcript via the same mapper the live stream uses; unknown id → 404 typed `agent-conversation-not-found` (a TYPELESS 404 on the same URL means the module is disabled — the client's setup callout branches on that difference) |
| `GET /api/agent/usage` | the visible-spend ledger summary |

All endpoints are `ExcludeFromDescription`: a flag-gated surface must never
drift the committed OpenAPI contract.

## The loop (AgentLoopService)

One HTTP POST = one **agent request** = up to `Agent:MaxTurnsPerRequest`
provider calls ("turns"). Every production guard is a readable line, not a
hidden knob:

| Guard | Default | Semantics |
| --- | --- | --- |
| `MaxTurnsPerRequest` | 10 | The FINAL turn re-sends with `ChatToolMode.None` and the full `Tools` list **intact** — the model must answer in text, and the byte-stable tool prefix stays cache-valid (narrow via tool mode, never by mutating `tools[]`). |
| `MaxConsecutiveToolErrors` | 3 | Tool failures fail **soft** (the model sees the error envelope and adapts) until the cap, then the spiral is terminated instead of billing more retries. |
| `MaxRequestUsd` | 0.50 | Checked **between** turns, never mid-stream: a provider call that started always finishes ("if it started, let it finish"). |
| `DailyUsdBudget` | 25 | Soft preflight at turn 0 → `429` ProblemDetails `agent-budget-exceeded` with a `resetAtUtc` extension (next UTC midnight, `TimeProvider`-derived). Deliberately no `Retry-After`: a budget is a quota, not a rate limit. |
| one-active-turn lock | — | Concurrent POST → `409 agent-turn-in-progress`. In-memory analog of a lease; a durable store should replace it with TTL + renewal. |
| detailed-errors-withheld | — | Raw exception text reaches logs, never the wire or the transcript (the `IncludeDetailedErrors=false` posture, hand-enforced). |

The guard names deliberately mirror MEAI's `FunctionInvokingChatClient`
knobs, so collapsing onto the middleware later is a rename (see the
alternative below).

**Cancellation:** browser `AbortController` → `HttpContext.RequestAborted` →
the provider call. Disconnect **cancels generation** — in the no-database
default, stopping billing beats resumability. The resumable upgrade
(generation continues server-side, chunks tee'd to Redis under a stream id,
client replays + tails on reconnect) is a documented seam, not shipped code.

`RunDetachedTurnAsync(conversationId, prompt, posture, ct)` is the
HttpContext-free entry point (integration-tested): same loop, parts drained
internally, result persisted to the store. `AgentToolPosture.Unattended`
advertises only read-tier tools and answers approval-tier calls with the
`not_authorized` envelope instead of parking. A future scheduler/sweeper or
`BackgroundWorkQueue` consumer calls exactly this method from a fresh DI
scope — the scheduling seam is code, not prose.

## The ledger (AgentUsageLedger) — the invisible-spend lesson

The design is distilled from a real incident: a production system whose
streaming code paths bypassed central accounting recorded only a small
fraction of actual LLM spend, and the difference — five figures monthly —
surfaced on the vendor bill, not in any dashboard. The rules that incident
bought, each pinned by a test here:

- **Exactly one `AgentUsageEntry` per provider call**, written in the loop's
  `finally` — a client abort disposes the stream mid-flight and STILL lands
  there. The provider billed for what streamed; so does the ledger.
- **Null usage is tolerated and still counted** — a call that surfaced no
  usage is an entry with zero tokens, never a missing entry.
- **Budgets gate before turns, never mid-stream.**
- **Spend is visible within minutes of first clone**: `GET /api/agent/usage`,
  the terminal `usage` SSE part, and OTel metrics under `Meter`/
  `ActivitySource "VueApp1.Agent"` (riding the existing `VueApp1.*`
  wildcards; `gen_ai.*` attribute names are experimental semconv; prompt
  text is never recorded).

`EstimatedUsd` comes from `Agent:Pricing` (per-model, per-MTok); unknown
models cost 0 in the estimate but their tokens are still recorded. Reconcile
against the vendor bill, not against the estimate. The in-memory ledger is
process-local (honest banner) — the durable upgrade is one DB row per entry,
1:1 reconcilable against the bill.

## The in-process MCP bridge (McpToolAdapter)

One tool definition, two consumers: `SetupMcpTools` registers the
`McpServerTool` singletons whenever `Mcp:Enabled || Agent:Enabled`; the
`/mcp` endpoint mapping stays gated on `Mcp:Enabled` alone (pinned by the
flag-matrix test). The loop dispatches through the SDK's only public seam:

- `McpServerToolAIFunction : AIFunction` lifts `Name`/`Description`/
  `JsonSchema` straight from `McpServerTool.ProtocolTool` — the model sees
  the identical input schema both ways.
- Dispatch builds a synthetic `RequestContext<CallToolRequestParams>` with
  `Services` = the current request scope and `User` = the caller's
  principal, anchored by a **lazily created, never-run**
  `McpServer.Create(NullTransport, …)` singleton (~15-line do-nothing
  transport). No experimental subclassing, no loopback HTTP, no JSON-RPC
  double-serialization.
- Results: `CallToolResult.IsError` → the `McpToolResults` error envelope
  text, fail-soft and **byte-identical** to what an external MCP client
  sees — one error vocabulary across REST, `/mcp` and the loop. Success →
  `structuredContent` raw JSON, else concatenated text blocks. Never
  parse→re-serialize.

Treat `ModelContextProtocol.*` AND `Microsoft.Extensions.AI.*` Dependabot
bumps as protocol upgrades: run `AgentEndpointTests` (the McpEndpointTests
precedent) — the bridge tests fail loudly if the SDK moves under us.

(For consuming *external* MCP servers, `McpClientTool : AIFunction` over an
HTTP/stdio client transport is the right mechanism — the in-process bridge
exists precisely to avoid self-consuming our own endpoint over HTTP.)

## Tool policy and approvals (AgentToolPolicy)

Tiers derive from the same five MCP annotations docs/MCP.md already mandates
for external trust UIs:

| Annotations | Tier | Execution |
| --- | --- | --- |
| `readOnlyHint: true` | ReadOnly | auto-executes |
| explicit `destructiveHint: false` (write) | Write | approval iff `Agent:RequireApprovalForWrites` (default true) |
| `destructiveHint: true` **or no annotations** | Destructive | approval, always — **fail-closed** (the MCP spec itself defaults destructive) |

The catalog is ordered deterministically (ordinal by name) and never mutated
mid-conversation — both are prompt-cache invariants enforced in code.

**Approval flow:** an approval-tier call freezes a `PendingApproval`
(toolCallId, the original `FunctionCallContent`, the **policy-surface
hash**), emits `tool-approval-required`, and finishes the stream with
`finishReason: "approval-required"`. The approval POST re-validates the hash
— ordered tool names + schema bytes + tier assignments + policy knobs — and
**fails closed with `409 agent-approval-conflict`** if the surface diverged
between freeze and execution: an approval granted under one surface must
never execute under another (the privilege-drift lesson, ported before auth
exists). Approve executes the FROZEN arguments; reject appends a
model-visible `approval_rejected` envelope so the model learns the human
said no (and why) instead of seeing an unanswered call.

**Double-execution proofing** (concurrent approval POSTs — a double-click, a
proxy retry): the pending and its hash are re-validated **under the
one-active-turn lock** (the pre-lock checks are only a cheap fast-reject;
between them and the lock acquisition a competing resume can run to
completion), and the store's `RemovePendingApproval` is the **atomic consume
gate** — if the pending was already consumed, the frozen args are never
executed a second time. A turn stream is also single-enumeration by
contract: it owns the turn lock from creation, so a second enumeration
throws instead of re-running provider/tool calls outside the lock.

The `{approved, reason}` DTO mirrors MEAI's `ToolApprovalResponseContent`
shape, so migrating to middleware approvals later is mechanical.

## SSE wire vocabulary (AgentStreamPart)

Vocabulary-aligned with the Vercel AI-SDK UI Message Stream parts —
**explicitly NOT useChat-protocol-compatible**; the in-repo composable is the
only contracted consumer, locked by wire-shape snapshot tests on both sides.
Every part carries `conversationId` + `turnId` so late events from a dead
run can never mutate current client state.

| Part | Payload |
| --- | --- |
| `text-start` / `text-delta` / `text-end` | `delta` on the delta |
| `reasoning-start` / `reasoning-delta` / `reasoning-end` | `delta` on the delta |
| `tool-input-available` | `toolCallId`, `toolName`, `argumentsJson` |
| `tool-output-available` | `toolCallId`, `resultJson`, `isError` |
| `tool-approval-required` | `toolCallId`, `toolName`, `argumentsJson` |
| `usage` | `inputTokens`, `cachedInputTokens`, `outputTokens`, `reasoningTokens`, `estimatedUsd` |
| `error` | nested RFC 9457-shaped `problem` (the union owns the top-level `type` key) |
| `finish` | `reason`: `stop` \| `max-turns` \| `budget-exceeded` \| `approval-required` \| `cancelled` |

Deliberately omitted: `tool-input-start`/`tool-input-delta` — the provider
adapters do not reliably surface argument deltas, and a dead part type in
the union is comprehension tax. The stream obeys every docs/REALTIME.md
invariant (under `/api`, never compressed, SW-denylisted, dev-proxied).

Replay (`GET /conversations/{id}`) derives completed parts from the
transcript through the same `AgentUiParts` mapper — one renderer for live
and replayed conversations, lossless tool-card re-render.

## Transcript model and durable seams

The transcript IS MEAI's block model: `List<ChatMessage>` whose `Contents`
interleave `TextContent`, `TextReasoningContent`, `FunctionCallContent`,
`FunctionResultContent`. No bespoke block hierarchy, no intermediate event
layer — a parallel entity re-introduces the mapping-bug class that "use the
entity directly" exists to kill. App-level state is limited to
`PendingApproval` and `AdditionalProperties` stamps (provider, turn id,
error flag).

- **Reasoning round-trip:** `TextReasoningContent.ProtectedData` is MEAI's
  opaque provider-roundtrip slot (thinking signatures, encrypted reasoning).
  Rule: *nothing outside the provider adapters ever reads ProtectedData.*
- **Provider switch:** assistant messages are stamped
  `agent.provider`; reasoning content from a DIFFERENT provider is stripped
  on the way into a request (a switch closes the reasoning-replay window).
  The full contiguous-tail window + sticky parse-failure suppression is the
  durable-scale design, not needed while the in-memory store holds live
  object graphs.
- **Stateless replay:** no `previous_response_id`, no provider-held
  conversation state, ever; every turn rebuilds input from the local store.
- **Durable store seam:** `IAgentConversationStore` → EF: one row per
  message, parts serialized via `AIJsonUtilities`. The fidelity boundary to
  know: **`ProtectedData` serializes and round-trips; `RawRepresentation`
  does not serialize.** A durable store keeps reasoning replay intact; only
  adapter-internal RawRepresentation extras are lossy.

## Prompt-cache discipline, as code placement

Agent loops re-send the whole transcript every turn — without cache hits the
input cost grows near-quadratically. The discipline lives in code, not in a
checklist:

- `AgentPrompts.SystemPrefix` is a compiled constant: byte-stable, no
  timestamps, no per-request bytes; dynamic context only ever APPENDS as
  trailing messages.
- `AgentToolPolicy` orders the catalog deterministically and exposes
  never-mutated tool lists; per-turn narrowing happens via
  `ChatToolMode.None`, never by shrinking `tools[]`.
- Verify cache behavior with `UsageDetails.CachedInputTokenCount` (the
  `cachedInputTokens` wire field), not vibes. Provider-specific knobs
  (Anthropic `cache_control` breakpoints) arrive with the provider PR.

## The 20-line alternative (and when to take it)

If you do not need per-event SSE visibility at dispatch time, cross-request
approval freezes, between-turn budget checks, or finally-accounting — MEAI's
middleware runs the same loop shape:

```csharp
// dotnet add package Microsoft.Extensions.AI  (the middleware package)
IChatClient client = new ChatClientBuilder(innerProviderClient)
    .UseFunctionInvocation(configure: c =>
    {
        c.MaximumIterationsPerRequest = 10;          // our MaxTurnsPerRequest
        c.MaximumConsecutiveErrorsPerRequest = 3;    // our MaxConsecutiveToolErrors
        // IncludeDetailedErrors stays false: exception text is withheld
        // from the model, same posture as the hand-rolled loop.
    })
    .Build();

var options = new ChatOptions
{
    Tools = [.. bridgedTools],
    // Load-bearing: with approvals in play, leave this FALSE. The
    // middleware's approval semantics are all-or-nothing per response —
    // if ANY call in a response requires approval, ALL calls in that
    // response require approval. One call per response keeps the human
    // gate per-tool instead of per-batch.
    AllowMultipleToolCalls = false,
};

await foreach (var update in client.GetStreamingResponseAsync(messages, options, ct))
{
    // FunctionCallContent/FunctionResultContent stream through here, but
    // dispatch already happened inside the middleware — you observe, you
    // do not own, the dispatch point.
}
```

For approvals, wrap a tool as `ApprovalRequiredAIFunction`; the middleware
then emits `ToolApprovalRequestContent` and resumes on a
`ToolApprovalResponseContent` in the next request (our REST DTO mirrors that
shape on purpose). What you give up is exactly what this module exists to
teach: the dispatch point — and with it per-call SSE events, the frozen-args
approval freeze, between-turn budget exits and the abort-proof ledger write.

Outgrowing both? **Microsoft.Agents.AI** (Agent Framework, GA) is the
graduation path — built on the same MEAI abstractions, so the tools,
transcript and policy investments transfer.

## Injection defence (loop-specific addendum to docs/AI.md)

Tool results, skill bodies and attachment text are untrusted model input;
detection is broken, so containment is structural: shipped tools are
read-only with no egress; destructive/unannotated tools require human
approval; unattended runs cannot reach approval-tier tools at all; untrusted
content never enters the cached prefix or any system-authority position; raw
exception text never reaches the model.

## Test strategy (zero secrets, forever)

`FakeChatClient` (shared, compile-linked into both test projects) scripts
the provider seam; `AgentEndpointTests` proves the whole module offline —
including THE proving test: a scripted `FunctionCallContent` dispatches
through the real DI `McpServerTool` registry into the real
`WeatherForecastService`, and the model-visible result carries the same
envelope semantics `/mcp` emits. Provider-fidelity claims that zero-secrets
CI cannot verify (thinking-signature round-trip, real cache hits) are
SDK-version-tagged doc notes with a manual smoke recipe — they arrive with
the provider PR.
