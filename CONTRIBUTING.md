# Contributing

Thanks for helping make this template better!

## Ground rules

- **One command gates everything**: `npm run check` (lint, format, type-check,
  tests, build, OpenAPI contract) must be green before you push.
- **Conventional Commits** for commit messages and PR titles (`feat:`, `fix:`,
  `chore:`, `docs:`, `ci:`, `test:`, `refactor:`) — PR titles are checked by CI
  and become the squash-merge subject.
- **Keep it lean.** This is a starter template, not a framework: prefer
  documentation over abstraction, npm one-liners over new scripts, and
  deliberate non-decisions (no DB, no auth) over half-baked defaults.
- New dependencies need a reason in the PR description. Anything with an npm
  install script must be added to `rebuild-trusted` (see
  `vueapp1.client/.npmrc`).

## Dev setup

See the README quickstart. TL;DR: install the .NET SDK from `global.json`,
Node from `.nvmrc`, then `npm ci --prefix vueapp1.client` and `npm run check`.

## Reporting bugs / proposing features

Use the issue forms. For security issues, see [SECURITY.md](SECURITY.md).
