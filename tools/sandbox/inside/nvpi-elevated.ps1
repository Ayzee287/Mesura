# Inside Windows Sandbox: how an elevated Mesura treats NVIDIA Profile Inspector in a per-user winget folder, on the real Windows (access lists, the unelevated shell token).
# 1. the Setup test class that needs an elevated process and a desktop shell (it skips on a developer session): real protected folders, the real token from the shell, a genuine release
#    staged and verified, a program running as the user failing to alter it;
# 2. `mesura setup nvpi-check` against a genuine NVIDIA Profile Inspector placed where winget puts a per-user package. The sandbox has no NVIDIA GPU, so the tool cannot read any profile there:
#    what this shows is the decision (which build, which folder, who runs it), not a profile export.
# Sandbox only: it creates folders under ProgramData and runs a program as the shell user.
. 'C:\Host\tools\sandbox\inside\common.ps1'
Say "== nvpi-elevated $(Get-Date -Format s) on $env:COMPUTERNAME as $env:USERNAME (admin: $(Is-Admin))"
Check 'this process is elevated (the tests need it)' (Is-Admin)
$runner = 'C:\Host\packages\tests\Mesura.Setup.Tests.exe'
$sample = 'C:\Host\packages\nvpi-sample'
Check 'the test runner and the genuine NVIDIA Profile Inspector sample are there' ((Test-Path $runner) -and (Test-Path "$sample\nvidiaProfileInspector.exe"))
$hash = (Get-FileHash "$sample\nvidiaProfileInspector.exe" -Algorithm SHA256).Hash
Check 'the sample is release 3.0.2.1' ($hash -eq '1EBD8129B3C564BF226291FB3344819FD59668066F0C5E03334A69A04A62859E') $hash

