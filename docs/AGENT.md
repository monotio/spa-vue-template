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
  inputs) — this is what the Attachments hydration relies on; no provider
  file APIs, no provider file IDs in the transcript (P1) either way.

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
| `POST /api/agent/attachments` | multipart upload (one file) → `{attachmentId, mediaType, fileName}`; limit violations are typed ProblemDetails — 413 `agent-attachment-too-large`, 415 `agent-attachment-type-not-allowed` |

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
`not_authorized` envelope. The name is **reserved at boot**: the loop
dispatches `load_skill` before consulting `AgentToolPolicy`, so an
`McpServerTool` registered under a loop-reserved name
(`AgentLoopService.LoopReservedToolNames`) would be silently shadowed for
the agent while external `/mcp` clients still saw it — the options
validator fails startup on the collision instead (flag-off deployments may
name external-only tools freely). Guard rails, all dispatched in
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

## Attachments (upload-then-reference, hydrate-on-replay)

Files reach the agent in two steps: `POST /api/agent/attachments` stores the
bytes in `IAttachmentStore` and returns `{attachmentId, mediaType, fileName}`;
the turn POST references ids in `attachmentIds`. Bytes never ride the JSON
turn body, and two incident-bought rules govern everything after the upload —
both are silent-failure-class, so both are pinned by tests:

**P1 — the transcript stores references, never bytes.** The persisted user
message keeps the typed text as its only content; the attachment references
(`AgentAttachmentRef` triples) ride `AdditionalProperties["agent.attachments"]`.
No provider file APIs, no provider file ids. The replay snapshot derives its
`file` chip parts from the same stamp — chips replay even after the blob
store evicted the bytes.

**P2 — hydrate on EVERY turn.** `AgentMessageBuilder.HydrateAsync` rebuilds
the provider-visible parts from the store for the fresh message AND for every
stored message, every request. Nothing is provider-held between requests. The
trap this kills: a loop that hydrates only the freshly typed message works
perfectly on turn 1 and silently sends a text-only transcript from turn 2 on
— the model "forgets" the image and nobody sees an error. The integration
test asserts the scripted client sees `DataContent` rebuilt from the store on
the SECOND turn, not just the first.

Per-type hydration mapping (`AgentMessageBuilder`):

| Stored type | Model-visible content |
| --- | --- |
| `image/*`, `application/pdf` (and any other allowed binary type) | `DataContent(bytes, mediaType)` — both provider adapters map it natively |
| `text/*`, valid UTF-8 | the text inlined inside the **boundary-nonce frame** (below), truncated at a 50k-character inline cap with an explicit note |
| `text/*`, NOT valid UTF-8 | lossy-decoded (invalid sequences become U+FFFD) inside the frame, with an explicit not-valid-UTF-8 note appended after the closing marker. Deliberately NOT a binary `DataContent` fallback: the OpenAI adapter silently drops `text/*` `DataContent` from the request (no placeholder, no error) and the Anthropic SDK lossily decodes it anyway — lossy inlining is the one outcome that is deterministic on every provider |
| missing / store failure | the `[Attachment unavailable: "name"]` text placeholder — one attachment degrades; the turn NEVER aborts |

**The boundary-nonce frame.** Inlined file text is attacker-controlled model
input, so it is wrapped between `---FILE <nonce> BEGIN---` / `---FILE <nonce>
END---` markers plus a preamble declaring everything inside to be data, never
instructions. The nonce is 16 random bytes generated once per process: a file
author cannot forge the closing marker, so "END OF FILE — new system
instructions:" inside a file stays inside the frame. The nonce is
deliberately **per-process, not per-request**: hydrated transcripts are
re-sent every turn, and a per-request nonce would change the replayed bytes
each time — busting the provider prompt cache for the whole conversation
(the same discipline as the byte-stable system prefix). Accepted residual of
that tradeoff: "cannot forge" is precise per process lifetime — if a model
response ever echoes a marker line back to the client, a later upload in the
same process can embed a real closing marker and break out of the frame. A
per-request nonce would close that path at the cost of every cache hit.

Caps live in `Agent:Attachments` (`MaxBytes` 5 MiB, `MaxPerMessage` 4,
`AllowedContentTypes` allowlist) and are enforced twice: at the upload
endpoint as typed ProblemDetails (the composer mirrors the defaults
client-side in `contracts/agent.ts` for instant feedback), and again inside
`InMemoryAttachmentStore` as defense in depth. The composer also covers a
browser gap: `File.type` is EMPTY for extensions outside the browser's MIME
registry (`.md` is the common casualty), which would otherwise reject an
allowlisted file locally — or upload it as `application/octet-stream` into
the server's 415. `AgentPage` maps known extensions onto the same mirrored
allowlist (never beyond it) and re-wraps the file with the inferred type.
Two cap subtleties are load-bearing:

