# Bundled FFmpeg

The installer and the portable zip include `ffmpeg.exe`. It is not committed to this repository. `scripts/Get-BundledFfmpeg.ps1` downloads a pinned archive during the release build, checks its SHA256, and copies three files next to `CyberSnap.exe`:

| File | Role |
|---|---|
| `ffmpeg.exe` | The encoder CyberSnap runs |
| `FFmpeg-LICENSE.txt` | Upstream license text from that archive |
| `FFmpeg-NOTICE.txt` | Version, source link, checksum, and what CyberSnap uses |

## Which build, and why

Pin: **FFmpeg 9.0.2 essentials**, Windows x64, from [GyanD/codexffmpeg 9.0.2](https://github.com/GyanD/codexffmpeg/releases/tag/9.0.2).

Upstream source: [FFmpeg commit 946fcce07b](https://github.com/FFmpeg/FFmpeg/commit/946fcce07b).

Archive SHA256: `60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba`

This build is GPLv3 because it includes [x264](https://www.videolan.org/developers/x264.html). CyberSnap's MP4 recorder calls `libx264` and AAC. GIF encoding calls `palettegen` and `paletteuse`. The trimmer and the audio waveform use the same executable. The essentials archive also contains `ffplay.exe` and `ffprobe.exe`. Those two are not shipped.

A smaller private build would drop codecs CyberSnap never calls. That binary would be ours to rebuild for every FFmpeg security fix. The essentials executable is a published, checksummed build of a known source commit, and the extra download size is about 30 MB on top of the existing installer.

## How CyberSnap finds it

`VideoRecorder.FindFfmpeg` checks these locations, in order:

1. The folder that contains `CyberSnap.exe`
2. `%AppData%\CyberSnap\ffmpeg.exe`
3. `ffmpeg.exe` on `PATH`

A developer build from `dotnet build` does not contain `ffmpeg.exe`. Recording then uses whatever FFmpeg is on `PATH`. To put the pinned binary next to a local publish:

```powershell
dotnet publish src/CyberSnap/CyberSnap.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ./publish-win64
./scripts/Get-BundledFfmpeg.ps1 -Destination ./publish-win64
```

Replacing the shipped `ffmpeg.exe` with another build is supported. The replacement has to provide `libx264`, AAC, the GIF muxer, and the `palettegen` and `paletteuse` filters.
