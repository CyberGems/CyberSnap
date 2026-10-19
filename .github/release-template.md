<p align="center">
  <img src="https://raw.githubusercontent.com/CyberGems/CyberSnap/main/services/cybersnap-share/public/logo.png" width="120" alt="CyberSnap">
</p>

## 📸 CyberSnap {{VERSION}}: Release Notes

### 🚀 What's new in this release

<!-- Maintainer: Rewrite the marked paragraph for every release. Use 25-45 words, lead with user-facing changes, and do not repeat the app name or version. -->
<!-- changelog-summary:start -->
OCR, QR, and trimming sessions now share tabbed windows, with independent clipboard and result preferences for each capture mode. The widget handles mixed-DPI monitors more reliably. Updated .NET 10 packages include a per-user installer that preserves startup choices without administrator prompts.
<!-- changelog-summary:end -->

> **New to CyberSnap?** A fast, privacy-focused Windows toolkit for screenshots, annotation, OCR, QR scanning, screen recording, and sharing.

---

### ✨ Key Features & Highlights

- 🗂️ **Tabbed results and trimming**:
  - OCR and QR/barcode results stay together in tabbed windows.
  - MP4 and GIF trimming sessions retain independent playback, selection, and export state when switching tabs.
  - Result windows open more reliably in the foreground; capture destination changes update the confirmation dock consistently.

- 🎛️ **Independent delivery preferences**:
  - Configure clipboard delivery separately for images, scrolling captures, OCR, QR/barcodes, MP4, GIF, and colors.
  - Ordinary and scrolling captures have separate preview preferences; MP4 and GIF have separate Trimmer preferences.
  - The widget's grouped More menu and open tools stay synchronized with Settings. OCR, QR, and color tools keep a result window available when auto-copy is disabled.

- 🖥️ **More reliable capture widget**:
  - Improved placement, popup positioning, monitor following, and active-window tracking across displays with different DPI scales.
  - Corrected clipped borders and hidden edge indicators, with consistent menu checkmarks and synchronized widget preferences.

- 📦 **.NET 10 and per-user installation**:
  - Migrated to .NET 10 LTS and updated SQLite components. Both packages include the .NET runtime and FFmpeg.
  - The installer now uses `%LocalAppData%\Programs\CyberSnap` without requesting administrator privileges. Shortcuts, associations, and startup registration apply to the current user.
  - The installer startup choice is honored on first launch; later changes remain effective. Uninstalling removes startup entries and shortcuts, and installed-app registration no longer creates duplicates.
  - Installer file properties carry the release's numeric version; packaging verifies it and rejects compiler warnings.
  - **Upgrading from an all-users installation in Program Files? Uninstall that copy before using this installer.** The new installer does not migrate a machine-wide installation in place. This change does not require administrator privileges for subsequent per-user installs or uninstalls.

- ✅ **Verification**:
  - Runtime, SQLite full-text search, delivery, tabs, monitor/DPI, and real MP4/GIF export smoke checks passed.
  - A disposable Windows VM passed 88 installation, actual sign-in startup, and uninstall checks using standard accounts in English and Spanish, with UAC enabled and no elevation prompts.

<details>
<summary><b>🌐 Ver notas de la versión en Español</b></summary>

### 🚀 Novedades de esta versión

Los resultados de OCR y QR y las sesiones de recorte se agrupan en ventanas con pestañas, con preferencias independientes de portapapeles y resultados para cada modo. El widget mejora su comportamiento entre monitores con distintas escalas DPI. Los paquetes actualizados a .NET 10 incluyen un instalador por usuario que respeta la elección de inicio automático sin pedir permisos de administrador.

---

### ✨ Novedades destacadas

- 🗂️ **Resultados y recorte con pestañas**:
  - Los resultados de OCR y QR/códigos de barras se agrupan en ventanas con pestañas.
  - Las sesiones de recorte MP4 y GIF conservan su estado de reproducción, selección y exportación al cambiar de pestaña.
  - Los resultados aparecen en primer plano con mayor fiabilidad y los cambios de destino actualizan correctamente la barra de confirmación de captura.

- 🎛️ **Preferencias de entrega independientes**:
  - Configura el portapapeles por separado para imágenes, capturas con desplazamiento, OCR, QR/códigos de barras, MP4, GIF y colores.
  - Las capturas normales y con desplazamiento tienen preferencias de vista previa independientes; MP4 y GIF tienen preferencias de Recortador separadas.
  - El menú Más del widget y las herramientas abiertas se sincronizan con Configuración. OCR, QR y color mantienen disponible su ventana de resultados cuando la copia automática está desactivada.

- 🖥️ **Widget de captura más fiable**:
  - Mejoras de posición, menús emergentes, seguimiento de monitores y detección de ventana activa entre pantallas con distintas escalas DPI.
  - Correcciones de bordes recortados e indicadores ocultos en los extremos, marcas de selección coherentes y preferencias sincronizadas.

- 📦 **.NET 10 e instalación por usuario**:
  - Migración a .NET 10 LTS y actualización de SQLite. Ambos paquetes incluyen el runtime de .NET y FFmpeg.
  - El instalador usa `%LocalAppData%\Programs\CyberSnap` sin solicitar permisos de administrador. Los accesos directos, las asociaciones y el inicio automático se aplican al usuario actual.
  - Se respeta la opción de inicio automático del instalador en el primer arranque y los cambios posteriores siguen funcionando. La desinstalación elimina las entradas de inicio y los accesos directos, y ya no se duplica la entrada en Aplicaciones instaladas.
  - Las propiedades del instalador incluyen la versión numérica correcta; el empaquetado la verifica y rechaza los avisos del compilador.
  - **Si tienes una instalación para todos los usuarios en Program Files, desinstálala antes de usar este instalador.** El nuevo instalador no migra esa instalación directamente. Las siguientes instalaciones y desinstalaciones por usuario no requieren permisos de administrador.

- ✅ **Verificación**:
  - Superadas las comprobaciones de runtime, búsqueda de texto de SQLite, entrega, pestañas, monitores/DPI y exportación real de MP4/GIF.
  - Una máquina virtual desechable de Windows superó 88 comprobaciones de instalación, inicio automático al iniciar sesión y desinstalación con cuentas estándar en inglés y español, con UAC activado y sin pedir elevación.

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