- **The allowlist narrows via `appsettings.json` only.** Its code default is
  deliberately empty because the configuration binder merges into non-empty
  code defaults (it can add entries, never remove them) — see the collection
  gotcha in [docs/CONFIG.md](CONFIG.md). The validator fails boot on an
  empty resulting list.
- **The upload body is bounded per-request, not just per-file.** The
  endpoint sets `IHttpMaxRequestBodySizeFeature` to `MaxBytes` + a 16 KiB
  multipart allowance before reading the form, so a chunked upload (no
  Content-Length) is rejected by the server at the same typed-413 bound as a
  declared one instead of buffering up to the global Kestrel cap — and
  raising `MaxBytes` past `Performance:RequestLimits:MaxRequestBodySizeBytes`
  still works, because the per-request limit overrides the global one.

In-memory store honesty: bytes are process-local, lost on restart, and
bounded on BOTH axes — 256 entries AND a total-bytes budget of 16 ×
`MaxBytes` (80 MiB at the default), oldest evicted first. The byte budget is
the real heap bound: a count cap alone would let an unauthenticated client
allocate 256 × `MaxBytes` (~1.3 GiB at the default) well inside the default
rate limit. Both loss modes degrade to the
placeholder — never to a failed turn. **Durable upgrade:** implement
`IAttachmentStore` over blob storage (Azure Blob / S3) — `SaveAsync` writes
the object, `GetAsync` streams it back, `Exists` is a HEAD request; the
placeholder degradation means the swap touches no call sites.

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
| `file` | `attachmentId`, `fileName`, `mediaType` — an attachment REFERENCE on a user message; replay-only (the composer renders its own uploads locally, so the live stream never emits it) |
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

## Scheduling — the seam is tested code, the sweeper is a recipe

The module ships **no scheduler, on purpose**. An in-memory sweeper would
silently lose every schedule on restart — and unlike the best-effort
`BackgroundWork/` queue (where loss-on-restart is a documented, acceptable
posture), a schedule is something a human RELIES on firing. A scheduler that
forgets is worse than no scheduler: absent capability fails loudly at design
time; a forgotten schedule fails silently in production weeks later. So the
scheduler waits for the database ([docs/DATA.md](DATA.md)) — but the seam it
plugs into is shipped and integration-tested today:

```csharp
// HttpContext-free, no SSE writer, unattended tool posture,
// result persisted to the conversation store:
var result = await loop.RunDetachedTurnAsync(
    conversationId, prompt, AgentToolPosture.Unattended, ct);
// result.FinishReason: stop | max-turns | budget-exceeded | turn-in-progress | …
```

Every loop guarantee holds unattended: budgets gate, the ledger accounts in
`finally`, approval-tier tools are not advertised and a hallucinated call
gets the `not_authorized` envelope, `load_skill` is stripped. Resolve
`AgentLoopService` from a **fresh DI scope per run** — exactly what a
`BackgroundWorkQueue` consumer or a hosted sweeper does naturally.

### The DB-sweep design (the durable upgrade, written down)

When you add persistence, do not create one recurring scheduler job per
schedule — registrations drift from rows on every edit/delete/restore. Run
**one sweep** and make the database the single source of truth:

- **Schema sketch** (generic; adapt names):

  ```sql
  CREATE TABLE Schedule (
      Id            INT PRIMARY KEY,
      Prompt        NVARCHAR(MAX) NOT NULL,  -- plain markdown, NO user templating
      CronExpression NVARCHAR(64) NULL,
      NextFireUtc   DATETIME2 NULL,          -- THE source of truth for "due"
      Paused        BIT NOT NULL DEFAULT 0,
      FailureStreak INT NOT NULL DEFAULT 0
  );
  CREATE TABLE ScheduleRun (
      Id              INT PRIMARY KEY,
      ScheduleId      INT NOT NULL REFERENCES Schedule(Id),
      ConversationId  NVARCHAR(64) NOT NULL, -- FRESH conversation per run
      TimeStartedUtc  DATETIME2 NOT NULL,
      TimeCompletedUtc DATETIME2 NULL,
      Outcome         NVARCHAR(32) NULL
  );
  -- Overlap protection lives in the CONSTRAINT, not in status-enum timing:
  CREATE UNIQUE INDEX UX_ScheduleRun_Active
      ON ScheduleRun(ScheduleId) WHERE TimeCompletedUtc IS NULL;
  ```

