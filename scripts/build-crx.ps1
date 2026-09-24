param(
    [string]$KeyFile = "staging\BrowserExtension\wdm-catcher.pem",
    [string]$SrcDir = "src\WDM.BrowserExtension",
    [string]$OutDir = "staging\BrowserExtension",
    [string]$ExpectedId = "jehagbjolooaohcbmlhegpmjeaakonof",
    # Final on-disk location of the per-machine install. Baked into
    # update.xml codebase + manifest update_url (no ExtensionSettings override).
    [string]$InstalledBase = "file:///C:/Program%20Files/WDM/BrowserExtension",
    [string]$ChromeExe = "C:\Program Files\Google\Chrome\Application\chrome.exe"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot  # repo root (this script lives in scripts\)
$keyFull = Join-Path $root $KeyFile
$srcFull = Join-Path $root $SrcDir
$outFull = Join-Path $root $OutDir

if (-not (Test-Path $keyFull)) { throw "Extension signing key not found: $keyFull (first pack generates it; guard it - same key = same extension ID)" }
if (-not (Test-Path (Join-Path $srcFull "manifest.json"))) { throw "Extension source not found: $srcFull" }
if (-not (Test-Path $ChromeExe)) { throw "Chrome not found: $ChromeExe" }
if (-not (Test-Path $outFull)) { New-Item -ItemType Directory -Path $outFull -Force | Out-Null }

# Stage a temp copy: point update_url at the installed location, drop the
# Firefox-only subfolder (separate manifest, must not ship inside the CRX).
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("wdm_crx_" + [System.Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tmp -Force | Out-Null
try {
    Get-ChildItem -LiteralPath $srcFull | Where-Object { $_.Name -ne "firefox" } | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $tmp $_.Name) -Recurse -Force
    }
    $mfPath = Join-Path $tmp "manifest.json"
    $mf = Get-Content -LiteralPath $mfPath -Raw | ConvertFrom-Json
    $mf.update_url = "$InstalledBase/update.xml"
    $mf | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $mfPath -Encoding UTF8
    $version = $mf.version

    & $ChromeExe --pack-extension="$tmp" --pack-extension-key="$keyFull" --no-first-run --no-default-browser-check 2>&1 | Out-Null
    $packed = "$tmp.crx"
    if (-not (Test-Path $packed)) { throw "chrome.exe --pack-extension produced no .crx" }
    Copy-Item -LiteralPath $packed -Destination (Join-Path $outFull "wdm-catcher.crx") -Force
    Remove-Item -LiteralPath $packed -Force -ErrorAction SilentlyContinue

    $updateXml = @"
<?xml version='1.0' encoding='UTF-8'?>
<gupdate xmlns='http://www.google.com/update2/response' protocol='2.0'>
  <app appid='$ExpectedId'>
    <updatecheck codebase='$InstalledBase/wdm-catcher.crx' version='$version' />
  </app>
</gupdate>
"@
    Set-Content -LiteralPath (Join-Path $outFull "update.xml") -Value $updateXml -Encoding UTF8
}
finally {
    if (Test-Path $tmp) { Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue }
}

# Verify the key still yields the expected stable ID (sha256(SPKI) -> a-p alphabet).
python (Join-Path $root "scripts\verify_ext_id.py") $keyFull $ExpectedId
if ($LASTEXITCODE -ne 0) { throw "Extension ID verification failed" }
Get-ChildItem -LiteralPath $outFull -Filter "wdm-catcher.*" | Select-Object Name, Length
