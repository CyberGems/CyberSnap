<#
.SYNOPSIS
  Encrypts an ImgBB API key for embedding as CyberSnap OOTB (out-of-the-box) credential.

.DESCRIPTION
  Produces a Base64 AES-GCM ciphertext compatible with DefaultCredentialVault.
  Paste the output into EmbeddedImgBBCiphertextBase64 in:
    src/CyberSnap/Services/Upload/DefaultCredentialVault.cs

  IMPORTANT:
  - Do NOT commit plaintext API keys.
  - Ciphertext is only obfuscation (client-side OSS); rotate if abused.
  - Prefer generating this on a maintainer machine and reviewing the PR carefully.

.PARAMETER ApiKey
  Plain ImgBB API key (or pass via -ApiKeyEnv / env CYBERSNAP_IMGBB_API_KEY).

.PARAMETER ApiKeyEnv
  If set, read the key from this environment variable name (default: CYBERSNAP_IMGBB_API_KEY).

.EXAMPLE
  $env:CYBERSNAP_IMGBB_API_KEY = "your-real-key"
  .\scripts\Protect-UploadVaultKey.ps1

.EXAMPLE
  .\scripts\Protect-UploadVaultKey.ps1 -ApiKey "your-real-key"
#>
[CmdletBinding()]
param(
    [string] $ApiKey,
    [string] $ApiKeyEnv = "CYBERSNAP_IMGBB_API_KEY"
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    $ApiKey = [Environment]::GetEnvironmentVariable($ApiKeyEnv)
}
if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    throw "No API key. Pass -ApiKey or set env $ApiKeyEnv."
}

$ApiKey = $ApiKey.Trim()

# Must match DefaultCredentialVault.DeriveKey entropy parts and AES-GCM packing.
$partA = [Text.Encoding]::UTF8.GetBytes("CyberSnap.Upload.Vault.v1")
$partB = [Text.Encoding]::UTF8.GetBytes("ImgBB.OOTB.2026")
$partC = [Text.Encoding]::UTF8.GetBytes("CyberGems|CyberSnap|Share")
$combined = New-Object byte[] ($partA.Length + $partB.Length + $partC.Length)
[Buffer]::BlockCopy($partA, 0, $combined, 0, $partA.Length)
[Buffer]::BlockCopy($partB, 0, $combined, $partA.Length, $partB.Length)
[Buffer]::BlockCopy($partC, 0, $combined, $partA.Length + $partB.Length, $partC.Length)

$sha = [Security.Cryptography.SHA256]::Create()
try {
    $key = $sha.ComputeHash($combined)
}
finally {
    $sha.Dispose()
}

$plain = [Text.Encoding]::UTF8.GetBytes($ApiKey)
$nonce = New-Object byte[] 12
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
try { $rng.GetBytes($nonce) } finally { $rng.Dispose() }

$cipher = New-Object byte[] $plain.Length
$tag = New-Object byte[] 16

# AesGcm is available on .NET (PowerShell 7+)
if (-not ("System.Security.Cryptography.AesGcm" -as [type])) {
    throw "AesGcm requires PowerShell 7+ / .NET Core. Run: pwsh -File $($MyInvocation.MyCommand.Path)"
}

$aes = [Security.Cryptography.AesGcm]::new($key, 16)
try {
    $aes.Encrypt($nonce, $plain, $cipher, $tag)
}
finally {
    $aes.Dispose()
}

$packed = New-Object byte[] (12 + 16 + $cipher.Length)
[Buffer]::BlockCopy($nonce, 0, $packed, 0, 12)
[Buffer]::BlockCopy($tag, 0, $packed, 12, 16)
[Buffer]::BlockCopy($cipher, 0, $packed, 28, $cipher.Length)
$b64 = [Convert]::ToBase64String($packed)

Write-Host ""
Write-Host "Paste this into DefaultCredentialVault.cs → EmbeddedImgBBCiphertextBase64:" -ForegroundColor Cyan
Write-Host ""
Write-Host "        `"$b64`";" -ForegroundColor Green
Write-Host ""
Write-Host "Then rebuild Release. Users without a personal key will use this OOTB credential." -ForegroundColor DarkGray
Write-Host "Env CYBERSNAP_IMGBB_API_KEY still overrides the embedded key for maintainers." -ForegroundColor DarkGray
Write-Host ""