- **The sweeper** is a hosted `BackgroundService` ticking on an injected
  `TimeProvider` (`PeriodicTimer` over `TimeProvider` — tests drive it with
  a fake clock, the BannedSymbols wall-clock rule stays intact). Each tick:
  select `NextFireUtc <= now AND NOT Paused`, insert the run row (the
  filtered unique index rejects an overlapping second run — if the previous
  run is still open, this tick simply skips), advance `NextFireUtc` from the
  cron expression (Cronos is the natural parser; deliberately not a shipped
  pin), then hand the run to a worker — the dormant `BackgroundWork/` queue
  is the in-process consumer shape, a durable scheduler from
  [docs/BACKGROUND.md](BACKGROUND.md) the restart-safe one. The worker
  creates a fresh conversation and calls `RunDetachedTurnAsync`.
- **Missed ticks are skipped, never caught up**: after downtime, advance
  `NextFireUtc` from *now* — firing six stale "daily summary" runs at boot
  is a bug report, not recovery. Corollaries: the minimum cron interval is
  the sweep granularity, and a cron tighter than the sweep is rejected at
  validation time, not silently underserved.
- **Fresh conversation per run.** Reusing one thread breaks the turn-budget
  and lock invariants and grows an unbounded transcript; a run's report
  links to its run row via the conversation id.
- **Auto-pause classifier**: pause a schedule after N consecutive
  *user-actionable* failures (a broken prompt, a deleted target) — but
  classify before counting: **operator-blocked** outcomes (daily budget
  exhausted, missing API key) must NOT count toward auto-pause (pausing a
  schedule because the operator's budget knob gated it punishes the wrong
  party — defer to the next tick instead), and **infra-transient** failures
  retry on the next tick for free. Put an enum-coverage test on the
  classifier so a newly added outcome cannot fall through to the wrong
  bucket silently.
- **Edits touch rows, never a scheduler registration** — pause is a flag,
  delete is soft, there is no second system to drift against. A manual "run
  now" inserts a run row directly (and 409s on the unique index if a run is
  in flight) without touching `NextFireUtc`.
- The sweep inherits every recurring-job rule in
  [docs/BACKGROUND.md](BACKGROUND.md): the sweep is its own retry, no-op
  ticks log at Trace, kill switches exist before you need them.

## Workflows — sequencing is code you own, not an engine

For a starter, a "workflow" is a **seeded conversation**: a named
`{startPrompt, skills[]}` resolved at conversation creation. The UI speaks
workflow names; skill names stay an implementation detail:

```csharp
public sealed record AgentWorkflow(string Name, string StartPrompt, string[] Skills);

// All-or-nothing at creation: a missing skill is a 400 HERE, never a
// mid-run surprise three turns in.
foreach (var name in workflow.Skills)
{
    if (!skillCatalog.TryGet(name, out var skill))
    {
        return ServiceResponse<string>.BadRequest($"Unknown skill '{name}'.");
    }
    // Append the body exactly the way load_skill would: a skill-stamped
    // tool-result message — content below the cached prefix, never
    // system-authority, counted against the same active-skill cap.
}
// Then run the start prompt as turn 1 (streamed, or detached for
// fire-and-forget) — the model wakes up mid-task with its instructions
// already loaded.
```

Multi-STEP orchestration is the same idea one level up: **code-owned
sequencing of turns and outcomes**. `RunDetachedTurnAsync` returns a finish
reason and the final text; a plain C# method that calls it N times —
branching on outcomes, feeding step N's result into step N+1's prompt,
bailing on `budget-exceeded` — is a workflow engine with zero dependencies,
full debuggability, and the same ledger/policy guarantees as every other
turn. Start there.

Reach for **Microsoft.Agents.AI** (Agent Framework) instead when the
sequencing itself becomes the product: multi-agent handoffs, fan-out/fan-in
graphs, human-in-the-loop checkpoints persisted across processes,
long-running state machines. It builds on the same MEAI abstractions, so the
tools, transcript model and policy investments transfer. What you should not
do is adopt an orchestration engine to run ONE loop — visual agent-builder
products from major vendors have already been launched and sunset inside a
year; a dependency graveyard is a real cost axis in this space, and code you
own cannot be discontinued.

## Durable upgrades — every in-memory seam in one table

Everything below ships in-memory with the same honest banner as
Idempotency/BackgroundWork: process-local, lost on restart, single-node.
The interfaces are shaped so each swap changes a registration, not call
sites. What the table adds are the **fidelity notes** — the places where a
naive durable swap is silently lossy:

