#Requires -Version 5.1
<#
.SYNOPSIS
  Pre-release validation gate: restore -> build -> fast tests -> extension syntax ->
  packaging publish -> artifact + version checks. Fails closed.
#>
$ErrorActionPreference = "Stop"

Write-Host "== restore =="
dotnet restore WDM.sln
if ($?) { } else { exit 1 }

Write-Host "== build Release =="
dotnet build WDM.sln -c Release --no-restore --nologo
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host "== fast tests =="
dotnet test WDM.sln -c Release --no-build --nologo --filter "Category!=Stress&Category!=Performance"
if ($LASTEXITCODE -ne 0) { Write-Host "FAIL: correctness tests"; exit 1 }

Write-Host "== extension syntax =="
$err = 0
Get-ChildItem "src/WDM.BrowserExtension" -Filter *.js -Recurse | ForEach-Object {
  node --check $_.FullName 2>&1 | Out-Null
  if ($LASTEXITCODE -ne 0) { Write-Host "SYNTAX FAIL: $($_.FullName)"; $err++ }
}
if ($err -gt 0) { exit 1 }

Write-Host "== packaging =="
dotnet publish src/WDM.App/WDM.csproj -c Release -r win-x64 --self-contained true -o staging
if ($LASTEXITCODE -ne 0) { exit 1 }
if (!(Test-Path "staging/WDM.exe")) { Write-Host "FAIL: staging/WDM.exe missing"; exit 1 }

Write-Host "== version checks =="
$csproj = Get-Content "src/WDM.App/WDM.csproj" -Raw
$iss = Get-Content "src/WDM.Setup/installer.iss" -Raw
if ($csproj -notmatch "<Version>([^<]+)</Version>") { Write-Host "FAIL: Version missing"; exit 1 }
$ver = $Matches[1].Trim()
if ($iss -notmatch [regex]::Escape($ver.Split('.')[0] + "." + $ver.Split('.')[1])) {
  Write-Host "WARN: installer.iss version may drift from csproj $ver"
}
Write-Host "RELEASE GATE PASS ($ver)"
