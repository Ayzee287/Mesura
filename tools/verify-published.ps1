# Checks what is actually published at an update source, the way the app would, without installing anything (docs/updates.md, docs/testing-0.3.md).
# It only downloads: the manifest, its signature and the packages it names (into a temporary folder that is deleted afterwards).
#
#   powershell -ExecutionPolicy Bypass -File tools\verify-published.ps1 -Channel preview -ExpectVersion 0.3.0-rc2
#   powershell -ExecutionPolicy Bypass -File tools\verify-published.ps1 -Channel preview -Base http://127.0.0.1:8097   # a local test server (flat layout)
#
# The default source is the real one (GitHub releases of Ayzee287/Mesura). Run it AFTER the owner has published a Preview release (an external step,
# docs/updates.md "Publishing a Preview release"); before that it reports that nothing is published, which is the true state, not a failure of the build.
# Checks: the manifest and its signature download; the signature verifies against the public key compiled into the app; the channel, expiry and
# version are as expected; every package URL is https; every package downloads and matches the signed size and SHA-256.
param(
  [ValidateSet('stable', 'preview')][string]$Channel = 'preview',
  [string]$Base = '',
  [string]$ExpectVersion = '',
  [switch]$NoPackage
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$results = New-Object System.Collections.Generic.List[string]; $failed = $false
function Check([string]$what, [bool]$ok, [string]$detail = '') { $script:failed = $script:failed -or -not $ok; $line = '{0,-5} {1} {2}' -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $what, $detail; $results.Add($line); Write-Host $line -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' }) }

$default = 'https://github.com/Ayzee287/Mesura/releases'
$name = "update-$Channel.json"
if ($Base) { $url = ($Base.TrimEnd('/')) + "/$name" }
elseif ($Channel -eq 'preview') { $url = "$default/download/preview/$name" }
else { $url = "$default/latest/download/$name" }
Write-Host "Source: $url"

$work = Join-Path ([IO.Path]::GetTempPath()) ('mesura-verify-' + [Guid]::NewGuid().ToString('N').Substring(0, 6))
New-Item -ItemType Directory $work | Out-Null
try {
  $manifestFile = Join-Path $work $name; $sigFile = "$manifestFile.sig"
  try {
    Invoke-WebRequest -Uri $url -OutFile $manifestFile -UseBasicParsing -TimeoutSec 30
    Invoke-WebRequest -Uri "$url.sig" -OutFile $sigFile -UseBasicParsing -TimeoutSec 30
  }
  catch {
    $code = $null; if ($_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode }
    Check '[published] the manifest and its signature can be downloaded' $false "$(if ($code) { "HTTP $code" } else { $_.Exception.Message }): nothing is published at this source yet (or it is unreachable)"
    throw 'stop'
  }
  Check '[published] the manifest and its signature download' $true
  # The app's trusted public key(s), as compiled into UpdateKeys.cs.
  $keys = [regex]::Matches((Get-Content -Raw (Join-Path $repo 'src\Mesura.Core\Update\UpdateKeys.cs')), '"(MFkw[A-Za-z0-9+/=]+)"') | ForEach-Object { $_.Groups[1].Value }
  $valid = $false
  foreach ($k in $keys) {
    dotnet run --project (Join-Path $repo 'tools\Mesura.ReleaseTool') -c Release -- verify --manifest $manifestFile --sig $sigFile --pub $k *> $null
    if ($LASTEXITCODE -eq 0) { $valid = $true; break }
  }
  Check '[published] the signature verifies against a key the app trusts' $valid
  $m = Get-Content -Raw $manifestFile | ConvertFrom-Json
  Check "[published] the channel is $Channel" ($m.channel -eq $Channel) "$($m.channel)"
  Check '[published] the manifest has not expired' ([DateTimeOffset]::Parse($m.expiresUtc) -gt [DateTimeOffset]::UtcNow) "expires $($m.expiresUtc)"
  if ($ExpectVersion) { Check "[published] the version is $ExpectVersion" ($m.version -eq $ExpectVersion) "$($m.version)" } else { Write-Host "      version offered: $($m.version), sequence $($m.sequence)" }
  if ($Channel -eq 'stable') { Check '[published] a stable manifest names no pre-release' ($m.version -notmatch '-') "$($m.version)" }
  Check '[published] the manifest names at least one package' (@($m.packages).Count -ge 1) "$(@($m.packages).Count) package(s)"
  foreach ($p in $m.packages) {
    $label = "$($p.kind) $($p.architecture) $(if ($p.language) { $p.language } else { '(no language)' })"
    $https = $p.url.StartsWith('https://') -or $p.url.StartsWith('http://127.0.0.1') -or $p.url.StartsWith('http://localhost')
    Check "[package $label] the address is https (or this PC)" $https $p.url
    if ($NoPackage) { continue }
    $file = Join-Path $work ([IO.Path]::GetFileName(([Uri]$p.url).AbsolutePath))
    try { Invoke-WebRequest -Uri $p.url -OutFile $file -UseBasicParsing -TimeoutSec 600 } catch { Check "[package $label] downloads" $false $_.Exception.Message; continue }
    $hash = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
    Check "[package $label] the size matches the signed manifest" ((Get-Item $file).Length -eq $p.size) "$((Get-Item $file).Length) vs $($p.size)"
    Check "[package $label] the SHA-256 matches the signed manifest" ($hash -eq $p.sha256.ToLowerInvariant())
  }
}
catch { if ($_.Exception.Message -ne 'stop') { Check '[published] the check itself ran' $false $_.Exception.Message } }
finally { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
Write-Host "`n$(@($results | Where-Object { $_ -like 'PASS*' }).Count) passed, $(@($results | Where-Object { $_ -like 'FAIL*' }).Count) failed."
if ($failed) { exit 1 }
