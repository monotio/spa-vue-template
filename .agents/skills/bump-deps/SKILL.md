---
name: bump-deps
description: Update npm and/or NuGet dependencies. Use when bumping packages, checking for outdated deps, or when the user mentions "update packages", "bump deps", "outdated".
license: MIT
argument-hint: '[frontend|backend|all]'
disable-model-invocation: true
allowed-tools: Glob Grep Read Edit Bash(ncu:*) Bash(npm:*) Bash(dotnet:*) WebSearch WebFetch
---

# Bump Dependencies

Update project dependencies, resolve breaking changes, verify the gate.

**User arguments:** $ARGUMENTS — `frontend`/`npm` = npm only,
`backend`/`dotnet`/`nuget` = NuGet only, empty/`all` = both.

## Phase 1: Snapshot

Record current versions so the changelog analysis has `{package, old, new}`:

- npm: `vueapp1.client/package.json`
- NuGet: `Directory.Packages.props` (+ `global.json` SDK pin)

## Phase 2: Update

### npm

1. `npx ncu` in `vueapp1.client` to list candidates.
2. Apply in waves, smallest risk first: patches/minors together; each MAJOR
   individually. Keep the caret pin style (policy: carets + lockfile; if the
   repo has adopted save-exact, beware `ncu -u` reintroduces carets — grep
   for `"^` afterwards).
3. `npm install` to refresh the lockfile; check `npm audit`.
4. If a new package carries an install script (`hasInstallScript` in the
   lockfile), STOP and surface it — it must be added to `rebuild-trusted`
   deliberately, never silently.

### NuGet

1. Per project: `dotnet list <proj>.csproj package --outdated`
   (NOT the solution — the esproj makes the solution-level command exit 1).
2. Edit versions in `Directory.Packages.props` only (CPM).
   **Never bump Microsoft.OpenApi to 3.x** (ASP.NET Core 10 compiles
   against 2.x).
3. `dotnet restore -p:RestoreForceEvaluate=true` to refresh the lockfiles;
   commit the `packages.lock.json` diffs.
4. Consider the SDK: `global.json` pins a feature band with
   `rollForward: latestFeature`; bump the pin when a new band ships.

## Phase 3: Research majors

For every major bump: read the release notes / migration guide (WebFetch the
GitHub releases page). Note breaking changes that touch this repo's usage
before deciding to keep the bump.

## Phase 4: Verify

`npm run check` must be green. For Vite/Vitest/TypeScript/ESLint majors also
run a production build (`npm run build`) and check for new warnings. Summarize
what changed, what was held back and why.
