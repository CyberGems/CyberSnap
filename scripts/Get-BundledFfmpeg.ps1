# Downloads the pinned FFmpeg essentials build and copies ffmpeg.exe next to a published CyberSnap.
# The archive is not stored in git. The release workflow runs this after dotnet publish.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Destination
)

$ErrorActionPreference = 'Stop'

# Gyan's essentials build. GPLv3 because it includes libx264, which MP4 recording requires.
# Source commit: https://github.com/FFmpeg/FFmpeg/commit/946fcce07b
$Version = '9.0.2'
$FileName = "ffmpeg-$Version-essentials_build.zip"
$Url = "https://github.com/GyanD/codexffmpeg/releases/download/$Version/$FileName"
$Sha256 = '60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba'

New-Item -ItemType Directory -Force -Path $Destination | Out-Null

$cacheDir = Join-Path $env:TEMP 'CyberSnapFfmpegCache'
New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null
$archivePath = Join-Path $cacheDir $FileName

$needsDownload = -not (Test-Path -LiteralPath $archivePath)
if (-not $needsDownload) {
    $cachedHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLower()
    $needsDownload = $cachedHash -ne $Sha256
}

if ($needsDownload) {
    Write-Output "Downloading $Url"
    & curl.exe -L --fail --retry 3 -o $archivePath $Url
    if ($LASTEXITCODE -ne 0) {
        throw "FFmpeg download failed with exit code $LASTEXITCODE"
    }

    $downloadedHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLower()
    if ($downloadedHash -ne $Sha256) {
        Remove-Item -LiteralPath $archivePath -Force
        throw "FFmpeg archive hash mismatch. Expected $Sha256, got $downloadedHash"
    }
}

$extractDir = Join-Path $cacheDir "extract-$Version"
if (Test-Path -LiteralPath $extractDir) {
    Remove-Item -LiteralPath $extractDir -Recurse -Force
}

Expand-Archive -LiteralPath $archivePath -DestinationPath $extractDir

$ffmpeg = Get-ChildItem -LiteralPath $extractDir -Recurse -Filter 'ffmpeg.exe' |
    Where-Object { $_.Directory.Name -eq 'bin' } |
    Select-Object -First 1
if (-not $ffmpeg) {
    throw "ffmpeg.exe was not found in $FileName"
}

$license = Get-ChildItem -LiteralPath $extractDir -Recurse -File |
    Where-Object { $_.Name -eq 'LICENSE' -or $_.Name -eq 'COPYING.GPLv3' } |
    Select-Object -First 1
if (-not $license) {
    throw "The FFmpeg license file was not found in $FileName"
}

Copy-Item -LiteralPath $ffmpeg.FullName -Destination (Join-Path $Destination 'ffmpeg.exe') -Force
Copy-Item -LiteralPath $license.FullName -Destination (Join-Path $Destination 'FFmpeg-LICENSE.txt') -Force

$notice = @"
CyberSnap includes this ffmpeg.exe so MP4 recording, GIF encoding, trimming, and audio waveforms work without a separate FFmpeg install.

Build: FFmpeg $Version essentials for Windows x64, from https://github.com/GyanD/codexffmpeg/releases/tag/$Version
Upstream source: https://github.com/FFmpeg/FFmpeg/commit/946fcce07b
Archive SHA256: $Sha256
License: GNU General Public License v3.0, because this build includes libx264. The license text is in FFmpeg-LICENSE.txt.

Only ffmpeg.exe is shipped. ffplay.exe and ffprobe.exe from the same archive are not.

CyberSnap looks for ffmpeg.exe beside CyberSnap.exe first, then in %AppData%\CyberSnap, then on PATH. Replacing this file with another ffmpeg.exe is supported. The replacement must provide libx264, AAC, the GIF muxer, and the palettegen and paletteuse filters.
"@
Set-Content -LiteralPath (Join-Path $Destination 'FFmpeg-NOTICE.txt') -Value $notice -Encoding utf8

$smokeDir = Join-Path $cacheDir 'smoke'
New-Item -ItemType Directory -Force -Path $smokeDir | Out-Null
$bundled = Join-Path $Destination 'ffmpeg.exe'

function Invoke-FfmpegSmoke {
    param(
        [string[]]$ArgumentList,
        [string]$OutputPath,
        [string]$FailureMessage
    )

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $bundled
    $psi.Arguments = $ArgumentList -join ' '
    $psi.UseShellExecute = $false
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $proc = [System.Diagnostics.Process]::Start($psi)
    $stderr = $proc.StandardError.ReadToEnd()
    $proc.WaitForExit()
    if ($proc.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $OutputPath)) {
        throw "$FailureMessage $stderr"
    }
}

$smokeMp4 = Join-Path $smokeDir 'smoke.mp4'
Invoke-FfmpegSmoke `
    -ArgumentList @('-hide_banner', '-y', '-f', 'lavfi', '-i', 'color=c=black:s=32x32:d=0.2', '-c:v', 'libx264', '-pix_fmt', 'yuv420p', "`"$smokeMp4`"") `
    -OutputPath $smokeMp4 `
    -FailureMessage 'Bundled ffmpeg.exe could not encode a libx264 MP4.'

$smokePalette = Join-Path $smokeDir 'palette.png'
Invoke-FfmpegSmoke `
    -ArgumentList @('-hide_banner', '-y', '-f', 'lavfi', '-i', 'color=c=red:s=32x32:d=0.2', '-vf', 'palettegen', "`"$smokePalette`"") `
    -OutputPath $smokePalette `
    -FailureMessage 'Bundled ffmpeg.exe could not run palettegen, which GIF encoding needs.'

Write-Output "Bundled FFmpeg $Version into $Destination"
