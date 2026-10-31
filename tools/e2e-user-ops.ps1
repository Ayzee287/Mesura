# Real checks of per-user (HKCU) operations, run WITHOUT administrator rights on the signed-in account. For each: the state is read with Windows' own tools, Mesura applies,
# the new state is read again independently (the registry, never Mesura's own reading), the run is reverted, and the third reading must equal the first, including
# "no such value". The covered kinds: a single value, several values, a bit field kept apart from the other bits, and a key created and removed.
#
#   input.accessibility-shortcuts-off    four Flags values; only bit 0x4 is cleared (the other bits stay)
#   audio.no-ducking                     one DWORD
#   gaming.game-mode-on                  one DWORD
#   privacy.no-typing-personalization    two DWORDs
#   explorer.classic-context-menu        a key whose default value is empty (a value Windows reads as "present"), created and removed
#
#   powershell -ExecutionPolicy Bypass -File tools\e2e-user-ops.ps1 -Cli <mesura.exe> [-Report <file>]
#
# Everything it changes it puts back (even when a step fails). Explorer is not restarted by it. Nothing is installed.
param(
  [Parameter(Mandatory = $true)][string]$Cli,
  [string]$Report = ''
)
$ErrorActionPreference = 'Stop'
$results = New-Object System.Collections.Generic.List[string]
function Say([string]$l) { $results.Add($l); Write-Host $l; if ($Report) { $l | Out-File $Report -Append -Encoding utf8 } }
function Check([string]$what, [bool]$ok, [string]$detail = '') { Say ('{0,-5} {1}{2}' -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $what, $(if ($detail) { "  ($detail)" } else { '' })) }
if ($Report) { Remove-Item $Report -ErrorAction SilentlyContinue }

function Mesura([string[]]$arguments) {
  $ErrorActionPreference = 'Continue'
  $text = (& $Cli @arguments 2>&1 | ForEach-Object { "$_" }) -join "`n"
  [pscustomobject]@{ Text = $text; Run = $(if ($text -match 'history run ([0-9A-Za-z-]+)') { $Matches[1] } else { $null }); Exit = $LASTEXITCODE }
}
function State([string]$id) { $t = (Mesura @('windows', 'show', '--id', $id)).Text; if ($t -match 'State here: ([^.]*)\.') { $Matches[1].Trim() } else { '?' } }
function Val([string]$key, [string]$name) {
  $k = Get-Item -LiteralPath "Registry::$key" -ErrorAction SilentlyContinue
  if (-not $k) { return $null }
  if ($k.GetValueNames() -notcontains $name) { return $null }
  $k.GetValue($name, $null, 'DoNotExpandEnvironmentNames')
}
$HK = 'HKEY_CURRENT_USER'

function Run-Op([string]$op, [scriptblock]$read, [scriptblock]$expectAfter, [string]$afterLabel, [scriptblock]$prime, [scriptblock]$restore) {
  $original = & $read
  Say "== ${op}: this PC's value = $($original | ConvertTo-Json -Compress)"
  try {
    # An operation already in place on this PC is first put into a not-applied state by hand, so that there is something to apply (it is put back at the end).
    if ((State $op) -like 'applied*') { & $prime $original; Say '      it was already applied here: set to a not-applied value first' }
    $before = & $read
    Check "[$op] Mesura reads it as not applied" ((State $op) -like '-*' -or (State $op) -like 'not applied*') (State $op)
    $apply = Mesura @('windows', 'apply', '--id', $op, '--apply')
    Check "[$op] apply recorded a run" ([bool]$apply.Run) "$($apply.Run) exit $($apply.Exit)"
    $after = & $read
    Check "[$op] $afterLabel (registry, read independently)" ([bool](& $expectAfter $before $after)) ($after | ConvertTo-Json -Compress)
    Check "[$op] Mesura reads it as applied" ((State $op) -like 'applied*') (State $op)
    $revert = Mesura @('windows', 'revert', '--run', $apply.Run, '--apply')
    $back = & $read
    Check "[$op] revert restored exactly the value from before the apply" (($back | ConvertTo-Json -Compress) -eq ($before | ConvertTo-Json -Compress)) "$($back | ConvertTo-Json -Compress); exit $($revert.Exit)"
    $again = Mesura @('windows', 'revert', '--run', $apply.Run, '--apply')
    Check "[$op] a second revert of the same run changes nothing" ((((& $read) | ConvertTo-Json -Compress)) -eq ($before | ConvertTo-Json -Compress)) "exit $($again.Exit)"
  } finally {
    & $restore $original
    Check "[$op] this PC's own value is back" ((((& $read) | ConvertTo-Json -Compress)) -eq ($original | ConvertTo-Json -Compress))
  }
}
$sticky = "$HK\Control Panel\Accessibility\StickyKeys"; $filter = "$HK\Control Panel\Accessibility\Keyboard Response"; $toggle = "$HK\Control Panel\Accessibility\ToggleKeys"; $mouse = "$HK\Control Panel\Accessibility\MouseKeys"
function SetStr([string]$key, [string]$name, $value) { if ($null -eq $value) { Remove-ItemProperty -LiteralPath "Registry::$key" -Name $name -ErrorAction SilentlyContinue } else { Set-ItemProperty -LiteralPath "Registry::$key" -Name $name -Value $value -Type String } }
Run-Op 'input.accessibility-shortcuts-off' { [ordered]@{ s = (Val $sticky 'Flags'); f = (Val $filter 'Flags'); t = (Val $toggle 'Flags'); m = (Val $mouse 'Flags') } } {
  param($b, $a) ($a.s -ne $null) -and ((([int]$a.s) -band 4) -eq 0) -and ((([int]$a.s) -bor 4) -eq (([int]$b.s) -bor 4)) -and ((([int]$a.f) -band 4) -eq 0) -and ((([int]$a.t) -band 4) -eq 0) -and ((([int]$a.m) -band 4) -eq 0)
} 'the shortcut bit (0x4) is clear in all four and every other bit is unchanged' { param($b) SetStr $sticky 'Flags' ([string](([int]$b.s) -bor 4)); SetStr $filter 'Flags' ([string](([int]$b.f) -bor 4)); SetStr $toggle 'Flags' ([string](([int]$b.t) -bor 4)); SetStr $mouse 'Flags' ([string](([int]$b.m) -bor 4)) } { param($b) SetStr $sticky 'Flags' $b.s; SetStr $filter 'Flags' $b.f; SetStr $toggle 'Flags' $b.t; SetStr $mouse 'Flags' $b.m }

