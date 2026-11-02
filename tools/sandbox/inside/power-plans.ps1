# Inside Windows Sandbox: the two Mesura power plans on a real powercfg (tools\e2e-power-plans.ps1, with -TryImport). The CLI is the one built from the current source.
. 'C:\Host\tools\sandbox\inside\common.ps1'
Say "== power-plans $(Get-Date -Format s) on $env:COMPUTERNAME"
$cli = Join-Path 'C:\Host\packages' $packages.cli
Check 'the CLI of the current source is there' (Test-Path $cli) $cli
Info 'power schemes before' ((powercfg /list) -join ' | ')
& 'C:\Host\tools\e2e-power-plans.ps1' -Cli $cli -TryImport -Report 'C:\Host\out\power-plans-e2e.txt'
Info 'power schemes after' ((powercfg /list) -join ' | ')
Say "== done $(Get-Date -Format s) (details in power-plans-e2e.txt)"
