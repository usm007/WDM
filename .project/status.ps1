#Requires -Version 5.1
<#
.SYNOPSIS
  Project-knowledge staleness report for WDM (PS 5.1-compatible, read-only).
.DESCRIPTION
  Reports knowledge-base age, tracked-vs-actual drift (without rewriting the
  manifest), git working-tree changes, and low-confidence areas.
  Run: powershell -File .project/status.ps1
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$stateDir = Join-Path $PSScriptRoot 'state'
$manifestPath = Join-Path $stateDir 'manifest.json'

Write-Output '=== project-knowledge status ==='
$docs = Get-ChildItem -Path $PSScriptRoot -Filter '*.md' -ErrorAction SilentlyContinue | Sort-Object Name
if ($docs.Count -eq 0) { Write-Output 'MISSING: no .project/*.md docs found.' }
else {
  $newest = ($docs | Sort-Object LastWriteTime -Descending | Select-Object -First 1).LastWriteTime
  $oldest = ($docs | Sort-Object LastWriteTime | Select-Object -First 1).LastWriteTime
  Write-Output ("knowledge docs: {0} files, oldest={1}, newest={2}" -f $docs.Count, $oldest, $newest)
  try {
    $kj = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'knowledge.json') -Raw | ConvertFrom-Json
    Write-Output ("knowledge.json: valid JSON, version={0}" -f $kj.version)
  } catch { Write-Output 'knowledge.json: INVALID or unreadable!' }
}

if (Test-Path -LiteralPath $manifestPath) {
  $age = (Get-Date) - (Get-Item -LiteralPath $manifestPath).LastWriteTime
  Write-Output ("manifest: age={0:N1}h ({1})" -f $age.TotalHours, (Get-Item -LiteralPath $manifestPath).LastWriteTime)
  Write-Output '--- drift since manifest (read-only) ---'
  & (Join-Path $PSScriptRoot 'refresh.ps1')
} else {
  Write-Output 'manifest: MISSING — run powershell -File .project/refresh.ps1 -Update'
}

Write-Output '--- git working tree ---'
try {
  $git = & git -C $root status --short --branch 2>&1
  Write-Output ($git -join "`n")
} catch { Write-Output 'git unavailable.' }

Write-Output '--- attention areas ---'
Write-Output '* embed (medium confidence): stream_catch.md gaps vs Embed/ — confirm before quoting.'
Write-Output '* theme-ui (medium): Themes/ + WPF-UI styling largely skimmed, not audited.'
Write-Output '* uncommitted UpdateChecker.cs silent-arg change — see known-issues.md.'
Write-Output '* .github/workflows/ empty — CI unknown.'
