# Fails the release if CyberSnap.exe still expects a separately installed .NET Desktop Runtime.
# A framework-dependent exe is what shows "You must install .NET Desktop Runtime to run this application."
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Exe
)

$ErrorActionPreference = 'Stop'

$resolved = Resolve-Path -LiteralPath $Exe
$bytes = [System.IO.File]::ReadAllBytes($resolved)
$text = [System.Text.Encoding]::ASCII.GetString($bytes)

if ($text.IndexOf('includedFrameworks') -lt 0 -or $text.IndexOf('Microsoft.WindowsDesktop.App') -lt 0) {
    throw "CyberSnap.exe does not embed the .NET Desktop Runtime. Publish with: dotnet publish -r win-x64 --self-contained true"
}

Write-Output "Self-contained bundle: PASS ($($resolved.Path))"
