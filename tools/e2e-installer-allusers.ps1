# Installs Mesura FOR ALL USERS, updates it, and uninstalls it, on a test PC or a virtual machine, and checks what the installer promises (0.5, docs/installing.md):
#   * the program goes to Program Files and is registered machine-wide (HKLM), nothing of it lands in a user's profile;
#   * each user's own data folder (%LOCALAPPDATA%\Mesura) is never touched by install, update or uninstall, and a second user's is never seen;
#   * the wizard's scope page offers both choices and "Only for me" is the default.
#
# DO NOT RUN THIS ON A PC WHOSE Mesura YOU USE. It installs and removes a machine-wide Mesura, and it will refuse to run when a Mesura is already
# registered on the PC, unless -Force is given. It has not been run by its author (no virtual machine was available when it was written); read it
# first, run it on a throwaway VM, and treat its first run as part of the test.
#
#   Run from an ELEVATED terminal:
#   powershell -ExecutionPolicy Bypass -File tools\e2e-installer-allusers.ps1 -Msi <Mesura-x.y.z-x64.msi> [-NewerMsi <a later package, to test the update>] [-Report <file>] [-Force]
param(
  [Parameter(Mandatory = $true)][string]$Msi,
  [string]$NewerMsi = '',
  [string]$Report = '',
  [switch]$Force
)
$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this from an elevated terminal.' }
$Msi = (Resolve-Path $Msi).Path
if ($NewerMsi) { $NewerMsi = (Resolve-Path $NewerMsi).Path }
$results = New-Object System.Collections.Generic.List[string]
function Say([string]$l) { $results.Add($l); Write-Host $l; if ($Report) { $l | Out-File $Report -Append -Encoding utf8 } }
function Check([string]$what, [bool]$ok, [string]$detail = '') { Say ('{0,-5} {1}{2}' -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $what, $(if ($detail) { "  ($detail)" } else { '' })) }
if ($Report) { Remove-Item $Report -ErrorAction SilentlyContinue }

# Refuse to disturb a Mesura that is in use.
$uninstall = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'
$existing = @($uninstall | ForEach-Object { Get-ChildItem $_ -ErrorAction SilentlyContinue } | Where-Object { (Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue).DisplayName -eq 'Mesura' })
if ($existing.Count -gt 0 -and -not $Force) { throw "A Mesura is already installed on this PC ($($existing.Count) entry/entries). This test is for a throwaway PC; use -Force only if you mean it." }

$programFiles = Join-Path $env:ProgramFiles 'Mesura'
$userDir = Join-Path $env:LOCALAPPDATA 'Programs\Mesura'
$data = Join-Path $env:LOCALAPPDATA 'Mesura'
$sentinel = Join-Path $data 'Setup\e2e-sentinel.txt'
New-Item -ItemType Directory -Force (Split-Path $sentinel) | Out-Null
'user data that no installer may touch' | Set-Content $sentinel -Encoding utf8

