# Builds the Windows Installer packages (English and Russian) from a published build (docs/build-and-publish.md):
#
#   artifacts\current\Mesura-<version>-x64.msi       English wizard (the one updates use)
#   artifacts\current\Mesura-<version>-x64-ru.msi    Russian wizard
#
# It installs nothing and changes nothing on this PC: it copies the files the installer carries into a payload folder, wraps the
# licence for the wizard, and runs the WiX build (the WiX SDK comes from NuGet, pinned in installer\Mesura.Installer.wixproj).
# Run tools\publish.ps1 first; this takes artifacts\current\Mesura as it is.
param(
  [string]$Out = '',
  [switch]$FailInstall   # a test-only package whose install fails after copying (proves the rollback); written as test-only\Mesura-<v>-x64-FAILTEST.msi (never beside the installers that are published)
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$current = if ($Out) { $Out } else { Join-Path $repo 'artifacts\current' }
$app = Join-Path $current 'Mesura'
if (-not (Test-Path (Join-Path $app 'Mesura.exe'))) { throw "No published build in $app. Run tools\publish.ps1 first." }

$info = Get-Content -Raw (Join-Path $current 'build.json') | ConvertFrom-Json
$stamp = $info.version                                  # 0.3.0-rc1 or 0.3.0
$parts = $stamp -split '-', 2
$numbers = $parts[0].Split('.')
$rc = if ($parts.Count -gt 1 -and $parts[1] -match '(\d+)$') { [int]$Matches[1] } else { 0 }
$msiVersion = '{0}.{1}.{2}.{3}' -f $numbers[0], $numbers[1], $(if ($numbers.Count -gt 2) { $numbers[2] } else { 0 }), $rc

# The payload: what is installed, nothing more. The tester scripts and their notes stay in the portable zip.
$payload = Join-Path $current 'installer-payload'
if (Test-Path $payload) { Remove-Item -Recurse -Force $payload }
New-Item -ItemType Directory -Path $payload, (Join-Path $payload 'cli') | Out-Null
$files = @('Mesura.exe', 'PenImc_cor3.dll', 'PresentationNative_cor3.dll', 'vcruntime140_cor3.dll', 'wpfgfx_cor3.dll', 'D3DCompiler_47_cor3.dll', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'README.md', 'CHANGELOG.md')
foreach ($f in $files) { Copy-Item -LiteralPath (Join-Path $app $f) -Destination $payload }
Copy-Item -LiteralPath (Join-Path $app 'cli\mesura.exe') -Destination (Join-Path $payload 'cli')

# The wizard shows the licence as RTF.
$license = (Get-Content -Raw -Encoding UTF8 (Join-Path $repo 'LICENSE')) -replace '\\', '\\' -replace '\{', '\{' -replace '\}', '\}' -replace "`r?`n", "\par`r`n"
$rtf = Join-Path $current 'installer-license.rtf'
[IO.File]::WriteAllText($rtf, "{\rtf1\ansi\deff0{\fonttbl{\f0 Segoe UI;}}\fs18 $license}", [Text.Encoding]::ASCII)

$wixout = Join-Path $current 'installer-build'
if (Test-Path $wixout) { Remove-Item -Recurse -Force $wixout }
# A WiX build is incremental and does not notice a changed property (version, FailInstall): always start clean.
foreach ($d in 'installer\obj', 'installer\bin') { $full = Join-Path $repo $d; if (Test-Path $full) { Remove-Item -Recurse -Force $full } }
dotnet build (Join-Path $repo 'installer\Mesura.Installer.wixproj') -c Release -v:q --nologo `
  "-p:PayloadDir=$payload" "-p:LicenseRtf=$rtf" "-p:IconFile=$(Join-Path $repo 'src\Mesura.App\Assets\mesura.ico')" `
  "-p:FailInstall=$(if ($FailInstall) { 1 } else { 0 })" "-p:MsiProductVersion=$msiVersion" "-p:AppVersion=$stamp" "-p:OutputPath=$wixout\"
if ($LASTEXITCODE -ne 0) { throw 'Building the installer failed.' }

foreach ($culture in @(@{ Dir = 'en-US'; Suffix = '' }, @{ Dir = 'ru-RU'; Suffix = '-ru' })) {
  $built = Get-ChildItem -Recurse -Path $wixout -Filter 'Mesura-*.msi' | Where-Object { $_.Directory.Name -eq $culture.Dir } | Select-Object -First 1
  if (-not $built) { throw "The $($culture.Dir) package wasn't built." }
  # The failing test packages go in their own folder, away from the files that are published: nothing that lists "the installers of this build" can pick them up by accident.
  $targetFolder = if ($FailInstall) { New-Item -ItemType Directory -Force -Path (Join-Path $current 'test-only') | Select-Object -ExpandProperty FullName } else { $current }
  $target = Join-Path $targetFolder "Mesura-$stamp-x64$($culture.Suffix)$(if ($FailInstall) { '-FAILTEST' }).msi"
  Copy-Item -LiteralPath $built.FullName -Destination $target -Force
  Write-Output ("{0}  {1:N1} MB" -f $target, ((Get-Item $target).Length / 1MB))
}
Remove-Item -Recurse -Force $wixout, $rtf
