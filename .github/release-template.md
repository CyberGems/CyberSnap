<p align="center">
  <img src="https://raw.githubusercontent.com/CyberGems/CyberSnap/main/services/cybersnap-share/public/logo.png" width="120" alt="CyberSnap">
</p>

## 📸 CyberSnap {{VERSION}}: Release Notes

### 🚀 What's new in this release

<!-- Maintainer: Rewrite the marked paragraph for every release. Use 25-45 words, lead with user-facing changes, and do not repeat the app name or version. -->
<!-- changelog-summary:start -->
Scheduled backups now protect settings and achievements with configurable intervals, retention, and recovery copies. Backup controls are easier to read, and files use short, local-time names. Tray menus stay within the screen with vertical taskbars, and the widget follows monitors without a corner detour.
<!-- changelog-summary:end -->

> **New to CyberSnap?** A fast, privacy-focused Windows toolkit for screenshots, annotation, OCR, QR scanning, screen recording, and sharing.

---

### ✨ Key Features & Highlights

- 💾 **Scheduled backups for settings and achievements**:
  - Enabled by default with a daily schedule and three retained copies. Choose startup, daily, weekly, or monthly backups, a custom folder, and a retention limit of 1–50 copies.
  - Backups run while the app is open; missed intervals produce one catch-up copy on the next launch.
  - Create, restore, or delete copies from Settings. Files are verified before old copies are removed, and restoration first creates a separate recovery copy.
  - API keys and passwords are excluded. Restoration preserves current credentials and backup preferences. Captures and recordings are not included.

- 🗂️ **Clearer backup controls and filenames**:
  - Available copies are part of Automatic backups; manual JSON export and import have their own Manual backup section.
  - Backup files use readable local timestamps, such as `CyberSnap-backup-2026-10-20_07-39-11.json`. A short suffix prevents collisions; older backup filenames remain supported.
  - Filenames, dates, sizes, and aligned actions stay visible. The page keeps scrolling over the backup list and folder path, and the schedule status uses normal text size and contrast.

- 🖥️ **Tray and widget positioning fixes**:
  - The tray menu stays inside the monitor's working area on secondary displays with a vertical taskbar, including mixed DPI scales.
  - Following the active monitor moves the widget directly to its final docked position, eliminating the brief jump to the left edge when docked at the top.

- ✅ **Verification and packages**:
  - Release builds and focused WPF smoke checks cover backups, tray placement, and widget monitor transitions.
  - The installer and portable packages include the .NET 10 runtime and FFmpeg. The installer remains per-user and does not request administrator privileges.

<details>
<summary><b>🌐 Ver notas de la versión en Español</b></summary>

### 🚀 Novedades de esta versión

Las copias programadas protegen la configuración y los logros con frecuencia, retención y copias de recuperación configurables. El panel mejora su legibilidad y usa nombres de archivo cortos con hora local. El menú del tray permanece dentro del monitor con barras verticales y el widget cambia de pantalla sin pasar por una esquina.

### ✨ Novedades destacadas

- 💾 **Copias programadas de configuración y logros**:
  - Activadas por defecto, diariamente y con tres copias conservadas. Elige al iniciar, diaria, semanal o mensual, una carpeta personalizada y entre 1 y 50 copias.
  - Funcionan mientras la aplicación está abierta; los intervalos pendientes crean una sola copia al volver a abrirla.
  - Crea, restaura o elimina copias desde Configuración. Se verifican antes de eliminar las antiguas y se guarda una copia de recuperación independiente antes de restaurar.
  - Se excluyen las claves API y contraseñas. Al restaurar se conservan las credenciales y preferencias de respaldo actuales. No se incluyen capturas ni grabaciones.

- 🗂️ **Panel de respaldos y nombres más claros**:
  - Las copias disponibles se integran en Copias de seguridad automáticas. La exportación e importación JSON tienen su propia sección Copia de seguridad manual.
  - Los archivos usan fecha y hora local, por ejemplo `CyberSnap-backup-2026-10-20_07-39-11.json`. Un sufijo corto evita colisiones y las copias con nombres anteriores siguen siendo compatibles.
  - Se muestran nombre, fecha y tamaño, con acciones alineadas. La rueda sigue desplazando la página sobre la lista y la ruta, y las fechas de programación tienen el tamaño y contraste normales del panel.

- 🖥️ **Correcciones de posición del tray y del widget**:
  - El menú del tray permanece dentro del área útil de los monitores secundarios con barra de tareas vertical, incluso entre pantallas con distintas escalas DPI.
  - Al seguir el monitor activo, el widget va directamente a su posición final y evita el salto momentáneo al lado izquierdo cuando está anclado arriba.

- ✅ **Verificación y paquetes**:
  - La compilación Release y las comprobaciones WPF cubren respaldos, posición del tray y transiciones del widget entre monitores.
  - El instalador y el paquete portable incluyen el runtime de .NET 10 y FFmpeg. La instalación sigue siendo por usuario y no solicita permisos de administrador.

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
