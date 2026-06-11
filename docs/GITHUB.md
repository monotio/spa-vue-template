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
   `npm run setup`, `skills_drift` → `npm run skills:sync`).
4. Test outputs (JUnit XML + coverage) are uploaded on every run, red or
   green: `gh run download <run-id> -n test-results-<os>` (e.g.
   `test-results-ubuntu-latest`).
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
