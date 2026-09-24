#Requires -Version 5.1
<#
.SYNOPSIS
  WDM developer test commands (PS 5.1-compatible).
.EXAMPLE
  powershell -File scripts/test.ps1
  powershell -File scripts/test.ps1 -Category RangeEngine
  powershell -File scripts/test.ps1 -Category Stress -Config Release
#>
param(
  [string]$Category = "",
  [string]$Config = "Release"
)
$ErrorActionPreference = "Stop"
$filter = ""
if ($Category -ne "") {
  if ($Category -eq "Fast") { $filter = "--filter `"Category!=Stress&Category!=Performance`"" }
  elseif ($Category -eq "Stress") { $filter = "--filter `"Category=Stress|Category=Performance`"" }
  else { $filter = "--filter `"Category=$Category`"" }
}
$cmd = "dotnet test WDM.sln -c $Config --nologo $filter"
Write-Host $cmd
Invoke-Expression $cmd
