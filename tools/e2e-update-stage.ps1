# Prepares the signed update folders that tools\e2e-update.ps1 -Staged uses, so the test can run as a different Windows user without the signing key
# (which stays in the key holder's profile and is read here only to sign; it is never copied).
#
#   powershell -ExecutionPolicy Bypass -File tools\e2e-update-stage.ps1 -New <folder of the new build> -Out <folder>
#
# Writes <Out>\ok (the new installer and a manifest signed for http://127.0.0.1:8097), <Out>\tamper (the same manifest edited after signing) and
# <Out>\fail (the test-only FAILTEST installer signed the same way), each with the files the local test server serves.
param(
  [Parameter(Mandatory = $true)][string]$New,
  [Parameter(Mandatory = $true)][string]$Out,
  [string]$Key = (Join-Path $env:USERPROFILE '.mesura-release\update-signing.pk8')
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$New = (Resolve-Path $New).Path
$info = Get-Content -Raw (Join-Path $New 'build.json') | ConvertFrom-Json
$v = $info.version
$failMsi = Join-Path $New "test-only\Mesura-$v-x64-FAILTEST.msi"
if (-not (Test-Path $failMsi)) { $failMsi = Join-Path $New "Mesura-$v-x64-FAILTEST.msi" }
if (-not (Test-Path $Key)) { throw "No signing key at $Key." }
New-Item -ItemType Directory -Force $Out | Out-Null
Set-Content (Join-Path $Out 'notes-en.txt') "- End-to-end test notes.`n- Second line." -Encoding UTF8
foreach ($kind in 'ok', 'tamper', 'fail') {
  $dir = Join-Path $Out $kind
  if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
  New-Item -ItemType Directory -Force $dir | Out-Null
  $msi = if ($kind -eq 'fail') { $failMsi } else { Join-Path $New "Mesura-$v-x64.msi" }
  if (-not (Test-Path $msi)) { Write-Host "skipped ${kind}: no $msi"; continue }
  Copy-Item $msi (Join-Path $dir "Mesura-$v-x64.msi")
  Copy-Item (Join-Path $New 'build.json') (Join-Path $dir 'build.json')
  dotnet run --project (Join-Path $repo 'tools\Mesura.ReleaseTool') -c Release -- manifest --channel preview --version $v --msi (Join-Path $dir "Mesura-$v-x64.msi") --url "http://127.0.0.1:8097/Mesura-$v-x64.msi" --notes-en (Join-Path $Out 'notes-en.txt') --key $Key --out $dir | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "signing the $kind manifest failed" }
  if ($kind -eq 'tamper') { $m = Join-Path $dir 'update-preview.json'; (Get-Content -Raw $m).Replace($v, '9.9.9') | Set-Content $m -NoNewline }
  Write-Host "staged $kind"
}
