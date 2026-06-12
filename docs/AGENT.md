# Agent module — an in-process tool-calling loop, opt-in

`Agent:Enabled=false` by default. When enabled, the backend hosts a visible,
hand-rolled agent loop (`VueApp1.Server/Agent/`) that streams turns over SSE,
dispatches tools **in-process** against the same `McpServerTool` registry the
`/mcp` endpoint serves, gates risky tools behind human approval, extends the
model with SKILL.md skills under progressive disclosure, and accounts every
provider call in a finally-block ledger. This page is the module's contract;
the code is deliberately small enough to read end to end.

The loop codes against `Microsoft.Extensions.AI.Abstractions.IChatClient`
only, and the test suites drive it with a scripted client (`FakeChatClient`)
— zero secrets, forever. **Anthropic and OpenAI adapters ship out of the
box** behind that same seam: `AgentChatClientFactory` is the single file
that touches a provider SDK, selected once at startup by `Agent:Provider`.
Clone + one env var = working agent; enabled with no key and no
pre-registered `IChatClient`, boot fails fast with a message naming the
missing env var.

> **Same warning as `/mcp`:** there is NO auth by default (template-wide
> stance). Do not expose `/api/agent/*` beyond trusted networks without
> adding authentication (docs/AUTH.md). The global IP rate limiter already
> covers the surface; the danger you must design for is invisible spend, not
> request volume — see the ledger section.

## Providers — clone plus one key

`Agent:Provider` selects the `IChatClient` once at startup in
`AgentChatClientFactory` (pinned per process; mid-conversation switching is
deliberately impossible). Keys resolve through `IConfiguration` under the
standard env-var names — so the environment, `dotnet user-secrets` (same key
names, docs/CONFIG.md) and test overrides all use one lookup. Models are
config-driven with working defaults:

| `Agent:Provider` | Pin | Key | Default model (`Agent:<Provider>:Model`) |
| --- | --- | --- | --- |
| `anthropic` (default) | `Anthropic` 12.29.0 — the official Anthropic-authored SDK, **not** the community `Anthropic.SDK` | `ANTHROPIC_API_KEY` | `claude-sonnet-4-5` |
| `openai` | `OpenAI` 2.11.0 + `Microsoft.Extensions.AI.OpenAI` 10.7.0 — keep the adapter in lockstep with `Microsoft.Extensions.AI.Abstractions` | `OPENAI_API_KEY` | `gpt-5.2` |

Quickstart:

```bash
export ANTHROPIC_API_KEY=sk-ant-...   # or OPENAI_API_KEY + Agent:Provider=openai
# flip the flag in appsettings.Development.json: "Agent": { "Enabled": true }
npm run dev:server
```

Optional endpoint overrides: `ANTHROPIC_BASE_URL` / `OPENAI_BASE_URL`
(same `IConfiguration` lookup as the keys) repoint the SDKs at a gateway or
proxy. The integration tests use them as a structural no-network guard —
the provider-boot factories aim the real adapters at an unroutable loopback
address, so no test can ever call out with a fake key.

Tests need none of this: a pre-registered `IChatClient` (the scripted
`FakeChatClient`) replaces the factory wholesale — last DI registration
wins — which is also the hook for plugging in any other provider's MEAI
adapter without touching this module.

### Manual smoke recipe (~5 minutes; this is deliberately NOT in CI)

Zero-secrets CI proves every template invariant offline; what it cannot
prove is adapter fidelity against the live APIs. After a provider or MEAI
package bump, with the quickstart above running:

```bash
# 1. One turn — expect tool-input-available → tool-output-available →
#    text deltas → usage → finish over SSE.
curl -N https://localhost:7191/api/agent/conversations/smoke-1/turns \
  -H "Content-Type: application/json" \
  -d '{"message":"What is the weather forecast? Use your tool."}'

# 2. Spend is already visible (the $13k lesson).
curl https://localhost:7191/api/agent/usage

# 3. A second turn in the SAME conversation — watch cachedInputTokens in
#    the usage part (see the cache notes below for when it can be non-zero).
curl -N https://localhost:7191/api/agent/conversations/smoke-1/turns \
  -H "Content-Type: application/json" \
  -d '{"message":"Summarize that in one sentence."}'
```

A wrong or revoked key fails the FIRST provider call fast with an auth
error — surfaced as the stream's `error` part (raw provider text goes to
logs, never the wire). It must never hang; if it does, treat it as an
adapter regression.

