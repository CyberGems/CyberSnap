[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$AppVersion,

    [string]$OutputDirectory,
    [string]$OutputBaseFilename,
    [string]$CompilerPath = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$scriptPath = Join-Path $repositoryRoot 'CyberSnap.iss'
if (-not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) {
    throw "Inno Setup compiler was not found: $CompilerPath"
}

$compilerArguments = @("/DAppVersion=$AppVersion")
if ($OutputDirectory) {
    $resolvedOutputDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
    New-Item -ItemType Directory -Path $resolvedOutputDirectory -Force | Out-Null
    $compilerArguments += "/O$resolvedOutputDirectory"
}
if ($OutputBaseFilename) {
    $compilerArguments += "/F$OutputBaseFilename"
}
$compilerArguments += $scriptPath

Push-Location $repositoryRoot
try {
    & $CompilerPath @compilerArguments 2>&1 | Tee-Object -Variable compilerOutput
    $compilerExitCode = $LASTEXITCODE
    if ($compilerExitCode -ne 0) {
        throw "Inno Setup failed with exit code $compilerExitCode."
    }
    if ($compilerOutput -match '^\s*Warning:') {
        throw 'Inno Setup emitted compiler warnings. Fix them before distributing the installer.'
    }
}
finally {
    Pop-Location
}

Write-Output 'Installer compilation: PASS (no compiler warnings).'