| In-memory default | Durable upgrade | The note that matters |
| --- | --- | --- |
| `InMemoryAgentConversationStore` | EF: one row per message, parts serialized with `AIJsonUtilities` | Two fidelity boundaries. (1) `TextReasoningContent.ProtectedData` **serializes and round-trips** — durable stores keep reasoning replay intact; what does NOT serialize is `RawRepresentation` (adapter-internal extras — acceptably lossy). (2) **`AdditionalProperties` stamps lose their CLR types on a JSON round-trip**: the live store holds a `Guid` under `agent.turnId` and an `IReadOnlyList<AgentAttachmentRef>` under `agent.attachments`; deserialized, both come back as `JsonElement`, so pattern matches like `stamped is IReadOnlyList<AgentAttachmentRef>` (the hydration gate in `AgentMessageBuilder.HydrateAsync` and the chip derivation in `AgentUiParts.FromMessage`) stop matching — attachments silently stop hydrating and chips vanish, with no error anywhere. A durable store must re-materialize stamps to their typed shapes on read, and a porting test must pin it. |
| one-active-turn `bool` lock (in the store) | TTL lease + per-turn renewal; abort on lease-id mismatch | Part of the store swap. The in-memory lock is single-node; two replicas without a lease will interleave turns and corrupt transcripts politely. |
| `AgentUsageLedger` | one DB row per `AgentUsageEntry` | Keep the `finally` write and use a fresh scope + non-cancelled token for it — a client abort already consumed tokens. Reconcile recorded spend against the **vendor bill** after deploy (within ~10% is healthy); the estimate column is for dashboards, the bill is the truth. |
| `InMemoryAttachmentStore` | blob storage (Azure Blob / S3): `SaveAsync` = put, `GetAsync` = stream, `Exists` = HEAD | The `[Attachment unavailable]` placeholder degradation means the swap touches no call sites. Keep the size/type caps enforced server-side — the blob store must not become a way around the upload endpoint's checks. |
| `FileSystemSkillCatalog` | DB-backed catalog (temporally versioned bodies buy audit/rollback for free on databases that support it) | Keep the L0 catalog stable per deploy — or accept that every catalog edit busts the cache prefix for all conversations, knowingly. Tenant/user-authored skills additionally need trust tiers; the content-only rule (no grant surface) must survive the storage change. |
| P5-lite (strip foreign-provider reasoning on switch) | contiguous-tail replay window + sticky suppression | Replay reasoning only for the unbroken tail of assistant turns since the last user message (window closers: user turn, zero-reasoning assistant turn, thread start, provider switch). A reasoning blob that fails to parse suppresses the WHOLE window until the next user boundary — never partial replay, never forward-healing: handing a model a reasoning history it never produced is the failure mode, reasoning-lossy is the accepted degradation. |
| no stream resume (disconnect cancels) | Redis tee: generation continues server-side, parts tee'd under a stream id; the client replays + tails on reconnect | This flips the billing posture — disconnect no longer stops spend — so it belongs behind the same deliberate decision as the budget knobs, not a default. |
| no scheduler | the DB-sweep sweeper above, over `RunDetachedTurnAsync` | — |
| no auth on `/api/agent/*` | [docs/AUTH.md](AUTH.md) when adopted | The approval endpoint and the usage ledger become per-user surfaces the day auth lands; the policy-surface hash already anticipates per-principal divergence. |

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

## Decision guide — who owns your loop?

Same format as [docs/AUTH.md](AUTH.md)/[docs/DATA.md](DATA.md): pick the
lane that matches what you actually need, and know what each lane costs.

### You need to OWN the dispatch point → this module's hand-rolled loop

Per-tool-call SSE events, the cross-request approval freeze (frozen args +
policy-surface hash), between-turn budget exits, the abort-proof `finally`
ledger write — every one of these requires owning the moment a tool call is
dispatched. Middleware hides exactly that moment. This is also the teaching
lane: ~150 readable lines instead of trust.

### One request, tools in, answer out, no human gate → `UseFunctionInvocation`

The 20-line alternative above. You observe dispatch, you do not own it —
fine when nothing needs to happen AT dispatch time. Keep
`AllowMultipleToolCalls = false` if you later add middleware approvals (the
all-or-nothing-per-response caveat), and keep the guard knobs explicit.

### The orchestration IS the product → Microsoft.Agents.AI

Multi-agent handoffs, workflow graphs, checkpoints that survive processes.
Same MEAI abstractions underneath, so the investment here transfers. Do not
take this lane for one loop's worth of behavior — see the Workflows section
for how far code-owned sequencing carries.

### You want the provider to run the agent → hosted managed-agent offerings

