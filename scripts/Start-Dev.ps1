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
    $executable = Join-Path $output 'CyberSnap.exe'
} else {
    $project = Join-Path $projectDirectory 'CyberSnap.csproj'
    dotnet build $project -c Debug
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed (exit code $LASTEXITCODE)."
    }
    $executable = dotnet msbuild $project -nologo -p:Configuration=Debug -getProperty:RunCommand
    if ($LASTEXITCODE -ne 0 -or -not $executable) {
        throw 'MSBuild could not resolve the Debug executable.'
    }
}

if (-not $PrepareOnly) {
    Assert-CyberSnapClosed
    $app = Get-Item -LiteralPath $executable
    # Start the native apphost directly so Windows applies its PerMonitorV2 manifest.
    Start-Process -FilePath $app.FullName -WorkingDirectory $app.DirectoryName
}
