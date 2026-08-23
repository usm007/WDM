---
name: awesome-claude-design
description: "Collection of 68 production-ready DESIGN.md templates from getdesign.md for Claude Design, Stitch and AI agents. Each file defines color, typography, components and tokens to scaffold a full design system in one drop. Use when needing brand inspiration, starting a design system from a known aesthetic, or bootstrapping tokens for a new UI."
---

# Awesome Claude Design — 68 DESIGN.md Templates

One `DESIGN.md` → full design system. This skill is a curated index of **68 DESIGN.md files** (VoltAgent/awesome-claude-design, sourced from getdesign.md) that Claude Design expands into colors, type scale, components, preview cards, and a working UI kit in a single upload.

> Core idea from getdesign.md: keep **token, rule, and rationale in the same file**. Specific enough for the agent to make the next decision, with the *why* so it stays on-system.

## What DESIGN.md is

| File | Who reads it | What it defines |
|------|--------------|-----------------|
| `AGENTS.md` | Coding agents | How to build the project |
| `DESIGN.md` | Design agents (Claude Design, Stitch…) | How the project should look and feel |

Each DESIGN.md has 9 sections Claude uses: Visual Theme & Atmosphere, Color Palette & Roles (semantic CSS variables), Typography Rules, Component Stylings, Layout Principles, Depth & Elevation, Do's & Don'ts, Responsive Behavior, Agent Prompt Guide.

## How to use (2 options)

**Option A — Start from a design system:** `claude.ai/design/#org` → Create new design system → upload DESIGN.md under Add assets.

**Option B — Start from a prototype:** new prototype → attach DESIGN.md → prompt *"Create a design system from this DESIGN.md"*.

Result package: `README.md` (brand context), `colors_and_type.css` (variables), Google Fonts fallbacks, `preview/` cards, working UI kit `index.html`, and a portable `SKILL.md`.

## Collection (pick one DESIGN.md to seed)

Source: https://github.com/VoltAgent/awesome-claude-design — live previews at https://getdesign.md/<brand>/design-md

**AI & LLM (12):** Claude, Cohere, ElevenLabs, Minimax, Mistral AI, Ollama, OpenCode AI, Replicate, RunwayML, Together AI, VoltAgent (void-black + emerald), xAI

**DevTools & IDEs (7):** Cursor, Expo, Lovable, Raycast, Superhuman, Vercel (black/white Geist), Warp

**Backend/DB/DevOps (8):** ClickHouse, Composio, HashiCorp, MongoDB, PostHog, Sanity, Sentry, Supabase

**Productivity/SaaS (7):** Cal.com, Intercom, Linear (ultra-minimal purple), Mintlify, Notion (warm minimal), Resend, Zapier

**Design/Creative (6):** Airtable, Clay, Figma, Framer (black/blue motion), Miro, Webflow

**Fintech/Crypto (7):** Binance, Coinbase (trust blue), Kraken, Mastercard (cream/orbital pills), Revolut, Stripe (purple gradients), Wise

**E-commerce/Retail (4):** Airbnb (coral), Meta, Nike (monochrome Futura), Shopify (neon green)

**Media/Consumer (11):** Apple (SF Pro), IBM Carbon, NVIDIA, Pinterest, PlayStation, SpaceX, Spotify, The Verge, Uber, Vodafone, WIRED

**Automotive (6):** BMW, Bugatti (cinema-black), Ferrari (chiaroscuro), Lamborghini (gold), Renault (aurora), Tesla

Full URLs in README: `https://getdesign.md/<slug>/design-md` (e.g. `https://getdesign.md/linear.app/design-md`, `https://getdesign.md/stripe/design-md`).

## When to use this skill

- Starting a new project/page and need a coherent visual direction fast
- User says "make it look like Linear/Stripe/Notion/etc" or gives a vibe word
- Need tokens (color, type, spacing, shadow) rather than one-off CSS
- Bootstrapping a WPF/WinUI dark theme for WDM — map CSS variables to XAML `Theme.xaml` resources

## Workflow for WDM (WPF) — mapping web tokens to XAML

1. Pick a DESIGN.md that matches WDM's utility-product context (suggestions: Linear, Superhuman, Vercel, VoltAgent for dark developer tool).
2. Download the DESIGN.md: `curl -o DESIGN.md https://getdesign.md/linear.app/design-md` (or via Claude Design preview).
3. Extract tokens: colors → `SolidColorBrush` in `Themes/Theme.xaml`, type scale → `TextBlock` styles, spacing → `Thickness` resources, radii/shadows → `CornerRadius`/`DropShadowEffect`.
4. Do not copy brand trademarks verbatim — use as inspiration for an original system; verify palette contrast meets WCAG 4.5:1.

## Tips

- Start in a fresh project — mixing brands mid-project muddles tokens.
- After scaffolding, keep prompting for screens: *"now build a pricing/empty-state/queue page"* stays on-brand.
- Export the generated `SKILL.md` to `.opencode/skills/<name>/` to re-summon the aesthetic later without re-uploading.

## License

Repo MIT (VoltAgent/awesome-claude-design). DESIGN.md files are **inspired by** observable patterns, not official systems — not affiliated/endorsed by named brands. Users must ensure downstream use complies with trademark/brand policies. Use as inspiration for an original system rather than 1:1 clone. See LICENSE in upstream repo.
