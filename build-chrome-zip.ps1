param(
    [string]$OutPath = "staging\BrowserExtension\wdm-extension.zip"
)

$ErrorActionPreference = "Stop"
$src = Join-Path $PSScriptRoot "src\WDM.BrowserExtension"
$out = Join-Path $PSScriptRoot $OutPath

$outDir = Split-Path $out
if (-not (Test-Path $outDir)) {
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
}

if (Test-Path $out) {
    Remove-Item $out -Force -ErrorAction SilentlyContinue
}

$tempDir = Join-Path $PSScriptRoot "staging\temp_chrome_ext"
if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

try {
    Get-ChildItem -Path $src | Where-Object { $_.Name -ne "firefox" } | ForEach-Object {
        Copy-Item -Path $_.FullName -Destination $tempDir -Recurse -Force
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($tempDir, $out)

    $size = (Get-Item $out).Length
    Write-Host "Built Chrome extension zip: $out ($([Math]::Round($size / 1KB, 2)) KB)"
}
finally {
    if (Test-Path $tempDir) {
        Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
