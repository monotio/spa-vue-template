# Setup

The machine-readable sources of truth: **.NET SDK** from `global.json`
(rollForward accepts newer 10.x), **Node** from `.nvmrc`. After installing
both: `npm run setup` (git hooks, npm ci + trusted rebuilds, locked NuGet
restore, dotnet tools), then `npm run check` to verify everything.

## macOS

```bash
brew install dotnet-sdk node gh   # or: brew install nvm && nvm install
dotnet dev-certs https --trust    # one-time; Vite + API share the dev cert
npm run setup && npm run check
```

## Windows

```powershell
winget install Microsoft.DotNet.SDK.10 OpenJS.NodeJS.LTS GitHub.cli
dotnet dev-certs https --trust
npm run setup; npm run check
```

## Linux

```bash
# .NET via your distro/Microsoft feed; Node via nvm (reads .nvmrc)
nvm install && nvm use
dotnet dev-certs https            # no --trust on Linux; browsers need manual trust or accept the warning
npm run setup && npm run check
```

WSL note: certs trusted in Windows are not trusted inside WSL (and vice
versa) — run `dotnet dev-certs https` in the environment that runs the app.

## Cloud / Codespaces

`.devcontainer/devcontainer.json` provisions everything (SDK image + Node 24
feature + setup). "Code → Create codespace" just works.

## Agents (headless/sandboxed environments)

- The repo boots with **zero secrets** — placeholder config is enough to
  build, test, and run. Keep it that way when adding services.
- Behind a proxy, the standard `HTTPS_PROXY`/`HTTP_PROXY`/`NO_PROXY` env vars
  are honored by both `dotnet restore` and npm — no special wiring.
- Sandboxes that block network by default: restore/build/test need network
  + escalation (see AGENTS.md, Agent Environment Notes).
- Supervised dev servers: run `npm run dev:server` and `npm run dev:client`
  in separate supervised processes; if a port is wedged from a dropped
  session, kill by path-scoped match (e.g.
  `pkill -f 'VueApp1.Server|vueapp1.client.*vite'`) — never a bare
  `pkill node`.
