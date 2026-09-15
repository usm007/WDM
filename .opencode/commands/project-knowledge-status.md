---
description: Report knowledge-base age, drift, stale modules, and low-confidence areas (read-only).
---

# /project-knowledge-status

Read-only staleness report. Do not edit knowledge files from this command.

1. Run: `powershell -File .project/status.ps1`
2. Summarize for the user:
   - knowledge-base age (doc mtimes, manifest age, `knowledge.json` validity)
   - changed files not yet reflected (drift + `git status`)
   - stale modules and missing documentation
   - low-confidence areas (embed: medium, theme-ui: medium — see `.project/knowledge.json`)
3. If drift exists, suggest `/project-knowledge-refresh` — do not refresh unprompted.
