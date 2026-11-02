# Inside Windows Sandbox: installs, updates, uninstalls and the move between "only for me" and "for all users", with the real Windows Installer and the real
# `mesura update copies` commands. Where Windows Installer's behaviour was NOT known when this was written, the script records what happens (INFO) instead of asserting
# it; the owner reads those lines. Results: C:\Host\out\report.txt on the host side.
. 'C:\Host\tools\sandbox\inside\common.ps1'
Say "== installer-moves $(Get-Date -Format s) on $env:COMPUTERNAME as $env:USERNAME (admin: $(Is-Admin))"
Remove-All
New-Item -ItemType Directory -Force (Join-Path $data 'Setup') | Out-Null
'user data no installer may touch' | Set-Content (Join-Path $data 'Setup\sentinel.txt') -Encoding utf8
function Sentinel-Ok { (Test-Path (Join-Path $data 'Setup\sentinel.txt')) -and ((Get-Content (Join-Path $data 'Setup\sentinel.txt') -Raw).Trim() -eq 'user data no installer may touch') }

if (-not (Test-Path 'C:\Host\out\skip-basic.txt')) {  # the host can create this file to skip the parts that already passed twice
# --- 1. each kind on its own -------------------------------------------------------------------------------------------------------------------------------
Check 'install for all users finishes' ((Install 'older' 'machine') -in 0, 3010)
Check 'it is in Program Files' (Test-Path (Join-Path $pf 'Mesura.exe')) $pf
Check 'HKLM names the folder' ((Get-ItemProperty 'HKLM:\SOFTWARE\Mesura' -ErrorAction SilentlyContinue).InstallDir -like "$pf*")
Info 'products after the all-users install' ((Products | ForEach-Object { "$($_.Scope) $($_.Code) $($_.Version)" }) -join '; ')
Remove-All
Check 'removing it leaves no product, no folder' ((@(Products).Count -eq 0) -and -not (Test-Path (Join-Path $pf 'Mesura.exe')))
Check 'install for me only finishes' ((Install 'older' 'user') -in 0, 3010)
Check 'it is in the user''s programs folder' (Test-Path (Join-Path $pu 'Mesura.exe')) $pu
Check 'HKCU names the folder' ((Get-ItemProperty 'HKCU:\SOFTWARE\Mesura' -ErrorAction SilentlyContinue).InstallDir -like "$pu*")
Remove-All
Check 'user data survived install and uninstall of both kinds' (Sentinel-Ok)

# --- 2. update in place ------------------------------------------------------------------------------------------------------------------------------------
[void](Install 'older' 'machine')
$before = (Products | ForEach-Object Code)
Check 'a newer all-users package upgrades in place' ((Install 'newer' 'machine') -in 0, 3010)
Info 'products after the update' ((Products | ForEach-Object { "$($_.Scope) $($_.Code) $($_.Version)" }) -join '; ')
Check 'exactly one product remains after the update' (@(Products).Count -eq 1)
Check 'the version is the newer one' ((Products | Select-Object -First 1).Version -like ((($packages.newer -replace '^Mesura-', '' -replace '-sandbox.*$', '')) + '*'))
Remove-All

}
# --- 3. the same package in both kinds (A5b): record what Windows Installer does ---------------------------------------------------------------------------
[void](Install 'older' 'user')
$code = Install 'older' 'machine'
Info 'same package, per-user then all-users' "exit $code; products: $((Products | ForEach-Object { "$($_.Scope) $($_.Code) $($_.Version)" }) -join '; ')"
Info 'Windows Installer record (context)' ((Contexts) -join ' | ')
Info 'files and registry after that' ("all-users exe: $(Test-Path (Join-Path $pf 'Mesura.exe')), cli: $(Test-Path (Join-Path $pf 'cli\mesura.exe')); per-user exe: $(Test-Path (Join-Path $pu 'Mesura.exe')), cli: $(Test-Path (Join-Path $pu 'cli\mesura.exe')); HKLM InstallDir: $((Get-ItemProperty 'HKLM:\SOFTWARE\Mesura' -ErrorAction SilentlyContinue).InstallDir); HKCU InstallDir: $((Get-ItemProperty 'HKCU:\SOFTWARE\Mesura' -ErrorAction SilentlyContinue).InstallDir)")
if (Test-Path (Join-Path $pf 'cli\mesura.exe')) { Info 'registrations (mesura update copies)' (RunCli (Join-Path $pf 'cli\mesura.exe') @('update', 'copies')) }
Remove-All

