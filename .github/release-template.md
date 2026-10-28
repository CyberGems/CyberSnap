<p align="center">
  <img src="https://raw.githubusercontent.com/CyberGems/CyberSnap/main/services/cybersnap-share/public/logo.png" width="120" alt="CyberSnap">
</p>

## 📸 CyberSnap {{VERSION}}: Release Notes

### 🚀 What's new in this release

<!-- IMPORTANT FOR AGENTS & MAINTAINERS: Always rewrite this entire section and highlights with the ACTUAL release notes for this specific new version before tagging. NEVER leave or copy previous version notes! Use 25-45 words for the summary, lead with user-facing changes, and do not repeat the app name or version. -->
<!-- changelog-summary:start -->
Customize capture previews with solid colors, then add outlines and margins to copied or saved images. The widget can follow your cursor between monitors, while clearer confirmation controls, steadier annotations and improved Pin imports smooth everyday capture.
<!-- changelog-summary:end -->

### ✨ Key Features & Highlights

- 🎨 **Capture Preview backgrounds, outlines and margins**:
  - Switch between the checkerboard and a solid preview background next to the zoom controls. Choose custom colors with HEX, RGB, HSV, palettes or screen sampling, and reset to the theme default when needed.
  - Background colors are remembered separately for light and dark themes (including grayscale), shared across capture tabs and sessions. Changing the preview background alone preserves the image's transparency.
  - Add an outline with a custom color and a thickness of 1–32 output pixels. Add a margin of 1–256 pixels per side using the chosen solid background color.
  - Outlines and margins grow outside the capture, preserving its content, and are included in saved and copied images. Tabs keep their individual effect settings, and new captures inherit the last saved choices.

- 🖼️ **Clearer Preview controls and export actions**:
  - Action buttons use aligned columns and larger labels. The compact bottom toolbar wraps when needed and shows final output dimensions, with the original dimensions available in contextual help.
  - The denser More menu adds **Copy image path** when the active tab has an existing file, and **Send to Paint** for a snapshot including the current image scale, outline and margin.
  - Maximizing and restoring Preview keeps the capture centered while preserving manual zoom.
  - Color pickers retain visible Reset and Accept actions when scrolling at high DPI. Slider tracks, editable pixel values, themed numeric inputs and a single-row standard palette make adjustments easier.

- 🧩 **A widget that can follow the cursor**:
  - Choose **Follow cursor** under the widget's Screen menu or in Settings to move it between monitors without an extra click. The choice persists across sessions and synchronizes between both places.
  - Monitor changes preserve the docked edge and position, with mixed-DPI placement. Following pauses while the panel is expanded, a menu is open, the widget is being dragged or a capture session is restoring.
  - **Follow active monitor** remains the default, and fixed-monitor choices remain available.
  - The primary capture button has a larger clickable tile, a tray-style hover background and more space above and below the capture mark.

- ✏️ **Steadier capture confirmation and annotations**:
  - Contextual confirmation guides explain the screenshot, Pin and Zoom flows. Pin and Zoom region selection keep the capture toolbar out of the way until confirmation, and retain their own capture destination.
  - Confirmation options open on click, with clearer state and menu dismissal. Selection tools stay visible, resizing grips remain steady and annotation history transitions are smoother.
  - Drag the dimension readout away from important content. Shortcut conflict checks respect the active stage instead of treating separate tool contexts as one set.

- 🔧 **Compatibility and reliability**:
  - Pin imports handle more image-format variations, including transparency, the first frame of animated GIFs, and high-bit-depth or CMYK sources.
  - About icons are synchronized from official suite repositories before release builds, with correct own-app filtering. Winget verification retries temporary GitHub read failures.
  - Automated checks cover preview output, color-picker interaction, confirmation flows, widget preference persistence, monitor placement, menus and mixed-DPI behavior.

<details>
<summary><b>🌐 Ver notas de la versión en Español</b></summary>

### 🚀 Novedades de esta versión

- **Vista previa personalizable**: alterna entre damero y fondo sólido, elige colores con HEX, RGB, HSV, paletas o cuentagotas, y vuelve al color predeterminado del tema. Los colores se recuerdan por separado para temas claros y oscuros y se comparten entre pestañas y sesiones. El fondo de Vista previa por sí solo conserva la transparencia de la imagen.
- **Delineado y margen**: añade un delineado de color y grosor configurable (1–32 píxeles de salida) y un margen de 1–256 píxeles por lado con el color del fondo sólido. Se agregan por fuera de la captura y se conservan al copiar y guardar. Cada pestaña mantiene sus ajustes; las nuevas capturas recuperan la última elección guardada.
- **Controles más claros**: botones alineados y etiquetas mayores, barra inferior compacta con dimensiones finales, menú Más más denso, **Copiar ruta de imagen** cuando existe un archivo para la pestaña y **Enviar a Paint** con escala, delineado y margen incluidos. La captura conserva el centro al maximizar y restaurar, respetando el zoom manual.
- **Pickers más cómodos**: Restablecer y Aceptar permanecen visibles al desplazar el contenido con DPI alto. Los sliders muestran su recorrido, los valores de píxeles son editables, los campos numéricos siguen el tema y la paleta estándar cabe en una fila.
- **Seguir cursor**: nueva opción en Pantalla y en Ajustes para mover el widget entre monitores sin clic adicional. Se guarda entre sesiones y se pausa mientras usas el panel, sus menús o el arrastre. **Seguir monitor activo** continúa como opción predeterminada. El botón principal tiene un área de clic y hover mayor, con más espacio vertical.
- **Confirmación y anotaciones**: guías contextuales para captura, Pin y Zoom; selección inicial sin la barra de captura; destino propio de Pin y Zoom; opciones mediante clic; herramientas visibles, grips estables, historial más fluido y etiqueta de dimensiones arrastrable. Los conflictos de atajos se revisan dentro de su etapa.
- **Compatibilidad y calidad**: mejoras en la importación de imágenes de Pin, incluidos transparencia, primer fotograma de GIF, alta profundidad de color y CMYK. Iconos oficiales de la suite sincronizados, reintentos en la verificación de winget y nuevas pruebas automáticas de exportación, menús, preferencias y múltiples monitores.

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
