# Guía para mantenedores

[English](maintainers.md) · **Español**

Internos de release y CI para quienes mantienen CyberSnap. Los pasos de compilación siguen en el [README](../README.es.md).

## Verificación del instalador

Ejecuta manualmente [Verify Windows installer](https://github.com/CyberGems/CyberSnap/actions/workflows/verify-installer.yml) para comprobar la instalación, el inicio automático al abrir una sesión real de Windows y la desinstalación en una VM desechable. Las comprobaciones usan cuentas estándar con las solicitudes de credenciales de UAC activadas y conservan el instalador y los registros durante siete días como artefactos de Actions, sin crear una release. El parámetro opcional `installer_run_id` reutiliza un instalador de prueba anterior únicamente si no han cambiado las fuentes del empaquetado. `scripts/Test-Installer.ps1` está pensado exclusivamente para esas VM alojadas y rechaza su ejecución local normal.

## Sincronización de los iconos del About

Antes de compilar cada release, CI actualiza los iconos del About desde los repositorios oficiales indicados en `scripts/SuiteIconSources.json`. Comprueba que todas las apps del catálogo tengan una fuente, que los PNG se decodifiquen y que cumplan los límites de tamaño y dimensiones cuadradas. Valida el conjunto completo antes de reemplazar imágenes; una fuente ausente o inválida detiene el release. El resumen del proceso muestra las URL y los hashes SHA256. El [workflow de verificación de iconos](https://github.com/CyberGems/CyberSnap/actions/workflows/verify-suite-icons.yml) comprueba el proceso sin publicar un release.

Al preparar un release localmente, ejecuta `pwsh -NoProfile -File scripts/Sync-SuiteIcons.ps1` antes de compilar y crear la etiqueta. Revisa los PNG modificados e inclúyelos en el commit de preparación del release. Añade `-Check` para detectar copias desactualizadas sin modificar archivos. Para trabajar sin conexión, `-Source Local` lee los originales de los repositorios vecinos (o de `-ProjectsRoot <carpeta>`).

La acción reutilizable para Windows de `.github/actions/sync-suite-icons` admite las otras apps mediante una configuración JSON relativa a su repositorio (entrada `config`). `destinations` indica las carpetas de iconos y `catalogs` enumera archivos y expresiones regulares cuyo primer grupo captura el identificador de la app. Los marcadores opcionales `start` / `end` limitan la búsqueda a la lista del About; `exclude` refleja los elementos que la interfaz filtra, como la propia app en un catálogo compartido. El registro compartido debe cubrir todas las apps mostradas. Cada proyecto fija la acción a un commit revisado; todas las imágenes se validan antes de modificar los destinos.