# --- 4. per-user older, then all-users newer (A4) -----------------------------------------------------------------------------------------------------------
Check 'older per-user install' ((Install 'older' 'user') -in 0, 3010)
$codeNew = Install 'newer' 'machine'
Info 'all-users newer over per-user older' "exit $codeNew; products: $((Products | ForEach-Object { "$($_.Scope) $($_.Code) $($_.Version)" }) -join '; ')"
Info 'Windows Installer record (context)' ((Contexts) -join ' | ')
$list = RunCli (Join-Path $pf 'cli\mesura.exe') @('update', 'copies')
Info 'registrations seen from the new all-users copy' $list
if (@(Products).Count -ge 2) {
  Check 'both copies are registered (two products, as expected)' $true
  Check 'the new copy offers to remove the other' ($list -match 'can be removed')
  $dry = RunCli (Join-Path $pf 'cli\mesura.exe') @('update', 'copies', 'remove')
  Check 'without --yes nothing is removed' ((@(Products).Count -ge 2) -and ($dry -match 'add --yes'))
  $removed = RunCli (Join-Path $pf 'cli\mesura.exe') @('update', 'copies', 'remove', '--yes')
  Info 'removal' $removed
  Check 'the per-user copy is gone' (-not (Test-Path (Join-Path $pu 'Mesura.exe')))
  Check 'the all-users copy is still installed and registered' ((Test-Path (Join-Path $pf 'Mesura.exe')) -and (@(Products).Count -eq 1))
}
else {
  Info 'Windows Installer already replaced the per-user copy when the all-users package was installed' 'the removal step is not needed in this direction; the registrations above show it'
  Check 'one product remains and Mesura is in Program Files' ((@(Products).Count -eq 1) -and (Test-Path (Join-Path $pf 'Mesura.exe')))
}
Check 'user data untouched by the move' (Sentinel-Ok)
Remove-All

# --- 5. all-users older, then per-user newer (A5) -----------------------------------------------------------------------------------------------------------
Check 'older all-users install' ((Install 'older' 'machine') -in 0, 3010)
$codeNew = Install 'newer' 'user'
Info 'per-user newer over all-users older' "exit $codeNew; products: $((Products | ForEach-Object { "$($_.Scope) $($_.Code) $($_.Version)" }) -join '; ')"
Info 'Windows Installer record (context)' ((Contexts) -join ' | ')
$cli = if (Test-Path (Join-Path $pu 'cli\mesura.exe')) { Join-Path $pu 'cli\mesura.exe' } else { Join-Path $pf 'cli\mesura.exe' }
Info 'registrations' (RunCli $cli @('update', 'copies'))
if ((@(Products).Count -ge 2) -and (Test-Path (Join-Path $pu 'cli\mesura.exe'))) {
  $removed = RunCli $cli @('update', 'copies', 'remove', '--yes')
  Info 'removal of the all-users copy' $removed
  Check 'the all-users copy is gone' (-not (Test-Path (Join-Path $pf 'Mesura.exe')))
  Check 'the per-user copy is still installed' (Test-Path (Join-Path $pu 'Mesura.exe'))
}
Check 'user data untouched' (Sentinel-Ok)
Remove-All
Check 'everything removed at the end' (@(Products).Count -eq 0)
Say "== done $(Get-Date -Format s)"



