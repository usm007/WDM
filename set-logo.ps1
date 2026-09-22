#Requires -Version 5.1
<#
.SYNOPSIS
  Sets a new WDM logo from a source PNG: regenerates assets, rebuilds, installs.
.DESCRIPTION
  1. Regenerates src\WDM\Assets\logo.png (256px) and WDM.ico (16-256 multi-size)
     plus the browser-extension icons (16/32/48/128, chrome + firefox folders)
     from the source image (padded to square, transparency preserved).
  2. Runs build-test-install.ps1 (publish -> Inno installer -> silent install).
  3. Refreshes the Windows icon cache so the new icon shows immediately.
  Requires python with Pillow on PATH (pip install Pillow).
.EXAMPLE
  powershell -File set-logo.ps1 -Source logo_designs\wdm.png
  powershell -File set-logo.ps1 -Source C:\pics\new.png -SkipBuild
  powershell -File set-logo.ps1 -Source logo_designs\wdm_dark.png -ExtensionSource logo_designs\wdm.png
#>
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [string]$ExtensionSource = "",
    [switch]$SkipBuild,
    [switch]$SkipCacheRefresh
)

$ErrorActionPreference = "Stop"

# 1. Resolve source image
$srcPath = $Source
if (-not [System.IO.Path]::IsPathRooted($srcPath)) { $srcPath = Join-Path $PSScriptRoot $srcPath }
if (-not (Test-Path $srcPath -PathType Leaf)) { throw "Source image not found: $Source" }
Write-Host "Source: $srcPath"
if ([string]::IsNullOrWhiteSpace($ExtensionSource)) { $ExtensionSource = $Source }
$extSrcPath = $ExtensionSource
if (-not [System.IO.Path]::IsPathRooted($extSrcPath)) { $extSrcPath = Join-Path $PSScriptRoot $extSrcPath }
if (-not (Test-Path $extSrcPath -PathType Leaf)) { throw "Extension source image not found: $ExtensionSource" }
Write-Host "Extension source: $extSrcPath"

# 2. python + Pillow check
$python = Get-Command python -ErrorAction SilentlyContinue
if (-not $python) { throw "python not found on PATH (needs Pillow: pip install Pillow)." }
& python -c "import PIL" 2>$null
if ($LASTEXITCODE -ne 0) { throw "Python Pillow is missing: pip install Pillow." }

# 3. Regenerate assets (argv avoids all quoting pain)
$pyCode = @'
import sys
from collections import deque
from PIL import Image, ImageFilter
src, assets, ext1, ext2, extsrc = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4], sys.argv[5]
def dewhite(im):
    # Opaque near-white background (common in exports): flood-fill from edges
    # only, so white highlights inside the glyph survive.
    if im.getpixel((0, 0))[3] == 0:
        return im
    px = im.load(); w, h = im.size
    seen = [[False]*h for _ in range(w)]
    q = deque()
    for x in range(w):
        q.append((x, 0)); q.append((x, h-1))
    for y in range(h):
        q.append((0, y)); q.append((w-1, y))
    def near_white(p):
        return p[3] == 255 and p[0] >= 245 and p[1] >= 245 and p[2] >= 245
    while q:
        x, y = q.popleft()
        if x < 0 or y < 0 or x >= w or y >= h or seen[x][y]:
            continue
        seen[x][y] = True
        p = px[x, y]
        if not near_white(p):
            continue
        px[x, y] = (255, 255, 255, 0)
        q.extend(((x+1, y), (x-1, y), (x, y+1), (x, y-1)))
    return im
def squared(path):
    im = dewhite(Image.open(path).convert("RGBA"))
    w, h = im.size
    s = max(w, h)
    sq = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    sq.paste(im, ((s - w) // 2, (s - h) // 2), im)
    # Optical sizing for 16-32px surfaces: expand bbox to 98% of frame and
    # slightly dilate alpha so thin strokes keep presence at tray/taskbar size.
    bb = sq.getbbox()
    if bb:
        bw, bh = bb[2] - bb[0], bb[3] - bb[1]
        if max(bw, bh) < 0.98 * s:
            crop = sq.crop(bb)
            f = 0.98 * s / max(bw, bh)
            crop = crop.resize((round(bw * f), round(bh * f)), Image.LANCZOS)
            sq = Image.new("RGBA", (s, s), (0, 0, 0, 0))
            sq.paste(crop, ((s - crop.size[0]) // 2, (s - crop.size[1]) // 2), crop)
    r, g, b, a = sq.split()
    a = a.filter(ImageFilter.MaxFilter(5))
    sq = Image.merge("RGBA", (r, g, b, a))
    return sq
sq = squared(src)
sq.resize((256, 256), Image.LANCZOS).save(assets + "\\logo.png", "PNG", optimize=True)
sq.save(assets + "\\WDM.ico", "ICO", sizes=[(16, 16), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
sqx = squared(extsrc)
for d in (ext1, ext2):
    for x in (16, 32, 48, 128):
        sqx.resize((x, x), Image.LANCZOS).save("%s\\icon%d.png" % (d, x), "PNG", optimize=True)
print("app assets from " + src + ", extension icons from " + extsrc)
'@
$assetsDir = Join-Path $PSScriptRoot "src\WDM\Assets"
$extDir = Join-Path $PSScriptRoot "src\WDM.BrowserExtension"
$ffDir = Join-Path $extDir "firefox"
$tmpPy = Join-Path $env:TEMP "wdm-set-logo.py"
Set-Content -LiteralPath $tmpPy -Value $pyCode -Encoding Ascii
try
{
    & python $tmpPy $srcPath $assetsDir $extDir $ffDir $extSrcPath
    if ($LASTEXITCODE -ne 0) { throw "Asset generation failed." }
}
finally
{
    Remove-Item $tmpPy -Force -ErrorAction SilentlyContinue
}

# 4. Build installer + install (handles its own versioning/UAC prompt)
if (-not $SkipBuild)
{
    & (Join-Path $PSScriptRoot "build-test-install.ps1")
}

# 5. Refresh icon cache
if (-not $SkipCacheRefresh)
{
    Write-Host "Refreshing icon cache ..."
    Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
    Start-Sleep 2
    Remove-Item (Join-Path $env:LOCALAPPDATA "IconCache.db") -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $env:LOCALAPPDATA "Microsoft\Windows\Explorer\iconcache*.db") -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $env:LOCALAPPDATA "Microsoft\Windows\Explorer\thumbcache*.db") -Force -ErrorAction SilentlyContinue
    Start-Process explorer
    Start-Sleep 3
    & ie4uinit.exe -show
}

Write-Host "Done."
