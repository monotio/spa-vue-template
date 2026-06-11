# GitHub workflow

## Commit & PR conventions

- **Conventional Commits** for messages AND PR titles (`feat:`, `fix:`,
  `chore:`, `docs:`, `ci:`, `test:`, `refactor:`); CI lints the title, which
  becomes the squash-merge subject.
- Reference issues with `Fixes #NNN` in the PR body so merges auto-close them.
- The PR template's checklist is the contract: `npm run check` green before
  requesting review.

## CI at a glance

| Workflow | What it gates |
| --- | --- |
| `ci.yml` | the full check pipeline; Windows leg on pushes to main |
| `dependency-review.yml` | new deps with known high-severity vulns |
| `codeql.yml` | static analysis (C# + JS/TS) |
| `title.yml` | conventional-commit PR titles |
| `scorecard.yml` | OpenSSF supply-chain posture (weekly) |
| `provenance.yml` | build attestations on main (verify: `gh attestation verify <artifact> --repo <owner>/<repo>`) |
| `template-cleanup.yml` | first push in generated repos only |
| `copilot-setup-steps.yml` | environment bootstrap for the Copilot coding agent (runs as a normal workflow only when the file itself changes) |

All actions are SHA-pinned; Dependabot maintains the pins (grouped
minor/patch; majors arrive as individual PRs with a cooldown window).

## Copilot coding agent

Assigning an issue or PR to GitHub's Copilot coding agent boots an ephemeral
VM; the repo controls that environment through
`.github/workflows/copilot-setup-steps.yml`:

- The job name must be **exactly `copilot-setup-steps`** or GitHub silently
  ignores the file. It mirrors `npm run setup` / the ci.yml setup steps —
  keep the three in sync when the bootstrap changes.
- A failed setup job does **not** block the agent anymore — it proceeds with
  a degraded environment. The symptom is silently missing dependencies, and
  this repo's `ignore-scripts=true` + `rebuild-trusted` combination is
  exactly the kind of step an agent cannot reconstruct from an error
  message, so treat a red `Copilot Setup Steps` run as a real defect.
- The file also runs as a normal workflow whenever it changes, so it
  self-validates in the PR that touches it.
- **Firewall model**: the agent VM sits behind a default-on egress firewall.
  The npm and NuGet registries are on the default allowlist; if the project
  grows dependencies on other hosts, extend the allowlist via the
  `COPILOT_AGENT_FIREWALL_ALLOW_LIST_ADDITIONS` Actions variable (repo or
  org level) — see GitHub's "Customizing or disabling the firewall for
  Copilot coding agent" docs for the exact semantics before loosening it.
- Copilot reads `AGENTS.md` natively; no extra instruction file is needed.

## Red CI: the retrieval contract (for agents)

Get a CI failure in one or two tool calls instead of scraping the full log
(this is the CI half of the local "read disk logs instead of re-running"
discipline in AGENTS.md):

1. Run id: `gh pr checks` or `gh run list --branch <branch> --limit 5`.
2. **`gh run view <run-id> --log-failed`** — only the failed steps' output,
   which for test steps already contains the per-test lines and assertion
   diffs. (MCP-equipped runtimes: the GitHub MCP server's `get_job_logs`
   with `failed_only` does the same in one call.)
3. The **Failure digest** step appends `failed-step:` / `local-repro:` lines
   to the job summary and to its own stdout. Note: the job summary has no
   `gh`/REST read endpoint — don't try to fetch it; the digest's repro
   mapping is simple anyway: every ci.yml step is one npm wrapper
   (`lint` → `npm run lint:check`, `fe_tests` → `npm run test:frontend`,
   `be_unit`/`be_integration` → `npm run test:backend`, bootstrap steps →
   `npm run setup`, `skills_drift` → `npm run skills:sync`). Caveat: the
   `be_integration` 20% line-coverage threshold gate is CI-only
   (coverlet.msbuild `/p:Threshold`); the local `--coverage` wrapper
   collects coverage but never fails on it — a green local repro plus a
   red CI `be_integration` step means the coverage gate, not a test.
4. The `test-results-<os>` artifact
   (`gh run download <run-id> -n test-results-ubuntu-latest`) contains the
   frontend Vitest JUnit XML only, and it exists only when the Frontend
   tests step actually ran — on earlier failures (setup, lint, format,
   type-check) the upload step itself errors and there is no artifact.
   Coverage output and backend test results are not uploaded; reach those
   failures via step 2.
5. Inline annotations are native, not bespoke: Vitest's `github-actions`
   reporter (wired in `test:ci`) and the .NET SDK's auto-detected GitHub
   logger both emit file/line annotations — don't add problem-matcher
   plumbing for them.

## Review hygiene

- **Bot/AI review comments are leads, not verdicts.** Triage them against the
  current code first — the `.claude/agents/comment-reviewer.md` agent
  classifies each unresolved comment (valid-act / valid-defer / stale /
  incorrect / opinion) with line-cited rationale.
- When responding to a review, push commits that address comments
  individually rather than one mega-fixup — reviewers can re-check
  comment-by-comment.

## Committed planning artifacts (PRDs)

Agent-built features fail at planning more often than at typing. For
complex features, commit the plan as `prds/YYYY-MM-feature.md` *before*
implementation:

- **Sections that earn their keep**: problem statement, decision table of
  approaches considered, phased implementation plan, testing strategy, edge
  cases, explicit out-of-scope.
- **Ground the PRD in this repo's docs** — cite the AGENTS.md/docs/*.md
  conventions it must follow, so any implementing agent (a different
  vendor's included) or human produces conforming code from the artifact
  alone.
- **Commit agent-generated plans/specs too** instead of leaving them in
  chat: git history is the archaeology a transcript never provides, and a
  committed plan is reviewable *before* the implementation exists.
- **When to skip the ceremony**: single-file changes, bug fixes with a
  failing test, anything one `npm run check` cycle validates. The PRD is for
  multi-session, multi-layer work.

## Comment hygiene before merging agent-authored branches

Long-lived agent-driven branches accumulate comment debris that reads as
noise — or misdirection — on the default branch. Sweep the diff's comments
before merge:

- **Delete outright**: references to planning artifacts (PRD sections,
  phase/roadmap markers like "P2/F3"), PR numbers, branch names — context
  that does not exist for a reader of the default branch.
- **Rewrite backward-looking narration into present-tense invariants** —
  narration explains a diff; an invariant explains the code:
  - Before: `// Switched from polling to a watcher because polling missed fast updates (PRD §4.2).`
  - After: `// A watcher, not polling: sub-interval updates must not be missed.`
- **Exception and log message strings count as comments** — operators
  reading logs have even less context than code readers; no
  "phase 2 fallback" in a runtime string.
- **Preserve**: genuine invariants, external-SDK quirks, forward-looking
  warnings ("this breaks if…") — the comments the next agent needs.

Automated PR-trimming passes catch dead code but reliably miss narrative
comments and runtime strings; this sweep is a manual review pass.

## Session hygiene (for agent-driven work)

- Leaving work mid-task: commit WIP to the feature branch with a `wip:`
  subject + a `NEXT:` paragraph in the body stating exactly where you
  stopped and what's verified vs untested. The next session (human or agent)
  resumes from the branch, not from memory.
- Salvaging a derailed PR: don't force-push over history others reviewed.
  Branch from the last good commit, cherry-pick the keepers, open fresh.

## Prompt templates for review asks

When asking an agent to review a PR, scope the angles explicitly, e.g.:
"review this diff for (1) correctness of the error-mapping branches,
(2) places a documented convention (AGENTS.md) is violated, (3) test gaps
for new branches — cite file:line for every finding, no style nits."
