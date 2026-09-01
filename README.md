<h1 align="center">CyberSnap — Screenshot & Screen Recorder</h1>

<p align="center">
  <strong>A full-featured screenshot, annotation, OCR, and screen-recording tool</strong> — capture anything, edit everything, and share anywhere. Built with .NET 9 and WPF.
</p>

<p align="center">
  <a href="https://github.com/CyberGems/CyberSnap/releases/latest">
    <img src="https://img.shields.io/badge/⚡_Download_Latest_Release-(Windows_64--bit)-0047B3?style=for-the-badge&logo=windows&logoColor=white" alt="Download Latest Release" />
  </a>
  <a href="https://github.com/CyberGems/CyberSnap/releases">
    <img src="https://img.shields.io/badge/All_Releases-Changelog-18181B?style=for-the-badge&logo=github&logoColor=white" alt="All Releases" />
  </a>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/license-GPL--3.0-blue.svg" alt="License" />
  <img src="https://img.shields.io/badge/platform-Windows%2010%2B-0078D4.svg?logo=windows&logoColor=white" alt="Platform" />
  <img src="https://img.shields.io/badge/version-1.12.0-00F0FF.svg" alt="Version" />
  <img src="https://img.shields.io/badge/.NET-9-512BD4.svg?logo=dotnet&logoColor=white" alt=".NET" />
  <a href="https://github.com/CyberGems/CyberSnap/wiki"><img src="https://img.shields.io/badge/%F0%9F%93%96_Wiki-Documentation-222222?style=flat-square&logo=github&logoColor=white" alt="Wiki" /></a>
</p>

A full-featured **screenshot, annotation, OCR, translation, screen-recording and sharing tool** for Windows. CyberSnap combines a floating capture widget, an annotation editor, multilingual text recognition, local image search, video trimming, and multi-destination upload — all in a single .NET 9 / WPF desktop application.

*Free and open source (GPLv3) — no ads, no tracking, and no data collection. Just enjoy it.*

---

## 📸 Why CyberSnap?

Most screenshot tools either do too little or bury features behind a paywall. CyberSnap gives you **professional-grade capture, editing, and sharing** — including OCR, translation, and 7 upload destinations — all in a free, open-source package with a polished cyberpunk aesthetic.

| Need | Solution |
|---|---|
| Capture anything | Area, window, full screen, scroll capture, MP4/GIF recording |
| Edit and annotate | Full-featured editor with shapes, text, rulers, colors, frames |
| Extract text from images | Multilingual OCR with Tesseract + integrated translation |
| Find past captures | Local SQLite index with full-text search across OCR content |
| Share anywhere | FTP, SFTP, S3, ImgBB, Imgur, Webhook, or CyberSnap Share |
| Work efficiently | Configurable hotkeys, floating widget, auto-start, system tray |

---

## ✨ Key Features

### 📷 Capture
- **Floating Capture Widget** — Always-available on-screen widget for one-click capture
- **Flexible Capture Modes** — Area, active window, full screen, and scroll capture (long pages stitched into one image)
- **Screen Recording** — Record to MP4 or GIF with built-in video trimming
- **Precision Tools** — Crosshair guides, capture magnifier, and smart window detection
- **Scroll Capture** — Automatically stitches long scrollable pages into a single image

### ✏️ Annotation Editor
- **Rich Canvas** — Shapes, text, image paste, rulers, custom colors, and frames
- **Auto-Open** — Opens automatically after each capture (configurable)
- **Undo/Redo** — Configurable history limit (1–200 steps)
- **Resize Handles** — Scale content or extend canvas
- **Pan Mode** — With optional object lock

### 🔤 OCR & Translation
- **Multilingual OCR** — Extract text from images with Tesseract
- **Language Auto-Detection** — Based on Windows display language
- **Integrated Translation** — Translate OCR-extracted text with configurable source/target languages
- **Local Search** — Full-text search across all OCR content in your capture history

### 📊 Gallery & History
- **Persistent History** — Captures, OCR text, barcodes, and colors with configurable retention
- **Search** — Find past captures by content, OCR text, or metadata
- **Click Actions** — Open in editor, copy to clipboard, or open in default viewer
- **Auto-Indexing** — SQLite local index with configurable search sources

### 📤 Upload & Share
- **7 Destinations** — FTP, SFTP, S3-compatible, ImgBB, Imgur, Webhook, or CyberSnap Share
- **Encrypted Credentials** — AES-GCM encryption for upload provider credentials
- **Configurable Format** — PNG or JPEG with quality control
- **Post-Upload Actions** — Open URL in browser after successful upload

