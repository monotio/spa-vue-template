# Styling — decision guide

The template ships **plain scoped CSS** (`<style scoped>` per component plus
the small global `src/assets/base.css`/`main.css`) and no framework. That is
a decision, not an omission:

- **Zero lock-in, zero churn**: framework major versions (Tailwind v3→v4
  rewrote configuration entirely) never touch you, and native CSS now covers
  the historical reasons to reach for tooling — nesting, `:has()`, container
  queries, cascade layers are all Baseline.
- **Smallest possible payload** for an app shell that gets PWA-precached
  onto every visitor's disk (docs/FRONTEND.md "Loading performance").
- **Agents handle vanilla CSS well**: a scoped style block next to its
  template is the most context-free styling unit an agent can edit safely.

Stay with it until a real trigger appears — a team standardizing on utility
classes, a deadline that needs a finished component set. Then:

## Tailwind CSS v4 (utility-first)

```bash
npm install tailwindcss @tailwindcss/vite -w vueapp1.client
```

```ts
// vite.config.ts
import tailwindcss from '@tailwindcss/vite';
// ...
plugins: [vue(), tailwindcss(), /* existing plugins */],
```

```css
/* src/assets/main.css */
@import 'tailwindcss';
@theme {
  --color-brand-500: oklch(0.65 0.2 250);
}
```

**The v4 agent trap**: v4 is CSS-first. There is no `tailwind.config.js`, no
`content` array (template detection is automatic), and no PostCSS setup when
using the Vite plugin. Agents trained on v3 tutorials generate all three —
reject that; configuration lives in `@theme` blocks in your CSS.

Template seams to check when adopting it:

- `.npmrc ignore-scripts=true`: Tailwind v4's native engine arrives as
  prebuilt per-platform packages (no postinstall), so it works as-is. Any
  styling dependency that DOES need its install script must be added to
  `rebuild-trusted` in `vueapp1.client/package.json` — deliberately a
  code-review event.
- Output stays one content-hashed stylesheet under `/assets`, so the
  immutable-caching contract and the PWA precache need no changes.

## UnoCSS (atomic, on-demand)

Same integration shape (`unocss` + its Vite plugin), an on-demand atomic
engine with fully swappable presets — the Vitesse-school alternative when
you want utility classes with a lighter, more configurable core than
Tailwind. Recipe: <https://unocss.dev/integrations/vite>.

## Component library (fastest path to "looks finished")

- **PrimeVue** — large component set; its unstyled mode pairs well with
  Tailwind/UnoCSS.
- **Vuetify** — Material Design, opinionated and complete.

Both are runtime dependencies that materially grow the bundle: take a
size snapshot before/after (docs/FRONTEND.md "Bundle analysis") and keep
the PWA precache app-shell-sized (`globIgnores`). **CSP caveat**: production CSP is
`style-src 'self'` (`ConfigureSecurityHeaders` in Program.cs). Build-time
stylesheets — Tailwind, UnoCSS, a library's compiled CSS — are fine; a
library that injects `<style>` tags at runtime (CSS-in-JS) needs nonces or
a CSP relaxation. Prefer ones that don't.

## What VueUse already covers

`@vueuse/core` ships with the template and is the *behavior* half of a
component library — `useDark`, `useMediaQuery`, `onClickOutside`,
element size/visibility — composables, not styles. Check it before adding a
UI dependency for behavior alone (docs/FRONTEND.md "VueUse").

Expect the recipes on this page to age faster than the rest of the docs
(styling tooling churns annually); re-verify versions before following them
verbatim.
