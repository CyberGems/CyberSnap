# Privacy — CyberSnap

CyberSnap runs on your PC and keeps your data on your PC. It has **no account, no telemetry, no analytics SDK and no ads**. It never sends your captures, recordings, OCR text or settings to its author or to anyone else.

Everything it knows (captures, recordings, OCR text or settings) stays in `%APPDATA%\CyberSnap\` (settings, logs and gallery), the save folder you choose for captures, and backups. The standard uninstaller leaves app data. The in-app `--uninstall` routine removes `%APPDATA%\CyberSnap`.

## When CyberSnap goes online

These are all the connections CyberSnap makes by itself. None of them sends your captures, recordings, OCR text or settings. Like any request on the internet, the server sees your IP address.

| When | Where | What for | How to turn it off |
|---|---|---|---|
| On startup, when enabled | `api.github.com/repos/CyberGems/CyberSnap/releases/latest` | Check for a newer version | `AutoCheckForUpdates` |

Translations contact `translation.googleapis.com` with `api.mymemory.translated.net` as fallback. Uploads go to the provider you choose (CyberSnap Share, ImgBB, Imgur, S3, FTP/SFTP or a webhook). OCR language downloads and all of these happen only when you start them.

## What CyberSnap reads on your PC

CyberSnap reads the screen area you choose to capture. That information stays on your screen. Anything that changes the system asks for confirmation first and, where possible, is backed up so it can be undone.

## Questions

Open an issue: https://github.com/CyberGems/CyberSnap/issues

For a privacy-specific question, use the [contact page](https://cybergems.org/contact). The suite-wide statement is at [cybergems.org/privacy](https://cybergems.org/privacy).
