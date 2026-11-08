<p align="center">
  <img src="https://raw.githubusercontent.com/CyberGems/CyberSnap/main/services/cybersnap-share/public/logo.png" width="120" alt="CyberSnap">
</p>

## 📸 CyberSnap {{VERSION}}: Release Notes

### 🚀 What's new in this release

<!-- IMPORTANT FOR AGENTS & MAINTAINERS: Always rewrite this entire section and highlights with the ACTUAL release notes for this specific new version before tagging. NEVER leave or copy previous version notes! Use 25-45 words for the summary, lead with user-facing changes, and do not repeat the app name or version. -->
<!-- changelog-summary:start -->
Achievements now show a progress start date, allow a clean reset, and persist independently from general settings. Gallery opens on its own, while the editor, multi-monitor scaling, capture recovery, widget placement, and recording metadata handling receive broad refinements.
<!-- changelog-summary:end -->

### ✨ Key Features & Highlights

- 🏆 **Achievement progress**:
  - See the date achievement tracking began and reset progress to start a new period.
  - Progress is stored separately from general settings. Existing history migrates once, and a reset does not import it again.

- 🖼️ **A more flexible Gallery and Editor**:
  - Open the Gallery independently from the Editor.
  - Editor controls and captions scale together across monitors, the palette remains visible at high DPI, and the zoom percentage resets the view.
  - The compact zoom controls, title actions, typography, icons and window chrome receive visual and interaction refinements.

- 🖥️ **More consistent multi-monitor behavior**:
  - Window scaling follows the selected UI scale and maintains physical size when moving between monitors with different DPI settings.
  - Widget, tray, notification, color picker and tooltip placement responds more reliably to monitor changes.
  - Optionally hide the floating widget during capture; it stays hidden through capture transitions and returns without flicker.

- 📸 **Steadier capture and recording**:
  - Capture overlays recover from paint failures, retry black frames once, and keep the capture toolbar hidden while selecting a region.
  - Preview actions and scrollbars have clearer spacing, and the widget no longer interrupts capture or menu interactions.
  - Release packages include `ffprobe.exe`; recording duration detection can also use FFmpeg tools available on `PATH`.

- 🔧 **Reliability fixes**:
  - Dialogs and floating windows retain their correct size and placement when Windows misses a monitor DPI update.
  - Single-instance cleanup releases only a mutex owned by the current process.

<details>
<summary><b>🌐 Ver notas de la versión en Español</b></summary>

### 🚀 Novedades de esta versión

El progreso de logros ahora muestra la fecha de inicio y permite reiniciar para comenzar un período nuevo. La Galería se abre sin el Editor, y se mejoran el escalado entre monitores, la confiabilidad de las capturas, la ubicación del widget y la lectura de metadatos de grabaciones.

- **Progreso de logros**: consulta cuándo comenzó el seguimiento y reinícialo para comenzar una etapa nueva. Se conserva aparte de la configuración general; el historial existente se migra una sola vez y no se vuelve a importar al reiniciar.
- **Galería y Editor más flexibles**: abre la Galería sin el Editor. Los controles y textos del Editor se escalan juntos entre monitores, la paleta permanece visible con DPI alto y el porcentaje de zoom restablece la vista. También se ajustan los controles compactos, las acciones de título, la tipografía, los iconos y el marco de las ventanas.
- **Mejoras entre monitores**: el escalado sigue el ajuste de interfaz y conserva mejor el tamaño físico al mover ventanas entre pantallas con distinto DPI. Widget, bandeja, notificaciones, selector de color y tooltips se ubican con más precisión. Puedes ocultar el widget durante las capturas; permanece oculto durante las transiciones y vuelve sin destellos.
- **Capturas y grabaciones más estables**: las superposiciones se recuperan de fallos de dibujo y reintentan una vez los fotogramas negros. La barra permanece oculta mientras seleccionas una región. Se mejora el espaciado de acciones y barras de desplazamiento de Vista previa y el widget ya no interfiere con menús o capturas.
- **Duración de medios**: los paquetes incluyen `ffprobe.exe`; la detección de duración también puede usar herramientas FFmpeg de `PATH`.
- **Correcciones de confiabilidad**: ventanas y diálogos conservan mejor tamaño y ubicación si Windows omite un cambio de DPI. La aplicación solo libera el mutex de instancia única si el proceso es su propietario.

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

**Installer**: [View VirusTotal report](https://www.virustotal.com/gui/file/{{INSTALLER_HASH}})

```text
{{INSTALLER_HASH}}
```

**Portable archive**: [View VirusTotal report](https://www.virustotal.com/gui/file/{{PORTABLE_HASH}})

```text
{{PORTABLE_HASH}}
```

---

*Crafted with precision by [CyberGems](https://cybergems.org)*
