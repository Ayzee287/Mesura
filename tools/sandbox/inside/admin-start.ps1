# Inside Windows Sandbox: Mesura's start-up with administrator rights by default (0.7, D049), on a real install, as far as a sandbox can show it.
# The sandbox's desktop shell is itself elevated (UAC off), so what it can show is the "already elevated" side of the decision and everything around it; the prompt itself
# (accepting, declining) needs a real PC with UAC on and is in docs/admin-start-0.7.md as a manual check.
#
#   1. An all-users install of the older package (a\, English) and of its Russian wizard package: the copy is in Program Files, in a folder only administrators can change.
#   2. Neither the app nor the command line asks Windows for administrator rights through a manifest: a manifest would make a declined prompt end the start altogether, and would
#      make the command line prompt for every script. (The window restarts itself with the "runas" verb when it decides to; the command line never does.)
#   3. Started from the Start menu shortcut, already elevated: one window, one process, the start-up log says "Continue (elevated now: True)", no --relaunched copy, no second process.
#   4. A second launch while it runs hands over to the running instance (still one process); Mesura.exe --exit ends it; --no-elevate starts without any decision either way.
#   5. The same in Russian (the Russian wizard's package installs the same files).
#   6. The command line runs a read-only command to the end without a window, and leaves no process behind.
#   7. Uninstalling leaves the user's data folder (settings the user chose) exactly as it was.
. 'C:\Host\tools\sandbox\inside\common.ps1'
Say "== admin-start $(Get-Date -Format s) on $env:COMPUTERNAME as $env:USERNAME (admin: $(Is-Admin))"
Check 'this process is elevated (so is the sandbox desktop)' (Is-Admin)
Remove-All
$a = Split-Path (Package 'older')
$enMsi = Package 'older'
$ruMsi = Get-ChildItem $a -Filter '*-ru.msi' | Select-Object -First 1 -ExpandProperty FullName
Info 'packages' "$enMsi ; $ruMsi"

function Manifest-Says([string]$exe, [string]$text) {
  $bytes = [IO.File]::ReadAllBytes($exe)
  $ascii = [Text.Encoding]::ASCII.GetString($bytes)
  $utf16 = [Text.Encoding]::Unicode.GetString($bytes)
  ($ascii.Contains($text)) -or ($utf16.Contains($text))
}
function Processes { @(Get-Process Mesura -ErrorAction SilentlyContinue) }
function Stop-Mesura { Get-Process Mesura -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep 2 }
function StartupLog { $f = Join-Path $data 'logs\mesura-startup.log'; if (Test-Path $f) { Get-Content $f -Raw } else { '' } }
function Wait-Window([int]$seconds = 40) {
  $d = (Get-Date).AddSeconds($seconds)
  while ((Get-Date) -lt $d) { $p = Processes | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1; if ($p) { return $p }; Start-Sleep -Milliseconds 500 }
  $null
}

