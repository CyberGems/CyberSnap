[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../publish-win64')
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '../src/CyberSnap/CyberSnap.csproj'
$outputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)

dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:PublishReadyToRun=true -o $outputPath
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed (exit code $LASTEXITCODE)."
}

& "$PSScriptRoot/Test-SelfContainedBundle.ps1" -Exe (Join-Path $outputPath 'CyberSnap.exe')
& "$PSScriptRoot/Get-BundledFfmpeg.ps1" -Destination $outputPath
