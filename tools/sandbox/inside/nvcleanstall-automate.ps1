# Inside Windows Sandbox: Mesura's own automation (`mesura setup nvcleanstall-ui`, UI Automation by AutomationId) drives the real NVCleanstall 1.19.0 window through the pages it knows:
# the official driver package from disk, the owner's components, the owner's tweaks, up to the "Finished" page. It does not press Install. The sandbox has no GPU, so NVCleanstall is
# fed the driver files by path (the same way the real flow does), and what the sandbox cannot show (the driver actually installing) stays unshown.
. 'C:\Host\tools\sandbox\inside\common.ps1'
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Say "== nvcleanstall-automate $(Get-Date -Format s) on $env:COMPUTERNAME"
$exe = 'C:\Host\packages\nvcleanstall\NVCleanstall_1.19.0.exe'
Check 'the package is the one winget names' ((Get-FileHash $exe -Algorithm SHA256).Hash -eq '9DD36EF956AF927CF41FA441F91B329A7973E13965E4E7D70E6FA9C1DF1CADE6')
$p = Start-Process $exe -ArgumentList '/install', '/VERYSILENT' -PassThru
Check 'NVCleanstall installs silently' ($p.WaitForExit(240000) -and $p.ExitCode -eq 0)
$app = 'C:\Program Files\NVCleanstall\NVCleanstall.exe'
$cli = Join-Path 'C:\Host\packages' $packages.cli
$package = Get-ChildItem 'C:\Host\packages\nvidia' -Filter '*.exe' | Select-Object -First 1
Check 'the CLI of the current source and the driver package are there' ((Test-Path $cli) -and $package) "$cli; $($package.Name)"
Start-Process $app | Out-Null
Start-Sleep 8
$started = Get-Date
$output = RunCli $cli @('setup', 'nvcleanstall-ui', '--package', $package.FullName, '--yes-drive-the-window')
Info 'mesura setup nvcleanstall-ui' ("took $([int]((Get-Date) - $started).TotalSeconds) s`r`n" + $output)
Check 'the automation reached the finished page' ($output -match 'Completed' -and $output -match 'Finished')
Check 'it ticked the owner''s components' ($output -match 'components ticked: Display Driver, HD Audio via HDMI')
Check 'NVCleanstall itself ticked Rebuild digital signature (read, not set)' ($output -match 'Rebuild digital signature: ticked')
Check 'it ticked the owner''s tweaks' ($output -match 'tweaks ticked: ' -and $output -match 'clean-install' -and $output -match 'msi')
try {
  $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
  $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height); [System.Drawing.Graphics]::FromImage($bmp).CopyFromScreen($b.X, $b.Y, 0, 0, $bmp.Size); $bmp.Save('C:\Host\out\after-automation.png')
} catch { Info 'no screenshot' $_.Exception.Message }
Get-Process NVCleanstall -ErrorAction SilentlyContinue | Stop-Process -Force
Say "== done $(Get-Date -Format s)"