foreach ($run in @(@{ Lang = 'en'; Msi = $enMsi }, @{ Lang = 'ru'; Msi = $ruMsi })) {
  Say "-- $($run.Lang) package: $($run.Msi)"
  Remove-All; Stop-Mesura
  if (Test-Path $data) { Remove-Item -Recurse -Force $data }
  $code = Msi @('/i', "`"$($run.Msi)`"", 'ALLUSERS=1')
  Check "$($run.Lang): all-users install succeeded" ($code -eq 0) "exit $code"
  $exe = Join-Path $pf 'Mesura.exe'; $cli = Join-Path $pf 'cli\mesura.exe'
  Check "$($run.Lang): Mesura.exe and the command line are in Program Files" ((Test-Path $exe) -and (Test-Path $cli))
  $acl = (Get-Acl $pf).Access | Where-Object { $_.IdentityReference -match 'Users|Everyone|Authenticated' -and $_.FileSystemRights -match 'Write|Modify|FullControl' }
  Check "$($run.Lang): the program folder is not writable by ordinary users (so administrator mode is allowed from it)" (-not $acl)
  Check "$($run.Lang): Mesura.exe has no requireAdministrator manifest" (-not (Manifest-Says $exe 'requireAdministrator'))
  Check "$($run.Lang): the command line has no requireAdministrator manifest" (-not (Manifest-Says $cli 'requireAdministrator'))
  Check "$($run.Lang): the command line has no highestAvailable manifest either" (-not (Manifest-Says $cli 'highestAvailable'))
  $lnk = @(Get-ChildItem (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs') -Filter 'Mesura*.lnk' -Recurse -ErrorAction SilentlyContinue)[0]
  Check "$($run.Lang): the Start menu shortcut exists" ($null -ne $lnk)
  if ($lnk) {
    $ws = New-Object -ComObject WScript.Shell; $sc = $ws.CreateShortcut($lnk.FullName)
    Info "$($run.Lang): shortcut" "target $($sc.TargetPath), arguments '$($sc.Arguments)'"
    Check "$($run.Lang): the shortcut starts Mesura.exe with no arguments" ($sc.TargetPath -ieq $exe -and -not $sc.Arguments)
    $bytes = [IO.File]::ReadAllBytes($lnk.FullName)
    $flags = [BitConverter]::ToUInt32($bytes, 0x14)
    Check "$($run.Lang): the shortcut does not carry the run-as-administrator flag (a declined prompt would then end the start)" (($flags -band 0x2000) -eq 0)
  }

  # 3. start from the shortcut
  if ($lnk) { Start-Process $lnk.FullName } else { Start-Process $exe }
  $w = Wait-Window
  Check "$($run.Lang): the window opened from the shortcut" ($null -ne $w)
  Start-Sleep 3
  $log = StartupLog
  Check "$($run.Lang): start-up log: already elevated, so no prompt and no restart" ($log -match 'administrator rights at start: Continue \(elevated now: True\)')
  Check "$($run.Lang): no --relaunched copy and no restart was made" ($log -notmatch 'relaunched' -and (@(Processes).Count -eq 1))
  Info "$($run.Lang): processes" "$(@(Processes).Count) Mesura process(es), pid $((Processes | ForEach-Object Id) -join ',')"

  # 4. a second launch hands over
  Start-Process $exe
  Start-Sleep 6
  Check "$($run.Lang): a second launch leaves one process (hand-over, not a second window)" (@(Processes).Count -eq 1)
  $o = & $exe --exit 2>&1; Start-Sleep 4
  Check "$($run.Lang): Mesura.exe --exit ends the running instance" (@(Processes).Count -eq 0)
  $p = Start-Process $exe -ArgumentList '--no-elevate' -PassThru
  $w = Wait-Window
  Check "$($run.Lang): --no-elevate starts as it is" ($null -ne $w -and @(Processes).Count -eq 1)
  Stop-Mesura

  # 6. the command line: read-only command, to the end, no window, nothing left behind
  $out = RunCli $cli @('windows', 'list')
  Check "$($run.Lang): the command line ran a read-only command to the end" ($out.Length -gt 0) "$($out.Length) characters"
  Check "$($run.Lang): it left no Mesura process and opened no window" (@(Processes).Count -eq 0)
}

# 7. data survives an uninstall
Say '-- user data and uninstall'
New-Item -ItemType Directory -Force (Join-Path $data 'Setup') | Out-Null
Set-Content (Join-Path $data 'Setup\app-settings.json') '{ "Language": "en", "StartupMode": "normal", "ElevateAtStart": false }' -Encoding utf8
$hash = (Get-FileHash (Join-Path $data 'Setup\app-settings.json')).Hash
$p = @(Products)[0]
if ($p) { [void](Msi @('/x', $p.Code)) }
Check 'uninstall removed the program folder' (-not (Test-Path (Join-Path $pf 'Mesura.exe')))
Check 'uninstall left the settings file byte for byte' ((Test-Path (Join-Path $data 'Setup\app-settings.json')) -and ((Get-FileHash (Join-Path $data 'Setup\app-settings.json')).Hash -eq $hash))
Say 'Done.'
