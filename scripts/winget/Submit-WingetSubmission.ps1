param(
    [Parameter(Mandatory = $true)]
    [string]$ManifestDirectory,

    [Parameter(Mandatory = $true)]
    [string]$GitHubToken
)

$ErrorActionPreference = 'Stop'

$previousToken = $env:GH_TOKEN
try {
    $env:GH_TOKEN = $GitHubToken
    & node (Join-Path $PSScriptRoot '../../.github/scripts/submit-winget.mjs') --manifest-directory $ManifestDirectory
    if ($LASTEXITCODE -ne 0) { throw 'The winget submission failed.' }
}
finally {
    $env:GH_TOKEN = $previousToken
}
