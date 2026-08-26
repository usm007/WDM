param(
    [string]$StagingDir = "staging",
    [string]$SignedXpi = ""
)

$ErrorActionPreference = "Stop"
$src = Join-Path $PSScriptRoot "src\WDM.BrowserExtension\firefox"
$out = Join-Path $PSScriptRoot "staging\BrowserExtension\wdm-catcher.xpi"

if (-not (Test-Path (Split-Path $out))) {
    New-Item -ItemType Directory -Path (Split-Path $out) -Force | Out-Null
}

if (Test-Path $out) { Remove-Item $out -Force -ErrorAction SilentlyContinue }

if ($SignedXpi -and (Test-Path $SignedXpi)) {
    Copy-Item $SignedXpi $out
    Write-Host "Bundled signed XPI from $SignedXpi"
    exit 0
}

if (-not (Test-Path $src)) {
    throw "Firefox extension source not found: $src"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$mode = if (Test-Path $out) { [System.IO.Compression.ZipArchiveMode]::Update } else { [System.IO.Compression.ZipArchiveMode]::Create }
$zip = [System.IO.Compression.ZipFile]::Open($out, $mode)
try {
    Get-ChildItem -LiteralPath $src -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($src.Length + 1).Replace('\', '/')
        $existing = $zip.GetEntry($rel)
        if ($null -ne $existing) { $existing.Delete() }
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $rel, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally {
    $zip.Dispose()
}

Write-Host "Built $out"
Write-Host "WARNING: This XPI is UNSIGNED. Firefox release builds will not install it. Upload to AMO (free, self-distribution) and pass the signed file via -SignedXpi."