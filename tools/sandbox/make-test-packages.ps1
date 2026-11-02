# Builds the packages the Windows Sandbox scenarios need, from the CURRENT source, into a scratch folder (never into artifacts\current, which holds the published build):
#
#   <Out>\a\Mesura\...      the published app and CLI of this source, labelled "sandbox"
#   <Out>\a\Mesura-<v>-sandbox-x64.msi, ...-x64-ru.msi            the older test package   (version from Directory.Build.props, e.g. 0.4.4)
#   <Out>\b\Mesura-<v2>-sandbox-x64.msi                           a newer test package     (-NewerVersion, default the next patch number)
#   <Out>\packages.json     the file names, for Start-MesuraSandbox.ps1
#
# It changes nothing on this PC apart from build output (and the installer\obj folder, which is not tracked). It installs nothing. The two packages differ in version, so they
# have different product codes: that is what the "move to the other kind of copy" scenarios need.
#
#   powershell -ExecutionPolicy Bypass -File tools\sandbox\make-test-packages.ps1 -Out <scratch folder> [-NewerVersion 0.4.9]
param(
  [Parameter(Mandatory = $true)][string]$Out,
  [string]$NewerVersion = ''
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$Out = [IO.Path]::GetFullPath($Out)
foreach ($kept in 'current', 'previous') { if ($Out.StartsWith((Join-Path $repo "artifacts\$kept"), [StringComparison]::OrdinalIgnoreCase)) { throw "artifacts\$kept holds published builds: use another folder (artifacts\sandbox is fine)." } }
$version = ([xml](Get-Content (Join-Path $repo 'Directory.Build.props'))).Project.PropertyGroup.Version
if (-not $NewerVersion) { $p = $version.Split('.'); $NewerVersion = "$($p[0]).$($p[1]).$([int]$p[2] + 5)" }
$a = Join-Path $Out 'a'; $b = Join-Path $Out 'b'
foreach ($d in $a, $b) { if (Test-Path $d) { Remove-Item -Recurse -Force $d }; New-Item -ItemType Directory -Force $d | Out-Null }

& (Join-Path $repo 'tools\publish.ps1') -Label 'sandbox' -Out $a
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
& (Join-Path $repo 'tools\build-installer.ps1') -Out $a
if ($LASTEXITCODE -ne 0) { throw 'installer build failed' }

# The newer package: the same files under another version, built from a copy of the build folder so the first stays as it was.
Copy-Item -Recurse -Force $a\* $b
$info = Get-Content -Raw (Join-Path $b 'build.json') | ConvertFrom-Json
$info.version = "$NewerVersion-sandbox"
$info | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $b 'build.json') -Encoding UTF8
Get-ChildItem $b -Filter 'Mesura-*.msi' | Remove-Item
& (Join-Path $repo 'tools\build-installer.ps1') -Out $b
if ($LASTEXITCODE -ne 0) { throw 'second installer build failed' }
# The test-only package whose install fails after copying (the rollback scenarios of tools\e2e-installer.ps1), beside the newer one in test-only\.
& (Join-Path $repo 'tools\build-installer.ps1') -Out $b -FailInstall
if ($LASTEXITCODE -ne 0) { throw 'failing test installer build failed' }

$names = [ordered]@{
  older = (Get-ChildItem $a -Filter 'Mesura-*-x64.msi' | Where-Object { $_.Name -notmatch '-ru' } | Select-Object -First 1).Name
  newer = (Get-ChildItem $b -Filter 'Mesura-*-x64.msi' | Where-Object { $_.Name -notmatch '-ru' } | Select-Object -First 1).Name
  cli   = 'a\Mesura\cli\mesura.exe'
}
$names | ConvertTo-Json | Set-Content (Join-Path $Out 'packages.json') -Encoding UTF8
Write-Output ("older {0}  newer {1}" -f $names.older, $names.newer)

