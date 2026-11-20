# Privacidad — CyberSnap

CyberSnap funciona en tu PC y guarda tus datos en tu PC. **No tiene cuenta, telemetría, SDK de analítica ni anuncios.** Nunca envía tu capturas, grabaciones, texto OCR o ajustes a su autor ni a nadie más.

Todo lo que sabe (capturas, grabaciones, texto OCR o ajustes) se queda en `%APPDATA%\CyberSnap\` (ajustes, logs y galería), la carpeta que elijas para las capturas y las copias de seguridad. El desinstalador estándar conserva los datos. La rutina interna `--uninstall` elimina `%APPDATA%\CyberSnap`.

## Cuándo CyberSnap se conecta a internet

Estas son todas las conexiones que CyberSnap hace por su cuenta. Ninguna envía tu capturas, grabaciones, texto OCR o ajustes. Como en cualquier petición de internet, el servidor ve tu dirección IP.

| Cuándo | Dónde | Para qué | Cómo desactivarlo |
|---|---|---|---|
| Al iniciar, si está activado | `api.github.com/repos/CyberGems/CyberSnap/releases/latest` | Consultar una versión nueva | `AutoCheckForUpdates` |

La traducción contacta `translation.googleapis.com` con `api.mymemory.translated.net` como alternativa. Las subidas van al proveedor que elijas (CyberSnap Share, ImgBB, Imgur, S3, FTP/SFTP o un webhook). Las descargas de idiomas OCR y todo esto ocurre solo cuando tú lo inicias.

## Qué lee CyberSnap de tu PC

CyberSnap lee el área de pantalla que eliges capturar. Esa información se queda en pantalla. Todo lo que cambia el sistema pide confirmación antes y, cuando es posible, guarda una copia para poder deshacerlo.

## Dudas

Abre un issue: https://github.com/CyberGems/CyberSnap/issues

Para una pregunta concreta sobre privacidad, usa la [página de contacto](https://cybergems.org/contact). La declaración general de la suite está en [cybergems.org/es/privacy](https://cybergems.org/es/privacy).
