# Real, elevated check of the two Mesura power plans on a TEST PC or a virtual machine (0.5): apply, read back with powercfg (never with Mesura's own
# reading), apply the other plan, undo, and see that the original plan is active again and both copies are gone.
#
# It changes the active power plan and creates and deletes two power schemes. It puts everything back, also when a step fails, and refuses to run if a
# scheme of the same GUID already exists (a leftover of an earlier run: delete it with powercfg /delete first). Never run it on a PC you are using
# without being ready to repair its power plan by hand (powercfg /setactive 381b4222-f694-41f0-9685-ff5bb260df2e puts Balanced back).
#
#   Run from an ELEVATED terminal:
#   powershell -ExecutionPolicy Bypass -File tools\e2e-power-plans.ps1 -Cli <mesura.exe> [-TryImport] [-Report <file>]
#
# -TryImport also exports Balanced to a .pow file and imports it again (the way the community plans are installed), to show whether import works on this
# PC. The owner once saw a power plan fail to import; Mesura copies a scheme with powercfg /duplicatescheme instead, and this is the comparison.
#
# Written before it could be run (no virtual machine was available): read it, then treat its first run as part of the test.
param(
  [Parameter(Mandatory = $true)][string]$Cli,
  [switch]$TryImport,
  [string]$Report = ''
)
$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this from an elevated terminal.' }
$Cli = (Resolve-Path $Cli).Path
$results = New-Object System.Collections.Generic.List[string]
function Say([string]$l) { $results.Add($l); Write-Host $l; if ($Report) { $l | Out-File $Report -Append -Encoding utf8 } }
function Check([string]$what, [bool]$ok, [string]$detail = '') { Say ('{0,-5} {1}{2}' -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $what, $(if ($detail) { "  ($detail)" } else { '' })) }
if ($Report) { Remove-Item $Report -ErrorAction SilentlyContinue }

$balanced = '381b4222-f694-41f0-9685-ff5bb260df2e'
$plans = @(
  @{ Id = 'power.mesura-balanced-plan'; Guid = 'b71ddbcf-9d21-46ea-9def-6f2a2378e65a'; Name = 'Balanced Mesura'
     # subgroup, setting, the value the plan wants on mains power; the battery side must stay what Balanced has
     Values = @(
       @('54533251-82be-4824-96c1-47b60b740d00', '45bcc044-d885-43e2-8605-ee0ec6e96b59', 100),
       @('2a737441-1930-4402-8d77-b2bebba308a3', '48e6b7a6-50f5-4782-a5d4-53bb8f07e226', 0),
       @('2a737441-1930-4402-8d77-b2bebba308a3', 'd4e98f31-5ffe-4ce1-be31-1b38b384c009', 1),
       @('0012ee47-9041-4b5d-9b77-535fba8b1442', '6738e2c4-e8a5-4a42-b16a-e040e769756e', 1200)) },
  @{ Id = 'power.mesura-maximum-plan'; Guid = '76d36892-93d0-4c33-9e09-c2e1fb87cf49'; Name = 'Maximum Performance Mesura'
     Values = @(
       @('54533251-82be-4824-96c1-47b60b740d00', '893dee8e-2bef-41e0-89c6-b55d0929964c', 100),
       @('54533251-82be-4824-96c1-47b60b740d00', '36687f9e-e3a5-4dbf-b1dc-15eb381c6863', 0),
       @('54533251-82be-4824-96c1-47b60b740d00', '45bcc044-d885-43e2-8605-ee0ec6e96b59', 100),
       @('501a4d13-42af-4429-9fd1-a8218c268e20', 'ee12f906-d277-404b-b6da-e5fa1a576df5', 0),
       @('2a737441-1930-4402-8d77-b2bebba308a3', '48e6b7a6-50f5-4782-a5d4-53bb8f07e226', 0),
       @('0012ee47-9041-4b5d-9b77-535fba8b1442', '6738e2c4-e8a5-4a42-b16a-e040e769756e', 0)) }
)

function Active { if ((powercfg /getactivescheme) -match '([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})') { $Matches[1] } else { '' } }
function Schemes { @(powercfg /list | ForEach-Object { if ($_ -match 'GUID: ([0-9a-f-]{36})') { $Matches[1] } }) }
function Value([string]$scheme, [string]$sub, [string]$setting, [string]$side) {
  # powercfg /query prints "Current AC Power Setting Index: 0x...." and the DC line for the setting
  $text = (powercfg /query $scheme $sub $setting) -join "`n"
  if ($text -match "Current $side Power Setting Index: 0x([0-9a-fA-F]+)") { [Convert]::ToUInt32($Matches[1], 16) } else { $null }
}
function Mesura([string[]]$arguments) {
  $ErrorActionPreference = 'Continue'
  $text = (& $Cli @arguments 2>&1 | ForEach-Object { "$_" }) -join "`n"
  [pscustomobject]@{ Text = $text; Run = $(if ($text -match 'history run ([0-9A-Za-z-]+)') { $Matches[1] } else { $null }); Exit = $LASTEXITCODE }
}

