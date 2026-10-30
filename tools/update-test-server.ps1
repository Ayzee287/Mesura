# A local stand-in for the update server, for testing the update flow end to end (docs/updates.md): serves the files of a folder over
# http://127.0.0.1:<port>/ with HTTP Range support, and can misbehave on request. Point Mesura at it with
#   $env:MESURA_UPDATE_BASE = 'http://127.0.0.1:8099'
# The app still verifies every signature and hash, so the server is no more trusted than GitHub is.
#
#   powershell -ExecutionPolicy Bypass -File tools\update-test-server.ps1 -Dir <folder> [-Port 8099] [-Mode ok|drop-once|slow|error500|corrupt-msi]
param(
  [Parameter(Mandatory = $true)][string]$Dir,
  [int]$Port = 8099,
  [ValidateSet('ok', 'drop-once', 'slow', 'error500', 'corrupt-msi')][string]$Mode = 'ok'
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path $Dir).Path
$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
$listener.Start()
Write-Host "Serving $root on http://127.0.0.1:$Port/ (mode $Mode). Ctrl+C or close to stop."
$dropped = $false
try {
  while ($listener.IsListening) {
    $ctx = $listener.GetContext()
    $req = $ctx.Request; $res = $ctx.Response
    $name = [Uri]::UnescapeDataString($req.Url.AbsolutePath.TrimStart('/'))
    $path = Join-Path $root $name
    try {
      if ($Mode -eq 'error500') { $res.StatusCode = 500; Write-Host "500 $name"; continue }
      if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { $res.StatusCode = 404; Write-Host "404 $name"; continue }
      $bytes = [IO.File]::ReadAllBytes($path)
      if ($Mode -eq 'corrupt-msi' -and $name.EndsWith('.msi')) { $bytes[1000] = $bytes[1000] -bxor 0xFF }
      $start = 0
      $range = $req.Headers['Range']
      if ($range -match 'bytes=(\d+)-') { $start = [int64]$Matches[1]; $res.StatusCode = 206; $res.AddHeader('Content-Range', "bytes $start-$($bytes.Length - 1)/$($bytes.Length)") }
      $count = $bytes.Length - $start
      $res.ContentLength64 = $count
      if ($Mode -eq 'drop-once' -and -not $dropped -and $name.EndsWith('.msi') -and $start -eq 0) {
        $dropped = $true
        $res.OutputStream.Write($bytes, 0, [int]($count / 3)); $res.OutputStream.Flush()
        $res.Abort(); Write-Host "dropped $name after a third"; continue
      }
      if ($Mode -eq 'slow' -and $name.EndsWith('.msi')) {
        $chunk = 1MB; $sent = 0
        while ($sent -lt $count) { $n = [Math]::Min($chunk, $count - $sent); $res.OutputStream.Write($bytes, [int]($start + $sent), [int]$n); $res.OutputStream.Flush(); $sent += $n; Start-Sleep -Milliseconds 400 }
      } else { $res.OutputStream.Write($bytes, [int]$start, [int]$count) }
      Write-Host "$($res.StatusCode) $name ($count bytes from $start)"
    }
    catch { Write-Host "error serving ${name}: $($_.Exception.Message)" }
    finally { try { $res.Close() } catch { } }
  }
}
finally { $listener.Stop() }