Provider-side agent runtimes (assistant/agent APIs with provider-held
threads, built-in tools like web search and code execution, per-vendor
flavors from every major lab and cloud) are the fastest path to a demo and
the most expensive path out. Know what you trade away, in this module's
vocabulary: **P2 dies** (the conversation lives with the vendor —
`previous_response_id`-style server state is the mechanism this module
structurally bans), the **ledger becomes their dashboard** (you reconcile
spend on their terms), **tool policy becomes their approval UX** (your
fail-closed tiers may not map), and migration out means re-importing
transcripts you never held. Legitimate when: prototyping, a hard
single-vendor commitment, or when their built-in tools (hosted browsing,
code sandboxes) are the actual feature. If you take this lane, keep YOUR
usage row per call anyway — the invisible-spend lesson does not care where
the loop runs.

### What this module deliberately does NOT do (so the lanes stay honest)

No durable stores (every seam in the table above is in-memory), no stream
resume, no scheduler (seam + recipe only), no multi-agent orchestration, no
conversation-list UI, no auth, one provider per process, no provider
built-in tools (web search, code execution — the tool surface is YOUR MCP
registry). Each absence is either a documented seam with a named upgrade or
a documented graduation path — if one of them is your day-one requirement,
start in the lane that ships it.

## Injection defence (loop-specific addendum to docs/AI.md)

Tool results, skill bodies and attachment text are untrusted model input;
detection is broken, so containment is structural. The frame to reason
with is the **lethal trifecta**: an agent that combines (1) access to
private data, (2) exposure to untrusted content, and (3) an egress channel
can be made to exfiltrate — no single prompt fixes that, only removing a
leg does. The shipped defaults break the egress leg (tools are read-only
with no egress) and fence the others: destructive/unannotated tools require
human approval; unattended runs cannot reach approval-tier tools at all;
untrusted content never enters the cached prefix or any system-authority
position; inlined attachment text is confined to the boundary-nonce frame
(see Attachments); raw exception text never reaches the model. **Re-run the
trifecta check every time you add a tool** — the first tool that can POST
to the internet (or send an email) reassembles all three legs at once.

## Test strategy (zero secrets, forever)

`FakeChatClient` (shared, compile-linked into both test projects) scripts
the provider seam; `AgentEndpointTests` proves the whole module offline —
including THE proving test: a scripted `FunctionCallContent` dispatches
through the real DI `McpServerTool` registry into the real
`WeatherForecastService`, and the model-visible result carries the same
envelope semantics `/mcp` emits. The attachment twin pins hydrate-on-replay
the same way: upload → turn 1 → a SECOND turn whose recorded provider call
must carry `DataContent` rebuilt from the store (first-turn-only hydration
is the silent regression that test exists to catch). Provider-fidelity claims that zero-secrets
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

## Deleting the module

Built to leave: one folder per side plus stitches. With `Agent:Enabled`
false you pay nothing and can keep all of it; to remove it entirely:

| Where | What |
| --- | --- |
| Backend | delete `VueApp1.Server/Agent/`; in `Program.cs` remove `SetupAgent`, `MapAgentEndpoints`, their call sites and the `Agent:Enabled` half of the `SetupMcpTools` condition (it reverts to `Mcp:Enabled` alone); drop the `Agent*` constants in `ProblemDetailTypes.cs` |
| Config | delete the `Agent` section from `appsettings.json` |
| Pins | remove `Microsoft.Extensions.AI.Abstractions`, `Anthropic`, `OpenAI`, `Microsoft.Extensions.AI.OpenAI` from `Directory.Packages.props` (keep `ModelContextProtocol.*` — that is the MCP module's), then a locked-mode-refresh restore to regenerate lockfiles |
| Tests | delete `VueApp1.Server.UnitTests/Agent/` and `VueApp1.Server.IntegrationTests/AgentEndpointTests.cs`, plus the `FakeChatClient` `<Compile Include>` link in the IntegrationTests csproj |
| Frontend | delete `pages/AgentPage.vue` (+ spec), `components/agent/`, `composables/useAgentStream.ts` (+ spec), `services/agent.ts`, `contracts/agent.ts` (+ snapshot spec); remove the `/agent` route from `router/index.ts` and the composable's narrow direct-fetch exception from the ESLint config |
| Docs | delete this file; remove the cross-links in docs/MCP.md, docs/BACKGROUND.md, docs/REALTIME.md, docs/AI.md, README.md, AGENTS.md |

Acceptance: `npm run check` green and `git diff docs/openapi/` empty — the
agent surface was never in the committed OpenAPI contract, so deleting it
must not move the contract by a byte.