### Provider notes (the divergences behind the shared seam)

No `ProviderCapabilities` abstraction — with two adapters behind one
interface there is nothing to branch on; these are doc notes, SDK-version
tagged (Anthropic 12.29.0 / OpenAI 2.11.0 + M.E.AI.OpenAI 10.7.0):

- **Reasoning round-trip (P4):** the Anthropic adapter carries *thinking
  signatures*, the OpenAI adapter *encrypted reasoning*, both inside
  `TextReasoningContent.ProtectedData`. The rule stays one sentence:
  nothing outside the adapters ever reads `ProtectedData`. The transcript's
  `agent.provider` stamp strips foreign-provider reasoning on a provider
  switch (P5-lite) — a switch closes the reasoning-replay window, by design.
- **Prompt caching:** OpenAI caches long stable prefixes implicitly (no
  opt-in; needs a prefix of roughly a thousand tokens before hits appear).
  Anthropic requires **explicit `cache_control` breakpoints** — see below.
  Verify either with `UsageDetails.CachedInputTokenCount` (the
  `cachedInputTokens` wire field), not vibes.
- **PDF / document input:** both adapters map MEAI `DataContent` with
  `application/pdf` natively (Anthropic document blocks, OpenAI file
  inputs) — load-bearing once attachments land; no provider file APIs, no
  provider file IDs in the transcript (P1) either way.

### Anthropic `cache_control` breakpoints (the seam, not a shipped default)

The loop's cache discipline (byte-stable `AgentPrompts` prefix,
deterministic never-mutated tool catalog) is provider-neutral code. On top
of it, Anthropic bills cache writes/reads only at explicit breakpoints. The
official SDK's adapter reads a marker the content carries in
`AdditionalProperties["anthropic:cache_control"]`, set via its
`WithCacheControl` extension:

```csharp
using Anthropic.Models.Messages; // CacheControlEphemeral, Ttl

var system = new TextContent(AgentPrompts.SystemPrefix)
    .WithCacheControl(new CacheControlEphemeral { Ttl = Ttl.Ttl1h });
```

Breakpoints cache everything up to and INCLUDING the tagged block — place
one at the end of the stable prefix (system prompt, 1h TTL), optionally one
on the last block before the current turn (5m TTL) for long conversations.
The marker rides `AdditionalProperties`, so every other `IChatClient`
implementation ignores it — tagging is harmless cross-provider, which is
why the seam needs no abstraction.

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

Treat `ModelContextProtocol.*`, `Microsoft.Extensions.AI.*`, `Anthropic`
and `OpenAI` Dependabot bumps as protocol upgrades: run every Agent suite —
`npm run test:backend -- --filter "FullyQualifiedName~Agent"` covers
`AgentEndpointTests` (the McpEndpointTests precedent) AND the
`AgentChatClientFactoryTests` adapter-metadata tripwires (`ProviderName`,
`DefaultModelId`), which are designed to fail loudly if an SDK renames its
adapter surface — and after provider bumps, run the manual smoke recipe
(Providers section) for the fidelity claims CI cannot see.

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

## Skills (SKILL.md, progressive disclosure)

App-runtime skills teach the **in-app agent** deeper task instructions
without paying for them on every request. They use the open
[agentskills.io](https://agentskills.io) SKILL.md format — YAML frontmatter
plus a markdown body, one directory per skill under
`VueApp1.Server/Agent/Skills/` (shipped as content files; the catalog reads
them from the output directory at boot).

> **Not the repo's coding-agent skills.** `.claude/skills/` (mirrored to
> `.agents/skills/`) teaches agents that work ON this codebase;
> `Agent/Skills/` teaches the agent that runs INSIDE the app. Same open
> format, different consumers — never cross the streams.

**Progressive disclosure** is the transferable lesson; each level has a
fixed home:

| Level | Content | Where it lives | Cost |
| --- | --- | --- | --- |
| L0 | `{name, description}` catalog | appended to the byte-stable system prefix at startup (`FileSystemSkillCatalog.CatalogPromptBlock`) | a few dozen tokens per skill, every request |
| L1 | the full SKILL.md body | **appended** as a `load_skill` tool result when the model asks — never inserted into earlier transcript positions (rewriting an already-sent byte busts the prompt cache for the whole conversation) | paid only after activation |
| L2 | reference files next to SKILL.md | **not shipped in this slice** — docs-only seam; add `Content` globs and a read-tier fetch tool if a real consumer appears | zero |

