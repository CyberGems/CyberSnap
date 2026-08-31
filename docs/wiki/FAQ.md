# Frequently Asked Questions

General questions about CyberSnap features, configuration, and troubleshooting.

---

## General

### What is CyberSnap?
CyberSnap is a free, open-source screenshot, annotation, OCR, translation, screen-recording, and sharing tool for Windows. It combines professional-grade capture, editing, and sharing features in a single application.

### Is CyberSnap free?
Yes. CyberSnap is completely free and open source under the GPLv3 license. There are no paid features, ads, or tracking. You can help keep it free [here](https://github.com/CyberGems/CyberSnap#%EF%B8%8F-donate).

### What Windows versions are supported?
Windows 10 (build 19041) or later, including Windows 11. x64, x86, and ARM64 architectures are supported.

### How much disk space does CyberSnap need?
The application itself requires approximately 100 MB. Capture history and images require additional space based on your usage. You can configure retention limits.

---

## Capture

### What capture modes are supported?
- **Area** — Select any rectangular region
- **Active window** — Capture the focused window automatically
- **Full screen** — Capture the entire desktop (all monitors)
- **Scroll capture** — Automatically stitch a scrolling page into one image
- **MP4 recording** — Record screen to video
- **GIF recording** — Record screen to animated GIF

### How do I capture a specific window?
1. Trigger area capture
2. Hover over the target window — it will be highlighted
3. Click to capture just that window
4. Or use the dedicated **Active Window** hotkey

### Can I capture multiple monitors?
Yes. Full screen capture captures all monitors. You can also select regions spanning multiple monitors.

### Why is scroll capture not working on my application?
Scroll capture requires the target window to respond to scroll wheel events. Some applications (games, video players) don't support this. Try the standard area capture instead.

---

## Editing

### Can I edit captures from other applications?
Yes. Open any image file with CyberSnap:
- Right-click an image → **Open with CyberSnap**
- Drag and drop an image onto the CyberSnap editor
- Command line: `CyberSnap.exe --editor "path\to\image.png"`

### What file formats can I export to?
- **PNG** — Lossless, best quality
- **JPEG** — Smaller file size with adjustable quality
- **BMP** — Uncompressed bitmap

### How do I blur sensitive information?
1. Open the capture in the editor
2. Select the **Blur** tool
3. Draw over the area you want to obscure
4. The blur is permanent once saved

---

## OCR & Translation

### What languages does OCR support?
CyberSnap uses Tesseract OCR, which supports 100+ languages. The language is auto-detected from your Windows display language.

### How accurate is OCR?
Accuracy depends on image quality. For best results:
- Use high-resolution captures
- Ensure good contrast between text and background
- Avoid skewed or rotated text
- Select the correct OCR language

### Can I translate extracted text?
Yes. After OCR, click **Translate** in the OCR Result window. CyberSnap supports Google Translate and MyMemory translation providers.

### Why is OCR not detecting any text?
- The image may not contain recognizable text
- The OCR language may not match the text language
- The image quality may be too low
- Try adjusting contrast or resolution

---

## Upload & Share

### Where can I upload captures?
- **FTP/SFTP** — Your own server
- **S3-compatible** — AWS S3, MinIO, Wasabi, Backblaze
- **ImgBB** — Free image hosting
- **Imgur** — Community sharing
- **Webhook** — Custom integrations
- **CyberSnap Share** — Built-in sharing server

### Are upload credentials secure?
Yes. All credentials are encrypted with AES-256-GCM and stored locally. They never leave your machine.

### Why did my upload fail?
Common reasons:
- Invalid credentials — Check username, password, or API key
- Network issues — Check your internet connection
- File too large — Some providers have size limits
- Server timeout — Increase the timeout in settings

### Can I use CyberSnap Share without my own server?
The default CyberSnap Share server is provided by CyberGems. For self-hosting, see the [CyberSnap Share documentation](https://github.com/CyberGems/CyberSnap/tree/main/services/cybersnap-share).

---

## Troubleshooting

### Hotkeys don't work
- Check if the hotkey conflicts with another application
- Verify hotkeys are configured in Settings
- Restart CyberSnap after changing hotkeys
- Some applications (games) may capture hotkeys exclusively

### CyberSnap doesn't start
- Ensure you have .NET 9 runtime (included in installer)
- Check Windows Event Viewer for errors
- Try running as Administrator
- Check antivirus isn't blocking the application

### Captures are black or blank
- Some applications use hardware acceleration that prevents capture
- Try disabling hardware acceleration in the target application
- On laptops, ensure the dedicated GPU is used for CyberSnap

### The Capture Widget is not visible
- Check Settings → Widget → Show widget
- The widget may be behind other always-on-top windows
- Verify the widget is docked to the correct monitor edge

### High CPU usage during recording
- Reduce the recording quality or frame rate
- Use a smaller recording region
- Close unnecessary applications
- Use MP4 instead of GIF for longer recordings

---

## Privacy & Security

### Does CyberSnap collect any data?
No. CyberSnap does not collect, transmit, or store any personal data on external servers. All data remains on your local machine.

### What data is stored locally?
- Capture images and recordings
- OCR-extracted text
- Settings and preferences
- Upload credentials (encrypted)
- History database

### Can I use CyberSnap offline?
Yes. CyberSnap works fully offline. Only upload and translation features require internet access.

---

## Contributing

### How can I report a bug?
Open an issue on [GitHub Issues](https://github.com/CyberGems/CyberSnap/issues) with:
- CyberSnap version
- Windows version
- Steps to reproduce
- Expected vs actual behavior

### How can I contribute code?
1. Fork the repository
2. Create a feature branch
3. Submit a pull request
4. Describe your changes in the PR description

### How can I help with translations?
UI strings are in JSON files under `src/CyberSnap/Localization/`. Submit a PR with your translation.

### How can I donate?
See the [Donate section](https://github.com/CyberGems/CyberSnap#%EF%B8%8F-donate) on the main README.
