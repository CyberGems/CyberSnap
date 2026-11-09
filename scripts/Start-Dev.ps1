[CmdletBinding()]
param([switch]$PrepareOnly)

$ErrorActionPreference = 'Stop'
$output = Join-Path $PSScriptRoot '../src/CyberSnap/bin/dev/publish'

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

& "$PSScriptRoot/Publish-CyberSnap.ps1" -OutputDirectory $output

if (-not $PrepareOnly) {
    Assert-CyberSnapClosed
    $output = (Resolve-Path -LiteralPath $output).Path
    Start-Process -FilePath (Join-Path $output 'CyberSnap.exe') -WorkingDirectory $output
}
