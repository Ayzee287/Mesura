# Shows what undoing a Mesura Windows run WOULD change, without undoing anything: `mesura windows revert --run <id>` without --apply is a preview. Picks the newest recorded Windows apply run
# unless -Run names one. Writes the preview to the Desktop. Nothing on the PC is changed by this script.
#
# The real undo is a separate decision: `mesura windows revert --run <id> --apply` (run it yourself when you want it; the verifier then reports the run's values as no longer in place, which is correct).
param(
  [string]$Run = '',
  [string]$Cli = '',
  [string]$Out = ''
)
$ErrorActionPreference = 'Continue'
if (-not $Out) { $Out = [Environment]::GetFolderPath('Desktop') }
if (-not $Cli) { foreach ($c in @((Join-Path $PSScriptRoot 'mesura.exe'), (Join-Path $PSScriptRoot 'cli\mesura.exe'), (Join-Path $env:LOCALAPPDATA 'Programs\Mesura\cli\mesura.exe'))) { if (Test-Path $c) { $Cli = $c; break } } }
if (-not $Cli) { Write-Host 'mesura.exe was not found: put it beside this script or pass -Cli.'; exit 3 }
if (-not $Run) {
  $history = Join-Path $env:LOCALAPPDATA 'Mesura\Setup\history'
  $Run = Get-ChildItem $history -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'windows-apply$' } | Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty Name
}
if (-not $Run) { Write-Host 'No recorded Windows apply run was found; nothing to preview.'; exit 0 }
Write-Host "Preview of undoing run $Run (nothing is changed):"
$text = (& $Cli windows revert --run $Run 2>&1 | ForEach-Object { "$_" })
$text | ForEach-Object { Write-Host $_ }
$file = Join-Path $Out "rollback-preview-$Run.txt"
[IO.File]::WriteAllLines($file, $text, (New-Object System.Text.UTF8Encoding($true)))
Write-Host "Wrote $file"
