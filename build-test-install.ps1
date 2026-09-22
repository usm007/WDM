#Requires -Version 5.1
<#
.SYNOPSIS
  Builds the latest Inno installer and installs it on this PC (local test builds).
.DESCRIPTION
  Version scheme: 2.8.2a, 2.8.2b, ... The letter maps to the numeric revision
  (a=1, b=2, ...) because assembly/Inno versions must be numeric:
    2.8.2a -> binaries 2.8.2.1, installer file WDM_Setup_2.8.2a.exe
  No repo files are modified: the version is passed via -p:Version (dotnet)
  and /dMyAppVersion (ISCC), both of which override the checked-in values.
  The install step needs admin: Windows will show one UAC prompt.
.EXAMPLE
  powershell -File build-test-install.ps1
  powershell -File build-test-install.ps1 -Tag c
  powershell -File build-test-install.ps1 -Base 2.8.3
#>
param(
    [string]$Base = "",
    [string]$Tag = "auto"
)

$ErrorActionPreference = "Stop"

function Get-NextTag([string]$base)
{
    $outDir = Join-Path $PSScriptRoot "output"
    $max = 96  # 'a' - 1
    if (Test-Path $outDir)
    {
        foreach ($f in Get-ChildItem $outDir -Filter ("WDM_Setup_" + $base + "?.exe") -File -ErrorAction SilentlyContinue)
        {
            if ($f.BaseName.Length -gt 0)
            {
                $ch = $f.BaseName[$f.BaseName.Length - 1]
                $code = [int][char]$ch
                if ($code -ge 97 -and $code -le 122 -and $code -gt $max) { $max = $code }
            }
        }
    }
    if ($max -ge 122) { throw "Tag range exhausted for base $base (past 'z'). Pass -Base with a new version." }
    return [char]($max + 1)
}

# 1. Resolve base version (default: csproj Version with patch bumped, e.g. 2.8.1 -> 2.8.2)
if ([string]::IsNullOrWhiteSpace($Base))
{
    $csproj = Join-Path $PSScriptRoot "src\WDM\WDM.csproj"
    [xml]$xml = Get-Content $csproj
    $cur = $xml.Project.PropertyGroup.Version
    if ([string]::IsNullOrWhiteSpace($cur)) { $cur = "2.8.1" }
    $parts = $cur.Split(".")
    $patch = 0
    if ($parts.Length -ge 3) { [int]::TryParse($parts[2], [ref]$patch) | Out-Null }
    $Base = "{0}.{1}.{2}" -f $parts[0], $parts[1], ($patch + 1)
}
if ($Base -notmatch '^\d+\.\d+\.\d+$') { throw "Base must look like 2.8.2, got '$Base'." }
Write-Host "Base version: $Base"

# 2. Resolve tag letter
if ($Tag -eq "auto") { $Tag = Get-NextTag $Base }
$Tag = $Tag.ToLowerInvariant()
if ($Tag -notmatch '^[a-z]$') { throw "Tag must be a single letter a-z, got '$Tag'." }
$revision = [int][char]$Tag - 96
$numeric = "$Base.$revision"
Write-Host "Test version: $Base$Tag (binaries $numeric)"

# 3. Locate ISCC
$iscc = $null
foreach ($cand in @(
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"))
{
    if (Test-Path $cand) { $iscc = $cand; break }
}
if (-not $iscc)
{
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { $iscc = $cmd.Source }
}
if (-not $iscc) { throw "ISCC.exe not found. Install Inno Setup 6: winget install --id JRSoftware.InnoSetup --exact --silent" }
Write-Host "ISCC: $iscc"

# 4. Publish fresh Release to staging
$staging = Join-Path $PSScriptRoot "staging"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
Write-Host "Publishing WDM $numeric -> $staging ..."
& dotnet publish (Join-Path $PSScriptRoot "src\WDM\WDM.csproj") -c Release -r win-x64 --self-contained true -o $staging -p:Version=$numeric --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
if (-not (Test-Path (Join-Path $staging "WDM.exe"))) { throw "Publish produced no WDM.exe." }

# 5. Compile Inno installer with overridden numeric version
$iss = Join-Path $PSScriptRoot "src\WDM.Setup\installer.iss"
Write-Host "Compiling installer ($numeric) ..."
& $iscc "/dMyAppVersion=$numeric" $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE." }
$built = Join-Path $PSScriptRoot ("output\WDM_Setup_" + $numeric + ".exe")
if (-not (Test-Path $built)) { throw "Expected installer not found: $built" }

# 6. Rename to lettered filename
$lettered = Join-Path $PSScriptRoot ("output\WDM_Setup_" + $Base + $Tag + ".exe")
Copy-Item $built $lettered -Force
Write-Host "Installer: $lettered"

# 7. Install silently (UAC prompt appears once; installer closes running WDM itself)
Write-Host "Installing $Base$Tag ... approve the UAC prompt."
$proc = Start-Process -FilePath $lettered -ArgumentList "/SILENT" -Verb RunAs -PassThru
$proc | Wait-Process -Timeout 600 -ErrorAction SilentlyContinue

# 8. Verify installed version
$installed = Get-Item "C:\Program Files\WDM\WDM.exe" -ErrorAction SilentlyContinue
if ($installed -and $installed.VersionInfo.FileVersion -eq $numeric)
{
    Write-Host "Installed OK: C:\Program Files\WDM\WDM.exe ($numeric)"
}
else
{
    $found = "(missing)"
    if ($installed) { $found = $installed.VersionInfo.FileVersion }
    throw "Install verification failed: installed version is $found, expected $numeric."
}

Write-Host ""
Write-Host "Done: $Base$Tag is installed. Activity log: %LocalAppData%\WDM-Data\activity.log"
Write-Host "Next run without -Tag will build the following letter automatically."
