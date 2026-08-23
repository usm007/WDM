---
name: design-md-chrome
description: "TypeUI Chrome extension that extracts live site styles into DESIGN.md or SKILL.md for Google Stitch, Claude, Codex and Cursor. Reads typography, colors, spacing, radius, shadows and motion from any tab and generates an implementation-ready blueprint. Use when converting a real website into a reusable design system spec."
---

# TypeUI DESIGN.md Extractor (Chrome Extension)

Extract real styles from any site and generate a `DESIGN.md` or `SKILL.md` blueprint for Stitch / Claude / Codex.

Source: https://github.com/bergside/design-md-chrome — curated skills at https://www.typeui.sh/design-skills — spec at https://www.typeui.sh/design-md

## Install

1. `chrome://extensions` → enable Developer mode → Load unpacked → select the cloned `design-md-chrome` folder (or `C:\Users\Admin\AppData\Local\Temp\opencode\skills-tmp\design-md-chrome`).
2. Pin the extension. Service worker: `service-worker.js`, content script: `content-script.js`, popup: `popup/popup.html`.

Requires permissions: `activeTab`, `scripting`, `storage`, `downloads`.

## Actions (popup)

| Action | What it does |
|--------|--------------|
| Auto-extract | Reads active tab: typography, colors, spacing, radius, shadows, motion |
| Generate `DESIGN.md` | Produces design-system markdown from extracted signals |
| Generate `SKILL.md` | Produces agent-ready skill markdown from extracted signals |
| Refresh | Re-runs extraction for current page state |
| Download | Saves output as `DESIGN.md` or `SKILL.md` |
| Explain (`?`) | Shows how file was generated (TypeUI reference) |

## Generated file structure (TypeUI DESIGN.md)

Follows the canonical blueprint in `DESIGN.md` of this repo:

- **Mission** — system objective + target product experience (one paragraph)
- **Brand** — product/brand, audience, product surface (web app / marketing / dashboard / mobile web)
- **Style Foundations** — visual style keywords, typography scale, color palette (semantic tokens + values), spacing scale, radius/shadow/motion tokens
- **Accessibility** — WCAG 2.2 AA, keyboard-first, focus-visible, contrast constraints
- **Writing Tone** — concise, confident, implementation-focused
- **Rules: Do** — use semantic tokens not raw hex; define all states (default/hover/focus-visible/active/disabled/loading/error); responsive & edge-case handling
- **Rules: Don't** — no low-contrast text, no hidden focus, no one-off spacing, no ambiguous labels
- **Guideline Authoring Workflow** — restate intent → foundations/tokens → anatomy/variants/interactions → a11y criteria → anti-patterns → QA checklist
- **Required Output Structure** — context, tokens, component rules, a11y, content/tone, anti-patterns, QA checklist
- **Component Rule Expectations** — keyboard/pointer/touch, spacing/typography tokens, long-content/overflow/empty-state handling
- **Quality Gates** — "must" for non-negotiables, "should" for recommendations, testable a11y, prefer system consistency

Optional TYPEUI_SH_MANAGED block markers for managed sections: `<!-- TYPEUI_SH_MANAGED_START -->` … `<!-- TYPEUI_SH_MANAGED_END -->`.

## When to use this skill

- User says "clone the look of <site>" or "extract design system from <url>"
- Need to bootstrap a DESIGN.md from a live reference instead of hand-authoring tokens
- Want a SKILL.md that is implementation-ready, measurable, and SEO-friendly for docs indexing

## Workflow for WDM

1. Open a reference site (e.g. Linear, Stripe) in Chrome with the extension loaded.
2. Click extension → Auto-extract → Generate DESIGN.md → Download. The file lands in Downloads.
3. Validate extraction: check contrast meets WCAG 4.5:1, spacing tokens map to WPF `Thickness`, typography to `Theme.xaml` styles.
4. Place as `DESIGN.md` in project root or convert to skill: `Generate SKILL.md` → save to `.opencode/skills/design-system-<scope>/SKILL.md` following the blueprint structure above (name must match directory, lowercase hyphen).
5. For full UI generation: feed the DESIGN.md to Claude Design or Stitch as described in `awesome-claude-design` skill; for WPF manual mapping, translate CSS variables to XAML brushes.

## Authoring rules (from blueprint)

- Keep language concise, operational, implementation-first (foundations → components → accessibility → QA).
- Prefer explicit, measurable constraints over vague advice; use consistent terminology.
- Keep `skill.md` under 500 lines when possible; include optional `reference.md`/`examples.md`/`scripts/` for deep specs.

## Local test

```bash
node tests/run-tests.mjs
```

Located at `C:\Users\Admin\AppData\Local\Temp\opencode\skills-tmp\design-md-chrome\tests`.

## License

MIT — see `LICENSE` in upstream repo. Extension homepage https://www.typeui.sh.
