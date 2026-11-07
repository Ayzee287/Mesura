# Inside Windows Sandbox: the bonus cursor pack (mesura bonus cursors) against a real registry, a real user32 and a real disk.
#   1. status before: NotApplied; the previews (no --apply) change nothing
#   2. apply: 17 files in Mesura's folder with their real headers, every role pointing at its file as REG_EXPAND_SZ, the scheme entry, Windows told to reload
#   3. apply again: nothing changes
#   4. one role changed by hand: status says Changed; restore puts back every other role and leaves the changed one alone
#   5. apply and restore once more: the registry equals the snapshot taken before, value by value, and the files, the saved cursors and the scheme entry are gone
# The sandbox is thrown away afterwards; the cursors of the owner's PC are never touched.
. 'C:\Host\tools\sandbox\inside\common.ps1'
Say "== bonus-cursors $(Get-Date -Format s) on $env:COMPUTERNAME as $env:USERNAME (admin: $(Is-Admin))"
$cli = Join-Path 'C:\Host\packages' $packages.cli
$key = 'HKCU:\Control Panel\Cursors'
$schemes = 'HKCU:\Control Panel\Cursors\Schemes'
$schemeName = 'W11 Cursor Light Free v2.2 by Jepri Creations'
$roles = 'Arrow', 'Help', 'AppStarting', 'Wait', 'Crosshair', 'IBeam', 'NWPen', 'No', 'SizeNS', 'SizeWE', 'SizeNWSE', 'SizeNESW', 'SizeAll', 'UpArrow', 'Hand', 'Person', 'Pin'
$folder = Join-Path $env:LOCALAPPDATA 'Mesura\Setup\bonus\cursors\w11-light-2.2'
$saved = Join-Path $env:LOCALAPPDATA 'Mesura\Setup\bonus\cursors\saved.json'

function Bonus([string[]]$more) {
  $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
  try { $text = (& $cli @(@('bonus', 'cursors') + $more) 2>&1 | ForEach-Object { "$_" }) -join "`n"; [pscustomobject]@{ Text = $text; Exit = $LASTEXITCODE } } finally { $ErrorActionPreference = $old }
}
# Every value of the Cursors key as name -> (kind, raw text). "(Default)" is the empty name.
function Snapshot([string]$path) {
  $map = [ordered]@{}
  $k = Get-Item -LiteralPath $path -ErrorAction SilentlyContinue
  if ($k) { foreach ($n in $k.GetValueNames()) { $map[$n] = [pscustomobject]@{ Kind = "$($k.GetValueKind($n))"; Value = "$($k.GetValue($n, $null, 'DoNotExpandEnvironmentNames'))" } } }
  $map
}
function Same($a, $b, [string[]]$except = @()) {
  $diff = @()
  foreach ($n in (@($a.Keys) + @($b.Keys) | Select-Object -Unique)) {
    if ($except -contains $n) { continue }
    if (-not $a.Contains($n) -or -not $b.Contains($n)) { $diff += "'$n' only on one side"; continue }
    if ($a[$n].Value -ne $b[$n].Value) { $diff += "'$n': '$($a[$n].Value)' vs '$($b[$n].Value)'" }
    elseif ($a[$n].Kind -ne $b[$n].Kind) { Info "'$n' kind differs ($($a[$n].Kind) vs $($b[$n].Kind)); same text '$($a[$n].Value)'" }
  }
  $diff
}

$before = Snapshot $key
$schemesBefore = Snapshot $schemes
Info "the Cursors key before: $($before.Count) values; the Schemes key: $($schemesBefore.Count) values"

# 1. before
$s = Bonus @('status')
Check '[1] status before says NotApplied' ($s.Text -match 'NotApplied' -and $s.Exit -eq 0) $s.Text
$p = Bonus @('apply')
Check '[1] the apply preview says what it would do and changes nothing' ($p.Text -match 'Preview only' -and $p.Exit -eq 0 -and -not (Test-Path $folder) -and (Same $before (Snapshot $key)).Count -eq 0) $p.Text
$p2 = Bonus @('restore')
Check '[1] the restore preview says there is nothing to put back' ($p2.Text -match 'nothing' -and $p2.Exit -eq 0)

