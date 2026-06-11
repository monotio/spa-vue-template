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
