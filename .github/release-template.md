<p align="center">
  <img src="https://raw.githubusercontent.com/CyberGems/CyberSnap/main/services/cybersnap-share/public/logo.png" width="120" alt="CyberSnap">
</p>

## 📸 CyberSnap {{VERSION}}: Release Notes

### 🚀 What's new in this release

<!-- Maintainer: Rewrite the marked paragraph for every release. Use 25-45 words, lead with user-facing changes, and do not repeat the app name or version. -->
<!-- changelog-summary:start -->
The setup wizard is overhauled with capture presets and a smarter hotkey step, the trimmer gains a media properties dialog, editor copy splits image and file targets, and the OCR window gets a refined copy row with global shortcuts.
<!-- changelog-summary:end -->

> **New to CyberSnap?** A fast, privacy-focused Windows toolkit for screenshots, annotation, OCR, QR scanning, screen recording, and sharing.

---

### ✨ Key Features & Highlights

- 🧙 **Setup wizard, rebuilt**:
  - After-capture presets (Basic / Intermediate / Advanced) with a live result preview, in the wizard and in Settings.
  - Smarter Print Screen step: names the real occupant, warns only when relevant, and refuses taken keys instead of saving dead hotkeys.
  - Sober sidebar, full translations, clickable steps.

- 🎬 **Video trimmer levels up**:
  - New Properties dialog: container, codecs, resolution, fps, bitrate, audio and trim selection via ffprobe.
  - Permanent filmstrip and waveform (flat line when there is no audio), aspect-matched thumbnails, recentered preview.
  - Copy honors mute, wrapped tooltips, livelier Cut hover.

- 📋 **Copy that goes where you paste**:
  - Editor Copy image (Photoshop-friendly) vs Copy file / name / path, grouped in a submenu in both menus, with Ctrl+C / Ctrl+Shift+C.
  - OCR window: copy row with auto-copy toggle, Ctrl+Enter and Ctrl+Shift+C badges, no layout jumps.

- 📦 **Distribution**:
  - Winget pipeline: every release now ships a validated manifest and submits to winget-pkgs automatically.
  - Spanish README.

<details>
<summary><b>🌐 Ver notas de la versión en Español</b></summary>

### 🚀 Novedades de esta versión

El asistente se renueva con preajustes de captura y paso de atajos más inteligente, el recortador estrena diálogo de propiedades, la copia del editor separa imagen y archivo, y la ventana OCR refina su fila de copiado con atajos globales.

---

### ✨ Novedades destacadas

- 🧙 **Asistente renovado**:
  - Preajustes tras la captura (Básico / Intermedio / Avanzado) con vista previa del resultado, en el asistente y en Configuración.
  - Paso de Print Screen más inteligente: nombra al ocupante real, avisa solo cuando importa y rechaza teclas ocupadas.
  - Lateral sobrio, traducción completa, pasos clicables.

- 🎬 **Recortador mejorado**:
  - Nuevo diálogo de Propiedades: contenedor, códecs, resolución, fps, bitrate, audio y selección (vía ffprobe).
  - Tira y forma de onda permanentes (línea plana sin audio), miniaturas según aspecto, vista recentrada.
  - Copiar respeta el silencio, tooltips ajustados, mejor hover en Cortar.

- 📋 **Copiar donde pegas**:
  - Copiar imagen (compatible con Photoshop) frente a copiar archivo / nombre / ruta, en submenú en ambos menús, con Ctrl+C / Ctrl+Shift+C.
  - Ventana OCR: fila de copiado con auto-copiado, insignias Ctrl+Enter y Ctrl+Shift+C, sin saltos.

- 📦 **Distribución**:
  - Pipeline de Winget: cada release genera un manifiesto validado y lo envía a winget-pkgs automáticamente.
  - README en español.

</details>

---

### 📦 Downloads & Packages

> ⬇️ **Direct download:** click a file name to download it now. Same files as in **Assets** at the bottom of this page.

| File | Description | Platform |
| :--- | :--- | :--- |
| **[`CyberSnap-Setup-{{VERSION}}.exe`](https://github.com/CyberGems/CyberSnap/releases/download/{{VERSION}}/CyberSnap-Setup-{{VERSION}}.exe)** | 🚀 **Recommended Installer** (Inno Setup with Start Menu, Desktop & Startup options) | Windows 10 / 11 (x64) |
| **[`CyberSnap-{{VERSION}}-Portable-win-x64.zip`](https://github.com/CyberGems/CyberSnap/releases/download/{{VERSION}}/CyberSnap-{{VERSION}}-Portable-win-x64.zip)** | 💼 **Portable Archive** (Extract and run without installation) | Windows 10 / 11 (x64) |

---

### VirusTotal and SHA256

- **Installer**: [View VirusTotal report](https://www.virustotal.com/gui/file/{{INSTALLER_HASH}}), SHA256: `{{INSTALLER_HASH}}`
- **Portable archive**: [View VirusTotal report](https://www.virustotal.com/gui/file/{{PORTABLE_HASH}}), SHA256: `{{PORTABLE_HASH}}`

---

*Crafted with precision by [CyberGems](https://cybergems.org)*
