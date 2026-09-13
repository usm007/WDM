param(
    [string]$Version = "",
    [string]$PublishDir = "publish",
    [string]$OutputDir = "releases",
    [string]$Channel = "",
    [switch]$SelfContained,
    [string]$Framework = "net8-x64-desktop"
)
# Self-contained is now the default for exe releases (bundles .NET 8 libs — no runtime install needed)
if (-not $PSBoundParameters.ContainsKey('SelfContained')) { $SelfContained = $true }

$ErrorActionPreference = "Stop"

if (-not $Version) {
    $csproj = Join-Path $PSScriptRoot "src\WDM\WDM.csproj"
    [xml]$xml = Get-Content $csproj
    $Version = $xml.Project.PropertyGroup.Version
    if (-not $Version) { $Version = "2.5.3" }
    Write-Host "Version from WDM.csproj: $Version"
}

$publishFull = Join-Path $PSScriptRoot $PublishDir
$outFull = Join-Path $PSScriptRoot $OutputDir

if (Test-Path $publishFull) { Remove-Item $publishFull -Recurse -Force }
New-Item -ItemType Directory -Path $publishFull -Force | Out-Null
if (-not (Test-Path $outFull)) { New-Item -ItemType Directory -Path $outFull -Force | Out-Null }

Write-Host "Publishing WDM $Version -> $publishFull (SelfContained=$SelfContained Framework=$Framework)"
if ($SelfContained) {
    dotnet publish src/WDM/WDM.csproj -c Release -r win-x64 --self-contained true -o $publishFull
} else {
    dotnet publish src/WDM/WDM.csproj -c Release -o $publishFull
}

# Ensure vpk is available
$vpk = Get-Command vpk -ErrorAction SilentlyContinue
if (-not $vpk) {
    Write-Host "vpk not found, installing dotnet tool vpk..."
    dotnet tool install -g vpk
    $vpk = Get-Command vpk -ErrorAction SilentlyContinue
    if (-not $vpk) { throw "vpk still not found after install. Ensure dotnet tools are on PATH." }
}

$packArgs = @("pack", "--packId", "WDM", "--packVersion", $Version, "--packDir", $publishFull, "--mainExe", "WDM.exe", "--outputDir", $outFull)
if ($Channel) { $packArgs += @("--channel", $Channel) }
if (-not $SelfContained -and $Framework) { $packArgs += @("--framework", $Framework) }

Write-Host "Running: vpk $($packArgs -join ' ')"
& vpk @packArgs
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed with exit code $LASTEXITCODE" }

Write-Host "Velopack artifacts in $outFull"
Get-ChildItem $outFull | Format-Table Name, Length
$setup = Join-Path $outFull "WDM-win-Setup.exe"
if (Test-Path $setup) {
    $mode = if ($SelfContained) { "self-contained (.NET 8 bundled, no runtime install needed)" } else { "framework-dependent (requires .NET 8, dotnet check: $Framework)" }
    Write-Host "Setup: $setup ($([math]::Round((Get-Item $setup).Length/1MB,2)) MB) - $mode (SelfContained=$SelfContained)"
}

# Create clean release upload folder: full installer, portable, self-contained update
# packages (delta + full) and the matching feed JSON.
# IMPORTANT: the auto-update feed (releases.win.json) must match the installed flavor.
# The installed base is self-contained (.NET bundled), so the feed MUST be self-contained
# too — a framework-only package applied over a self-contained install would delete the
# bundled .NET runtime during Velopack obsolete-file removal. Framework builds
# (-SelfContained:$false) remain available for manual/dev use but are never uploaded here.
$uploadDir = Join-Path $PSScriptRoot "release_upload"
if (Test-Path $uploadDir) { Remove-Item $uploadDir -Recurse -Force }
New-Item -ItemType Directory -Path $uploadDir -Force | Out-Null
$fullSetup = Join-Path $outFull "WDM-win-Setup.exe"
$portable = Join-Path $outFull "WDM-win-Portable.zip"
# Prefer the self-contained delta as the small update package, fallback to the full nupkg
$deltaPkg = Get-ChildItem $outFull -Filter "WDM-$Version-delta.nupkg" -ErrorAction SilentlyContinue | Select-Object -First 1
$fullPkg = Get-ChildItem $outFull -Filter "WDM-$Version-full.nupkg" -ErrorAction SilentlyContinue | Select-Object -First 1
$updatePkg = if ($deltaPkg) { $deltaPkg } else { $fullPkg }
$releasesJson = Join-Path $outFull "releases.win.json"
if (Test-Path $fullSetup) { Copy-Item $fullSetup (Join-Path $uploadDir "WDM-Full-Setup-$Version.exe") }
if (Test-Path $portable) { Copy-Item $portable (Join-Path $uploadDir "WDM-Portable-$Version.zip") }
if ($updatePkg -and (Test-Path $updatePkg.FullName)) { Copy-Item $updatePkg.FullName (Join-Path $uploadDir $updatePkg.Name) -Force }
if ($fullPkg -and (Test-Path $fullPkg.FullName)) { Copy-Item $fullPkg.FullName (Join-Path $uploadDir $fullPkg.Name) -Force }
if (Test-Path $releasesJson) { Copy-Item $releasesJson (Join-Path $uploadDir "releases.win.json") -Force }
Write-Host ""
Write-Host "Release upload in $uploadDir :"
Get-ChildItem $uploadDir | Format-Table Name, @{N="SizeMB";E={"{0:F2}" -f ($_.Length/1MB)}}, Length
Write-Host "  1) WDM-Full-Setup-$Version.exe  -> full installer for new users (Velopack Setup, self-contained .NET 8)"
Write-Host "  2) WDM-Portable-$Version.zip     -> portable, self-contained, no .NET install needed"
Write-Host "  3) $($updatePkg.Name)  -> small update package - in-app updater downloads this delta, NOT the full installer"
Write-Host "  4) $($fullPkg.Name)  -> full update package - fallback for updaters too far behind for delta"
Write-Host "  5) releases.win.json -> Required by Velopack GithubSource to resolve update packages (self-contained feed)"
Write-Host "Updater: VelopackUpdateService downloads only the update package (delta ~3MB) with progress bar, then ApplyAndRestart."

Write-Host ""
Write-Host "Next: upload $uploadDir/* to GitHub Release tag v$Version (full nupkg required so the feed can resolve it)."
Write-Host "Existing installs via Velopack will get delta patch-only updates with progress bar and auto-restart."
Write-Host "Dotnet: self-contained .NET 8 is now the default (exe contains all libs, ~140-160MB). Pass -SelfContained:`$false -Framework $Framework for small framework-dependent build."
