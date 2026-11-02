# Shared by the scenarios that run INSIDE Windows Sandbox (see tools\sandbox\Start-MesuraSandbox.ps1). Refuses to run anywhere else.
$ErrorActionPreference = 'Stop'
if ($env:USERNAME -ne 'WDAGUtilityAccount') { throw 'These scripts install and remove software and change power plans: they run only inside Windows Sandbox (user WDAGUtilityAccount).' }
$script:Report = 'C:\Host\out\report.txt'
New-Item -ItemType Directory -Force 'C:\Host\out' | Out-Null
try { Start-Transcript -Path ('C:\Host\out\transcript-' + [IO.Path]::GetFileNameWithoutExtension($MyInvocation.ScriptName) + '.txt') -Force | Out-Null } catch { }
New-Item -ItemType Directory -Force (Split-Path $script:Report) | Out-Null
function Say([string]$line) { Write-Host $line; Add-Content -Path $script:Report -Value $line -Encoding utf8 }
function Check([string]$what, [bool]$ok, [string]$detail = '') { Say ('{0,-5} {1}{2}' -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $what, $(if ($detail) { "  ($detail)" } else { '' })) }
function Info([string]$what, [string]$detail = '') { Say ('{0,-5} {1}{2}' -f 'INFO', $what, $(if ($detail) { "  ($detail)" } else { '' })) }
function Is-Admin { ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
$packages = Get-Content -Raw 'C:\Host\packages\packages.json' | ConvertFrom-Json
function Package([string]$which) { Join-Path (Join-Path 'C:\Host\packages' $(if ($which -eq 'older') { 'a' } else { 'b' })) $packages.$which }
function Msi([string[]]$arguments) {
  $p = Start-Process msiexec -ArgumentList ($arguments + @('/qn', '/norestart')) -PassThru
  if (-not $p.WaitForExit(300000)) { Say ('FAIL  msiexec ' + ($arguments -join ' ') + ' did not finish in 5 minutes; it was stopped'); try { Stop-Process -Id $p.Id -Force } catch { }; return -9999 }
  $p.ExitCode
}
function Install([string]$which, [string]$scope) {
  $props = if ($scope -eq 'machine') { @('ALLUSERS=1') } else { @('ALLUSERS=2', 'MSIINSTALLPERUSER=1') }
  Msi (@('/i', "`"$(Package $which)`"") + $props)
}
# Every Mesura product Windows Installer knows, by product code and the context it is installed in (UserData: S-1-5-18 is machine-wide, the user's SID is per-user). The
# Uninstall keys are NOT used for the context: a per-user package installed from an elevated process has its Uninstall entry under HKLM (measured here, 2026-11-02).
function Products {
  $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
  $found = @()
  foreach ($c in @(@('machine', 'S-1-5-18'), @('user', $sid))) {
    $k = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\$($c[1])\Products"
    foreach ($e in @(Get-ChildItem $k -ErrorAction SilentlyContinue)) {
      $p = Get-ItemProperty "$($e.PSPath)\InstallProperties" -ErrorAction SilentlyContinue
      if ($p.DisplayName -eq 'Mesura' -and $p.UninstallString -match '(\{[0-9A-Fa-f-]{36}\})') { $found += [pscustomobject]@{ Code = $Matches[1]; Name = 'Mesura'; Version = $p.DisplayVersion; Scope = $c[0] } }
    }
  }
  $found
}
function Remove-All { foreach ($p in Products) { [void](Msi @('/x', $p.Code)) } }
$pf = Join-Path $env:ProgramFiles 'Mesura'
$pu = Join-Path $env:LOCALAPPDATA 'Programs\Mesura'
$data = Join-Path $env:LOCALAPPDATA 'Mesura'
function RunCli([string]$exe, [string[]]$arguments) {
  $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
  try { (& $exe @arguments 2>&1 | Out-String).Trim() } finally { $ErrorActionPreference = $old }
}
# Windows Installer's own record of each Mesura product and the context it is installed in (UserData: S-1-5-18 is machine-wide, the user's SID is per-user).
function Contexts {
  $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
  foreach ($c in @(@('machine', 'S-1-5-18'), @('user', $sid))) {
    $k = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\$($c[1])\Products"
    foreach ($e in @(Get-ChildItem $k -ErrorAction SilentlyContinue)) {
      $p = Get-ItemProperty "$($e.PSPath)\InstallProperties" -ErrorAction SilentlyContinue
      if ($p.DisplayName -eq 'Mesura') { "$($c[0]): v$($p.DisplayVersion), location '$($p.InstallLocation)', uninstall '$($p.UninstallString)'" }
    }
  }
}

