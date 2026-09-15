#Requires -Version 5.1
<#
.SYNOPSIS
  Incremental project-knowledge refresh for WDM (PS 5.1-compatible).
.DESCRIPTION
  Stage 1-2 of the pipeline: snapshot repo structure + detect changes vs.
  .project/state/manifest.json, map them to affected modules, and report which
  knowledge files need targeted re-analysis. Never rewrites knowledge docs itself.
  Run: powershell -File .project/refresh.ps1 [-Update]
#>
[CmdletBinding()]
param([switch]$Update)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$stateDir = Join-Path $PSScriptRoot 'state'
$manifestPath = Join-Path $stateDir 'manifest.json'

$includeExt = @('.cs', '.xaml', '.csproj', '.sln', '.json', '.js', '.html', '.iss', '.ps1', '.bat', '.manifest')
$excludeRe = '(^|\\)(bin|obj|publish|publish_fw|releases|releases_fw|release_upload|staging|output|\.git|\.vs|node_modules|\.project)\\|^docs\\|motion-audits|skills-lock\.json|preparing-states-preview'

$allFiles = Get-ChildItem -Path $root -Recurse -File -ErrorAction SilentlyContinue |
  Sort-Object FullName

$files = foreach ($f in $allFiles) {
  $rel = $f.FullName.Substring($root.Length + 1)
  if ($rel -notmatch $excludeRe -and ($includeExt -contains $f.Extension)) { $f }
}

$entries = foreach ($f in $files) {
  $rel = $f.FullName.Substring($root.Length + 1)
  $h = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash
  [pscustomobject]@{ path = $rel; sha256 = $h; bytes = $f.Length }
}

$old = @{}
if (Test-Path -LiteralPath $manifestPath) {
  $m = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
  foreach ($e in $m.files) { $old[$e.path] = $e.sha256 }
}

$new = @{}
foreach ($e in $entries) { $new[$e.path] = $e.sha256 }

$added = @($entries | Where-Object { -not $old.ContainsKey($_.path) } | ForEach-Object { $_.path })
$deleted = @($old.Keys | Where-Object { -not $new.ContainsKey($_) } | Sort-Object)
$changed = @($entries | Where-Object { $old.ContainsKey($_.path) -and $old[$_.path] -ne $_.sha256 } | ForEach-Object { $_.path })

function Get-AffectedModules($paths) {
  $mods = New-Object System.Collections.Generic.HashSet[string]
  foreach ($p in $paths) {
    if ($p -like 'src/WDM/Services/DownloadEngine.cs*') { [void]$mods.Add('download-engine -> .project/data-flow.md#1, modules.md') }
    elseif ($p -like 'src/WDM/Services/HlsDownloader.cs*') { [void]$mods.Add('hls -> .project/data-flow.md#1, modules.md') }
    elseif ($p -like 'src/WDM/Services/MediaResolver.cs*' -or $p -like 'src/WDM/Services/YtDlpRunner.cs*' -or $p -like 'src/WDM/Services/EngineManager.cs*') { [void]$mods.Add('media-ytdlp -> .project/data-flow.md#2, modules.md, dependencies.md') }
    elseif ($p -like 'src/WDM/Services/Embed/*') { [void]$mods.Add('embed -> .project/modules.md, data-flow.md#2') }
    elseif ($p -like 'src/WDM/Services/CaptureServer.cs*' -or $p -like 'src/WDM.BrowserExtension/*') { [void]$mods.Add('capture+extension -> .project/data-flow.md#3, modules.md') }
    elseif ($p -like 'src/WDM/Services/TaskStore.cs*' -or $p -like 'src/WDM/Services/AtomicFile.cs*') { [void]$mods.Add('persistence -> .project/data-flow.md#4, modules.md') }
    elseif ($p -like 'src/WDM/Services/VelopackUpdateService.cs*' -or $p -like 'src/WDM/Services/UpdateChecker.cs*' -or $p -like 'src/WDM.Setup/*') { [void]$mods.Add('updates -> .project/data-flow.md#5, modules.md') }
    elseif ($p -like 'src/WDM/WDM.csproj*' -or $p -like 'WDM.sln*') { [void]$mods.Add('project-config -> .project/overview.md, dependencies.md, knowledge.json') }
    elseif ($p -like 'src/WDM/ViewModels/*' -or $p -like 'src/WDM/Models/*' -or $p -like 'src/WDM/*.xaml*') { [void]$mods.Add('ui-shell -> .project/modules.md, architecture.md') }
    else { [void]$mods.Add('other -> verify scope manually') }
  }
  return @($mods | Sort-Object)
}

$affected = Get-AffectedModules($added + $changed + $deleted)

Write-Output "project-knowledge refresh: $($entries.Count) tracked files"
Write-Output "added=$($added.Count) changed=$($changed.Count) deleted=$($deleted.Count)"
if ($added.Count -gt 0) { Write-Output '--- added ---'; $added | ForEach-Object { Write-Output "  + $_" } }
if ($changed.Count -gt 0) { Write-Output '--- changed ---'; $changed | ForEach-Object { Write-Output "  ~ $_" } }
if ($deleted.Count -gt 0) { Write-Output '--- deleted ---'; $deleted | ForEach-Object { Write-Output "  - $_" } }
if ($affected.Count -gt 0 -and ($added.Count + $changed.Count + $deleted.Count) -gt 0) {
  Write-Output '--- affected modules (targeted re-analysis) ---'
  $affected | ForEach-Object { Write-Output "  * $_" }
} else {
  Write-Output 'no drift: knowledge base is current.'
}

if ($Update -or $added.Count -eq 0 -and $changed.Count -eq 0 -and $deleted.Count -eq 0 -and -not (Test-Path -LiteralPath $manifestPath)) {
  # first run or explicit update: write manifest
}
if ($Update -or -not (Test-Path -LiteralPath $manifestPath)) {
  if (-not (Test-Path -LiteralPath $stateDir)) { New-Item -ItemType Directory -Path $stateDir | Out-Null }
  $manifest = [pscustomobject]@{
    generatedAt = (Get-Date).ToUniversalTime().ToString('o')
    root = $root
    files = $entries
  }
  $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
  Write-Output "manifest written: .project/state/manifest.json ($($entries.Count) files)"
}
