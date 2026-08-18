param(
    [string]$StagingDir = "staging"
)

$ErrorActionPreference = "Stop"
$src = Join-Path $PSScriptRoot "src\WDM.BrowserExtension\firefox"
$out = Join-Path $PSScriptRoot "staging\BrowserExtension\wdm-catcher.xpi"

if (-not (Test-Path $src)) {
    throw "Firefox extension source not found: $src"
}
if (-not (Test-Path (Split-Path $out))) {
    New-Item -ItemType Directory -Path (Split-Path $out) -Force | Out-Null
}

if (Test-Path $out) { Remove-Item $out -Force }

Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::Open($out, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem -LiteralPath $src -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($src.Length + 1).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $rel, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally {
    $zip.Dispose()
}

Write-Host "Built $out"