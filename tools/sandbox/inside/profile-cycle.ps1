# Inside Windows Sandbox: the owner's supervised rollback cycle (tools\tester\Test-ProfileCycle.ps1) on a real Windows, standing in for the owner's PC after the first apply:
#   Maximum performance is applied, then the script previews the undo, and with -Apply -Confirmation YES undoes it, proves the undo and the negative control, applies the same operations again and
#   proves that too. Every line must be PASS. The sandbox is thrown away afterwards.
. 'C:\Host\tools\sandbox\inside\common.ps1'
Say "== profile-cycle $(Get-Date -Format s) on $env:COMPUTERNAME as $env:USERNAME (admin: $(Is-Admin))"
$cli = Join-Path 'C:\Host\packages' $packages.cli
$ErrorActionPreference = 'Continue'   # a native command's stderr (the catalog line) must not stop the scenario
$script = 'C:\Host\tools\tester\Test-ProfileCycle.ps1'
New-Item -ItemType Directory -Force 'C:\Host\out\cycle' | Out-Null
$apply = (& $cli windows apply --profile maximum-performance --apply 2>&1 | ForEach-Object { "$_" }) -join "`n"
Info 'Maximum performance applied (the owner''s starting state)' $(if ($apply -match 'history run ([0-9A-Za-z-]+)') { $Matches[1] } else { 'no run id' })

$preview = (& powershell -NoProfile -ExecutionPolicy Bypass -File $script -Cli $cli -Out 'C:\Host\out\cycle' 2>&1 | ForEach-Object { "$_" })
$preview | Out-File 'C:\Host\out\cycle\1-preview.txt' -Encoding utf8
Check '[preview] the preview says nothing was changed' (@($preview | Where-Object { $_ -match 'Preview only: nothing was changed' }).Count -gt 0)

$cancel = (& powershell -NoProfile -ExecutionPolicy Bypass -File $script -Cli $cli -Out 'C:\Host\out\cycle' -Apply -Confirmation 'no' 2>&1 | ForEach-Object { "$_" })
Check '[cancel] anything but YES cancels and changes nothing' (@($cancel | Where-Object { $_ -match 'Cancelled: nothing was changed' }).Count -gt 0)

$cycle = (& powershell -NoProfile -ExecutionPolicy Bypass -File $script -Cli $cli -Out 'C:\Host\out\cycle' -Apply -Confirmation 'YES' 2>&1 | ForEach-Object { "$_" })
$code = $LASTEXITCODE
$cycle | Out-File 'C:\Host\out\cycle\2-cycle.txt' -Encoding utf8
foreach ($l in @($cycle | Where-Object { $_ -match '^(FAIL)' })) { Say "      $l" }
Check '[cycle] every judged step passed (exit code 0)' ($code -eq 0) "exit $code"
foreach ($step in '[1 start]', '[2 undone] the revert run', '[2 undone] the original run', '[2 undone] the plan', '[3 applied again] the new run', '[3 applied again] nothing') {
  Check "[cycle] the line '$step ...' is a PASS" (@($cycle | Where-Object { $_.StartsWith('PASS') -and $_.Contains($step) }).Count -gt 0)
}
Say "== profile-cycle done $(Get-Date -Format s)"