$audio = "$HK\Software\Microsoft\Multimedia\Audio"
function SetDword([string]$key, [string]$name, $value) { if ($null -eq $value) { Remove-ItemProperty -LiteralPath "Registry::$key" -Name $name -ErrorAction SilentlyContinue } else { if (-not (Test-Path -LiteralPath "Registry::$key")) { New-Item -Path "Registry::$key" -Force | Out-Null }; Set-ItemProperty -LiteralPath "Registry::$key" -Name $name -Value ([int]$value) -Type DWord } }
Run-Op 'audio.no-ducking' { [ordered]@{ v = (Val $audio 'UserDuckingPreference') } } { param($b, $a) $a.v -eq 3 } 'UserDuckingPreference is 3' { param($b) SetDword $audio 'UserDuckingPreference' 1 } { param($b) SetDword $audio 'UserDuckingPreference' $b.v }

$bar = "$HK\Software\Microsoft\GameBar"
Run-Op 'gaming.game-mode-on' { [ordered]@{ v = (Val $bar 'AutoGameModeEnabled') } } { param($b, $a) $a.v -eq 1 } 'AutoGameModeEnabled is 1' { param($b) SetDword $bar 'AutoGameModeEnabled' 0 } { param($b) SetDword $bar 'AutoGameModeEnabled' $b.v }

$ip = "$HK\Software\Microsoft\InputPersonalization"
Run-Op 'privacy.no-typing-personalization' { [ordered]@{ t = (Val $ip 'RestrictImplicitTextCollection'); i = (Val $ip 'RestrictImplicitInkCollection') } } { param($b, $a) ($a.t -eq 1) -and ($a.i -eq 1) } 'both Restrict* values are 1' { param($b) SetDword $ip 'RestrictImplicitTextCollection' 0; SetDword $ip 'RestrictImplicitInkCollection' 0 } { param($b) SetDword $ip 'RestrictImplicitTextCollection' $b.t; SetDword $ip 'RestrictImplicitInkCollection' $b.i }

$clsid = "$HK\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}"
Run-Op 'explorer.classic-context-menu' {
  $k = Get-Item -LiteralPath "Registry::$clsid\InprocServer32" -ErrorAction SilentlyContinue
  [ordered]@{ key = [bool]$k; value = $(if ($k) { [string]$k.GetValue('') } else { $null }) }
} { param($b, $a) $a.key -eq $true -and $a.value -eq '' } 'the InprocServer32 key exists with an empty default value' { param($b) Remove-Item -LiteralPath "Registry::$clsid" -Recurse -Force -ErrorAction SilentlyContinue } {
  param($b)
  if (-not $b.key) { Remove-Item -LiteralPath "Registry::$clsid" -Recurse -Force -ErrorAction SilentlyContinue }
  elseif (-not (Test-Path -LiteralPath "Registry::$clsid\InprocServer32")) { New-Item -Path "Registry::$clsid\InprocServer32" -Force | Out-Null; Set-Item -LiteralPath "Registry::$clsid\InprocServer32" -Value '' }
}

$failed = @($results | Where-Object { $_ -like 'FAIL*' }).Count
Say ''; Say ("{0} lines, {1} failed" -f $results.Count, $failed)
if ($failed -gt 0) { exit 1 }
exit 0
