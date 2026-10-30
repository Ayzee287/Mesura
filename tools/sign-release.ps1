# Authenticode-signs files (the two executables, then the installers) with a code-signing certificate you own, and timestamps them.
# It never creates, stores or prints a certificate or password: the certificate comes from the Windows certificate store (-Thumbprint, for a
# hardware token or an installed certificate) or from a PFX file whose password is read from the environment (MESURA_SIGN_PFX_PASSWORD).
# This is Windows' own Authenticode (what SmartScreen reads); it is separate from the update manifest's ECDSA signature, which is unchanged.
#
#   powershell -ExecutionPolicy Bypass -File tools\sign-release.ps1 -Path artifacts\current\Mesura\Mesura.exe, artifacts\current\Mesura\cli\mesura.exe -Thumbprint <sha1>
#   $env:MESURA_SIGN_PFX_PASSWORD = '...'; powershell -File tools\sign-release.ps1 -Path <files> -PfxPath C:\keys\mesura.pfx
#
# Order in a release (docs/build-and-publish.md): sign the executables after `publish` and before the zip and the installer payload are made
# (`tools\publish.ps1 -Sign`), then sign the MSIs after they are built (`tools\build-installer.ps1 -Sign`) and before `make-release.ps1` hashes them,
# because the update manifest carries the installer's SHA-256 and a later signature would change it.
#
# What it cannot do: Azure Trusted Signing and cloud HSM services sign through their own signtool dlib; use their documented command for those
# files and this script for everything else. No certificate is bundled: a self-signed one proves the mechanism and nothing to SmartScreen.
param(
  [Parameter(Mandatory = $true)][string[]]$Path,
  [string]$Thumbprint = '',
  [string]$PfxPath = '',
  [string]$TimestampServer = 'http://timestamp.digicert.com'
)
$ErrorActionPreference = 'Stop'

$cert = $null
if ($Thumbprint) {
  $cert = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -ErrorAction SilentlyContinue | Where-Object { $_.Thumbprint -eq $Thumbprint.Replace(' ', '').ToUpperInvariant() } | Select-Object -First 1
  if (-not $cert) { throw "No certificate with thumbprint $Thumbprint in the CurrentUser or LocalMachine store." }
} elseif ($PfxPath) {
  if (-not (Test-Path $PfxPath)) { throw "No PFX file at $PfxPath." }
  $secure = if ($env:MESURA_SIGN_PFX_PASSWORD) { ConvertTo-SecureString $env:MESURA_SIGN_PFX_PASSWORD -AsPlainText -Force } else { $null }
  $cert = if ($secure) { Get-PfxCertificate -FilePath $PfxPath -Password $secure } else { Get-PfxCertificate -FilePath $PfxPath }
} else {
  throw 'Give -Thumbprint or -PfxPath: no certificate is built into this script.'
}
if (-not ($cert.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq '1.3.6.1.5.5.7.3.3' })) { throw 'That certificate is not a code-signing certificate (no Code Signing key usage).' }
if (-not $cert.HasPrivateKey) { throw 'That certificate has no private key here.' }

$failed = $false
foreach ($file in $Path) {
  $full = (Resolve-Path -LiteralPath $file).Path
  $result = Set-AuthenticodeSignature -FilePath $full -Certificate $cert -TimestampServer $TimestampServer -HashAlgorithm SHA256
  $status = (Get-AuthenticodeSignature -FilePath $full).Status
  # A chain that ends at a root this PC doesn't trust (a self-signed test certificate) reads as UnknownError/NotTrusted, but the signature is there.
  $signed = $result.SignerCertificate -ne $null
  '{0,-12} {1}  ({2})' -f $(if ($signed) { 'signed' } else { 'NOT SIGNED' }), $full, $status
  if (-not $signed) { $failed = $true }
}
if ($failed) { throw 'At least one file was not signed.' }
