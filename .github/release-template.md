<p align="center">
  <img src="https://raw.githubusercontent.com/CyberGems/CyberSnap/main/services/cybersnap-share/public/logo.png" width="120" alt="CyberSnap">
</p>

## 📸 CyberSnap {{VERSION}}: Release Notes

### 🚀 What's new in this release

<!-- IMPORTANT FOR AGENTS & MAINTAINERS: Always rewrite this entire section and highlights with the ACTUAL release notes for this specific new version before tagging. NEVER leave or copy previous version notes! Use 25-45 words for the summary, lead with user-facing changes, and do not repeat the app name or version. Keep the Spanish mirror inside the details block in sync: translate the summary and rewrite the highlights. -->
<!-- COVERAGE: list every commit since the previous tag before drafting. Give every user-facing change at least one highlight and group related commits into one. Skip internal refactors, CI, tests and dependency bumps with no visible effect; keep small visible fixes such as copy, tooltips, shortcuts, themes, icons, spacing, stability and perceptible performance. Review the draft if 15+ user-facing commits produce fewer than 6 highlights, or 40+ total commits fewer than 8. Patch releases may have 1-3 highlights or a plain maintenance-only note. -->
<!-- changelog-summary:start -->
This maintenance release makes the floating widget's hover tooltips appear reliably, trims the in-app update notes, adds the website to the Windows uninstall entry, and refreshes dependencies while keeping PDF export license-free.
<!-- changelog-summary:end -->

> **New to CyberSnap?** An all-in-one Windows capture suite with area, window and scrolling captures, MP4 and GIF recording, an editor, OCR and QR reading, and a searchable gallery.

---

### ✨ Key Features & Highlights

- **Reliable widget tooltips.** Hover help in the floating widget now appears every time: a tooltip no longer closes itself while its popup window is still being created, and hopping between adjacent buttons shows the next tip immediately instead of leaving that control without help.
- **Cleaner update notes.** The in-app update notes skip the "Before you install" guidance, because updates install automatically.
- **Clearer Windows entry and steady PDF export.** The uninstall entry in Windows Settings shows the CyberSnap website and description, and multi-page PDF export copies the first page's exact size instead of relying on a deprecated API. Dependencies are refreshed, keeping PDFsharp on its MIT-licensed release.

<details>
<summary><b>🇪🇸 Ver notas de la versión en Español</b></summary>

### 🚀 Novedades de esta versión

Esta versión de mantenimiento hace que los tooltips del widget flotante aparezcan con fiabilidad, recorta las notas de actualización dentro de la app, añade el sitio web a la entrada de desinstalación de Windows y renueva dependencias manteniendo la exportación a PDF libre de licencias.

> **¿Nuevo en CyberSnap?** Una suite de captura todo en uno para Windows: capturas de área, ventana y página desplazable, grabación en MP4 y GIF, editor, OCR y lectura de códigos QR, y una galería con búsqueda por contenido.

---

### ✨ Novedades destacadas

- **Tooltips del widget fiables.** La ayuda al pasar el puntero por el widget flotante aparece siempre: un tooltip ya no se cierra solo mientras su ventana emergente se está creando y saltar entre botones contiguos muestra la siguiente ayuda de inmediato en lugar de dejar ese control sin ella.
- **Notas de actualización más limpias.** Las notas dentro de la app omiten la guía «Antes de instalar», porque las actualizaciones se instalan automáticamente.
- **Entrada de Windows más clara y exportación a PDF estable.** La entrada de desinstalación en Configuración de Windows muestra el sitio web y la descripción de CyberSnap, y la exportación a PDF multipágina copia el tamaño exacto de la primera página en lugar de una API obsoleta. Las dependencias se renuevan, manteniendo PDFsharp en su versión con licencia MIT.

### ℹ️ Antes de instalar

- **Windows:** esta versión aún no está firmada digitalmente. SmartScreen puede avisar antes de ejecutarla: **Más información › Ejecutar de todas formas**. Cuando Smart App Control está activado, Windows rechaza programas sin firmar; consulta la [política de firma de código](https://github.com/CyberGems/.github/blob/main/CODE_SIGNING.md). Verifica la descarga con los checksums SHA256 que aparecen más abajo.

</details>

---

### ℹ️ Before you install

- **Windows:** this release is not code-signed yet. SmartScreen may ask before it runs: **More info › Run anyway**. Where Smart App Control is on, Windows refuses unsigned programs; see the [code signing policy](https://github.com/CyberGems/.github/blob/main/CODE_SIGNING.md). Verify the download with the SHA256 checksums below.

---

### 📦 Downloads & Packages

> ⬇️ **Direct download:** click a file name to download it now. Same files as in **Assets** at the bottom of this page.

| File | Description | Platform |
| :--- | :--- | :--- |
| **[`CyberSnap-Setup-{{VERSION}}.exe`](https://github.com/CyberGems/CyberSnap/releases/download/{{VERSION}}/CyberSnap-Setup-{{VERSION}}.exe)** | 🚀 **Recommended Installer** (Inno Setup with Start Menu, Desktop & Startup options) | Windows 10 / 11 (x64) |
| **[`CyberSnap-{{VERSION}}-Portable-win-x64.zip`](https://github.com/CyberGems/CyberSnap/releases/download/{{VERSION}}/CyberSnap-{{VERSION}}-Portable-win-x64.zip)** | 💼 **Portable Archive** (Extract and run without installation) | Windows 10 / 11 (x64) |

---

### 🔐 VirusTotal and SHA256

> 🛡️ The report opens on VirusTotal and does not download anything. Sometimes one or two antivirus providers flag a safe file while the rest clear it; that is usually a false alarm. Our software is threat-free and the source code is public, so anyone can check it. Compare the SHA-256 below with your download and use only the links on this page.
> 🛡️ El informe se abre en VirusTotal y no descarga nada. A veces uno o dos proveedores de antivirus marcan un archivo seguro mientras los demás lo dan por limpio; suele ser una falsa alarma. Nuestro software está libre de amenazas y el código es público, cualquiera puede comprobarlo. Compara el SHA-256 de abajo con tu descarga y usa solo los enlaces de esta página.

**Installer**: [View VirusTotal report](https://www.virustotal.com/gui/file/{{INSTALLER_HASH}})

```text
{{INSTALLER_HASH}}
```

**Portable**: [View VirusTotal report](https://www.virustotal.com/gui/file/{{PORTABLE_HASH}})

```text
{{PORTABLE_HASH}}
```

The checksums are also available as a downloadable [SHA256SUMS.txt](https://github.com/CyberGems/CyberSnap/releases/download/{{VERSION}}/SHA256SUMS.txt).

---

📜 **Full changelog:** browse every version in [CHANGELOG.md](https://github.com/CyberGems/CyberSnap/blob/main/CHANGELOG.md).

*Crafted with precision by [CyberGems](https://cybergems.org)*
