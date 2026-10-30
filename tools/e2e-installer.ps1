# Lifecycle check of the Windows Installer package, without publishing or touching a real install (docs/testing-0.3.md, docs/installing.md).
# Real msiexec runs, per user, into a throwaway folder (spaces and Cyrillic in the path), every result read back from the registry and the disk:
#
#   fresh install  ·  reinstall over itself  ·  uninstall (folder, registration and shortcut gone)  ·  install again
#   upgrade over an older build  ·  a failed update (the old version must be back AND still uninstallable)  ·  a failed first install
#   a failed update must not leave Windows Installer "component clients" behind (they make a later uninstall keep the folder)
#
#   powershell -ExecutionPolicy Bypass -File tools\e2e-installer.ps1 -Old <folder of the old build> -New <folder of the new build> [-Report <file>]
#
# Both folders are tools\publish.ps1 + tools\build-installer.ps1 outputs. The new one also needs the test package from
# `tools\build-installer.ps1 -FailInstall` (Mesura-<v>-x64-FAILTEST.msi) for the failure scenarios; without it they are reported as SKIPPED, not passed.
# It refuses to run when Mesura is registered for this user (HKCU\Software\Mesura), never touches %LOCALAPPDATA%\Programs\Mesura, and puts your Start menu
# Mesura shortcut back. No UAC prompt appears (per-user installs).
param(
  [Parameter(Mandatory = $true)][string]$Old,
  [Parameter(Mandatory = $true)][string]$New,
  [string]$Work = '',
  [string]$Report = ''
)
$ErrorActionPreference = 'Stop'
$Old = (Resolve-Path $Old).Path; $New = (Resolve-Path $New).Path
$oldInfo = Get-Content -Raw (Join-Path $Old 'build.json') | ConvertFrom-Json; $newInfo = Get-Content -Raw (Join-Path $New 'build.json') | ConvertFrom-Json
$oldMsi = Join-Path $Old "Mesura-$($oldInfo.version)-x64.msi"; $newMsi = Join-Path $New "Mesura-$($newInfo.version)-x64.msi"
$failMsi = Join-Path $New "test-only\Mesura-$($newInfo.version)-x64-FAILTEST.msi"
if (-not (Test-Path $failMsi)) { $failMsi = Join-Path $New "Mesura-$($newInfo.version)-x64-FAILTEST.msi" }   # builds before rc2 kept it beside the installers
$newRu = Join-Path $New "Mesura-$($newInfo.version)-x64-ru.msi"
if (-not $Work) { $Work = Join-Path ([IO.Path]::GetTempPath()) ('mesura-inst-' + [Guid]::NewGuid().ToString('N').Substring(0, 6)) }
$cyrillic = 'Программы'
$install = Join-Path $Work "$cyrillic Mesura"
$lnk = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Mesura.lnk'
$results = New-Object System.Collections.Generic.List[string]; $failed = $false; $script:n = 0
function Check([string]$what, [bool]$ok, [string]$detail = '') { $script:failed = $script:failed -or -not $ok; $line = '{0,-5} {1} {2}' -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $what, $detail; $results.Add($line); Write-Host $line -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' }) }
function Skip([string]$what, [string]$why) { $line = "SKIP  $what ($why)"; $results.Add($line); Write-Host $line -ForegroundColor Yellow }
function Msi([string]$pkg, [string]$verb, [string]$extra = '') {
  $script:n++
  $a = '/' + $verb + ' "' + $pkg + '" /qn ' + $extra + ' /l*v "' + $Work + '\msi-' + $script:n + '-' + $verb + '.log"'
  (Start-Process msiexec -ArgumentList $a -Wait -PassThru).ExitCode
}
function Reg { try { Get-ItemProperty 'HKCU:\Software\Mesura' -ErrorAction Stop } catch { $null } }
function Hash([string]$f) { if (Test-Path -LiteralPath $f) { (Get-FileHash -LiteralPath $f -Algorithm SHA256).Hash } else { '' } }
function Files { if (Test-Path -LiteralPath $install) { @(Get-ChildItem -LiteralPath $install -Recurse -File).Count } else { 0 } }
# Products named Mesura registered for this user (product codes), read from the registry.
function Products {
  $sid = ([Security.Principal.WindowsIdentity]::GetCurrent()).User.Value
  $list = @()
  foreach ($root in "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\$sid\Products") {
    foreach ($k in Get-ChildItem $root -ErrorAction SilentlyContinue) {
      $ip = Get-ItemProperty "$($k.PSPath)\InstallProperties" -ErrorAction SilentlyContinue
      if ($ip.DisplayName -eq 'Mesura') { $list += $k.PSChildName }
    }
  }
  $list
}
# Windows Installer's list of which products use the components whose key path is Mesura's registration. After an uninstall there must be none: a left-over
# client (a product that is no longer registered) makes a later uninstall keep the files and the registration.
function Clients {
  $sid = ([Security.Principal.WindowsIdentity]::GetCurrent()).User.Value
  $base = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\$sid\Components"
  $set = @{}
  foreach ($k in Get-ChildItem $base -ErrorAction SilentlyContinue) {
    $p = Get-ItemProperty $k.PSPath
    foreach ($name in $k.GetValueNames()) { if ("$($p.$name)" -match 'Software\\Mesura') { $set[$name] = $true } }
  }
  @($set.Keys)
}
function Wipe { if (Test-Path -LiteralPath $install) { Remove-Item -LiteralPath $install -Recurse -Force -ErrorAction SilentlyContinue } }
function Clean-Slate([string]$why) {
  # Whatever a scenario left is removed by the supported route (uninstall by each package); the check below fails loudly if it still isn't clean.
  foreach ($pkg in @($newMsi, $newRu, $oldMsi, $failMsi) | Where-Object { Test-Path $_ }) { if ((Products).Count -gt 0 -or (Reg)) { Msi $pkg 'x' | Out-Null } }
  Wipe
  if ((Reg) -or (Products).Count -gt 0 -or (Clients).Count -gt 0) { throw "Not clean before '$why': registry=$([bool](Reg)) products=$((Products) -join ',') clients=$((Clients) -join ',')" }
}

New-Item -ItemType Directory -Force $Work | Out-Null
$lnkBackup = Join-Path $Work 'Mesura.lnk.bak'; $hadLnk = Test-Path $lnk; if ($hadLnk) { Copy-Item $lnk $lnkBackup -Force }
if (Get-Process Mesura -ErrorAction SilentlyContinue) { throw 'Mesura is running; close it first.' }
if (Reg) { throw 'Mesura is already registered for this user (HKCU\Software\Mesura); this check will not touch a real installation.' }
if ((Products).Count -gt 0 -or (Clients).Count -gt 0) { throw "Windows Installer still knows Mesura on this PC (products: $((Products) -join ','); component clients: $((Clients) -join ',')). Remove it first." }
$prop = 'INSTALLFOLDER="' + $install + '"'
Write-Host "Old $($oldInfo.version), new $($newInfo.version); work folder $Work"
try {
  Write-Host "`n== fresh install, reinstall, uninstall, install again" -ForegroundColor Cyan
  $code = Msi $newMsi 'i' $prop
  Check '[fresh] the package installs per user into a folder with spaces and Cyrillic' ($code -eq 0 -and (Test-Path (Join-Path $install 'Mesura.exe'))) "exit $code"
  Check '[fresh] the registration (HKCU) names this folder, this version and the installer language (en)' ((Reg).InstallDir.TrimEnd('\') -eq $install -and (Reg).Version -eq $newInfo.version -and (Reg).Language -eq 'en') "$((Reg).Version) $((Reg).Language)"
  Check '[fresh] the command line is installed beside the app' (Test-Path (Join-Path $install 'cli\mesura.exe'))
  Check '[fresh] the installed Mesura.exe is the one in the build, byte for byte' ((Hash (Join-Path $install 'Mesura.exe')) -eq (Hash (Join-Path $New 'Mesura\Mesura.exe')))
  Check '[fresh] exactly one product is registered' ((Products).Count -eq 1)
  $filesAfterInstall = Files
  $code = Msi $newMsi 'i' $prop
  Check '[reinstall] installing the same package again succeeds and keeps one product with the same files' ($code -eq 0 -and (Products).Count -eq 1 -and (Files) -eq $filesAfterInstall) "exit $code"
  $code = Msi $newMsi 'x'
  Check '[uninstall] succeeds' ($code -eq 0) "exit $code"
  Check '[uninstall] the program folder is gone' (-not (Test-Path -LiteralPath $install))
  Check '[uninstall] the registration is gone' (-not (Reg))
  Check '[uninstall] no product and no component client is left in Windows Installer' ((Products).Count -eq 0 -and (Clients).Count -eq 0) "products=$((Products) -join ',') clients=$((Clients) -join ',')"
  $code = Msi $newMsi 'i' $prop
  Check '[install again] installing after an uninstall works' ($code -eq 0 -and (Test-Path (Join-Path $install 'Mesura.exe')) -and (Reg).Version -eq $newInfo.version)

  if (Test-Path $oldMsi) {
    Write-Host "`n== upgrade over an older build" -ForegroundColor Cyan
    Clean-Slate 'upgrade'
    $code = Msi $oldMsi 'i' $prop
    Check '[upgrade] the old build installs' ($code -eq 0 -and (Reg).Version -eq $oldInfo.version) "exit $code, $((Reg).Version)"
    $oldHash = Hash (Join-Path $install 'Mesura.exe')
    $code = Msi $newMsi 'i' $prop
    Check '[upgrade] the new package installs over it into the same folder' ($code -eq 0 -and (Reg).Version -eq $newInfo.version) "exit $code, $((Reg).Version)"
    Check '[upgrade] the program files are the new build, byte for byte' ((Hash (Join-Path $install 'Mesura.exe')) -eq (Hash (Join-Path $New 'Mesura\Mesura.exe')) -and (Hash (Join-Path $install 'cli\mesura.exe')) -eq (Hash (Join-Path $New 'Mesura\cli\mesura.exe')))
    Check '[upgrade] the old build is not registered next to it' ((Products).Count -eq 1)
    $code = Msi $newMsi 'x'
    Check '[upgrade] uninstalling afterwards removes the folder and the registration' ($code -eq 0 -and -not (Test-Path -LiteralPath $install) -and -not (Reg) -and (Clients).Count -eq 0) "exit $code"

    if (Test-Path $newRu) {
      Write-Host "`n== language of the package over an installed one" -ForegroundColor Cyan
      Clean-Slate 'language'
      Msi $newRu 'i' $prop | Out-Null
      Check '[language] the Russian package registers its language (ru)' ((Reg).Language -eq 'ru')
      $code = Msi $newMsi 'i' $prop
      Check '[language] the English package installs over the Russian one in place: one product, no side-by-side copy' ($code -eq 0 -and (Reg).Version -eq $newInfo.version -and (Products).Count -eq 1) "exit $code, products $((Products).Count)"
      $code = Msi $newRu 'i' $prop
      Check '[language] and the Russian one back over the English one' ($code -eq 0 -and (Products).Count -eq 1 -and (Reg).Language -eq 'ru') "exit $code"
      Msi $newMsi 'x' | Out-Null
    }

    if (Test-Path $failMsi) {
      Write-Host "`n== a failed update" -ForegroundColor Cyan
      Clean-Slate 'failed update'
      Msi $oldMsi 'i' $prop | Out-Null
      $oldHash = Hash (Join-Path $install 'Mesura.exe'); $oldFiles = Files
      $code = Msi $failMsi 'i' $prop
      Check '[failed update] the failing package reports failure' ($code -ne 0) "exit $code"
      Check '[failed update] the previous version is back: registry and files, byte for byte' ((Reg).Version -eq $oldInfo.version -and (Hash (Join-Path $install 'Mesura.exe')) -eq $oldHash -and (Files) -eq $oldFiles) "registry $((Reg).Version), files $(Files)/$oldFiles"
      Check '[failed update] exactly one product is registered (no ghost of the failed package)' ((Products).Count -eq 1) ((Products) -join ',')
      $code = Msi $oldMsi 'x'
      Check '[failed update] the restored version still uninstalls: exit 0' ($code -eq 0) "exit $code"
      Check '[failed update] ...and the program folder is gone' (-not (Test-Path -LiteralPath $install)) "files left: $(Files)"
      Check '[failed update] ...and the registration is gone' (-not (Reg))
      Check '[failed update] ...and nothing is left in Windows Installer' ((Products).Count -eq 0 -and (Clients).Count -eq 0) "products=$((Products) -join ',') clients=$((Clients) -join ',')"
    }
  }
  else { Skip '[upgrade] over an older build' "no old package at $oldMsi" }

  if (Test-Path $failMsi) {
    Write-Host "`n== a failed first install" -ForegroundColor Cyan
    Clean-Slate 'failed first install'
    $code = Msi $failMsi 'i' $prop
    Check '[failed first install] fails and leaves no program folder, no registration, nothing in Windows Installer' ($code -ne 0 -and -not (Test-Path -LiteralPath $install) -and -not (Reg) -and (Products).Count -eq 0 -and (Clients).Count -eq 0) "exit $code"
  }
  else { Skip '[failed update] and [failed first install]' "no FAILTEST package at $failMsi (tools\build-installer.ps1 -FailInstall)" }

  Skip '[cancel] cancelling the wizard / mid-install' 'needs the interactive wizard; a cancel mid-install is a rollback like the failed scenarios above (exit 1602); to be clicked through by a tester (docs/testing-0.3.md)'
}
finally {
  Get-Process Mesura -ErrorAction SilentlyContinue | Stop-Process -Force
  foreach ($pkg in @($newMsi, $newRu, $oldMsi, $failMsi) | Where-Object { Test-Path $_ }) { if ((Products).Count -gt 0 -or (Reg)) { Msi $pkg 'x' | Out-Null } }
  if ($hadLnk) { Copy-Item $lnkBackup $lnk -Force } elseif (Test-Path $lnk) { Remove-Item $lnk -Force }
  Write-Host "`nStart menu shortcut restored: $(if ($hadLnk) { (New-Object -ComObject WScript.Shell).CreateShortcut($lnk).TargetPath } else { 'none before, none now' })"
  $results.Add("      cleanup: registry=$([bool](Reg)) products=$((Products).Count) clients=$((Clients).Count)")
  if ($Report) { $results | Set-Content -Encoding UTF8 $Report }
}
Write-Host "`n$(@($results | Where-Object { $_ -like 'PASS*' }).Count) passed, $(@($results | Where-Object { $_ -like 'FAIL*' }).Count) FAILED, $(@($results | Where-Object { $_ -like 'SKIP*' }).Count) skipped. Work folder: $Work"
if ($failed) { exit 1 }