$original = Active
$runs = New-Object System.Collections.Generic.List[string]
try {
  Say "== Mesura CLI $Cli; active plan before: $original"
  foreach ($p in $plans) { if ((Schemes) -contains $p.Guid) { throw "A scheme $($p.Guid) ($($p.Name)) already exists: remove it with powercfg /delete $($p.Guid) first." } }
  $before = @{}
  foreach ($p in $plans) { foreach ($v in $p.Values) { $before["$($p.Guid)|$($v[1])|DC"] = Value $balanced $v[0] $v[1] 'DC'; $before["$($p.Guid)|$($v[1])|AC"] = Value $balanced $v[0] $v[1] 'AC' } }

  foreach ($p in $plans) {
    Say "-- $($p.Name)"
    $preview = Mesura @('windows', 'apply', '--id', $p.Id)
    Check 'the preview names the plan and changes nothing' ($preview.Text -match [regex]::Escape($p.Id) -and (Active) -eq $original -and -not ((Schemes) -contains $p.Guid)) "exit $($preview.Exit)"
    $apply = Mesura @('windows', 'apply', '--id', $p.Id, '--apply')
    Check 'the apply finishes and records a run' ($apply.Exit -eq 0 -and $apply.Run) "exit $($apply.Exit), run $($apply.Run)"
    if ($apply.Run) { $runs.Add($apply.Run) }
    Check 'the scheme exists, copied from Balanced, under its own name' (((Schemes) -contains $p.Guid) -and ((powercfg /query $p.Guid | Select-Object -First 3) -join ' ') -match [regex]::Escape($p.Name))
    Check 'it is the active plan' ((Active) -eq $p.Guid) (Active)
    foreach ($v in $p.Values) {
      $ac = Value $p.Guid $v[0] $v[1] 'AC'
      $base = $before["$($p.Guid)|$($v[1])|AC"]
      if ([string]::IsNullOrEmpty($base) -and [string]::IsNullOrEmpty($ac)) { Say "INFO  mains value $($v[1].Substring(0, 8)) is not registered on this PC (Balanced has none either): not written, as designed" }
      else { Check "mains value $($v[1].Substring(0, 8)) is $($v[2])" ($ac -eq $v[2]) "read $ac" }
      $dc = Value $p.Guid $v[0] $v[1] 'DC'
      Check "battery value $($v[1].Substring(0, 8)) is Balanced's" ($dc -eq $before["$($p.Guid)|$($v[1])|DC"]) "read $dc, Balanced $($before["$($p.Guid)|$($v[1])|DC"])"
    }
    $edited = @($p.Values | Where-Object { (Value $balanced $_[0] $_[1] 'AC') -ne $before["$($p.Guid)|$($_[1])|AC"] })
    Check 'Windows'' own Balanced plan was not edited' ($edited.Count -eq 0) "$($edited.Count) value(s) differ"
    $again = Mesura @('windows', 'apply', '--id', $p.Id)
    Check 'a second preview says there is nothing to do' ($again.Text -match 'already done|applied' -and $again.Text -notmatch 'change\s+Apply')
    # undo this run before the next plan, so each is tested from the same start
    if ($apply.Run) {
      $revert = Mesura @('windows', 'revert', '--run', $apply.Run, '--apply')
      Check 'the undo finishes' ($revert.Exit -eq 0) "exit $($revert.Exit)"
      Check 'the previous plan is active again' ((Active) -eq $original) (Active)
      Check 'the copy is deleted' (-not ((Schemes) -contains $p.Guid))
      $runs.Remove($apply.Run) | Out-Null
    }
  }

  if ($TryImport) {
    Say '-- powercfg export and import of Balanced (the community plans are installed this way)'
    $file = Join-Path $env:TEMP 'mesura-e2e-balanced.pow'
    Remove-Item $file -ErrorAction SilentlyContinue
    powercfg /export $file $balanced | Out-Null
    Check 'powercfg /export writes a file' ((Test-Path $file) -and (Get-Item $file).Length -gt 0) "exit $LASTEXITCODE"
    $listBefore = Schemes
    $out = (powercfg /import $file) -join ' '
    $code = $LASTEXITCODE
    $listAfter = Schemes
    $new = @($listAfter | Where-Object { $listBefore -notcontains $_ })
    Check 'powercfg /import creates a scheme' ($code -eq 0 -and $new.Count -eq 1) "exit $code; $out"
    foreach ($g in $new) { powercfg /delete $g | Out-Null }
    Check 'the imported scheme is deleted again' (((Schemes) | Where-Object { $new -contains $_ }).Count -eq 0)
    Remove-Item $file -ErrorAction SilentlyContinue
  }
}
finally {
  foreach ($r in $runs) { try { $null = Mesura @('windows', 'revert', '--run', $r, '--apply') } catch { Say "could not undo run $r : $_" } }
  if ($original -and (Active) -ne $original) { powercfg /setactive $original | Out-Null; Say "active plan put back: $original" }
  foreach ($p in $plans) { if ((Schemes) -contains $p.Guid) { powercfg /delete $p.Guid | Out-Null; Say "leftover scheme deleted: $($p.Name)" } }
  $failed = @($results | Where-Object { $_ -like 'FAIL*' }).Count
  Say ("{0} lines, {1} failed; active plan now {2}" -f $results.Count, $failed, (Active))
}
