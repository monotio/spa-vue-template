# Realtime push — decision guide

The template ships **no realtime transport**: most apps need none on day
one, and the wrong default (a hub dependency, sticky sessions) taxes
everyone. When polling stops being enough, pick the row that matches:

| Transport | Direction | Reach for it when | Cost |
| --- | --- | --- | --- |
| **SSE** (`EventSource`) | server → client | notifications, progress, live dashboards — the common case | zero new dependencies (.NET 10 and the browser both ship it); one-way only |
| **SignalR** (`@microsoft/signalr`) | bidirectional | clients also send over the channel; per-user/group fan-out; automatic reconnect | one npm package + hub plumbing; scale-out needs a backplane (Redis) |
| **Raw WebSocket** | bidirectional | binary frames or a custom protocol — you own reconnect, heartbeats, fan-out | the most code for the least help; pick only with a concrete reason |
| **WebTransport** | bidirectional, datagrams | media/game-style streams (unreliable delivery, multiplexing) | Baseline Newly Available (2026); not a SPA-push tool, and SignalR has no WebTransport transport |

## Default: Server-Sent Events

One-way push is the 90% case and is now dependency-free end to end: .NET 10
ships native SSE (`TypedResults.ServerSentEvents` over an
`IAsyncEnumerable<T>`; `SseItem<T>` from `System.Net.ServerSentEvents` when
you need event ids/retry hints), and every Baseline browser ships
`EventSource` with built-in reconnect:

```csharp
app.MapGet("/api/notifications/stream",
    (INotificationFeed feed, CancellationToken ct) =>
        TypedResults.ServerSentEvents(feed.StreamAsync(ct), eventType: "notification"));
```

On the client, VueUse's `useEventSource` (already shipped — docs/FRONTEND.md
"VueUse") covers a single consumer; use the event-bus architecture below
once multiple stores consume the same connection.

Template invariants to keep when you add a stream endpoint:

- **Never compress the stream.** The pipeline runs `UseResponseCompression()`
  globally; `text/event-stream` is deliberately not in the compressible MIME
  list — adding it would buffer events into silence. Leave it out.
- **Keep stream endpoints under `/api`** — the service worker's
  `navigateFallbackDenylist` and the no-API-caching contract
  (vite.config.ts) then already exclude them, and the Vite dev proxy
  (`^/api`) forwards them. Never let a `runtimeCaching` rule match a stream.
- **`useFetch` is not for streams** — it buffers and parses one JSON body.
  `EventSource` (or `fetch` + `ReadableStream` for POST-initiated streams)
  is the right client; keep it inside one service/composable so the
  direct-fetch lint exception stays narrow.
- **The endpoint joins the OpenAPI harvest**: run `npm run openapi:sync`
  after mapping it, or `ExcludeFromDescription()` if you treat the stream as
  out-of-contract.

## POST-initiated streams: useAgentStream is the in-repo reference

`EventSource` can only GET. When the stream is the RESPONSE to a POST (a
body-carrying request that starts work — the agent module's
`/api/agent/.../turns` is the shipped example), the client is `fetch` +
`ReadableStream`, and the template ships exactly one sanctioned consumer:
`src/composables/useAgentStream.ts` (the narrow `no-direct-fetch` allowlist
entry in `eslint.config.ts`). Copy its `parseSseStream` rather than writing
a new one — it encodes the chunk-boundary failure modes that silently
corrupt naive `split('\n\n')` parsers and is locked by adversarial fixtures
(`src/composables/__tests__/useAgentStream.spec.ts`):

- **multi-byte UTF-8 split mid-character** across reads — decode with ONE
  `TextDecoder` in `{ stream: true }` mode; never `decode()` per chunk.
  Compute fixture split points in UTF-8 **bytes**: `String#indexOf` counts
  UTF-16 units, and a code-unit offset lands on a character boundary in the
  byte array — a fixture that never splits mid-character can never fail;
- **CRLF split across reads** — a chunk ending in `\r` must not let the
  `\n` arriving in the next chunk spawn a phantom empty line (double
  dispatch). The discriminating fixture splits the `\r\n` inside a
  MULTI-line `data:` event: at an event boundary, a phantom blank line is
  masked by the legitimate dispatch that follows it;
- **frames split across reads** — accumulate lines, dispatch only on the
  blank line;
- **incomplete event at EOF is discarded** (WHATWG rule) — half a JSON
  payload must never reach `JSON.parse`.

Abort is part of the money story, not just cleanup: the composable's
latest-wins `AbortController` propagates fetch abort →
`HttpContext.RequestAborted` → the provider call is cancelled and the agent
ledger still bills tokens-to-date (docs/AGENT.md). Wire types for the part
union are hand-written in `src/contracts/agent.ts` because the agent surface
is `ExcludeFromDescription` — see the sync rule documented there.

## Bidirectional upgrade: SignalR

When clients also send (chat, collaboration, presence), upgrade to SignalR
rather than raw WebSocket — reconnect-with-backoff, per-user/group sends,
and transport negotiation are exactly the code you don't want to own:
`AddSignalR()` + `MapHub<T>()` server-side,
`npm install @microsoft/signalr -w vueapp1.client` client-side. Scale-out
past one node needs a backplane. Keep the architecture below unchanged —
only the transport module swaps.

## Architecture: components never touch the transport

The classic mistake is binding components to the connection — each mounts
its own `EventSource`/hub callback, and you get reconnect storms, duplicate
handlers, and type-free payloads. The durable shape, whatever the transport:

1. **Lazy transport chunk**: the connection module (and any transport
   library) loads via dynamic `import()` on first need — visitors who never
   hit a realtime page never download it (the same rationale as the vendor
   split in docs/FRONTEND.md).
2. **One singleton client** owns the connection and re-emits server messages
   as typed `CustomEvent`s over a compile-time detail map.
3. **Stores subscribe; components read stores.** Each Pinia store patches
   its own state from events; the UI stays declarative.
4. **rAF coalescing for high-frequency streams**: buffer the latest value
   per key, flush once per `requestAnimationFrame` — a 100 Hz progress
   stream becomes at most 60 renders/s instead of 100.

```ts
// src/realtime/events.ts — the compile-time contract between transport and stores
interface RealtimeEvents {
  'notification:created': { id: string; title: string };
  'job:progress': { jobId: string; percent: number };
}

const bus = new EventTarget();

export function emitRealtime<K extends keyof RealtimeEvents>(type: K, detail: RealtimeEvents[K]) {
  bus.dispatchEvent(new CustomEvent(type, { detail }));
}

export function onRealtime<K extends keyof RealtimeEvents>(
  type: K,
  handler: (detail: RealtimeEvents[K]) => void,
): () => void {
  const listener = (event: Event) => handler((event as CustomEvent<RealtimeEvents[K]>).detail);
  bus.addEventListener(type, listener);
  return () => bus.removeEventListener(type, listener);
}
```

The transport module's only job is connect (once), parse, `emitRealtime`.
A store subscribes during setup and patches state; `onRealtime` returns the
disposer so effect scopes clean up. New message types extend the interface —
any consumer reading a field the server stopped sending fails at compile
time, not in production.