### 🛠️ Standalone Tools
- **Color Picker** — Sample any color on screen
- **Ruler** — On-screen measurement tool
- **Barcode / QR Scanner** — Scan codes standalone or on top of a capture

### 🖥️ Desktop Integration
- **System Tray** — Runs in background with custom context menu
- **Configurable Hotkeys** — Capture, OCR, recording, ruler, color picker, repeat-last-area
- **Auto-Start** — Launch at Windows sign-in
- **Auto-Update** — Built-in updater with toast notifications
- **Setup Wizard** — First-run configuration assistant

### 🎨 Customization
- **29 Languages** — Full UI localization (including English and Spanish)
- **3 Themes** — Light, dark, and system-following
- **Adjustable UI Scale** — Adapt to any display

---

## 🛠️ Tech Stack & Architecture

- **Platform:** Windows 10 (build 19041) or later — x64, x86, ARM64
- **Framework:** .NET 9 + WPF (PerMonitorV2 high DPI)
- **Database:** SQLite (local history and search index)
- **Capture:** DirectX via Vortice.Direct2D1 / Vortice.Direct3D11
- **OCR:** Tesseract
- **Barcode:** ZXing.Net
- **Installer:** Inno Setup

```
CyberSnap/
├── src/
│   ├── CyberSnap/              Main app (WPF, entry point)
│   │   ├── App.xaml/.cs        Application definition & main entry
│   │   ├── Capture/            Capture overlay, recording, scrolling capture
│   │   ├── Services/           Business logic services
│   │   │   ├── Upload/         Upload providers (FTP, SFTP, S3, ImgBB, Imgur, Webhook)
│   │   │   ├── History/        History management
│   │   │   ├── HotkeyService.cs
│   │   │   ├── OcrService.cs
│   │   │   ├── TranslationService.cs
│   │   │   └── SettingsService.cs
│   │   ├── UI/                 Windows and controls
│   │   │   ├── Editor/         Annotation editor
│   │   │   ├── Settings/       Settings window
│   │   │   ├── History/        Gallery/history window
│   │   │   └── Share/          Share dialogs
│   │   ├── Localization/       29 language JSON files
│   │   ├── Models/             Data models
│   │   ├── Helpers/            Utility helpers
│   │   └── Native/             Native interop
│   └── CyberSnap.AppModel/     Shared models and settings schemas
├── scripts/                    Utilities (upload API-key encryption)
├── CyberSnap.iss               Inno Setup installer script
└── CyberSnap.sln               Solution
```

---

## 🚀 Getting Started

### Install

