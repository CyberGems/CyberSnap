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
$installerVersionText = $AppVersion -replace '^v', ''
if ($installerVersionText -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$') {
    throw 'AppVersion must be a numeric release version, optionally prefixed with v.'
}
$installerVersion = [version]::Parse($installerVersionText)
$versionParts = @($installerVersion.Major, $installerVersion.Minor, $installerVersion.Build, [Math]::Max(0, $installerVersion.Revision))
if (@($versionParts | Where-Object { $_ -gt 65535 }).Count) {
    throw 'Each installer version component must fit in a Windows version resource (0-65535).'
}
$expectedFileVersion = $versionParts -join '.'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$scriptPath = Join-Path $repositoryRoot 'CyberSnap.iss'
if (-not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) {
    throw "Inno Setup compiler was not found: $CompilerPath"
}

$compilerArguments = @("/DAppVersion=$installerVersionText")
$resolvedOutputDirectory = $repositoryRoot
if ($OutputDirectory) {
    $resolvedOutputDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
    New-Item -ItemType Directory -Path $resolvedOutputDirectory -Force | Out-Null
    $compilerArguments += "/O$resolvedOutputDirectory"
}
if (-not $OutputBaseFilename) { $OutputBaseFilename = "CyberSnap-Setup-$AppVersion" }
$compilerArguments += "/F$OutputBaseFilename"
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
    $installerPath = Join-Path $resolvedOutputDirectory "$OutputBaseFilename.exe"
    $actualFileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($installerPath)
    $numericFileVersion = @($actualFileVersion.FileMajorPart, $actualFileVersion.FileMinorPart, $actualFileVersion.FileBuildPart, $actualFileVersion.FilePrivatePart) -join '.'
    if ($numericFileVersion -ne $expectedFileVersion) {
        throw "Installer file version mismatch: expected $expectedFileVersion, got $numericFileVersion."
    }
}
finally {
    Pop-Location
}

Write-Output 'Installer compilation: PASS (no compiler warnings).'
Write-Output "Installer file version: PASS ($numericFileVersion)."