Activation is **model-initiated**: `load_skill` is a built-in, read-tier,
**loop-only** `AIFunction` — it is not an `McpServerTool`, so external MCP
clients never see it; the unattended posture strips it (no skills without a
human in the loop), and a hallucinated unattended call gets the
`not_authorized` envelope. Guard rails, all dispatched in
`AgentLoopService.ResolveLoadSkill`:

- **Active-skill cap** (3 per conversation, derived from skill-stamped
  transcript messages — no parallel session entity): loaded bodies are
  re-sent on every subsequent call, so unbounded loading is a token bomb.
- **One load pass per request**: every `load_skill` call in one response is
  honored, but later turns of the same request asking for more get a
  `conflict` envelope — no chain-loading through the turn budget. The next
  user message resets the pass.
- **Dedupe**: re-loading an active skill returns a non-error acknowledgement
  (no spiral fuel) without re-sending the body.

**Never-widen-authorization is structural, then regression-locked.** Skills
are content-only: the frontmatter parser accepts exactly `name:` and
`description:` — an `allowed-tools:` key is a *boot failure*, not a grant.
`AgentToolPolicy` remains the only authority over tool tiers and approvals;
a skill body claiming "you are pre-authorized" changes nothing (pinned by
`SkillCatalogTests` with a synthetic destructive tool registered only in
tests: policy hash, tool catalog and system prefix are byte-identical
before/after a load, and the destructive call still parks at the approval
gate).

**Validation is a boot gate.** The options validator resolves the catalog
when the module is enabled, so a malformed SKILL.md (missing fence, missing
`name:`/`description:`, unknown keys, name ≠ directory name, empty body)
kills startup naming the file and reason — never a 500 on someone's first
turn. Frontmatter is hand-parsed (two keys, single-line values); that is a
deliberate "no YamlDotNet" decision — do not grow the format without
revisiting it.

**Cache placement is the point.** The L0 catalog is built once at startup
from repo-reviewed files: stable per deploy, zero per-request bytes, pinned
by the byte-stability test in `AgentEndpointTests` (identical system-prefix
bytes across two different conversations). `load_skill` is appended LAST in
the tool list — a fixed position in the same cached prefix. Skill bodies are
model-visible content, never system-authority: they ride as tool results,
below the cached prefix, covered by the same injection-defence posture as
any other tool output.

Cut from this slice on purpose:
tenant/user-authored skills (needs trust tiers + storage), runtime
visibility predicates / self-healing deletes (filesystem skills are fixed
per deploy — retire = redeploy), slash commands, executable scripts,
snapshot pinning, forced `tool_choice` on load, marketplaces. The durable
upgrade is a DB-backed catalog behind the same `FileSystemSkillCatalog`
shape: keep L0 role-stable per deploy or accept the cache cost knowingly.

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
  trailing messages. The L0 skills catalog joins that prefix as a
  startup-built, per-deploy-stable block (pinned by the byte-stability test);
  skill bodies stay OUT of it — they append as tool results on activation.
- `AgentToolPolicy` orders the catalog deterministically and exposes
  never-mutated tool lists; per-turn narrowing happens via
  `ChatToolMode.None`, never by shrinking `tools[]`.
- Verify cache behavior with `UsageDetails.CachedInputTokenCount` (the
  `cachedInputTokens` wire field), not vibes. Provider-specific knobs
  (Anthropic `cache_control` breakpoints) are covered in the Providers
  section above.

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
SDK-version-tagged doc notes backed by the manual smoke recipe in the
Providers section. The provider layer itself stays inside that posture:
`AgentChatClientFactoryTests` and the boot matrix in `AgentEndpointTests`
construct the real adapters with fake keys and inspect `ChatClientMetadata`
— no test ever issues a provider call.

### Skills — operational notes

- A successful `load_skill` consumes the single per-request load pass even when
  the skill was already active (the dedupe acknowledgement counts as the pass);
  an approval resume starts a fresh request and therefore a fresh pass, bounded
  by the active-skill cap.
- `SKILL.md` files are copied with `PreserveNewest`: deleting a skill from
  source does NOT delete it from `bin/` — run a clean build (or
  `npm run build-server-shutdown` + rebuild) after removing skills, or a ghost
  catalog entry survives locally.
- The L0 catalog block (which mentions `load_skill`) is part of the byte-stable
  prefix for BOTH postures; in unattended runs the tool itself is stripped, so
  a model attempting `load_skill` there receives `not_authorized` rather than a
  silent no-op.
