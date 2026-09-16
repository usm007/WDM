# Pre-release gate: every ui:SymbolIcon Symbol="..." in XAML must exist in the
# packaged WPF-UI SymbolRegular enum. A bad name compiles fine and kills the
# app at startup with a XAML parse crash (see v2.7.5 FireworkFilled24).
# Fails the build gate (exit 1) on any invalid symbol.
# Run: powershell -File scripts/Verify-XamlSymbols.ps1
$ErrorActionPreference = "Stop"
$root = Join-Path $PSScriptRoot ".."
$dll = Get-ChildItem (Join-Path $env:USERPROFILE ".nuget\packages\wpf-ui") -Recurse -Filter "Wpf.Ui.dll" |
    Where-Object { $_.FullName -match "net8\.0-windows" } | Select-Object -First 1
if (-not $dll) { throw "WPF-UI net8.0-windows DLL not found in NuGet cache. Run dotnet restore first." }
$bytes = [IO.File]::ReadAllText($dll.FullName)
$syms = Get-ChildItem (Join-Path $root "src\WDM") -Filter *.xaml -Recurse |
    Select-String -Pattern 'Symbol="([A-Za-z0-9]+)"' -AllMatches |
    ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value } |
    Sort-Object -Unique
$bad = @($syms | Where-Object { -not $bytes.Contains($_) })
Write-Host "Checked $($syms.Count) distinct Symbol values against $($dll.FullName)"
if ($bad.Count -gt 0) {
    Write-Host "INVALID SYMBOLS:"; $bad | ForEach-Object { Write-Host "  $_" }
    exit 1
}
Write-Host "All symbols valid."
