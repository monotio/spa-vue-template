# Background work — decision guide

The template ships **no job scheduler**: scheduler choice is storage choice
(.NET 10 still has no built-in one), and half its users would inherit the
wrong one. What it does ship is the seam every option plugs into:
`BackgroundWork/` — a tested, dormant bounded-channel queue + draining
`BackgroundService` (uncomment `AddBackgroundWorkQueue()` in Program.cs).

## Which tier do you need?

| Need | Reach for |
| --- | --- |
| "Run this after the response" (email, cache warm, webhook fan-out) | The shipped `IBackgroundWorkQueue` — in-process, no deps. **Queued items are LOST on restart**; acceptable for best-effort work only. |
| Recurring/cron schedules, still no persistence | A periodic-`Timer` `BackgroundService`, or a lightweight scheduler lib (Coravel, NCronJob, TickerQ) — same loss-on-restart caveat unless the lib persists. |
| Work that must survive restarts, retries with backoff, a dashboard | Hangfire or Quartz.NET + a database ([docs/DATA.md](DATA.md)). Only the enqueue sites change shape — the envelope rules below carry over. |

A consumer already waiting for this seam: **detached agent turns**. The
opt-in Agent module exposes an HttpContext-free, integration-tested entry
point (`AgentLoopService.RunDetachedTurnAsync`, unattended tool posture)
designed to be called from a queue worker or a future DB-sweep scheduler —
the recipe, including the schedule schema and overlap protection, is in
[docs/AGENT.md](AGENT.md) "Scheduling". Resolve the loop from the item's
fresh DI scope, exactly like any other enqueued work.

The shipped queue's hard-won part is not the channel; it is the **envelope**:
ambient context (current `Activity`, culture, the request's DI scope) does
NOT flow across an enqueue boundary. The queue captures trace context +
culture at enqueue, restores them in a fresh DI scope per item, isolates
per-item failures, and **fails closed on attribution** — `initiator` is a
required argument, never a guessed "system" default that would silently
misattribute user-initiated work in logs and traces.

## Invariants for any at-least-once scheduler

Scheduler-agnostic rules that only reveal themselves as production incidents:

1. **Thin attributed wrapper over a plain testable core.** Scheduler
   attributes (retry policy, queue name) go on a `Background_X` wrapper whose
   body only resolves the scope and calls `XCore(...)`; the core signature
   contains zero scheduler types. Be explicit about who calls which form:

   | Entry point | Calls |
   | --- | --- |
   | Scheduler enqueue/recurring registration | `Background_X` (the only caller) |
   | Inline execution on the request path | `XCore` |
   | Unit tests | `XCore` |
   | Another job needing the logic | `XCore` — never enqueue to reuse code |

2. **Sweeps clear flags by compare-and-set, inside the try** *(applies once
   you have a database — [docs/DATA.md](DATA.md))*. A sweep that processes
   "flagged" rows must clear the flag where `flagTimestamp == the value it
   originally read`, and must do so inside the try: clearing after the catch
   eats the retry signal of a transient failure, and clearing without the
   timestamp guard clobbers a concurrent re-flag that arrived mid-sweep.

3. **Retries replay the originally-captured arguments.** Any state encoded
   in job arguments (lease ids, one-shot tokens) is resurrected verbatim on
   every retry — so releasing/consuming it must be gated on the same
   is-final-attempt condition as failure emission, never done unconditionally
   before a retryable rethrow (or attempt 2 holds a lease attempt 1 already
   released). This class of bug passes every single-attempt test you write.

## Recurring-job discipline

Operational rules for the cron/recurring tier (Hangfire/Quartz/TickerQ)
that are otherwise learned mid-incident:

- **Sweep-style jobs are their own retry**: disable the scheduler's retry
  ladder (Hangfire: `Attempts = 0`) and make each run an idempotent bounded
  batch — the next scheduled run IS the retry; a retry ladder on a sweep is
  a retry storm.
- **Every recurring job gets a concurrency lock and a stable name constant**
  (`nameof`-derived): a renamed job id silently orphans the old registration
  and double-registers the new one.
- **No-op runs log at Trace**, not Information — a sweep that found nothing
  is dashboard noise 95 times a day.
- **Heavy drains self-schedule their follow-up run** and keep the cron only
  as the safety net — cranking cron frequency for throughput couples your
  load to the clock.
- **A typed non-retryable exception short-circuits the ladder**: validation
  and permanently-bad-input failures must die on attempt 1, not burn the
  default retry budget first.
- **Build kill switches before you need them**: one env-var to pause all
  workers, one to pick up nothing, both checked at job pickup — a bad deploy
  plus at-least-once retries is how outages compound, and mid-incident is
  too late to add the brake.

## When you adopt a scheduler

- Keep every job idempotent — at-least-once delivery means it WILL run twice.
- Keep the `TimeProvider` rule: jobs that compute "now" take it injected.
- Wire the scheduler's instrumentation into the existing `VueApp1.*`
  OpenTelemetry pipeline, and link (don't parent) job spans to the enqueuing
  trace — the shipped `BackgroundWorkProcessor` shows the pattern.
