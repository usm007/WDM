# WDM Chrome force-install SPIKE (HKCU, dev only - touches daily profile).
# Real ID pinned via staging/BrowserExtension/wdm-catcher.pem -> manifest.json "key".
# Run from elevated-or-normal PowerShell: powershell -ExecutionPolicy Bypass -File .\install_chrome_policy_dev.ps1
# SURGICAL: reuses our own slot or first free 1..99, merges our key into any
# existing ExtensionSettings JSON (backed up to %TEMP%), never overwrites
# other products' entries.
$ErrorActionPreference = 'Stop'
$extId = 'jehagbjolooaohcbmlhegpmjeaakonof'
$updateXml = 'file:///E:/WDM-master/staging/BrowserExtension/update.xml'
$crxAllow = 'file:///E:/WDM-master/staging/BrowserExtension/*'

$base = 'HKCU:\SOFTWARE\Policies\Google\Chrome'
$force = "$base\ExtensionInstallForcelist"
$Sources = "$base\ExtensionInstallSources"

function Find-PolicySlot([string]$key, [string]$wantPrefix) {
  $existing = $null; $free = $null
  if (Test-Path $key) {
    $props = Get-ItemProperty $key -ErrorAction SilentlyContinue
    foreach ($p in ($props | Get-Member -MemberType NoteProperty | Where-Object { $_.Name -match '^\d+$' })) {
      $v = [string]$props.($p.Name)
      if ($v.StartsWith($wantPrefix)) { return $p.Name }
    }
    for ($i = 1; $i -le 99; $i++) {
      if (-not (Get-ItemProperty $key -Name "$i" -ErrorAction SilentlyContinue)) { $free = "$i"; break }
    }
  } else { $free = '1' }
  if ($free) { return $free }
  throw "No free policy slot under $key"
}

New-Item -Path $force -Force | Out-Null
$slot = Find-PolicySlot $force "$extId;"
New-ItemProperty -Path $force -Name $slot -Value "$extId;$updateXml" -PropertyType String -Force | Out-Null

New-Item -Path $Sources -Force | Out-Null
$srcSlot = Find-PolicySlot $Sources $crxAllow
New-ItemProperty -Path $Sources -Name $srcSlot -Value $crxAllow -PropertyType String -Force | Out-Null

# Merge our key into existing ExtensionSettings instead of overwriting it.
New-Item -Path $base -Force | Out-Null
$current = (Get-ItemProperty $base -Name 'ExtensionSettings' -ErrorAction SilentlyContinue).ExtensionSettings
if ($current) {
  $backup = Join-Path $env:TEMP ("wdm-extsettings-backup-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + ".json")
  Set-Content -LiteralPath $backup -Value $current
  Write-Host "Backed up existing ExtensionSettings to $backup"
  try { $obj = $current | ConvertFrom-Json } catch { throw "Existing ExtensionSettings is not valid JSON; backup kept at $backup, aborting to avoid clobbering it." }
} else { $obj = New-Object PSObject }
$obj | Add-Member -NotePropertyName $extId -NotePropertyValue ([PSCustomObject]@{
  installation_mode = 'force_installed'
  update_url = $updateXml
  override_update_url = $true
}) -Force
$settings = $obj | ConvertTo-Json -Compress -Depth 5
New-ItemProperty -Path $base -Name 'ExtensionSettings' -Value $settings -PropertyType String -Force | Out-Null

Write-Host 'Wrote HKCU Chrome policy spike:'
Write-Host "  Forcelist $slot = $extId;$updateXml"
Write-Host "  Sources   $srcSlot = $crxAllow"
Write-Host "  Settings    = $settings"
Write-Host ''
Write-Host 'Next: kill ALL chrome.exe, relaunch Chrome, check chrome://policy then chrome://extensions'
Write-Host 'Rollback: powershell -ExecutionPolicy Bypass -File .\uninstall_chrome_policy_dev.ps1'