Download the [Inno Setup installer](https://github.com/CyberGems/CyberSnap/releases/latest) and follow the wizard. The installer registers `.csnp` file associations, creates shortcuts, and offers to start with Windows.

### Build from Source

**Prerequisites:** .NET 9 SDK, Windows 10 SDK (10.0.19041.0+), Visual Studio 2022 or `dotnet` CLI

```powershell
# Build (Debug)
dotnet build src/CyberSnap/CyberSnap.csproj

# Publish self-contained for x64
dotnet publish src/CyberSnap/CyberSnap.csproj `
  -c Release -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -o ./publish-win64
```

Build the installer with [Inno Setup](https://jrsoftware.org/isinfo.php) from `CyberSnap.iss`.

---

## ⌨️ Keyboard Shortcuts

All capture actions are bound to **configurable hotkeys** in Settings:

| Action | Default Hotkey |
|---|---|
| Capture area | Configurable |
| Repeat last area | Configurable |
| Active window | Configurable |
| Full screen | Configurable |
| Scroll capture | Configurable |
| Record MP4 | Configurable |
| Record GIF | Configurable |
| OCR | Configurable |
| Color picker | Configurable |
| QR & Barcode scan | Configurable |
| Ruler | Configurable |

Open the annotation editor directly: `CyberSnap.exe --editor`

---

## ❓ Frequently Asked Questions

### What capture modes does CyberSnap support?

Area selection, active window, full screen, repeat area, scroll capture (long pages stitched into one image), MP4 recording, and GIF recording.

### How does OCR work?

CyberSnap uses Tesseract to extract text from captured images. The language is auto-detected from your Windows display language, and you can search across all OCR text in your capture history. Extracted text can also be translated via the integrated translation service.

### Where can I upload captures?

CyberSnap supports FTP, SFTP, S3-compatible storage, ImgBB, Imgur, Webhook, and CyberSnap Share. Upload credentials are encrypted locally with AES-GCM.

### Where is my data stored?

All captures, history, and settings are stored locally on your machine. Upload provider credentials are kept in an encrypted vault. No data is sent to third parties except the upload providers you explicitly configure.

### How do I change hotkeys?

Go to **Settings** and navigate to the hotkeys section. CyberSnap includes conflict detection to warn you if a hotkey is already in use by another application.

### Can I use CyberSnap in my language?

Yes. CyberSnap supports 29 languages including English, Spanish, German, French, Japanese, Korean, Chinese (Simplified and Traditional), and many more. UI strings are in JSON files under `src/CyberSnap/Localization/` and can be edited without recompiling.

---

## 🤝 Contributing

Contributions are welcome. Please open an issue describing the change before starting large work, and submit pull requests against the main branch.

## 🙏 Acknowledgments

Originally forked from [OddSnap](https://github.com/jasperdevs/odd-snap) by [jasperdevs](https://github.com/jasperdevs). CyberSnap has since been extensively rewritten and expanded by [CyberGems](https://cybergems.org/).

This project also builds on open-source components including Tesseract OCR, ZXing, SQLite, and Inno Setup — thanks to their authors and maintainers.

---

## ❤️ Donate

**CyberSnap** is one of the gems in [CyberGems](https://github.com/CyberGems#-all-apps--repositories), a personal suite I've spent thousands of hours building and refining for my own use. I've decided to share the entire collection with the world — completely free and open-source.

If you'd like to support this work, a donation would mean a lot. Thank you! 🙏

<p align="center">
  <a href="https://www.paypal.com/donate/?hosted_button_id=M4PY3UPJA5Y6Q"><img src="https://img.shields.io/badge/Donate-PayPal-0070BA?style=for-the-badge&logo=paypal" alt="Donate via PayPal" /></a>
  <a href="https://ko-fi.com/cybergems"><img src="https://img.shields.io/badge/Support_me_on_Ko--fi-FF5E5B?style=for-the-badge&logo=ko-fi&logoColor=white" alt="Support me on Ko-fi" /></a>
  <a href="https://buymeacoffee.com/cybergems"><img src="https://img.shields.io/badge/Buy%20Me%20a%20Coffee-FFDD00?style=for-the-badge&logo=buy-me-a-coffee&logoColor=black" alt="Buy Me a Coffee" /></a>
</p>

<div align="center">

<details>
<summary><b>Crypto donations (BTC, ETH, USDT, LTC) — click to view addresses</b></summary>

| Asset | Address | QR |
|---|---|---|
| **BTC** | <pre><code>bc1q5mxzz05nmvsheqzx7970euswta3fksxzcfzag4</code></pre> | <img src="src/CyberSnap/Assets/Donate/qr-btc.png" width="90" height="90" alt="BTC QR" /> |
| **ETH** | <pre><code>0x79b703Ec0f77493679Fcd280aF3b983E20c580B8</code></pre> | <img src="src/CyberSnap/Assets/Donate/qr-eth.png" width="90" height="90" alt="ETH QR" /> |
| **USDT (ERC20 / BEP20)** | <pre><code>0x79b703Ec0f77493679Fcd280aF3b983E20c580B8</code></pre> | <img src="src/CyberSnap/Assets/Donate/qr-eth.png" width="90" height="90" alt="USDT QR" /> |
| **USDT (TRC20)** | <pre><code>TSVbSk1HSyZ1NprCnAYiw56ECwXgH887mD</code></pre> | <img src="src/CyberSnap/Assets/Donate/qr-usdt-tron.png" width="90" height="90" alt="USDT TRC20 QR" /> |
| **LTC** | <pre><code>LWGnEHgcFCE2BRkzLnsdPDD8Y8ZeDK577X</code></pre> | <img src="src/CyberSnap/Assets/Donate/qr-ltc.png" width="90" height="90" alt="LTC QR" /> |

> ⚠️ Send only the selected asset on the indicated network. Using the wrong network will result in permanent loss of funds.

</details>

</div>

---

## 📄 License

CyberSnap is distributed under the terms of the GNU General Public License v3.0. See [`LICENSE`](LICENSE) for the full license text.

---

<div align="center" style="background:#0D0F17; border:1px solid rgba(0,255,255,0.12); border-radius:12px; padding:28px 20px; margin-top:32px;">

### Thanks for using CyberSnap! 🎉

Made by [**CyberGems**](https://cybergems.org)

</div>
