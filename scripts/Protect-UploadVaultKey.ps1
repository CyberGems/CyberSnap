<#
.SYNOPSIS
  Encrypts an upload API key for embedding as CyberSnap OOTB (out-of-the-box) credential.

.DESCRIPTION
  Produces a Base64 AES-GCM ciphertext compatible with DefaultCredentialVault.
  Paste the output into:
    - EmbeddedImgBBCiphertextBase64 (ImgBB) — default env CYBERSNAP_IMGBB_API_KEY
    - EmbeddedCyberGemsCiphertextBase64 (Share) — use -ApiKeyEnv CYBERSNAP_SHARE_API_KEY
  in: src/CyberSnap/Services/Upload/DefaultCredentialVault.cs

  IMPORTANT:
  - Do NOT commit plaintext API keys.
  - Ciphertext is only obfuscation (client-side OSS); rotate if abused.
  - Prefer generating this on a maintainer machine and reviewing the PR carefully.

.PARAMETER ApiKey
  Plain API key (or pass via -ApiKeyEnv).

.PARAMETER ApiKeyEnv
  Environment variable name to read the key from (default: CYBERSNAP_IMGBB_API_KEY).
  Use CYBERSNAP_SHARE_API_KEY for CyberGems Share.

.EXAMPLE
  $env:CYBERSNAP_IMGBB_API_KEY = "your-real-key"
  .\scripts\Protect-UploadVaultKey.ps1

.EXAMPLE
  .\scripts\Protect-UploadVaultKey.ps1 -ApiKey "your-share-key" -ApiKeyEnv CYBERSNAP_SHARE_API_KEY
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

# Same crypto for both; target constant depends on which secret you encrypted.
$isShare = $ApiKeyEnv -match 'SHARE|CYBERGEMS' -or $env:CYBERSNAP_VAULT_TARGET -match 'SHARE|CYBERGEMS'
$constName = if ($isShare) { "EmbeddedCyberGemsCiphertextBase64" } else { "EmbeddedImgBBCiphertextBase64" }
$envHint = if ($isShare) { "CYBERSNAP_SHARE_API_KEY" } else { "CYBERSNAP_IMGBB_API_KEY" }

Write-Host ""
Write-Host "Paste this into DefaultCredentialVault.cs → $constName`:" -ForegroundColor Cyan
Write-Host ""
Write-Host "        `"$b64`";" -ForegroundColor Green
Write-Host ""
if (-not $isShare) {
    Write-Host "Tip: for CyberGems Share OOTB, re-run with -ApiKeyEnv CYBERSNAP_SHARE_API_KEY" -ForegroundColor Yellow
    Write-Host "     (or set `$env:CYBERSNAP_VAULT_TARGET = 'SHARE') so this message names the Share constant." -ForegroundColor Yellow
}
Write-Host "Then rebuild Release. Users without a personal key will use this OOTB credential." -ForegroundColor DarkGray
Write-Host "Env $envHint still overrides the embedded key for maintainers." -ForegroundColor DarkGray
Write-Host ""
