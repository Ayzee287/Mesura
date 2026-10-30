# Signs the update manifest for a build and lists the files to publish (docs/updates.md). Local only: it publishes and uploads nothing.
#
#   powershell -File tools\make-release.ps1 -Channel preview -Url https://github.com/Ayzee287/Mesura/releases/download/preview/Mesura-0.3.0-rc1-x64.msi `
#     -NotesEn notes-en.md [-NotesRu notes-ru.md] [-Build artifacts\current] [-Key $env:USERPROFILE\.mesura-release\update-signing.pk8]
#
# Writes, into the build folder, update-<channel>.json and update-<channel>.json.sig (the manifest and its signature) and SHA256SUMS.txt
# (the installers, the zip, the manifest). The private key is read from the file named; it is never copied or printed.
param(
  [Parameter(Mandatory = $true)][ValidateSet('stable', 'preview')][string]$Channel,
  [Parameter(Mandatory = $true)][string]$Url,
  [Parameter(Mandatory = $true)][string]$NotesEn,
  [string]$NotesRu = '',
  [string]$Build = '',
  [string]$Key = (Join-Path $env:USERPROFILE '.mesura-release\update-signing.pk8'),
  [int]$Days = 180,
  [string]$Local = ''   # a local test server base (http://127.0.0.1:8099): the url inside the manifest points there instead
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$current = if ($Build) { $Build } else { Join-Path $repo 'artifacts\current' }
$info = Get-Content -Raw (Join-Path $current 'build.json') | ConvertFrom-Json
$version = $info.version
$msi = Join-Path $current "Mesura-$version-x64.msi"
$msiRu = Join-Path $current "Mesura-$version-x64-ru.msi"
if (-not (Test-Path $msi)) { throw "No installer at $msi. Run tools\build-installer.ps1 first." }
if (-not (Test-Path $Key)) { throw "No signing key at $Key. Make one once with: dotnet run --project tools\Mesura.ReleaseTool -- keygen --dir <folder outside the repository>" }
$args = @('manifest', '--channel', $Channel, '--version', $version, '--msi', $msi, '--url', $(if ($Local) { "$Local/Mesura-$version-x64.msi" } else { $Url }),
  '--notes-en', $NotesEn, '--key', $Key, '--out', $current, '--days', $Days)
if ($NotesRu) { $args += @('--notes-ru', $NotesRu) }
# The Russian installer is offered to people who installed the Russian one (an update keeps the installed language).
if (Test-Path $msiRu) { $args += @('--msi-ru', $msiRu, '--url-ru', $(if ($Local) { "$Local/Mesura-$version-x64-ru.msi" } else { $Url -replace '-x64\.msi$', '-x64-ru.msi' })) }
dotnet run --project (Join-Path $repo 'tools\Mesura.ReleaseTool') -c Release -- @args
if ($LASTEXITCODE -ne 0) { throw 'Signing the manifest failed.' }

$files = Get-ChildItem $current -File | Where-Object { $_.Name -match '^(Mesura-.*\.(msi|zip)|update-.*\.json(\.sig)?)$' } | Sort-Object Name
$lines = foreach ($f in $files) { '{0}  {1}' -f (Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $f.Name }
[IO.File]::WriteAllLines((Join-Path $current 'SHA256SUMS.txt'), $lines, (New-Object Text.UTF8Encoding($false)))
Write-Output "Publish these files (a GitHub release; stable is a normal release, preview is the 'preview' pre-release):"
$files | ForEach-Object { Write-Output ("  {0}  ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB)) }
Write-Output '  SHA256SUMS.txt'
