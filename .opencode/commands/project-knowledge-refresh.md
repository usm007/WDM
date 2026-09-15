---
description: Incrementally refresh the .project/ knowledge base after repo changes (staged, no full regen).
---

# /project-knowledge-refresh

Staged incremental refresh. Do NOT re-scan the whole repo or rewrite all knowledge files.

1. Run change detection (PS 5.1-compatible):
   `powershell -File .project/refresh.ps1`
   This reports added / changed / deleted tracked files (`.cs`, `.xaml`, `.csproj`, `.sln`,
   extension `.json`/`.js`, `.iss`, `.ps1`) excluding `bin/`, `obj/`, `publish*/`, `releases*/`,
   `staging/`, `output/`, `.git/` — plus affected modules.
2. For each affected module ONLY: read the listed knowledge file(s), then diff/inspect the changed
   source files (targeted reads, not repo-wide search).
3. Apply targeted edits to the affected `.project/*.md` rows and `knowledge.json`
   (keep FACT/INFERENCE/UNCERTAINTY labels; keep file:line references accurate).
4. If imports, public APIs, deps (`WDM.csproj`), or config changed, also update
   `dependencies.md` / `data-flow.md` as appropriate — still scoped to the diff.
5. Write the new baseline: `powershell -File .project/refresh.ps1 -Update`
6. Report: files changed, modules touched, knowledge files updated, anything needing manual review.
