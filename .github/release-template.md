<p align="center">
  <img src="https://raw.githubusercontent.com/CyberGems/CyberSnap/main/services/cybersnap-share/public/logo.png" width="120" alt="CyberSnap">
</p>

## 📸 CyberSnap {{VERSION}}: Release Notes

### 🚀 What's new in this release

<!-- Maintainer: Rewrite the marked paragraph for every release. Use 25-45 words, lead with user-facing changes, and do not repeat the app name or version. -->
<!-- changelog-summary:start -->
Zoom freezes and magnifies your screen with seamless region or full-view capture, confirmation, and annotations. Pin keeps image references floating across monitors with zoom, opacity, shadows, and borders. A reorganized widget, clearer tooltips, and unified tool shortcuts streamline everyday capture.
<!-- changelog-summary:end -->

### ✨ Key Features & Highlights

- 🔍 **Zoom, integrated with the capture workflow**:
  - Freeze the monitor under the pointer at 2× by default, pan with the mouse, and zoom from 1.25× to 4× with the wheel or `+` / `-`.
  - Press `C` or your capture hotkey for a region, or `Enter` for the full view. Refine the selection and annotate in the usual confirmation stage, then follow your normal clipboard, save, Editor, Gallery, and optional Preview preferences.
  - Canceling returns to the same Zoom view. Entering capture preserves the enlarged image without a distracting transition.
  - Configure initial zoom, smoothing, animation, and the help bar, enabled by default, under Settings → Tools → Zoom.

- 📌 **Pin to Screen for temporary image references**:
  - Pin a selected region, clipboard image, or image file; also available from Preview, Gallery, widget, and capture bar. Regions support confirmation and annotations before pinning.
  - Move references between monitors, zoom with the wheel or `+` / `-`, change opacity with `Ctrl` + wheel or the Opacity menu, copy with `Ctrl+C`, save with `Ctrl+S`, or send to the Editor.
  - Middle-click or `0` resets zoom and opacity. Hover for copy, More, and close controls; right-click for the full menu, with icons and monitor-aware placement.
  - Settings control initial scale, smoothing, always-on-top behavior, capture visibility, shadow, and border. Shadow and border default to on; the hover outline stays visible when the normal border is disabled.
  - The source chooser uses a 40% dimmed backdrop, mixed-DPI centering, and subtle outside-click feedback. Clipboard import is disabled when no image is available, with a contextual tooltip.
  - References are temporary and do not automatically save to Gallery. Menu and close lifecycle fixes prevent disposed-menu crashes.

- ⌨️ **Consistent tools and keyboard controls**:
  - One configurable global shortcut per tool, also shown in capture-bar tooltips. Existing assignments and stage-specific annotation/confirmation controls are preserved; tools remain unassigned by default.
  - `Tab` / `Shift+Tab` navigates visible capture-toolbar buttons; `Space` or `Enter` activates the focused button.
  - Settings groups OCR, Color Picker, QR, Ruler, Zoom, and Pin under Tools, removes obsolete standalone terminology, and keeps Editor shortcut cards at a readable width.

- 🧩 **A clearer widget, tray, and Settings**:
  - Nine widget actions form a 3×3 grid with a subtle tool separator; the tray pairs Zoom and Pin in aligned columns.
  - Image, MP4, and GIF cursor preferences move into their respective widget submenus and remain independent.
  - Useful two-line English/Spanish tooltips cover the widget, tray, and capture bar. Widget tips stay above the panel, and collapsing no longer flashes cyan.
  - Settings navigation, subtabs, hover states, sound toggles, and footer scrolling are refined. Capture Preview gets its own distinct, softer default sound, and the installer uses the app artwork.

- 📦 **Packages and winget**:
  - Installer and portable packages include the .NET 10 runtime and FFmpeg. Installation remains per-user without administrator prompts.
  - Release automation updates the oldest pending CyberSnap winget PR and closes duplicates instead of accumulating submissions. Availability in winget still depends on upstream review.

<details>
<summary><b>🌐 Ver notas de la versión en Español</b></summary>

### 🚀 Novedades de esta versión

- **Zoom integrado**: congela y amplía de 1.25× a 4×. `C` o el atajo de captura selecciona una región; `Enter` selecciona la vista completa. Ajusta y anota en la confirmación habitual, con Vista previa opcional. Cancelar devuelve al mismo Zoom y la transición a captura conserva la imagen ampliada. Barra de ayuda opcional, activada por defecto.
- **Fijar en pantalla (Pin)**: referencias temporales desde región, portapapeles, archivo, Vista previa, Galería, widget o barra de captura. Mueve entre monitores, amplía con rueda o `+` / `-`, cambia opacidad con `Ctrl` + rueda o su menú, copia, guarda o abre en el Editor. Clic central o `0` restablece zoom y opacidad. Sombra y borde configurables, activados por defecto; el borde al pasar el mouse siempre permanece disponible.
- **Pin más pulido**: selector con fondo atenuado al 40%, centrado entre distintas escalas y aviso sutil al hacer clic fuera. Portapapeles deshabilitado si no contiene una imagen, con tooltip dinámico. Menús con iconos que permanecen en su monitor y correcciones de los cierres inesperados al usar o cerrar la herramienta.
- **Atajos y barra de captura**: un atajo global por herramienta, conservando tus asignaciones y los controles de cada etapa. `Tab` / `Shift+Tab` recorre botones visibles; `Espacio` o `Enter` activa el enfocado. Configuración agrupa las seis herramientas y mejora el ancho de las tarjetas de atajos del Editor.
- **Widget y tray**: cuadrícula 3×3 con separación de herramientas, Zoom y Pin alineados en el tray, y ajustes de cursor independientes en los submenús de Imagen, MP4 y GIF. Tooltips útiles de dos líneas en ambos idiomas, siempre visibles sobre el widget, y sin destello cian al ocultarse.
- **Otros ajustes**: navegación y subpestañas de Configuración, hover consistente, toggles de Sonidos sin recortes, scroll separado del footer, sonido predeterminado propio para Vista previa e ilustraciones de la app en el instalador.
- **Distribución**: instalador y portable con .NET 10 y FFmpeg incluidos. La automatización mantiene actualizado el PR más antiguo de winget y cierra duplicados; su aprobación depende de los revisores de winget.

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
