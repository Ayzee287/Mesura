# Starts one test scenario inside Windows Sandbox, a disposable Windows that is thrown away when its window closes.
# It is the only place the installer, the power plans and the network experiments are meant to run: never on the PC you use.
#
#   powershell -ExecutionPolicy Bypass -File tools\sandbox\Start-MesuraSandbox.ps1 -Scenario installer-moves -Packages <folder made by make-test-packages.ps1> [-Out <folder for results>]
#
# Scenarios (tools\sandbox\inside\<name>.ps1):
#   installer-moves   per-user and all-users installs, update, uninstall and the move between them (docs/hardware-test-plan-0.5.md A2, A4, A5)
#   installer-wizard  what the real wizard shows when Mesura is already installed for the other scope (pages read with UI Automation, screenshots, cancelled)
#   nvpi-elevated     an elevated Mesura and NVIDIA Profile Inspector in a per-user winget folder: real access lists, the unelevated shell token, a genuine release verified and staged (needs the "tests" and "nvpi-sample" folders beside the packages)
#   stable-ops        the e2e scripts for real Windows in one go: install lifecycle with a failed update, all-users install/update/uninstall, per-user and machine-wide operations, power plans
#   power-plans       the two Mesura power plans on a real powercfg (A1)
#   dscp              QoS policy creation, removal and a packet capture of marked traffic (docs/dscp-research-0.5.md)
#   nvcleanstall-ui   installs NVCleanstall (artifacts\sandbox\nvcleanstall) and records its window with UI Automation and screenshots
#   nvcleanstall-automate   Mesura's own automation (mesura setup nvcleanstall-ui) drives the real NVCleanstall window up to its Finished page; nothing is installed
#   shell             opens the sandbox with the folders mapped and runs nothing (to look around by hand)
#
# What is shared with the host: this repository's tools folder and the packages folder, both READ ONLY; the results folder, writable. Networking is on (the DSCP scenario and winget
# need it). The sandbox has no GPU: nothing about NVIDIA or AMD can be tested in it, and its network adapter is virtual, so adapter-tuning writes are NOT covered either.
param(
  [Parameter(Mandatory = $true)][ValidateSet('installer-moves', 'installer-wizard', 'nvpi-elevated', 'stable-ops', 'power-plans', 'dscp', 'nvcleanstall-ui', 'nvcleanstall-automate', 'shell')][string]$Scenario,
  [Parameter(Mandatory = $true)][string]$Packages,
  [string]$Out = ''
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$sandboxExe = Join-Path $env:WINDIR 'System32\WindowsSandbox.exe'
if (-not (Test-Path $sandboxExe)) { throw 'Windows Sandbox is not enabled on this PC (the feature Containers-DisposableClientVM, then a restart).' }
if (Get-Process WindowsSandbox, WindowsSandboxClient -ErrorAction SilentlyContinue) { throw 'A Windows Sandbox is already running: close it first (only one can run).' }
$Packages = (Resolve-Path $Packages).Path
if (-not $Out) { $Out = Join-Path $env:TEMP "mesura-sandbox-results-$(Get-Date -Format yyyyMMdd-HHmmss)" }
New-Item -ItemType Directory -Force $Out | Out-Null
$wsb = Join-Path $Out "$Scenario.wsb"
$command = if ($Scenario -eq 'shell') { 'explorer.exe C:\Host\out' } else { "powershell -ExecutionPolicy Bypass -NoExit -File C:\Host\tools\sandbox\inside\$Scenario.ps1" }
$xml = @"
<Configuration>
  <VGpu>Disable</VGpu>
  <Networking>Enable</Networking>
  <MemoryInMB>4096</MemoryInMB>
  <MappedFolders>
    <MappedFolder><HostFolder>$($repo)\tools</HostFolder><SandboxFolder>C:\Host\tools</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$Packages</HostFolder><SandboxFolder>C:\Host\packages</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$Out</HostFolder><SandboxFolder>C:\Host\out</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
  </MappedFolders>
  <LogonCommand><Command>$command</Command></LogonCommand>
</Configuration>
"@
Set-Content $wsb $xml -Encoding UTF8
Write-Output "Starting Windows Sandbox for '$Scenario'. Results will be in $Out (report.txt)."
Start-Process $wsb


