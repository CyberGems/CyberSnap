[CmdletBinding()]
param()

$sourcePath = Join-Path $PSScriptRoot '..\src\CyberSnap\Assets\CyberSnap_square.png'
$servicePath = Join-Path $PSScriptRoot '..\services\cybersnap-share\public\logo.png'

if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
    throw "Canonical CyberSnap logo is missing: $sourcePath"
}

if (-not (Test-Path -LiteralPath $servicePath -PathType Leaf)) {
    throw "CyberSnap Share logo is missing: $servicePath"
}

$sourceHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
$serviceHash = (Get-FileHash -LiteralPath $servicePath -Algorithm SHA256).Hash

if ($sourceHash -ne $serviceHash) {
    throw "CyberSnap Share logo is out of sync. Copy the canonical asset to services/cybersnap-share/public/logo.png."
}

Write-Output 'CyberSnap Share brand asset: PASS (logo.png matches CyberSnap_square.png).'
