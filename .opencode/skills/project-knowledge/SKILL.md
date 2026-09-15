---
name: project-knowledge
description: Understand and maintain the project's architecture, modules, relationships, conventions, decisions, and development knowledge efficiently.
---

# Project Knowledge

Use the `.project/` knowledge layer so you don't re-explore the repo every session.
Knowledge files are an index, not the truth — **source code is always authoritative**.

## Workflow

1. **Start compact:** read `.project/overview.md` first (small context budget).
2. **Scope to subsystem:** find the relevant row in `.project/modules.md`, then read ONLY the
   detail files that matter (`architecture.md`, `data-flow.md`, `dependencies.md`,
   `conventions.md`, `decisions.md`, `known-issues.md`, `test-map.md`, `knowledge.json`).
3. **Inspect source only when necessary:** use file:line references from the knowledge files
   (e.g. `src/WDM/Services/DownloadEngine.cs`) instead of repo-wide searches.
4. **Prefer relationships over blind search:** entry chain is
   `Program.cs → App.xaml.cs → MainWindow + MainViewModel`; engine/media/capture/persistence
   boundaries are in `modules.md` — follow them.
5. **Respect quality labels:** FACT = backed by source; INFERENCE = strong structural conclusion;
   UNCERTAINTY = unverified. Never invent architecture, deps, or behavior. Verify cheap claims
   by reading the cited file before acting.
6. **Token discipline:** keep the overview in context; leave detail files on disk until needed;
   never paste large source passages into knowledge files.

## Maintaining the knowledge base

- After architecture or important behavior changes, update the affected `.project/*.md` rows and
  `knowledge.json` (bump `version`, fix module confidences) — targeted edits, never full rewrites.
- Staged refresh pipeline: structure → change detection → affected modules → targeted analysis →
  knowledge update. See `/project-knowledge-refresh`. Check staleness with
  `/project-knowledge-status`.
- Regenerate the file manifest after meaningful changes:
  `powershell -File .project/refresh.ps1` (updates `.project/state/manifest.json`).
- Never document generated output (`bin/`, `obj/`, `publish*/`, `releases*/`, `staging/`,
  `*.wdmstate`, user-data JSON) as architecture.

## Automatic mode (global)

If the global `project-knowledge-auto` skill / plugin is present, it handles repo detection,
compact PROJECT CONTEXT injection, bootstrapping repos without `.project/`, and auto-marking
stale areas (`.project/state/stale.json`, gitignored). This skill then covers detail workflow only.
Clear `stale.json` (or re-baseline via `refresh.ps1 -Update`) after applying knowledge updates.
