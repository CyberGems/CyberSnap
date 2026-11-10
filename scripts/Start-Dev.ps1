[CmdletBinding()]
param(
    [switch]$Packaged,
    [switch]$PrepareOnly
)

$ErrorActionPreference = 'Stop'
$projectDirectory = Join-Path $PSScriptRoot '../src/CyberSnap'

function Assert-CyberSnapClosed {
    $mutex = $null
    # This is the same session-wide mutex used by App.Startup.cs and CyberSnap.iss.
    if ([Threading.Mutex]::TryOpenExisting('CyberSnapScreenshotTool_SingleInstance', [ref]$mutex)) {
        $mutex.Dispose()
        if ($PSUICulture -like 'es*') {
            throw 'CyberSnap sigue abierto. Elige Salir en su icono de la bandeja antes de iniciar CyberSnap Dev.'
        }
        throw 'CyberSnap is still running. Choose Exit from its tray icon before starting the dev build.'
    }
}

if (-not $PrepareOnly) {
    Assert-CyberSnapClosed
}

if ($Packaged -or $PrepareOnly) {
    $output = Join-Path $projectDirectory 'bin/dev/publish'
    & "$PSScriptRoot/Publish-CyberSnap.ps1" -OutputDirectory $output
} else {
    $output = Join-Path $projectDirectory 'bin/dev/quick'
    $project = Join-Path $projectDirectory 'CyberSnap.csproj'
    dotnet build $project -c Debug -o $output
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed (exit code $LASTEXITCODE)."
    }
}

if (-not $PrepareOnly) {
    Assert-CyberSnapClosed
    $output = (Resolve-Path -LiteralPath $output).Path
    # Start the native apphost directly so Windows applies its PerMonitorV2 manifest.
    Start-Process -FilePath (Join-Path $output 'CyberSnap.exe') -WorkingDirectory $output
}
