# LLM features — day-one discipline

The template ships **zero AI code** — the provider and SDK choice is yours.
What this page gives you is the discipline that must exist *before* the
first LLM feature merges: these habits are cheap on day one and expensive to
retrofit. (The other direction — serving tools TO agents — is
[docs/MCP.md](MCP.md).)

## Where the call lives

An LLM call is an outbound dependency like any other; it gets the template's
existing seams, not a parallel architecture:

- A service returning `ServiceResponse<T>`; controllers stay thin. Map
  provider failures to stable `ProblemDetailTypes` URIs (rate-limited,
  content-filtered, model-unavailable) so the frontend branches on `type`,
  never on message strings.
- API keys flow through the standard config layering (user secrets in dev,
  env vars in prod — [docs/CONFIG.md](CONFIG.md)), and the feature is
  config-gated like OpenTelemetry, so the **zero-secrets boot stays true**:
  `npm run check` must pass with no key present.
- The outbound-HTTP rules in [docs/PATTERNS.md](PATTERNS.md) apply: explicit
  timeouts, and no blind retries of non-idempotent generations you have
  already been billed for.

## Injection defence: separation, not sanitization

Prompt injection is the SQL injection of the agentic era, and it is
**unsolved** — no provider ships a trusted/untrusted channel split (OWASP
GenAI LLM01 remains the top risk). The mitigations are structural:

- **User-controlled data never enters system/developer instructions.** It
  goes in user-role messages, wrapped in explicit delimiters
  (`<user_data>…</user_data>`), with an instruction that the wrapped content
  is data to operate on, never instructions to follow. Escaping prevents
  crashes; it does not prevent injection.
- Operator-authored content that must live inside an instruction prompt gets
  **unpredictable framing** — e.g. a per-call random token in the delimiter
  — so an embedded closing tag cannot break out of the frame.
- **Validate output before acting on it**: parse against a schema
  (structured outputs), allowlist any field that drives behavior, and treat
  free text as untrusted display content. Model output that becomes HTML,
  SQL, a shell command, or a fetched URL is an injection sink (for URLs,
  the SSRF handler in [docs/PATTERNS.md](PATTERNS.md) is mandatory).
- **Scope tool permissions to the task**: a summarization call gets no
  tools; an agent loop gets the minimal set, read-only by default,
  destructive tools behind confirmation/preview — the same doctrine as
  [docs/MCP.md](MCP.md) "Tool-design doctrine", applied from the other side
  of the boundary.

## Prompts are code

- **Centralize prompts in one greppable module** (a constants class on the
  backend) — never inline in services. Inline prompts dodge review, dodge
  diffs, and multiply when copy-pasted.
- **Version them**: stamp a version identifier alongside meaningful prompt
  changes so logs, traces, and evals can attribute behavior shifts to the
  prompt revision that caused them.
- **Every prompt gets an eval, updated in the same PR as the prompt.** A
  prompt change is a behavior change shipped without a compiler; the eval
  (promptfoo, DeepEval, Braintrust — the tool matters less than the rule) is
  its regression test. Keep eval runs **out of `npm run check`**: they need
  API keys and nondeterministic latency — run them as a separate opt-in
  command or workflow, like the load-test wrapper.
