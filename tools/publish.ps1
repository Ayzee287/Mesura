# Builds and publishes Mesura into one predictable place (quality pass, 2026-10-24; architecture §4.11):
#
#   artifacts\current\Mesura\Mesura.exe          the app (window and tray), self-contained: no .NET needed on the PC
#   artifacts\current\Mesura\cli\mesura.exe      the command line (Mesura.exe and mesura.exe are one name to Windows,
#                                                so they can't share a folder)
#   artifacts\current\Mesura\Start here.txt      what to do with the folder
#   artifacts\current\Mesura-<version>-win-x64.zip   the same folder as one file
#   artifacts\current\BUILD.txt                  version, commit, uncommitted changes or not, when, and file hashes
#
# The build that was in artifacts\current moves to artifacts\previous\<date>-<version>-<commit>\ first, so the newest
# build is always in the same folder and older ones stay for comparison, clearly apart. Run it with
# tools\run-current.cmd (add --no-elevate to skip the administrator prompt).
#
# Build output only: it changes nothing on this PC, never writes to %LOCALAPPDATA%\Programs\Mesura (the installed copy)
# and creates no shortcut or start-up entry. -Label names the build (default "review": version 0.1.0-review, shown in
# Settings, in the zip's name and at the top of Start here.txt); -Label '' builds the plain version for a release.
param(
  [string]$Label = 'review',
  [string]$Out = ''
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$artifacts = Join-Path $repo 'artifacts'
$current = if ($Out) { $Out } else { Join-Path $artifacts 'current' }
$installed = Join-Path $env:LOCALAPPDATA 'Programs\Mesura'
if ([IO.Path]::GetFullPath($current).TrimEnd('\').StartsWith([IO.Path]::GetFullPath($installed).TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
  throw "Refusing to publish into the installed Mesura ($installed). Publish to artifacts\current and copy by hand if you mean to."
}

$version = ([xml](Get-Content (Join-Path $repo 'Directory.Build.props'))).Project.PropertyGroup.Version
$stamp = if ($Label) { "$version-$Label" } else { $version }
$commit = (git -C $repo rev-parse --short HEAD).Trim()
$dirty = [bool](git -C $repo status --porcelain)
$app = Join-Path $current 'Mesura'
$zip = Join-Path $current "Mesura-$stamp-win-x64.zip"

# The previous build (if any) keeps its own folder under artifacts\previous, named by when it was built and what it is.
function Move-ToPrevious([string]$folder, [string]$name) {
  $previous = Join-Path $artifacts 'previous'
  New-Item -ItemType Directory -Force -Path $previous | Out-Null
  $target = Join-Path $previous $name
  $n = 2
  while (Test-Path $target) { $target = Join-Path $previous "$name-$n"; $n++ }
  Move-Item -LiteralPath $folder -Destination $target
  Write-Output "Kept the earlier build in $target"
}

if (Test-Path $current) {
  $info = Join-Path $current 'build.json'
  $name = if (Test-Path $info) {
    $b = Get-Content -Raw $info | ConvertFrom-Json
    "{0}-{1}-{2}{3}" -f $b.builtLocal.Substring(0, 10), $b.version, $b.commit, $(if ($b.uncommitted) { '-dirty' } else { '' })
  } else { (Get-Item $current).LastWriteTime.ToString('yyyy-MM-dd') + '-unknown' }
  Move-ToPrevious $current $name
}

# Builds published before this layout (artifacts\Mesura, audit-build, redesign-build, setup-build) move under
# artifacts\previous once, by date and name, so artifacts\ holds current\, previous\ and the Track A kit (publish\).
foreach ($legacy in @(
    @{ Path = 'Mesura'; Name = '2026-10-22-0.1.0-f83c949'; Zip = 'Mesura-0.1.0-win-x64.zip' },
    @{ Path = 'audit-build'; Name = '2026-10-22-0.1.0-audit' },
    @{ Path = 'redesign-build'; Name = '2026-10-23-0.1.0-redesign' },
    @{ Path = 'setup-build'; Name = '2026-10-24-0.1.0-setup' })) {
  $path = Join-Path $artifacts $legacy.Path
  if (-not (Test-Path $path)) { continue }
  if ($legacy.Zip) {
    # The first build kept its folder and zip side by side at the top: they move together.
    $holder = Join-Path $artifacts "_$($legacy.Name)"
    New-Item -ItemType Directory -Force -Path $holder | Out-Null
    Move-Item -LiteralPath $path -Destination (Join-Path $holder 'Mesura')
    $legacyZip = Join-Path $artifacts $legacy.Zip
    if (Test-Path $legacyZip) { Move-Item -LiteralPath $legacyZip -Destination $holder }
    Move-ToPrevious $holder $legacy.Name
  } else {
    Move-ToPrevious $path $legacy.Name
  }
}

New-Item -ItemType Directory -Force -Path $current | Out-Null

# Single-file is set in each project; only self-contained and "no debug files" are given here (neither changes what
# restore resolves, so the committed lock files stay as they are). No debug files means no build paths in the output.
# The file version (what Windows Installer compares to decide whether to replace an installed file) must go up with every build: the release candidate
# number is not part of it ("0.3.0-rc2" is file version 0.3.0.0 otherwise), and an upgrade that installs the new files before it removes the old product
# (installer\Package.wxs, docs/installing.md) leaves a same-versioned file in place. Hours since 2026-10-01 (UTC) are monotonic whichever repository or machine builds (a commit count is not: the public repository has a different history), and fit in 16 bits for years.
$buildHours = [int](([datetime]::UtcNow - [datetime]'2026-10-01').TotalHours)
$fileVersion = "$version.$buildHours"
$versionArgs = @("-p:FileVersion=$fileVersion"; if ($Label) { "-p:Version=$stamp" })
# Every publish starts from a clean Release build: an incremental WPF build once left out the compiled pages after only
# a localization file had changed, and that app stopped at start ("Cannot locate resource 'mainwindow.xaml'").
foreach ($project in 'src\Mesura.App\Mesura.App.csproj', 'src\Mesura.Cli\Mesura.Cli.csproj') {
  dotnet clean (Join-Path $repo $project) -c Release -v:q --nologo | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "Cleaning $project failed." }
}
dotnet publish (Join-Path $repo 'src\Mesura.App\Mesura.App.csproj') -c Release --self-contained -p:DebugType=none @versionArgs -o $app
if ($LASTEXITCODE -ne 0) { throw 'Publishing the app failed.' }
dotnet publish (Join-Path $repo 'src\Mesura.Cli\Mesura.Cli.csproj') -c Release --self-contained -p:DebugType=none @versionArgs -o (Join-Path $app 'cli')
if ($LASTEXITCODE -ne 0) { throw 'Publishing the command line failed.' }
# The Track A kit's operator files ship with the kit (CLAUDE.md), not with the everyday app.
Remove-Item (Join-Path $app 'cli\capture.cmd'), (Join-Path $app 'cli\OPERATOR.txt') -ErrorAction SilentlyContinue

$startHere = Get-Content -Raw -Encoding UTF8 (Join-Path $PSScriptRoot 'Start here.txt')
if ($Label) {
  $startHere = @"
THIS IS A REVIEW BUILD ($stamp, commit $commit$(if ($dirty) { ' with uncommitted changes' })). Keep it separate from the Mesura you use:
- Run Mesura.exe from this folder (or tools\run-current.cmd in the repository). Don't copy it over the installed Mesura
  in %LOCALAPPDATA%\Programs\Mesura.
- In Settings, leave "Start in the tray when I sign in" and "Show in the Start menu" as they are: switching them on
  here points Windows at this copy instead of the installed one.
- It shares %LOCALAPPDATA%\Mesura with the installed Mesura: the same history (Activity), preferences and edited
  catalogs. Nothing changes until you confirm an Apply, Install, Undo or profile run.
The rest of this file is for the release itself.

"@ + $startHere
}
[System.IO.File]::WriteAllText((Join-Path $app 'Start here.txt'), $startHere, (New-Object System.Text.UTF8Encoding($false)))

# The licence, the third-party notices, the readme and the changelog travel with the files (the MIT licence and the
# components' notices require their text to accompany the executables).
foreach ($doc in 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'README.md', 'CHANGELOG.md') {
  $source = Join-Path $repo $doc
  if (-not (Test-Path $source)) { throw "$doc is missing from the repository root; a build without it can't be released." }
  Copy-Item -LiteralPath $source -Destination (Join-Path $app $doc)
}
# The diagnostic collector for a PC where Mesura doesn't start: double-click, read-only, writes one text file to the Desktop.
foreach ($d in 'Diagnose Mesura.cmd', 'Diagnose-Mesura.ps1') { Copy-Item -LiteralPath (Join-Path $repo "tools\diagnose\$d") -Destination (Join-Path $app $d) }
foreach ($d in 'Test Mesura operations.cmd', 'Test-MesuraOperations.ps1') { Copy-Item -LiteralPath (Join-Path $repo "tools\tester\$d") -Destination (Join-Path $app $d) }
Copy-Item -LiteralPath (Join-Path $repo 'docs\for-testers.md') -Destination (Join-Path $app 'FOR-TESTERS.md')
# The executables' hashes inside the folder (the zip's own hash can only sit beside the zip: SHA256SUMS.txt).
$inside = foreach ($rel in 'Mesura.exe', 'cli\mesura.exe') { '{0}  {1}' -f (Get-FileHash (Join-Path $app $rel) -Algorithm SHA256).Hash.ToLowerInvariant(), $rel.Replace('\', '/') }
[System.IO.File]::WriteAllText((Join-Path $app 'EXE-SHA256.txt'), (($inside -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))

# The same folder as one zip, for a USB stick or a download folder (unzip it on the PC, then start Mesura.exe). Entries
# are written one by one with '/' separators, as the zip format wants: ZipFile.CreateFromDirectory in Windows
# PowerShell 5.1 (.NET Framework) writes 'cli\mesura.exe', which tools outside Windows unpack as one odd file name.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$root = (Resolve-Path $app).Path.TrimEnd('\')
$archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
  foreach ($file in Get-ChildItem -Path $root -Recurse -File) {
    $name = 'Mesura/' + $file.FullName.Substring($root.Length + 1).Replace('\', '/')
    [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $name, [System.IO.Compression.CompressionLevel]::Optimal)
  }
}
finally {
  $archive.Dispose()
}

# What this build is, in a file beside it: the version and commit the executables carry, and the hashes to tell copies apart.
$exe = Join-Path $app 'Mesura.exe'
$productVersion = (Get-Item $exe).VersionInfo.ProductVersion
$build = [ordered]@{
  version     = $stamp
  commit      = $commit
  uncommitted = $dirty
  productVersion = $productVersion
  builtLocal  = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
  app         = 'Mesura\Mesura.exe'
  cli         = 'Mesura\cli\mesura.exe'
  zip         = (Split-Path $zip -Leaf)
  sha256      = [ordered]@{
    app = (Get-FileHash $exe -Algorithm SHA256).Hash
    cli = (Get-FileHash (Join-Path $app 'cli\mesura.exe') -Algorithm SHA256).Hash
    zip = (Get-FileHash $zip -Algorithm SHA256).Hash
  }
}
$build | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $current 'build.json')
# The zip's hash for whoever downloads it, in the format `sha256sum -c` and Get-FileHash comparisons read.
[System.IO.File]::WriteAllText((Join-Path $current 'SHA256SUMS.txt'),
  ('{0}  {1}' -f $build.sha256.zip.ToLowerInvariant(), $build.zip) + "`n", (New-Object System.Text.UTF8Encoding($false)))
$text = @"
Mesura $stamp
Commit:      $commit$(if ($dirty) { ' (built from a working tree with uncommitted changes)' } else { '' })
Executables: $productVersion
Built:       $($build.builtLocal)
Run:         Mesura\Mesura.exe   (or tools\run-current.cmd in the repository)
Zip:         $($build.zip)
SHA-256:     Mesura.exe $($build.sha256.app)
             cli\mesura.exe $($build.sha256.cli)
             zip $($build.sha256.zip)
Earlier builds: ..\previous\ (one folder each, named date-version-commit).
"@
[System.IO.File]::WriteAllText((Join-Path $current 'BUILD.txt'), $text, (New-Object System.Text.UTF8Encoding($false)))

if (-not $productVersion.Contains($commit)) {
  Write-Warning "The executable's version ($productVersion) doesn't name commit $commit."
}

Write-Output "Published $stamp ($commit$(if ($dirty) { ', uncommitted changes' })) to $(Resolve-Path $app)"
Write-Output "Zipped to $(Resolve-Path $zip)"
Write-Output "Run it: tools\run-current.cmd"
