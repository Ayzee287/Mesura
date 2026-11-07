# Does Mesura read the same installed apps when it runs as administrator as when it does not? READ ONLY: it runs `mesura apps list` twice (the second time through a UAC prompt) and compares.
# It installs, removes and changes nothing. The UAC prompt appears once, on your desktop; decline it and the script says so.
#
#   powershell -ExecutionPolicy Bypass -File tools\tester\Check-ElevatedAppDetection.ps1 [-Cli <mesura.exe>] [-Out <folder>]
#   powershell -ExecutionPolicy Bypass -File tools\tester\Check-ElevatedAppDetection.ps1 -SelfTest      (no Mesura, no prompt: tries the script's own comparison on made-up lists)
#
# What a pass means: every app has the same state in both runs, and none reads "unknown" (can't check) in the elevated run unless it does in the plain one. The per-user apps (Discord, Spotify,
# Vencord) are the ones that matter: before 0.7 their check went through a launch as the signed-in user that could fail only when elevated.
param(
  [string]$Cli = '',
  [string]$Out = '',
  [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'

# ---- the comparison (pure: lists in, verdict out), so that it can be tried without Mesura and without a prompt ------------------------------------------------------------
function States($lines) { $m = @{}; foreach ($l in $lines) { if ($l -match '^\s*\[\s*([^\]]*?)\s*\]\s+(\S+)\s') { $m[$Matches[2]] = $Matches[1] } }; $m }
function Compare-AppStates($plainLines, $elevatedLines) {
  $a = States $plainLines; $b = States $elevatedLines
  $problems = @()
  # An empty or unreadable list must never read as a pass: nothing was compared.
  if ($a.Count -eq 0) { $problems += 'the plain run listed no apps (the output was not understood)' }
  if ($b.Count -eq 0) { $problems += 'the elevated run listed no apps (the output was not understood)' }
  if ($a.Count -gt 0 -and $b.Count -gt 0) {
    foreach ($id in $a.Keys) { if (-not $b.ContainsKey($id)) { $problems += "$id : listed in the plain run, missing in the elevated one" } elseif ($a[$id] -ne $b[$id]) { $problems += "$id : plain '$($a[$id])', elevated '$($b[$id])'" } }
    foreach ($id in $b.Keys) { if (-not $a.ContainsKey($id)) { $problems += "$id : listed in the elevated run, missing in the plain one" } }
    foreach ($id in $b.Keys) { if ($b[$id] -eq 'unknown' -and $a[$id] -ne 'unknown') { $problems += "$id : reads 'unknown' (can't check) only when elevated" } }
  }
  [pscustomobject]@{ Plain = $a.Count; Elevated = $b.Count; Problems = @($problems | Select-Object -Unique) }
}

if ($SelfTest) {
  $same = @('  [installed] discord  Discord', '  [-        ] vencord  Vencord', '  [installed] steam    Steam')
  $cases = @(
    @{ Name = 'the same list passes'; Plain = $same; Elevated = $same; Pass = $true },
    @{ Name = 'a different state fails'; Plain = $same; Elevated = @('  [-        ] discord  Discord', '  [-        ] vencord  Vencord', '  [installed] steam    Steam'); Pass = $false },
    @{ Name = 'unknown only when elevated fails'; Plain = $same; Elevated = @('  [unknown  ] discord  Discord', '  [-        ] vencord  Vencord', '  [installed] steam    Steam'); Pass = $false },
    @{ Name = 'unknown in both is not a difference'; Plain = @('  [unknown  ] discord  Discord'); Elevated = @('  [unknown  ] discord  Discord'); Pass = $true },
    @{ Name = 'an empty plain list fails (nothing was compared)'; Plain = @(); Elevated = $same; Pass = $false },
    @{ Name = 'an empty elevated list fails'; Plain = $same; Elevated = @('error: something'); Pass = $false },
    @{ Name = 'an app missing in one run fails'; Plain = $same; Elevated = $same[0..1]; Pass = $false }
  )
  $bad = 0
  foreach ($c in $cases) {
    $r = Compare-AppStates $c.Plain $c.Elevated
    $ok = (($r.Problems.Count -eq 0) -eq $c.Pass)
    Write-Host ('{0,-5} {1}' -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $c.Name)
    if (-not $ok) { $bad++ }
  }
  if ($bad -gt 0) { Write-Host "$bad self-test case(s) failed." -ForegroundColor Red; exit 1 }
  Write-Host 'Self-test: every case came out as it should.' -ForegroundColor Green
  exit 0
}
if (-not $Cli) { foreach ($c in @((Join-Path $PSScriptRoot 'mesura.exe'), (Join-Path $PSScriptRoot 'cli\mesura.exe'), (Join-Path $env:LOCALAPPDATA 'Programs\Mesura\cli\mesura.exe'), (Join-Path $env:ProgramFiles 'Mesura\cli\mesura.exe'))) { if (Test-Path $c) { $Cli = $c; break } } }
if (-not $Cli -or -not (Test-Path $Cli)) { throw 'mesura.exe was not found: pass -Cli <path>.' }
if (-not $Out) { $Out = Join-Path ([Environment]::GetFolderPath('Desktop')) 'mesura-elevated-detection' }
New-Item -ItemType Directory -Force $Out | Out-Null
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($isAdmin) { throw 'Start this script from a normal (not elevated) terminal: it asks for the elevation itself, once.' }

$plain = Join-Path $Out 'apps-plain.txt'; $elevated = Join-Path $Out 'apps-elevated.txt'
& $Cli apps list 2>&1 | Out-File $plain -Encoding utf8
Write-Host 'Plain run saved. Now the elevated run: approve the UAC prompt.' -ForegroundColor Cyan
$inner = "& '$Cli' apps list 2>&1 | Out-File '$elevated' -Encoding utf8"
try {
  Start-Process powershell -Verb RunAs -Wait -WindowStyle Hidden -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', $inner
} catch { Write-Host "The elevated run did not start ($($_.Exception.Message)). Nothing else was done." -ForegroundColor Yellow; exit 2 }
if (-not (Test-Path $elevated)) { Write-Host 'No elevated result was written.' -ForegroundColor Yellow; exit 2 }

$r = Compare-AppStates (Get-Content $plain) (Get-Content $elevated)
Write-Host ("Apps read: {0} plain, {1} elevated." -f $r.Plain, $r.Elevated)
if ($r.Problems.Count -eq 0) { Write-Host 'PASS: the same state for every app in both runs.' -ForegroundColor Green; $code = 0 }
else {
  Write-Host 'FAIL: the runs differ (a winget answer can also be slow once: run the script a second time before reporting it):' -ForegroundColor Red
  $r.Problems | ForEach-Object { Write-Host "  $_" }; Write-Host "Send the folder $Out"; $code = 1
}
"Check of $(Get-Date -Format s): plain $($r.Plain), elevated $($r.Elevated), problems $($r.Problems.Count)" | Out-File (Join-Path $Out 'result.txt') -Encoding utf8
exit $code