function Msi-Run([string]$package, [string[]]$arguments) {
  $p = Start-Process msiexec -ArgumentList (@('/i', "`"$package`"") + $arguments + @('/qn', '/norestart')) -Wait -PassThru
  $p.ExitCode
}
function Product-Context {
  # 'machine' when Windows Installer keeps the product under S-1-5-18, 'user' under the signed-in user's SID, else 'none'.
  $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
  foreach ($c in @(@('machine', 'S-1-5-18'), @('user', $sid))) {
    $k = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\$($c[1])\Products"
    $hit = @(Get-ChildItem $k -ErrorAction SilentlyContinue | Where-Object { (Get-ItemProperty "$($_.PSPath)\InstallProperties" -ErrorAction SilentlyContinue).DisplayName -eq 'Mesura' })
    if ($hit.Count -gt 0) { return $c[0] }
  }
  'none'
}

try {
  Say "== package $(Split-Path $Msi -Leaf); machine $env:COMPUTERNAME; user $env:USERNAME"

  # 1. The all-users install, as the updater and a silent install do it
  $code = Msi-Run $Msi @('ALLUSERS=1')
  Check 'the all-users install finishes' ($code -in 0, 3010) "exit $code"
  Check 'Mesura.exe is in Program Files' (Test-Path (Join-Path $programFiles 'Mesura.exe')) $programFiles
  Check 'the command line is installed beside it' (Test-Path (Join-Path $programFiles 'cli\mesura.exe'))
  Check 'nothing was put in the user''s own programs folder' (-not (Test-Path (Join-Path $userDir 'Mesura.exe')))
  Check 'Windows Installer keeps the product machine-wide' ((Product-Context) -eq 'machine') (Product-Context)
  $reg = Get-ItemProperty 'HKLM:\SOFTWARE\Mesura' -ErrorAction SilentlyContinue
  Check 'HKLM\Software\Mesura names the install folder' ($reg -and ($reg.InstallDir.TrimEnd('\') -ieq $programFiles)) "$($reg.InstallDir)"
  Check 'the machine-wide Start menu has the shortcut' (Test-Path (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Mesura.lnk'))
  $acl = (Get-Acl $programFiles).Access | Where-Object { $_.IdentityReference -match 'Users$' -and $_.FileSystemRights -match 'Write|Modify|FullControl' -and $_.AccessControlType -eq 'Allow' }
  Check 'ordinary users cannot write to the program folder' (-not $acl) (($acl | ForEach-Object { "$($_.IdentityReference) $($_.FileSystemRights)" }) -join '; ')
  Check 'the user''s own data is untouched' ((Get-Content $sentinel -Raw).Trim() -eq 'user data that no installer may touch')
  Check 'no shared data folder was created' (-not (Test-Path (Join-Path $env:ProgramData 'Mesura')))

  # 2. The same package again (a repair-style major upgrade), then a newer one when given
  $code = Msi-Run $Msi @('ALLUSERS=1')
  Check 'installing the same package again over it finishes' ($code -in 0, 3010) "exit $code"
  Check 'there is still one machine-wide product' ((Product-Context) -eq 'machine')
  if ($NewerMsi) {
    $code = Msi-Run $NewerMsi @('ALLUSERS=1')
    Check 'a newer package upgrades the all-users copy in place' ($code -in 0, 3010) "exit $code"
    Check 'the program is still in Program Files, not moved' (Test-Path (Join-Path $programFiles 'Mesura.exe'))
    Check 'the product is still machine-wide, once' ((Product-Context) -eq 'machine')
    Check 'the user''s own data is still untouched' ((Get-Content $sentinel -Raw).Trim() -eq 'user data that no installer may touch')
  }

  # 3. Uninstall
  # after the update the newer package is the installed product: that is the one to remove
  $installedPackage = if ($NewerMsi) { $NewerMsi } else { $Msi }
  $x = Start-Process msiexec -ArgumentList "/x `"$installedPackage`" /qn /norestart" -Wait -PassThru
  Check 'the uninstall finishes' ($x.ExitCode -in 0, 3010) "exit $($x.ExitCode)"
  Check 'the program files are gone' (-not (Test-Path (Join-Path $programFiles 'Mesura.exe')))
  Check 'the machine-wide registration is gone' (-not (Test-Path 'HKLM:\SOFTWARE\Mesura'))
  Check 'the shortcut is gone' (-not (Test-Path (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Mesura.lnk')))
  Check 'the user''s own data survived the uninstall' (Test-Path $sentinel)

  # 4. The wizard: the scope page (drive it by hand or with tools\e2e-installer-wizard.ps1, which checks the default); record the result here
  Say '      not automated here: the wizard''s scope page with "For all users of this PC" chosen, and the UAC prompt it triggers when run unelevated'
}
finally {
  if (Test-Path $sentinel) { Remove-Item $sentinel -Force -ErrorAction SilentlyContinue }
  $failed = @($results | Where-Object { $_ -like 'FAIL*' }).Count
  Say ("{0} lines, {1} failed" -f $results.Count, $failed)
}
