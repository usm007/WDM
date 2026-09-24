# Rollback for install_chrome_policy_dev.ps1 - removes only WDM's HKCU spike.
# SURGICAL: deletes only Forcelist values starting with our extension ID,
# only Sources values pointing at WDM/BrowserExtension, and only our key
# inside the shared ExtensionSettings JSON (other tenants preserved).
# Forcelist is compacted back to 1..N so other products' entries keep applying.
$ErrorActionPreference = 'Continue'
$extId = 'jehagbjolooaohcbmlhegpmjeaakonof'
$base = 'HKCU:\SOFTWARE\Policies\Google\Chrome'
$force = "$base\ExtensionInstallForcelist"
$Sources = "$base\ExtensionInstallSources"

$deletedForcelist = $false
if (Test-Path $force) {
  foreach ($p in (Get-ItemProperty $force -ErrorAction SilentlyContinue | Get-Member -MemberType NoteProperty | Where-Object { $_.Name -match '^\d+$' })) {
    $v = [string](Get-ItemProperty $force -Name $p.Name -ErrorAction SilentlyContinue).($p.Name)
    if ($v.StartsWith("$extId;")) { Remove-ItemProperty -Path $force -Name $p.Name -ErrorAction SilentlyContinue; $deletedForcelist = $true }
  }
  if ($deletedForcelist) {
    $rest = @()
    foreach ($p in (Get-ItemProperty $force -ErrorAction SilentlyContinue | Get-Member -MemberType NoteProperty | Where-Object { $_.Name -match '^\d+$' } | Sort-Object { [int]$_.Name })) {
      $rest += [string](Get-ItemProperty $force -Name $p.Name -ErrorAction SilentlyContinue).($p.Name)
    }
    foreach ($p in (Get-ItemProperty $force -ErrorAction SilentlyContinue | Get-Member -MemberType NoteProperty | Where-Object { $_.Name -match '^\d+$' })) {
      Remove-ItemProperty -Path $force -Name $p.Name -ErrorAction SilentlyContinue
    }
    for ($i = 0; $i -lt $rest.Count; $i++) {
      New-ItemProperty -Path $force -Name ("{0}" -f ($i + 1)) -Value $rest[$i] -PropertyType String -Force | Out-Null
    }
  }
}
if (Test-Path $Sources) {
  foreach ($p in (Get-ItemProperty $Sources -ErrorAction SilentlyContinue | Get-Member -MemberType NoteProperty | Where-Object { $_.Name -match '^\d+$' })) {
    $v = [string](Get-ItemProperty $Sources -Name $p.Name -ErrorAction SilentlyContinue).($p.Name)
    if ($v.Contains('WDM\BrowserExtension') -or $v.Contains('WDM/BrowserExtension')) { Remove-ItemProperty -Path $Sources -Name $p.Name -ErrorAction SilentlyContinue }
  }
}
$current = (Get-ItemProperty $base -Name 'ExtensionSettings' -ErrorAction SilentlyContinue).ExtensionSettings
if ($current -and $current.Contains($extId)) {
  try {
    $obj = $current | ConvertFrom-Json
    $obj.PSObject.Properties.Remove($extId)
    $remaining = @($obj.PSObject.Properties | ForEach-Object { $_.Name })
    if ($remaining.Count -eq 0) { Remove-ItemProperty -Path $base -Name 'ExtensionSettings' -ErrorAction SilentlyContinue }
    else {
      $merged = $obj | ConvertTo-Json -Compress -Depth 5
      New-ItemProperty -Path $base -Name 'ExtensionSettings' -Value $merged -PropertyType String -Force | Out-Null
    }
  } catch { Write-Host 'ExtensionSettings parse failed; left untouched to avoid clobbering other entries.' }
}
foreach ($k in @($force, $Sources)) {
  if ((Test-Path $k) -and -not (Get-ItemProperty $k -ErrorAction SilentlyContinue | Get-Member -MemberType NoteProperty | Where-Object { $_.Name -ne 'PSPath' -and $_.Name -ne 'PSParentPath' -and $_.Name -ne 'PSChildName' -and $_.Name -ne 'PSDrive' -and $_.Name -ne 'PSProvider' })) {
    Remove-Item $k -Force -ErrorAction SilentlyContinue
  }
}
Write-Host 'WDM HKCU Chrome policy spike removed (other entries preserved). Kill ALL chrome.exe and relaunch to confirm.'