foreach ($f in 'C:\', 'C:\ProgramData', 'C:\Program Files') { Info "icacls $f" ((icacls $f 2>&1 | Out-String).Trim()) }
# Windows Sandbox's image gives "Authenticated Users" Modify on the root of C:\ ITSELF (a standard Windows gives it only "create folders" there, and Modify on what is below). Mesura's folder check
# (ProgramFolderTrust) reads every parent up to the root, so here it rightly says nothing under C:\ is protected, Program Files included. To see the check on a root like a standard Windows has, the
# disposable sandbox's root is set to the standard form first (this changes only the sandbox, which is thrown away).
$old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
icacls 'C:\' /remove:g 'NT AUTHORITY\Authenticated Users' | Out-Null
icacls 'C:\' /grant 'NT AUTHORITY\Authenticated Users:(AD)' | Out-Null
icacls 'C:\' /grant 'NT AUTHORITY\Authenticated Users:(OI)(CI)(IO)(M)' | Out-Null
$ErrorActionPreference = $old
Info 'icacls C:\ after making it like a standard Windows root' ((icacls 'C:\' 2>&1 | Out-String).Trim())
# Who owns a folder an elevated process creates with an explicit access list and no owner set (what the updater's ProgramDataProtectedFolders did): the account, or the Administrators group?
$ownerProbe = Join-Path $env:ProgramData ('MesuraOwnerProbe' + [guid]::NewGuid().ToString('N').Substring(0, 6))
$sec = New-Object System.Security.AccessControl.DirectorySecurity
$sec.SetAccessRuleProtection($true, $false)
foreach ($sid in 'S-1-5-18', 'S-1-5-32-544') { $sec.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule((New-Object System.Security.Principal.SecurityIdentifier($sid)), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))) }
$sec.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule((New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-545')), 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
(New-Object System.IO.DirectoryInfo($ownerProbe)).Create($sec)
Info 'owner of a folder made with an explicit access list and no owner set' ((Get-Acl $ownerProbe).Owner)
Remove-Item $ownerProbe -Recurse -Force
$env:MESURA_NVPI_SAMPLE = $sample
# The sandbox's desktop shell is itself elevated (UAC off), so there is no unelevated token to take from it. A program that is not an administrator is stood in for by a standard local user made here
# (random password, never written anywhere, removed afterwards); the tests also write a control file as that user, so "could not write" cannot mean "did not run".
$probePassword = 'Pr0be!' + ([guid]::NewGuid().ToString('N')).Substring(0, 7)  # at most 14 characters: net user asks for confirmation beyond that
$old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
$made = (net user mesuraprobe $probePassword /add 2>&1 | Out-String); $madeCode = $LASTEXITCODE
Info 'net user /add' "exit $madeCode; $($made.Trim())"
Check 'a standard (non-administrator) local user was made for the probe' (($madeCode -eq 0) -and -not ((net localgroup Administrators) -match 'mesuraprobe'))
$env:MESURA_PROBE_USER = 'mesuraprobe'; $env:MESURA_PROBE_PASSWORD = $probePassword
$out = & $runner -class 'Mesura.Setup.Tests.NvidiaElevatedRealWindowsTests' 2>&1 | Out-String
net user mesuraprobe /delete | Out-Null
Remove-Item Env:\MESURA_PROBE_PASSWORD
$ErrorActionPreference = $old
Info 'real-Windows tests' $out
Check 'the real-Windows tests passed, none skipped' (($out -match 'Failed: 0') -and ($out -match 'Skipped: 0') -and ($out -match 'Total: 5'))

# the updater's own protected folder, made for real (owner, access list, folder trust)
$upd = 'C:\Host\packages\tests-updating\Mesura.Updating.Tests.exe'
if (Test-Path $upd) {
  $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
  $outU = & $upd -method '*A_real_protected_folder_made_elevated*' 2>&1 | Out-String
  $ErrorActionPreference = $old
  Info 'updater protected folder' $outU
  Check 'the updater''s real protected folder test passed, not skipped' (($outU -match 'Failed: 0') -and ($outU -match 'Skipped: 0') -and ($outU -match 'Total: 1'))
}

# 2. the decision on a winget per-user copy
$pkg = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages\Orbmu2k.nvidiaProfileInspector_Microsoft.Winget.Source_8wekyb3d8bbwe'
New-Item -ItemType Directory -Force $pkg | Out-Null
Copy-Item "$sample\*" $pkg
$cli = Join-Path 'C:\Host\packages' $packages.cli
$stdout = 'C:\Host\out\nvpi-check.txt'
$p = Start-Process $cli -ArgumentList 'setup', 'nvpi-check' -RedirectStandardOutput $stdout -RedirectStandardError 'C:\Host\out\nvpi-check-err.txt' -PassThru -WindowStyle Hidden
# NVIDIA Profile Inspector cannot read profiles without an NVIDIA GPU: it shows an error and waits, Mesura gives up on it after 90 seconds per attempt and leaves it open for the user (by design)
if (-not $p.WaitForExit(420000)) { try { $p.Kill() } catch { } ; $check = 'mesura setup nvpi-check did not finish in 7 minutes' } else { $check = '' }
$check = ($check + "`r`n" + (Get-Content $stdout -Raw -ErrorAction SilentlyContinue) + (Get-Content 'C:\Host\out\nvpi-check-err.txt' -Raw -ErrorAction SilentlyContinue) + "`r`nexit code $($p.ExitCode)").Trim()
Info 'mesura setup nvpi-check' $check
Check 'it found the per-user copy, knows the build and sees the folder as user-writable' (($check -match 'Verified:\s+yes, release 3\.0\.2\.1') -and ($check -match 'programs running as you can change it') -and ($check -match 'This process: administrator'))
Get-Process nvidiaProfileInspector -ErrorAction SilentlyContinue | Stop-Process -Force
try {
  Add-Type -AssemblyName System.Drawing, System.Windows.Forms
  $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
  $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height); [System.Drawing.Graphics]::FromImage($bmp).CopyFromScreen($b.X, $b.Y, 0, 0, $bmp.Size); $bmp.Save('C:\Host\out\after-nvpi-check.png')
} catch { }
Say "== done $(Get-Date -Format s)"
