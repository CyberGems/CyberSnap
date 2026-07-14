# CyberSnap

<!-- TODO: add real screenshots to docs/screenshots/ and replace the placeholders below -->

<p align="center">
  <img src="src/CyberSnap/Assets/CyberSnap_square.png" width="160" alt="CyberSnap logo" />
</p>

<p align="center">
  <!-- TODO: confirm license (GPL-3.0 provisional) -->
  <img src="https://img.shields.io/badge/license-GPL--3.0-blue.svg" alt="License" />
  <img src="https://img.shields.io/badge/platform-Windows%2010%2B-0078D4.svg" alt="Platform" />
  <img src="https://img.shields.io/badge/.NET-9.0-512BD4.svg" alt=".NET" />
  <img src="https://img.shields.io/badge/version-1.5.6-green.svg" alt="Version" />
</p>

**CyberSnap** is a screenshot, annotation, OCR, translation, screen-recording and sharing
tool for Windows, built by **CyberGems**. It combines a floating capture widget, an
annotation editor, multilingual text recognition (OCR), local image search, video trimming
and multi-destination upload in a single .NET 9 / WPF desktop application.

> Official site: [CyberGems.org](https://CyberGems.org) · Source: [github.com/CyberGems/CyberSnap](https://github.com/CyberGems/CyberSnap)

## Screenshots

<!-- TODO: add real images and update the paths -->
| Capture widget | Annotation editor | Gallery / History |
| --- | --- | --- |
| ![Capture widget](docs/screenshots/capture-widget.png) | ![Editor](docs/screenshots/editor.png) | ![Gallery](docs/screenshots/gallery.png) |

| OCR & translation | Settings |
| --- | --- |
| ![OCR](docs/screenshots/ocr.png) | ![Settings](docs/screenshots/settings.png) |

## Features

- **Floating Capture Widget** — a always-available on-screen widget to start any capture in
  one click, with an optional "open in editor" toggle and always-on-top support.
- **Flexible capture** — area, active window, full screen and *scroll capture* (long,
  scrollable pages stitched into one image).
- **Screen recording** — record to **MP4** or **GIF** and trim them afterwards with the
  built-in *Video Trimmer*.
- **Annotation editor** — canvases with shapes, text, image paste, rulers, custom colors and
  frames; opens automatically after each capture if you want.
- **Multilingual OCR** — extract text from images with Tesseract and search within it across
  your captures. Language is auto-detected from your Windows display language.
- **Translation** — translate OCR-extracted text via the integrated translation service.
- **Barcode / QR** — scan codes with ZXing, standalone or on top of a capture.
- **Color picker & ruler** — standalone tools for color sampling and on-screen measurement.
- **Local image search** — a local (SQLite) index that lets you search the OCR text and
  content of your captures.
- **Gallery / History** — a persistent history of captures, OCR text, codes and colors, with
  search.
- **Configurable hotkeys** — capture, OCR, recording, ruler, color picker and repeat-last-area
  actions are all reassignable.
- **Upload & share** — send images to **FTP**, **SFTP**, **S3-compatible**, **ImgBB**,
  **Imgur**, **Webhook** or **CyberSnap Share**.
- **Localization** — UI translated into 29 languages (see `src/CyberSnap/Localization`).
- **Themes & scaling** — light / dark / system-following theme and adjustable UI scale.
- **Start with Windows** — optional launch at sign-in and run in the background from the
  system tray.

## Requirements

- **OS:** Windows 10 (build 19041 / 20H2) or later.
- **Runtime:** .NET 9 Desktop Runtime (for the framework-dependent installer) or none (for a
  *self-contained* build).
- **Architectures:** `x64`, `x86`, `ARM64`.

## Installation

Download the Inno Setup installer (`CyberSnap-Setup-<version>.exe`) and follow the wizard. The
installer:

- registers the `.csnp` project-file association,
- creates Start Menu and (optionally) desktop shortcuts,
- offers to launch CyberSnap when Windows starts.

> Downloads: [github.com/CyberGems/CyberSnap/releases](https://github.com/CyberGems/CyberSnap/releases)

## Building from source

### Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- Windows 10 SDK (10.0.19041.0 or newer)
- Visual Studio 2022 (".NET desktop development" workload) or just the `dotnet` CLI

### Repository layout

```
CyberSnap/
├── src/
│   ├── CyberSnap/          # Main app (WPF, entry point)
│   └── CyberSnap.AppModel/ # Shared models and settings schemas
├── scripts/                # Utilities (e.g. upload API-key encryption)
├── CyberSnap.iss           # Inno Setup installer script
├── CyberSnap.sln           # Solution
└── LICENSE
```

### Build and publish

From the repository root:

```powershell
# Restore and build (Debug)
dotnet build src/CyberSnap/CyberSnap.csproj

# Publish a self-contained build for x64
dotnet publish src/CyberSnap/CyberSnap.csproj `
  -c Release -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -o ./publish-win64
```

Repeat the command with `-r win-x86` or `-r win-arm64` for the other architectures.

### Building the installer

The installer is built with [Inno Setup](https://jrsoftware.org/isinfo.php) from
`CyberSnap.iss`, which packages the contents of `./publish-win64`.

<!-- TODO: document the exact .iss build step if it differs -->

## Quick start

- The app lives in the **system tray**; use the **floating Capture Widget** to start quick
  captures.
- Open the **Annotation editor** with `CyberSnap.exe --editor` (or the "CyberSnap Editor"
  shortcut created by the installer).
- **Capture actions** (area, active window, full screen, scroll capture, MP4/GIF recording,
  OCR, color picker, code scan, ruler) are bound to **configurable hotkeys** in *Settings*.
- The **history** of captures, OCR text, codes and colors is browsable from the *Gallery*.

<!-- TODO: list the real default hotkeys here if desired -->

## Configuration and data

CyberSnap stores its settings and history data under the app's user storage paths
(`AppStoragePaths`). Upload provider credentials are kept in an encrypted *vault*
(AES-GCM, `DefaultCredentialVault`); out-of-the-box embedded keys are obfuscated with the
`scripts/Protect-UploadVaultKey.ps1` script.

## Localization

UI strings live in `src/CyberSnap/Localization/*.json` (29 languages, including `en.json` and
`es.json`). To add or fix a language, edit the corresponding JSON file; changes load without
recompiling.

## Privacy and security

- **Upload credentials** are encrypted locally and are not sent to third parties except the
  providers you configure.
- The **search index** and history are **local** (SQLite on the user's machine).
- OCR uses Tesseract data (`tessdata`) that may reside on the machine or in Windows.

## Troubleshooting

<!-- TODO: expand with real cases -->
- **Hotkey conflict:** if a hotkey does not respond, another program may be using it; reassign
  it in *Settings* (CyberSnap includes conflict detection).
- **OCR not working:** verify the Tesseract language data (`tessdata`) is available for the
  selected language.
- **App won't start:** make sure the .NET 9 runtime is installed, or use a *self-contained* build.

## Contributing

<!-- TODO: add contribution guide / Code of Conduct if applicable -->
Contributions are welcome. Please open an issue or pull request describing the change.

## License

<!-- TODO: confirm between MIT and GPL; the bundled LICENSE file is GPL-3.0 -->
Distributed under the terms of the **GNU General Public License v3.0**. See
[`LICENSE`](LICENSE) for details.
