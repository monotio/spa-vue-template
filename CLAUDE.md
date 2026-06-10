# VueApp1 Agent Playbook

@AGENTS.md

## Claude Code Specific

- Path-scoped rules auto-load from `.claude/rules/` when touching matching
  files (e.g. test files load the testing rules).
- **NO USER-LOCAL MEMORY.** Don't store project knowledge in the user-local
  `~/.claude/.../memory/` system — all learnings, gotchas, and project context
  belong in git-committed files (`AGENTS.md`, `docs/*.md`) where every agent
  and human benefits.
- Read-only subagent types (search/research) cannot Edit/Write — dispatch any
  file-modifying task to a general-purpose agent.