# 2. apply
$a = Bonus @('apply', '--apply')
Check '[2] apply exits 0' ($a.Exit -eq 0) $a.Text
Check '[2] Windows was asked to reload the cursors (no "next sign-in" note)' ($a.Text -notmatch 'next sign-in')
$files = @(Get-ChildItem $folder -File -ErrorAction SilentlyContinue)
Check '[2] 17 files are in Mesura''s folder' ($files.Count -eq 17) "$($files.Count) files"
$bad = @()
foreach ($f in $files) {
  $b = [IO.File]::ReadAllBytes($f.FullName)
  $ok = if ($f.Extension -eq '.cur') { $b.Length -gt 22 -and $b[0] -eq 0 -and $b[1] -eq 0 -and $b[2] -eq 2 -and $b[3] -eq 0 } else { $b.Length -gt 12 -and [Text.Encoding]::ASCII.GetString($b, 0, 4) -eq 'RIFF' -and [Text.Encoding]::ASCII.GetString($b, 8, 4) -eq 'ACON' }
  if (-not $ok) { $bad += $f.Name }
}
Check '[2] every file has the header of a real cursor (.cur: type 2; .ani: RIFF/ACON)' ($bad.Count -eq 0) ($bad -join ', ')
$after = Snapshot $key
$wrong = @()
foreach ($r in $roles) {
  if (-not $after.Contains($r)) { $wrong += "$r missing"; continue }
  if ($after[$r].Kind -ne 'ExpandString') { $wrong += "$r is $($after[$r].Kind)" }
  if (-not (Test-Path -LiteralPath $after[$r].Value)) { $wrong += "$r points at a file that is not there: $($after[$r].Value)" }
  if ($after[$r].Value -notlike "$folder\*") { $wrong += "$r points outside Mesura's folder: $($after[$r].Value)" }
}
Check '[2] all 17 roles are REG_EXPAND_SZ values pointing at a file in Mesura''s folder' ($wrong.Count -eq 0) ($wrong -join '; ')
Check '[2] the default value names the scheme' ($after[''].Value -eq $schemeName) "'$($after[''].Value)'"
$sv = (Get-ItemProperty -LiteralPath $schemes -Name $schemeName -ErrorAction SilentlyContinue).$schemeName
Check '[2] the scheme is listed in Mouse properties with 17 paths' ($sv -and $sv.Split(',').Count -eq 17) "$($sv.Split(',').Count)"
Check '[2] the cursors from before are saved' (Test-Path $saved)
$s2 = Bonus @('status')
Check '[2] status says Applied' ($s2.Text -match 'Applied' -and $s2.Text -notmatch 'NotApplied')
# user32 itself: after the reload the arrow cursor of this session is not the one of the system scheme any more (it was loaded from the pack's file)
Add-Type -Namespace M -Name U -MemberDefinition '[DllImport("user32.dll")] public static extern IntPtr LoadCursor(IntPtr h, IntPtr id);'
$arrow = [M.U]::LoadCursor([IntPtr]::Zero, [IntPtr]32512)
Info 'user32 returns an arrow cursor handle after the reload' ("0x{0:X}" -f [int64]$arrow)

# 3. again
$again = Bonus @('apply', '--apply')
Check '[3] applying again says it is already applied and changes no value' ($again.Exit -eq 0 -and $again.Text -match 'already applied' -and (Same $after (Snapshot $key)).Count -eq 0) $again.Text

# 4. one role changed by hand, then restore
$handBefore = $after['Hand'].Value
Set-ItemProperty -LiteralPath $key -Name 'Hand' -Value 'C:\Windows\Cursors\aero_link.cur'
$s3 = Bonus @('status')
Check '[4] status says Changed after a role was changed by hand' ($s3.Text -match 'Changed') $s3.Text
$r = Bonus @('restore', '--apply')
Check '[4] restore exits 0' ($r.Exit -eq 0) $r.Text
$mid = Snapshot $key
Check '[4] the role changed by hand was left alone' ($mid['Hand'].Value -eq 'C:\Windows\Cursors\aero_link.cur') "'$($mid['Hand'].Value)'"
$d = Same $before $mid @('Hand')
Check '[4] every other value is what it was before' ($d.Count -eq 0) ($d -join '; ')
Check '[4] the pack''s files, the saved cursors and the scheme entry are gone' (-not (Test-Path $folder) -and -not (Test-Path $saved) -and -not (Get-ItemProperty -LiteralPath $schemes -Name $schemeName -ErrorAction SilentlyContinue))

# 5. a clean round trip
# put the hand back first so the round trip starts from the snapshot
if ($before.Contains('Hand')) { Set-ItemProperty -LiteralPath $key -Name 'Hand' -Value $before['Hand'].Value } else { Remove-ItemProperty -LiteralPath $key -Name 'Hand' -ErrorAction SilentlyContinue }
[void](Bonus @('apply', '--apply'))
$r2 = Bonus @('restore', '--apply')
Check '[5] a second restore exits 0' ($r2.Exit -eq 0) $r2.Text
$end = Snapshot $key
$d2 = Same $before $end
Check '[5] the Cursors key equals the snapshot from before, value by value' ($d2.Count -eq 0) ($d2 -join '; ')
$schemesEnd = Snapshot $schemes
Check '[5] the Schemes key equals what it was' ((Same $schemesBefore $schemesEnd).Count -eq 0)
Check '[5] no file or saved state is left' (-not (Test-Path $folder) -and -not (Test-Path $saved))
$s4 = Bonus @('status')
Check '[5] status says NotApplied again' ($s4.Text -match 'NotApplied')
$none = Bonus @('restore', '--apply')
Check '[5] restoring with nothing saved says so and changes nothing' ($none.Text -match 'nothing' -and (Same $before (Snapshot $key)).Count -eq 0) $none.Text
Say "== done $(Get-Date -Format s)"
