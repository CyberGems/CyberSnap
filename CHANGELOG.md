# Changelog

All notable changes to CyberSnap are documented here.

## [1.25.1] - 2026-11-15

### 🇺🇸 English

- Show the widget's hover help for the next control immediately when the pointer moves between adjacent buttons, instead of sometimes leaving it without a tooltip.
- Restore PDF export, which failed with a "The loaded license is not valid" error after a library update by returning to the license-free PDFsharp release.

### 🇪🇸 Español

- Muestra la ayuda al pasar el ratón por el siguiente control del widget en cuanto el puntero se mueve entre botones contiguos, en lugar de dejarlo a veces sin tooltip.
- Restaura la exportación a PDF, que fallaba con el error "La licencia cargada no es válida" tras una actualización de la biblioteca, volviendo a la versión de PDFsharp sin licencia.

## [1.25.0] - 2026-11-11

### 🇺🇸 English

- Add Screen, Window, and Repeat to the floating widget, along with shortcuts to Gallery, Editor, and Achievements.
- Let the widget hide each of its Capture modes, Recording, Tools, and Gallery and editor sections independently, remember those choices, and fit the visible content.
- Include a file alongside copied images so captures can be pasted into File Explorer. This also covers Preview's Save As with auto-copy enabled, Gallery's Copy image, and the Editor's current canvas with unsaved edits.
- Add contextual wiki help for Achievements in English and Spanish.
- Open widget submenus on demand, keep tooltips clear of controls, and render More menu icons at the display's pixel size.
- Hide the widget on the display occupied by a fullscreen app and keep it available on the other displays.
- Rename the Dark theme to Gray and apply its palette to checkerboards, Color Picker, and Ruler.
- Expand the About description and give it the full width of the card.
- Capture the previously active app when choosing Window from the tray or widget.
- Prepare the MP4 and GIF recording bar during region selection and show it at its final position on mixed-DPI displays.
- Remember Capture Preview's maximized state and play the capture sound only once when closing multiple tabs together.

### 🇪🇸 Español

- Añade Pantalla, Ventana y Repetir al widget flotante, junto con accesos a Galería, Editor y Logros.
- Permite al widget ocultar por separado sus secciones de Modos de captura, Grabación, Herramientas y Galería y editor, recordar esas elecciones y ajustarse al contenido visible.
- Incluye un archivo junto a las imágenes copiadas para pegar capturas en el Explorador de archivos. También cubre Guardar como de Vista previa con la copia automática activada, Copiar imagen de la Galería y el lienzo actual del Editor con cambios sin guardar.
- Añade ayuda contextual de la wiki para Logros en inglés y español.
- Abre los submenús del widget al usarlos, mantiene los tooltips lejos de los controles y dibuja los iconos del menú Más al tamaño de píxeles del monitor.
- Oculta el widget en la pantalla ocupada por una app a pantalla completa y lo mantiene disponible en las demás pantallas.
- Renombra el tema Oscuro como Gris y aplica su paleta a dameros, Selector de color y Regla.
- Amplía la descripción de Acerca de y le da todo el ancho de la tarjeta.
- Captura la app activa anterior al elegir Ventana desde la bandeja o el widget.
- Prepara la barra de grabación de MP4 y GIF durante la selección de región y la muestra en su posición final en pantallas con DPI mixto.
- Recuerda el estado maximizado de Vista previa de captura y reproduce el sonido de captura una sola vez al cerrar varias pestañas juntas.

## [1.24.2] - 2026-11-09

### 🇺🇸 English

- Position notifications from the first display using the target monitor's work area in physical pixels.
- Keep Settings and Gallery centered on the monitor where they were opened, even while layout adapts to its DPI.
- Prevent partial uninstalls while CyberSnap is running by prompting the user to close it before removing files.
- Preserve both FFmpeg executables when copying an installation payload.
- Align update actions to the right in About, with **Update now** last.
- Use a softer hover for interactive window titles, with a transparent background at rest.
- Refresh the installer header and sidebar with the current CyberSnap artwork.
- Launch the standard Debug executable directly for faster local UI checks with the same per-monitor DPI behavior.

### 🇪🇸 Español

- Posiciona las notificaciones desde la primera pantalla usando el área de trabajo del monitor de destino en píxeles físicos.
- Mantiene Configuración y Galería centradas en el monitor donde se abrieron, aunque el diseño se adapte a su DPI.
- Evita desinstalaciones parciales mientras CyberSnap está en ejecución pidiendo cerrar la app antes de eliminar archivos.
- Conserva ambos ejecutables de FFmpeg al copiar un paquete de instalación.
- Alinea las acciones de actualización a la derecha en Acerca de, con **Actualizar ahora** al final.
- Usa un hover más suave para los títulos de ventana interactivos, con fondo transparente en reposo.
- Renueva la cabecera y la barra lateral del instalador con la imagen actual de CyberSnap.
- Abre directamente el ejecutable Debug estándar para comprobaciones locales más rápidas de la interfaz, con el mismo comportamiento de DPI por monitor.

## [1.24.1] - 2026-11-08

### 🇺🇸 English

- Achievement progress now shows its start date and can be reset for a fresh run. Progress is stored separately from general settings, with existing history migrated once.
- The Gallery can be opened independently of the Editor.
- An option can hide the floating widget during captures.
- Editor controls, zoom actions, and the annotation palette adapt more consistently to display scaling.
- Window scaling and placement respond more reliably when moving between monitors with different DPI settings.
- Capture overlays recover from paint issues, retry black frames once, and keep the capture toolbar hidden during region selection.
- Preview actions and scrollbars have clearer spacing, while the widget restores more smoothly after captures.
- Recording duration detection can use bundled or system FFmpeg tools, and release packages now include `ffprobe.exe`.
- Tray menus, notifications, color picker windows, tooltips, and dialogs retain their size and placement across monitor DPI changes.
- The widget no longer reappears prematurely during capture transitions, and its topmost behavior no longer interferes with menus or captures.
- Single-instance cleanup only releases a mutex owned by the current process.
- The CyberSnap Share logo now matches the canonical app logo for release branding validation.

### 🇪🇸 Español

- El progreso de logros muestra ahora su fecha de inicio y se puede reiniciar para empezar de cero. Se guarda aparte de la configuración general y el historial existente se migra una sola vez.
- La Galería se puede abrir sin depender del Editor.
- Una opción permite ocultar el widget flotante durante las capturas.
- Los controles del Editor, las acciones de zoom y la paleta de anotaciones se adaptan con más consistencia al escalado de pantalla.
- El escalado y la ubicación de ventanas responden con más fiabilidad al moverse entre monitores con diferente DPI.
- Las superposiciones de captura se recuperan de fallos de dibujo, reintentan una vez los fotogramas negros y mantienen la barra de captura oculta durante la selección de región.
- Las acciones y barras de desplazamiento de Vista previa tienen un espaciado más claro y el widget se restaura con más suavidad tras las capturas.
- La detección de duración de grabaciones puede usar herramientas FFmpeg incluidas o del sistema, y los paquetes de la versión ahora incluyen `ffprobe.exe`.
- Los menús de la bandeja, las notificaciones, las ventanas del selector de color, los tooltips y los diálogos conservan su tamaño y ubicación ante cambios de DPI del monitor.
- El widget ya no reaparece antes de tiempo durante las transiciones de captura y su comportamiento de ventana superior ya no interfiere con menús ni capturas.
- La limpieza de instancia única solo libera un mutex propiedad del proceso actual.
- El logo de CyberSnap Share coincide con el logo canónico de la app para la validación de marca de la versión.

## [1.23.0] - 2026-10-27

### 🇺🇸 English

- Checkerboard or solid capture-preview backgrounds, custom colors per theme, and a reset to the theme default.
- Configurable outlines (1–32 output pixels) and solid-color margins (1–256 pixels per side), included when copying or saving captures.
- Per-tab image-path copying and export to Paint from Preview's More menu.
- Optional cursor-following widget placement across monitors, available in the widget menu and Settings and remembered between sessions.
- Contextual confirmation guides for screenshots, Pin and Zoom.
- Preview action layout, label readability, compact menus, final image-size display and toolbar wrapping.
- Color-picker scrolling at high DPI, fixed Reset/Accept actions, slider interaction, editable pixel values and the standard palette layout.
- Widget capture-button hit area and hover background, with more vertical space around the capture mark.
- Confirmation options, selection grips, draggable dimension readout, annotation history and stage-specific shortcut validation.
- Pin image imports, including transparency, the first frame of animated GIFs, and high-bit-depth or CMYK sources.
- About icon synchronization, winget verification retries and automated capture/widget regression coverage.
- Fixed the capture preview losing its center after maximizing and restoring the window, while preserving manual zoom.
- Fixed Pin and Zoom confirmation inheriting an ordinary screenshot destination.
- Fixed capture toolbars appearing during Pin/Zoom region selection, selection toolbar visibility and repeated More-menu reopening.

### 🇪🇸 Español

- Fondos de Vista previa de captura a damero o color sólido, colores personalizados por tema y restablecimiento al valor predeterminado del tema.
- Delineados configurables (1–32 píxeles de salida) y márgenes de color sólido (1–256 píxeles por lado), incluidos al copiar o guardar capturas.
- Copia de la ruta de imagen por pestaña y exportación a Paint desde el menú Más de Vista previa.
- Colocación opcional del widget siguiendo el cursor entre monitores, disponible en el menú del widget y en Configuración y recordada entre sesiones.
- Guías contextuales de confirmación para capturas, Pin y Zoom.
- Diseño de las acciones de Vista previa, legibilidad de etiquetas, menús compactos, muestra del tamaño final de imagen y ajuste de la barra de herramientas.
- Desplazamiento del selector de color con DPI alto, acciones Restablecer/Aceptar fijas, interacción con los sliders, valores de píxeles editables y la disposición de la paleta estándar.
- Área de clic del botón de captura del widget y fondo al pasar el cursor, con más espacio vertical alrededor de la marca de captura.
- Opciones de confirmación, grips de selección, etiqueta de dimensiones arrastrable, historial de anotaciones y validación de atajos por etapa.
- Importaciones de imágenes en Pin, incluidas transparencia, primer fotograma de GIF animados y fuentes de alta profundidad de bits o CMYK.
- Sincronización de iconos de Acerca de, reintentos de verificación de winget y cobertura automatizada de regresión de captura/widget.
- Corregida la pérdida del centrado de la Vista previa de captura al maximizar y restaurar la ventana, conservando el zoom manual.
- Corregida la confirmación de Pin y Zoom que heredaba un destino de captura ordinario.
- Corregidas las barras de captura que aparecían durante la selección de región en Pin/Zoom, la visibilidad de la barra de selección y la reapertura repetida del menú Más.

## [1.22.0] - 2026-10-24

### 🇺🇸 English

- Zoom joins the capture workflow: freeze the screen at 2x by default, pan and zoom from 1.25x to 4x, capture a region or the full view, and return to the same Zoom view after canceling, with the enlarged image preserved when entering capture.
- Pin to Screen keeps temporary image references floating across monitors: pin a region, clipboard image, or file (also from Preview, Gallery, the widget, or the capture bar), move them between screens, zoom with the wheel, adjust opacity with Ctrl+wheel, copy, save, or send to the Editor.
- Pin references get configurable initial scale, smoothing, always-on-top behavior, shadow, and border, with a dimmed source chooser, a contextual tooltip when the clipboard has no image, and fixes for menu and close lifecycle crashes.
- Each tool gets one configurable global shortcut shown in capture-bar tooltips, with Tab and Shift+Tab stepping through the capture toolbar and Space or Enter activating the focused button, while Settings groups OCR, Color Picker, QR, Ruler, Zoom, and Pin under Tools.
- The widget reorganizes its nine actions into a 3x3 grid with a tool separator, the tray pairs Zoom and Pin in aligned columns, and cursor preferences move into the Image, MP4, and GIF submenus.
- Useful two-line English/Spanish tooltips cover the widget, tray, and capture bar, Capture Preview gets its own softer default sound, and Settings navigation, subtabs, hover states, and sound toggles are refined.
- Installer and portable packages include the .NET 10 runtime and FFmpeg, installation stays per-user without administrator prompts, and release automation updates the oldest pending winget PR and closes duplicates.

### 🇪🇸 Español

- Zoom se integra en el flujo de captura: congela la pantalla a 2x por defecto, haz pan y zoom de 1.25x a 4x, captura una región o la vista completa y vuelve al mismo Zoom tras cancelar, con la imagen ampliada conservada al entrar en la captura.
- Fijar en pantalla (Pin) mantiene referencias de imagen temporales flotando entre monitores: fija una región, imagen del portapapeles o archivo (también desde Vista previa, Galería, el widget o la barra de captura), muévelas entre pantallas, amplía con la rueda, cambia la opacidad con Ctrl+rueda, copia, guarda o envía al Editor.
- Las referencias fijadas tienen escala inicial, suavizado, siempre visible, sombra y borde configurables, un selector con fondo atenuado, un tooltip contextual cuando el portapapeles no tiene imagen y correcciones de cierres inesperados en los menús.
- Cada herramienta tiene un atajo global configurable visible en los tooltips de la barra de captura, con Tab y Shift+Tab para recorrer la barra y Espacio o Enter para activar el botón enfocado, y Configuración agrupa OCR, Selector de color, QR, Regla, Zoom y Pin en Herramientas.
- El widget reorganiza sus nueve acciones en una cuadrícula 3x3 con separador de herramientas, el tray alinea Zoom y Pin en columnas y las preferencias de cursor se mueven a los submenús de Imagen, MP4 y GIF.
- Tooltips útiles de dos líneas en inglés y español cubren widget, tray y barra de captura, Vista previa estrena un sonido predeterminado propio más suave y se refinan la navegación de Configuración, las subpestañas, los estados hover y los toggles de sonido.
- El instalador y el paquete portable incluyen el runtime de .NET 10 y FFmpeg, la instalación sigue siendo por usuario sin permisos de administrador y la automatización actualiza el PR de winget más antiguo y cierra duplicados.

## [1.21.0] - 2026-10-20

### 🇺🇸 English

- Scheduled backups protect settings and achievements: enabled by default with a daily schedule and three retained copies, with startup, daily, weekly, or monthly intervals, a custom folder, and retention from 1 to 50 copies.
- Backups run while the app is open, missed intervals produce one catch-up copy on the next launch, and copies can be created, restored, or deleted from Settings, with verification before old copies are removed and a separate recovery copy before restoring.
- API keys and passwords are excluded from backups, and captures and recordings are not included.
- Backup controls separate available copies (Automatic backups) from manual JSON export and import (Manual backup), and files use readable local-time names such as `CyberSnap-backup-2026-10-20_07-39-11.json`.
- Filenames, dates, sizes, and aligned actions stay visible while the page keeps scrolling over the backup list and folder path, and the schedule status returns to normal text size and contrast.
- The tray menu stays inside the monitor's working area on secondary displays with a vertical taskbar, including mixed DPI scales.
- Following the active monitor moves the widget directly to its final docked position, removing the brief jump to the left edge when docked at the top.
- Installer and portable packages include the .NET 10 runtime and FFmpeg, and installation remains per-user without administrator prompts.

### 🇪🇸 Español

- Copias programadas protegen la configuración y los logros: activadas por defecto con programación diaria y tres copias conservadas, con intervalos al iniciar, diarios, semanales o mensuales, una carpeta personalizada y retención de 1 a 50 copias.
- Las copias se ejecutan con la app abierta, los intervalos pendientes generan una sola copia de puesta al día al reabrirla y se pueden crear, restaurar o eliminar desde Configuración, verificando antes de borrar las antiguas y guardando una copia de recuperación independiente antes de restaurar.
- Las claves API y las contraseñas quedan excluidas de las copias, y las capturas y grabaciones no se incluyen.
- Los controles separan las copias disponibles (Copias de seguridad automáticas) de la exportación e importación JSON manual (Copia de seguridad manual), y los archivos usan nombres legibles con hora local, como `CyberSnap-backup-2026-10-20_07-39-11.json`.
- Nombre, fecha, tamaño y acciones alineadas permanecen visibles mientras la página sigue desplazándose sobre la lista y la ruta de carpeta, y el estado de la programación recupera el tamaño y contraste normales de texto.
- El menú de la bandeja permanece dentro del área de trabajo del monitor en pantallas secundarias con barra de tareas vertical, incluso con escalas DPI mixtas.
- Al seguir el monitor activo, el widget va directo a su posición final anclada, eliminando el salto momentáneo al borde izquierdo cuando está anclado arriba.
- El instalador y el paquete portable incluyen el runtime de .NET 10 y FFmpeg, y la instalación sigue siendo por usuario sin permisos de administrador.

## [1.20.1] - 2026-10-19

### 🇺🇸 English

- OCR and QR/barcode results share tabbed windows, and MP4 and GIF trimming sessions keep independent playback, selection, and export state when switching tabs.
- Result windows open more reliably in the foreground, and capture destination changes update the confirmation dock consistently.
- Clipboard delivery is configured separately for images, scrolling captures, OCR, QR/barcodes, MP4, GIF, and colors, with independent preview preferences for ordinary and scrolling captures and separate Trimmer preferences for MP4 and GIF.
- The widget's grouped More menu and open tools stay synchronized with Settings, and OCR, QR, and color tools keep a result window available when auto-copy is disabled.
- The capture widget places itself, its popups, and monitor following more reliably across displays with different DPI scales, with fixes for clipped borders, hidden edge indicators, and inconsistent menu checkmarks.
- CyberSnap migrates to .NET 10 LTS with updated SQLite components, and both packages bundle the .NET runtime and FFmpeg.
- The installer now installs per user under `%LocalAppData%\Programs\CyberSnap` without administrator prompts, honors the startup choice on first launch, cleans up shortcuts and startup entries on uninstall, and no longer duplicates the installed-app registration.
- Upgrading from an all-users installation in Program Files requires uninstalling that copy first; the new installer does not migrate machine-wide installations in place.

### 🇪🇸 Español

- Los resultados de OCR y QR/códigos de barras comparten ventanas con pestañas, y las sesiones de recorte MP4 y GIF conservan su estado de reproducción, selección y exportación al cambiar de pestaña.
- Las ventanas de resultados aparecen en primer plano con más fiabilidad y los cambios de destino actualizan la barra de confirmación de forma consistente.
- La entrega al portapapeles se configura por separado para imágenes, capturas con desplazamiento, OCR, QR/códigos de barras, MP4, GIF y colores, con preferencias de vista previa independientes para capturas normales y con desplazamiento y preferencias del Recortador separadas para MP4 y GIF.
- El menú Más del widget y las herramientas abiertas se sincronizan con Configuración, y OCR, QR y color mantienen su ventana de resultados disponible cuando la copia automática está desactivada.
- El widget de captura se ubica, posiciona sus menús emergentes y sigue monitores con más fiabilidad entre pantallas con distinto DPI, con correcciones de bordes recortados, indicadores ocultos y marcas de menú inconsistentes.
- CyberSnap migra a .NET 10 LTS con SQLite actualizado, y ambos paquetes incluyen el runtime de .NET y FFmpeg.
- El instalador ahora instala por usuario en `%LocalAppData%\Programs\CyberSnap` sin permisos de administrador, respeta la opción de inicio automático en el primer arranque, limpia accesos y entradas de inicio al desinstalar y ya no duplica el registro de aplicaciones instaladas.
- Para actualizar desde una instalación para todos los usuarios en Program Files hay que desinstalar esa copia antes; el nuevo instalador no migra directamente instalaciones a nivel de equipo.

## [1.19.0] - 2026-10-15

### 🇺🇸 English

- Overflowing tab strips gain chevron scroll buttons with smooth animated scrolling that keeps the active tab anchored, plus bigger tabs, a neutral close disc, a full-width accent underline, and a lighter close X on hover.
- Truncated editor tab titles render correctly.
- Color Picker and QR Scanner windows follow the suite's title-bar conventions with accent badges, ghost buttons, and in-window toggles, and their More menu carries tool-specific branches such as repick and a link to the Standalone Tools wiki page.
- Suite-standard taskbar titles now include the color picker.
- Editor modals use accent ghost buttons and roomier actions, and the save prompt answers N for Don't save via a key badge.
- Preview actions get ghost hover, accent hover text, and roomier buttons, plus a faster pill simulation, and the About window refreshes with accent buttons and a cleaner layout.
- New and updated English and Spanish strings cover preview actions, delivery toggles, window titles, and tooltips.
- The installer skips the program group page and installs shortcuts into the shared Programs folder.

### 🇪🇸 Español

- Las tiras de pestañas desbordadas ganan flechas de desplazamiento con animación suave que mantiene anclada la pestaña activa, además de pestañas más grandes, disco de cierre neutro, subrayado de acento a todo el ancho y una X de cierre más clara al pasar el cursor.
- Los títulos truncados de las pestañas del editor se muestran correctamente.
- Las ventanas Selector de Color y Escáner QR siguen las convenciones de barra de título de la suite con insignias de acento, botones fantasma e interruptores en la ventana, y su menú Más incluye ramas propias como volver a elegir y un acceso a la wiki de Herramientas Independientes.
- Los títulos de la barra de tareas estándar de la suite incluyen ahora el selector de color.
- Los modales del editor usan botones fantasma de acento y acciones más amplias, y el aviso de guardado responde N a No guardar mediante una insignia de tecla.
- Las acciones de Vista previa ganan hover fantasma, texto de acento y botones más amplios, además de una simulación de pastilla más rápida, y la ventana Acerca de se renueva con botones de acento y un diseño más limpio.
- Cadenas nuevas y actualizadas en inglés y español cubren acciones de vista previa, interruptores de entrega, títulos de ventana y tooltips.
- El instalador omite la página de grupo de programas e instala los accesos en la carpeta Programas compartida.

## [1.18.0] - 2026-10-09

### 🇺🇸 English

- The setup wizard is rebuilt with after-capture presets (Basic, Intermediate, Advanced) and a live result preview, available in the wizard and in Settings.
- The smarter Print Screen step names the real occupant of the key, warns only when relevant, and refuses taken keys instead of saving dead hotkeys, and the wizard adds a sober sidebar, full translations, and clickable steps.
- The video trimmer gains a Properties dialog showing container, codecs, resolution, fps, bitrate, audio, and the trim selection via ffprobe.
- The filmstrip and waveform become permanent (a flat line when there is no audio), thumbnails match the aspect ratio, and the preview recenters.
- Trimmer copy honors mute, tooltips wrap, and the Cut button gets a livelier hover.
- Editor copy separates Copy image (Photoshop-friendly) from Copy file, name, and path in a submenu with Ctrl+C and Ctrl+Shift+C.
- The OCR window gains a copy row with an auto-copy toggle and Ctrl+Enter and Ctrl+Shift+C badges, without layout jumps.
- Every release now ships a validated winget manifest and submits it to winget-pkgs automatically, and a Spanish README joins the repository.

### 🇪🇸 Español

- El asistente de configuración se reconstruye con preajustes tras la captura (Básico, Intermedio, Avanzado) y una vista previa del resultado, disponibles en el asistente y en Configuración.
- El paso de Print Screen más inteligente nombra al ocupante real de la tecla, avisa solo cuando importa y rechaza teclas ocupadas en lugar de guardar atajos muertos, y el asistente añade un lateral sobrio, traducción completa y pasos clicables.
- El recortador de vídeo estrena un diálogo de Propiedades con contenedor, códecs, resolución, fps, bitrate, audio y la selección de recorte, vía ffprobe.
- La tira de fotogramas y la forma de onda pasan a ser permanentes (línea plana sin audio), las miniaturas respetan el aspecto y la vista se recentra.
- Copiar en el recortador respeta el silencio, los tooltips se ajustan y el botón Cortar tiene un hover más vivo.
- La copia del Editor separa Copiar imagen (compatible con Photoshop) de Copiar archivo, nombre y ruta en un submenú con Ctrl+C y Ctrl+Shift+C.
- La ventana OCR estrena una fila de copiado con interruptor de auto-copiado e insignias Ctrl+Enter y Ctrl+Shift+C, sin saltos de diseño.
- Cada versión genera un manifiesto de winget validado y lo envía a winget-pkgs automáticamente, y el repositorio añade un README en español.

## [1.17.0] - 2026-10-05

### 🇺🇸 English

- The tray panel calms down: the donate heart rests in neutral gray and only blushes rose on hover, leaving Exit as the single red.
- A header options menu replaces the chevron and toggles capture modes, recording, tools, and gallery sections with smooth animations and a reposition helper.
- Title-bar, gallery, card, filter, and preview menus all close reliably on the second click.
- Hotkeys move fully to the standalone flow with one row per utility, recording shortcuts actually register, and reset writes honest unassigned defaults.
- Undo defaults to Ctrl+Z, and long tooltips wrap and dismiss themselves.
- The Print Screen banner appears only while PrtSc is held elsewhere, names the occupant, collapses quietly, and can assign PrtSc directly.

### 🇪🇸 Español

- El panel de la bandeja se calma: el corazón de donar descansa en gris neutro y solo se tiñe de rosa al pasar el cursor, dejando Salir como único rojo.
- Un menú de opciones en la cabecera sustituye al chevron y muestra u oculta las secciones de modos de captura, grabación, herramientas y galería con animaciones suaves y un ayudante de reposicionamiento.
- Los menús de títulos, galería, tarjetas, filtros y vista previa cierran de forma fiable al segundo clic.
- Los atajos pasan por completo al flujo independiente con una fila por utilidad, los atajos de grabación sí se registran y el restablecimiento escribe valores honestos sin asignar.
- Deshacer usa Ctrl+Z por defecto y los tooltips largos se ajustan y se cierran solos.
- El aviso de Print Screen solo aparece mientras PrtSc está ocupada por otra app, nombra al ocupante, se retrae sin estridencias y permite asignar PrtSc directamente.

## [1.16.0] - 2026-10-03

### 🇺🇸 English

- Updates are confirmed before downloading or installing: the About window shows a changelog peek with View Release, Download, and Skip this version actions, downloading waits for your approval, and installing asks again before closing.
- One click copies support details to the clipboard from About.
- The editor scales the canvas and its annotations to 1x, 2x, or 4x from a single menu button with pixel sizes, while zoom keeps changing only the view.
- Maximized capture previews stop at the monitor work area so taskbars stay visible.
- Extract text runs OCR straight from the preview.
- Copying to the clipboard and showing the result window become separate choices for OCR, QR, and color results, and the window reports Copied with a Close action when the text is already on the clipboard.
- Title bars use a single More menu led by Donate, confirm buttons show Enter and Esc keycaps, and the trimmer explains Exact trim versus Fast trim in plain language.
- The tray menu gains Donate, and overflow hints appear in the pin tip and settings.
- Recording works on a clean PC with FFmpeg bundled beside the app, login starts the installed copy exactly once, and the published build is verified self-contained.

### 🇪🇸 Español

- Las actualizaciones se confirman antes de descargar o instalar: la ventana Acerca de muestra una vista de los cambios con las acciones Ver versión, Descargar y Omitir esta versión, la descarga espera tu aprobación y la instalación pide confirmación antes de cerrar.
- Un clic copia los detalles de soporte al portapapeles desde Acerca de.
- El editor escala el lienzo y sus anotaciones a 1x, 2x o 4x desde un solo botón de menú con tamaños en píxeles, mientras el zoom sigue cambiando solo la vista.
- Las vistas previas de captura maximizadas se detienen en el área de trabajo del monitor para no tapar la barra de tareas.
- Extraer texto ejecuta el OCR directamente desde la vista previa.
- Copiar al portapapeles y mostrar la ventana de resultados son opciones separadas para los resultados de OCR, QR y color, y la ventana indica Copiado con una acción Cerrar cuando el texto ya está en el portapapeles.
- Las barras de título usan un solo menú Más encabezado por Donar, los botones de confirmación muestran las teclas Intro y Esc, y el recortador explica Recorte exacto y Recorte rápido en lenguaje claro.
- El menú de la bandeja incluye Donar y los avisos de desborde aparecen en la ayuda del pin y en ajustes.
- La grabación funciona en un equipo limpio con FFmpeg incluido junto a la app, el inicio de sesión abre la copia instalada una sola vez y la compilación publicada se verifica autocontenida.

## [1.15.0] - 2026-09-17

### 🇺🇸 English

- The video trimmer is overhauled: frame-accurate precise cut mode, two-pass palette for clean GIF colors, filmstrip timeline thumbnails with click-to-seek, an audio waveform strip, and real ffmpeg export progress with cancel support.
- Trimmer exports overwrite atomically without blocking the UI, accept drag-and-drop MP4 and GIF files, estimate output size, share segments, and add keyboard shortcuts with range handles snapped to the frame grid.
- Loop and time display preferences persist, history refreshes after a trim, and videos longer than an hour are handled reliably.
- The Gallery redesign replaces the filter dropdown with a tab strip, adds a minimalist icon header with status at search-bar level, collapsible date groups with item counts, and unified batch selection across filters.
- Gallery cards get category tints and an overflow pill with a ghost button and press animation, and relative timestamps are localized to Spanish.
- The scroll capture bar hugs its content with true Manual gates and a clean mode caption, keeps its drag position, and gains a ready badge, a help button with a usage guide, and new glyphs.
- Capture flow polish: single-click dock pills, unified grip dots, tactile titlebar press and hover animations, copy and close buttons with Enter key support, numpad Enter confirmation everywhere, and multi-monitor hardening that keeps the recording frame, size pills, and selections on the starting monitor with the recording bar visible and a discard confirmation.
- The color picker gains an optional detail window with favorite format preview, truly read-only fields, unified OCR/QR copy buttons, manual header drag, and foreground activation retry.
- A brand refresh brings new multi-size app and window icons, a bigger editor logo, and a refreshed picker header.
- The CyberSnap Share web viewer and expired page are redesigned bilingually, with a refreshed landing and branding, an updated logo, share badges reordered with Reddit first, and social and website links.
- The updater shows a changelog preview in a themed dialog and degrades gracefully when NAudio assemblies are missing, while About and docs rework Donate and See also with suite apps, replace the FAQ with compact wiki and docs links, and the tray quick panel gains collapsible secondary capture modes.

### 🇪🇸 Español

- El recortador de vídeo se renueva por completo: modo de corte preciso fotograma a fotograma, paleta de dos pasadas para colores GIF limpios, miniaturas de línea de tiempo con salto por clic, tira de forma de onda y progreso de exportación ffmpeg real con cancelación.
- Las exportaciones del recortador sobrescriben de forma atómica sin bloquear la interfaz, aceptan arrastrar y soltar MP4 y GIF, estiman el tamaño de salida, comparten segmentos y añaden atajos de teclado con tiradores ajustados a la cuadrícula de fotogramas.
- Las preferencias de bucle y visualización de tiempo persisten, el historial se actualiza tras recortar y los vídeos de más de una hora se manejan con fiabilidad.
- La Galería sustituye el desplegable de filtros por una tira de pestañas, añade una cabecera minimalista de iconos con el estado a la altura del buscador, grupos de fechas colapsables con conteo de elementos y selección por lote unificada entre filtros.
- Las tarjetas de la Galería reciben tintes por categoría y una pastilla de desborde con botón fantasma y animación al pulsar, y las fechas relativas se traducen al español.
- La barra de captura con desplazamiento se ajusta a su contenido con filtros Manual reales y un rótulo de modo limpio, conserva su posición arrastrada y estrena distintivo de listo, botón de ayuda con guía de uso y nuevos glifos.
- Pulido del flujo de captura: pastillas del dock de un clic, puntos de agarre unificados, animaciones táctiles de pulsación y hover en la barra de título, botones de copiar y cerrar con tecla Enter, confirmación con Enter numérico en todas partes y endurecimiento multi-monitor que mantiene el marco de grabación, las pastillas de tamaño y las selecciones en el monitor inicial, con la barra de grabación visible y confirmación al descartar.
- El selector de color estrena una ventana de detalle opcional con vista previa del formato favorito, campos de solo lectura reales, botones de copiado unificados de OCR/QR, arrastre manual de la cabecera y reintento de activación en primer plano.
- El refresco de marca trae nuevos iconos de app y ventana multi-tamaño, un logo mayor en el Editor y una cabecera renovada del selector.
- El visor web de CyberSnap Share y su página de caducidad se rediseñan en ambos idiomas, con portada y marca renovadas, logo actualizado, distintivos con Reddit primero y enlaces sociales y web.
- El actualizador muestra una vista previa del changelog en un diálogo temático y degrada con elegancia si faltan ensamblados de NAudio; Acerca de y la documentación renuevan Donar y Véase también con apps de la suite, sustituyen las FAQ por enlaces compactos a la wiki y docs, y el panel rápido de la bandeja añade modos de captura secundarios colapsables.

## [1.14.0] - 2026-09-08

### 🇺🇸 English

- The editor welcome experience is modernized with a clean dashed drop badge, a Quick Start chip on the welcome overlay, and quick-start guidance inside the Help submenu.
- Action buttons (New canvas, Open, Paste, Capture, Quick Start) use crisp vector icons with balanced padding, fixing the deformed question mark glyph.
- Minimum canvas zoom extends down to 10% for large images and high-resolution captures.
- The capture widget shows a subtle pulsing LED badge whenever a newer version of CyberSnap is available.
- The About dialog redesigns its footer icon row and adds direct donation links (Buy Me a Coffee, PayPal, Crypto) in English and Spanish.
- Measurement labels are easier to see, with stronger hover contrast across capture preview cards and zoom inspection.
- Documentation clarifies Windows SmartScreen reputation guidance for open-source hobby software and updates wiki links across window help menus.

### 🇪🇸 Español

- La bienvenida del Editor se moderniza con una insignia de arrastre punteada y limpia, un acceso a Inicio rápido en la superposición de bienvenida y una guía de inicio rápido dentro del submenú Ayuda.
- Los botones de acción (Nuevo lienzo, Abrir, Pegar, Capturar, Inicio rápido) usan iconos vectoriales nítidos con espaciado equilibrado, arreglando el glifo de interrogación deformado.
- El zoom mínimo del lienzo baja al 10% para imágenes grandes y capturas de alta resolución.
- El widget de captura muestra un sutil distintivo LED pulsante siempre que hay una versión más reciente de CyberSnap.
- El diálogo Acerca de renueva su fila de iconos del pie y añade enlaces directos de donación (Buy Me a Coffee, PayPal, Crypto) en inglés y español.
- Las etiquetas de medida se ven mejor, con más contraste al pasar el cursor en las tarjetas de vista previa de captura y la inspección de zoom.
- La documentación aclara la reputación de SmartScreen de Windows para software amateur de código abierto y actualiza los enlaces de la wiki en los menús de ayuda de las ventanas.

## [1.13.0] - 2026-09-04

### 🇺🇸 English

- The recording bar becomes a compact mini bar with redesigned transport glyphs, a live size meter, and a format badge as the recording indicator.
- Pause works for GIF and MP4, Space and Enter hit the bar instead of the window below, and the bar can be dragged, blinks a tray LED while capturing, and opens Video & GIF settings from the overlay.
- A first-run tip explains that Windows hides new tray icons behind the overflow caret, and Settings can open the Windows page for choosing which icons appear on the taskbar.
- New captures stack as tabs in the capture preview instead of replacing the current one, with right-click menus on preview and editor tabs even when only one capture is open.
- The capture tutorial is refreshed and the editor bubble closes when focus leaves, while Pan and Pick can set color and stroke and manual editor opens stay on Pick.
- Trimmer previews load at actual size with a hover zoom bar for frame inspection.
- Window menus include Help submenus with wiki links for Editor, Gallery, Settings, and more.
- Complete English and Spanish catalogs with localization safeguards, faster scrolling capture with preview handoff, scan animation on OCR and barcode flows, and sturdier capture widget restoration.

### 🇪🇸 Español

- La barra de grabación se convierte en una mini barra compacta con glifos de transporte rediseñados, medidor de tamaño en vivo y una insignia de formato como indicador de grabación.
- La pausa funciona en GIF y MP4, Espacio y Enter actúan sobre la barra en lugar de la ventana inferior, y la barra se puede arrastrar, parpadea con un LED en la bandeja mientras graba y abre los ajustes de Vídeo y GIF desde la superposición.
- Un aviso inicial explica que Windows oculta los nuevos iconos de bandeja tras el caret de desborde, y Ajustes puede abrir la página de Windows para elegir qué iconos aparecen en la barra de tareas.
- Las nuevas capturas se apilan como pestañas en la vista previa de captura en lugar de reemplazar la actual, con menús contextuales en las pestañas de vista previa y editor aunque solo haya una captura abierta.
- El tutorial de captura se renueva y la burbuja del editor se cierra al perder el foco; Pan y Pick pueden fijar color y trazo, y las aperturas manuales del editor permanecen en Pick.
- Las vistas previas del recortador cargan a tamaño real con una barra de zoom al pasar el cursor para inspeccionar fotogramas.
- Los menús de ventana incluyen submenús de Ayuda con enlaces a la wiki para Editor, Galería, Ajustes y más.
- Catálogos completos en inglés y español con salvaguardas de localización, captura con desplazamiento más rápida con entrega a la vista previa, animación de escaneo en los flujos de OCR y códigos de barras y una restauración del widget de captura más robusta.

## [1.12.0] - 2026-08-31

### 🇺🇸 English

- The Cut Out strip tool slices a band out of the canvas and closes the gap, with the same confirm and cancel controls as Crop, and rotate and flip move into the editor palette.
- Selected annotations gain compact corner arrows, a dedicated rotate cursor, and a fine plus handle at the object center, with selection restored after Undo.
- Editor document tabs open extra captures as tabs instead of replacing the current document, and double-clicking empty tab-bar space starts a new canvas.
- Quiet Hours mute notification toasts during a chosen window so captures stay silent overnight.
- The Emoji tool restores the last glyph and opens the drawer, stamps on canvas click and closes, tucks away when the tool button is clicked again, and resizes with the wheel or [ and ].
- A status-bar button switches between Fit to window and Original size, with larger icons.
- Drawing tools always arm a palette color (cyan by default) so the first shape is never a shadow-only stroke, color and stroke chips hint Select a drawing tool when they do not apply, and fade and slide banners are faster and dismiss on hover.

### 🇪🇸 Español

- La herramienta de recorte en tira extrae una franja del lienzo y cierra el hueco, con los mismos controles de confirmar y cancelar que Recortar, y rotar y voltear pasan a la paleta del editor.
- Las anotaciones seleccionadas ganan flechas compactas en las esquinas, un cursor de rotación propio y un asa en cruz fina en el centro del objeto, con la selección restaurada tras Deshacer.
- Las pestañas de documentos del editor abren capturas adicionales como pestañas en lugar de reemplazar el documento actual, y doble clic en el espacio vacío de la barra de pestañas inicia un lienzo nuevo.
- Las Horas de silencio silencian los avisos emergentes durante la franja elegida para que las capturas nocturnas no molesten.
- La herramienta de emojis recupera el último glifo y abre el cajón, estampa con un clic en el lienzo y cierra, se guarda al pulsar de nuevo el botón de la herramienta y cambia de tamaño con la rueda o [ y ].
- Un botón de la barra de estado alterna entre Ajustar a la ventana y Tamaño original, con iconos más grandes.
- Las herramientas de dibujo siempre arman un color de paleta (cian por defecto) para que la primera figura nunca sea un trazo solo de sombra, los chips de color y trazo avisan Selecciona una herramienta de dibujo cuando no aplican, y los banners de fundido y deslizamiento son más rápidos y se descartan al pasar el cursor.

## [1.11.0] - 2026-08-30

### 🇺🇸 English

- The Ultimate CyberGem achievement unlocks upon completing all achievements across all categories, with an exclusive large centered showcase badge featuring a turquoise diamond glow and a dedicated symbolic recognition disclaimer.
- The Achievements window tracks first unlock dates in tooltips using the local Windows date format, marks newly earned achievements with an animated New! badge until reviewed, and gains vibrant multi-theme tier palettes.
- A standalone QR and barcode scanner result window detects formats automatically (QR, Aztec, Data Matrix, Code 128, EAN, UPC, and more), shows a high-quality source thumbnail, and offers one-click Copy and close.
- Language toggles instantly between English and Spanish across the OCR result, QR scanner, annotations editor, achievements, and settings windows, with live theme updates across WPF windows and Windows Forms dialogs.
- Editor toolbar buttons (Undo, Redo, Save, Export, Share) size dynamically to eliminate label clipping in all languages.
- OCR gains multilingual text extraction with built-in translation models (MyMemory and Google Translate API), plus search highlights, character counts, and cascade window positioning.

### 🇪🇸 Español

- El logro Ultimate CyberGem se desbloquea al completar todos los logros de todas las categorías, con una gran insignia exclusiva centrada con brillo turquesa de diamante y un aviso simbólico de reconocimiento dedicado.
- La ventana de Logros registra las fechas del primer desbloqueo en los tooltips con el formato local de Windows, marca los logros recién conseguidos con una insignia New! animada hasta revisarlos y estrena paletas de niveles vibrantes por tema.
- Una ventana de resultados independiente para el escáner QR y códigos de barras detecta formatos automáticamente (QR, Aztec, Data Matrix, Code 128, EAN, UPC y más), muestra una miniatura de alta calidad de la fuente y ofrece Copiar y cerrar con un clic.
- El idioma alterna al instante entre inglés y español en las ventanas de resultado de OCR, escáner QR, editor de anotaciones, logros y ajustes, con temas actualizados en vivo en ventanas WPF y diálogos de Windows Forms.
- Los botones de la barra del editor (Deshacer, Rehacer, Guardar, Exportar, Compartir) calculan su ancho dinámicamente para eliminar textos recortados en todos los idiomas.
- El OCR incorpora extracción de texto multilingüe con modelos de traducción integrados (MyMemory y la API de Google Translate), además de resaltados de búsqueda, conteo de caracteres y posicionamiento de ventanas en cascada.

## [1.10.0] - 2026-08-25

### 🇺🇸 English

- The capture overlay and preview dialog replace the Done checkmark with a clean L-shaped Enter arrow icon.
- Pending state pills use the same Enter icon instead of the double chevron.
- The preview Done button is slightly larger (16px), recolored to the timer's muted blue-gray, and hovers with a subtle zoom-out instead of sliding.
- The preview timer ring stays visible at all times, swapping the countdown number for a pause icon during pauses.
- On pause the ring refills with a fast reverse animation, resuming smoothly from full, and on idle the countdown restarts from full with the number back.

### 🇪🇸 Español

- La superposición de captura y el diálogo de vista previa sustituyen la marca de Hecho por un icono limpio de flecha Enter en forma de L.
- Las pastillas de estado pendiente usan el mismo icono de Enter en lugar del doble chevron.
- El botón Hecho de la vista previa es algo mayor (16px), recoloreado al azul grisáceo suave del temporizador y, al pasar el cursor, hace un sutil zoom-out en lugar de deslizarse.
- El anillo del temporizador de la vista previa permanece visible en todo momento y cambia el número de la cuenta regresiva por un icono de pausa durante las pausas.
- Al pausar, el anillo se rellena con una animación inversa rápida y continúa con suavidad desde lleno; en reposo, la cuenta regresiva reinicia desde lleno y el número vuelve.

## [1.9.0] - 2026-08-23

### 🇺🇸 English

- Capture previews gain 2x and 4x upscaling to enhance resolution directly from the preview overlay, with the copy action gated by the selected scale factor and unified hover and pan cursors.
- A standalone color picker samples desktop colors live with anti-aliased RGB display pills.
- A precision desktop ruler integrates high-contrast halos, pill close controls, and refined Escape key navigation, and capture bar quick-launch buttons invoke the standalone measurement and color tools.
- The preview Done button gets a luminous sheen sweep animation with a breathing effect.
- The auto-close countdown fades out on cursor movement and resets when idle, and the zoom percentage reset button gets clear hover states and dedicated styling.
- Potential tray callback crashes and layout clipping when opening system viewers are fixed.
- The About window expands in height with custom themed scrollbars for the complete update progress, and confirmation dock edges align with standardized action pills and no unnecessary widget background transparency.

### 🇪🇸 Español

- Las vistas previas de captura incorporan escalado 2x y 4x para mejorar la resolución directamente desde la superposición de vista previa, con la acción de copiar condicionada al factor elegido y cursores unificados de hover y desplazamiento.
- Un selector de color independiente muestrea los colores del escritorio en vivo con pastillas RGB con antialiasing.
- Una regla de escritorio de precisión integra halos de alto contraste, controles de cierre en pastilla y navegación con Escape refinada, y los botones de acceso rápido de la barra de captura invocan las herramientas independientes de medición y color.
- El botón Hecho de la vista previa estrena una animación de brillo deslizante con efecto de respiración.
- La cuenta regresiva de cierre automático se desvanece al mover el cursor y se reinicia en reposo, y el botón de restablecer el porcentaje de zoom gana estados de hover claros y estilo dedicado.
- Se corrigen posibles fallos de callbacks de la bandeja y el recorte de diseño al abrir visores del sistema.
- La ventana Acerca de se amplía en altura con barras de desplazamiento temáticas para ver todo el progreso de actualización, y los bordes del dock de confirmación se alinean con pastillas de acción estandarizadas y sin transparencia innecesaria del fondo del widget.

## [1.8.0] - 2026-08-19

### 🇺🇸 English

- A dedicated Backup & Data section in Settings unifies application data, cache management, and direct folder navigation, with data storage options relocated from General.
- Every module gets its own high-definition window icon (Capture Preview, Achievements, Configuration, Gallery, OCR, Video & GIF Trimmer, Annotations Editor), and all window titles standardize with CyberSnap branding across English and Spanish.
- The editor top bar measures its title dynamically to prevent text clipping regardless of DPI scaling or interface language.
- The Achievements window is redesigned with milestone progression rails, active streak counters, statistics cards, and medal showcases, plus an introductory guide, themed scrollbars, and an interactive title bar burger menu with quick shortcuts to Configuration and About.
- Detected windows and UI elements can be captured with a single click, bypassing the confirmation overlay for rapid workflows.
- The Capture Preview dialog gains a responsive countdown timer, instant hover cancellation, an auto-close toggle, pan and zoom support, and crisp high-DPI vector action icons.
- The editor welcome banner is redesigned with a frosted glass card, and annotations gain adaptive Figma-style selection handles, animated Undo and Eraser buttons, context-aware outward-deploying flyouts, and an expanded emoji picker with bilingual search placeholders.
- The update flow runs unattended with an assisted progress bar and smooth auto-restart, vector icon rendering is optimized for standard and high-DPI screens, and the entire new interface is fully bilingual in English and Spanish.

### 🇪🇸 Español

- Una sección dedicada de Copia de seguridad y datos en Ajustes unifica los datos de la aplicación, la gestión de caché y la navegación directa a carpetas, con las opciones de almacenamiento reubicadas desde General.
- Cada módulo estrena su propio icono de ventana en alta definición (Vista previa, Logros, Configuración, Galería, OCR, Recortador de vídeo y GIF, Editor de anotaciones), y todos los títulos de ventana se estandarizan con la marca CyberSnap en inglés y español.
- La barra superior del editor mide su título dinámicamente para evitar textos recortados sin importar el escalado de DPI ni el idioma de la interfaz.
- La ventana de Logros se rediseña con raíles de progresión de hitos, contadores de rachas activas, tarjetas de estadísticas y vitrina de medallas, además de un texto guía introductorio, barras de desplazamiento temáticas y un menú hamburguesa interactivo en la barra de título con accesos rápidos a Configuración y Acerca de.
- Las ventanas y elementos de interfaz detectados se capturan con un solo clic, saltándose la superposición de confirmación para flujos rápidos.
- El diálogo de Vista previa de captura gana un temporizador de cuenta regresiva receptivo, cancelación instantánea al pasar el cursor, interruptor de cierre automático, soporte de pan y zoom e iconos vectoriales nítidos para pantallas de alto DPI.
- El banner de bienvenida del editor se rediseña con una tarjeta de cristal esmerilado, y las anotaciones ganan asas de selección adaptables estilo Figma, botones animados de Deshacer y Borrador, menús flotantes contextuales y un selector de emojis ampliado con marcadores de búsqueda bilingües.
- El flujo de actualización funciona desatendido con barra de progreso asistida y reinicio automático suave, el renderizado de iconos vectoriales se optimiza para pantallas estándar y de alto DPI, y toda la interfaz nueva es completamente bilingüe en inglés y español.

## [1.7.0] - 2026-08-17

### 🇺🇸 English

- All window, taskbar, and tray icons update to official multi-resolution 32bpp assets from 16px to 256px, with crisp alpha transparency in the tray and no dark halos on transparent backgrounds.
- The capture toolbar replaces its branding watermark with a circular question mark help button centered between the drag grip and the first tool button, with a larger hit target, hover illumination, and quick-start guide trigger.
- Toolbar dragging is restricted to the dedicated grip handle.
- The video recorder button consolidates GIF recording into a hold-to-switch flyout: a quick click starts MP4 recording, while holding or hovering smoothly reveals the GIF option.
- The capture right-click menu is reorganized into Capture and Tools sections, making every capture action (area, from center, scroll, fullscreen, MP4/GIF recorder) and tool (OCR, QR & barcodes, color picker, ruler) reachable from the canvas, with a Back navigation item and a red X to cancel.
- Tool button right-click menus are cleaned up and redundant exit confirmation dialogs are removed.
- Thread sleep delays leave the capture pipeline, window detection runs asynchronously on overlay startup, system dark mode lookups are cached, and standalone tool banners move to a lightweight layered window architecture.
- The About window presents cleanly without scrollbars, and new and updated components ship with unified English and Spanish strings.

### 🇪🇸 Español

- Todos los iconos de ventana, barra de tareas y bandeja se actualizan a recursos oficiales multi-resolución de 32 bpp de 16 a 256 píxeles, con transparencia alfa nítida en la bandeja y sin halos oscuros en fondos transparentes.
- La barra de captura sustituye su marca de agua por un botón circular de interrogación centrado entre el agarre de arrastre y el primer botón de herramienta, con área de clic mayor, iluminación al pasar el cursor y acceso a la guía de inicio rápido.
- El arrastre de la barra queda restringido al asa dedicada.
- El botón de la grabadora de vídeo integra la grabación GIF en un desplegable de mantener para cambiar: un clic rápido inicia la grabación MP4 y mantener o pasar el cursor revela suavemente la opción GIF.
- El menú contextual de captura se reorganiza en las secciones Captura y Herramientas, con todas las acciones de captura (área, desde el centro, desplazamiento, pantalla completa, grabadora MP4/GIF) y herramientas (OCR, QR y códigos de barras, selector de color, regla) al alcance del lienzo, además de un elemento Volver y una X roja para cancelar.
- Los menús contextuales de los botones de herramienta se limpian y se eliminan los diálogos de confirmación de salida redundantes.
- Las pausas de hilo salen de la canalización de captura, la detección de ventanas pasa a ser asíncrona al abrir la superposición, las consultas del modo oscuro del sistema se almacenan en caché y los banners de herramientas independientes pasan a una arquitectura de ventanas por capas ligera.
- La ventana Acerca de se presenta sin barras de desplazamiento y los componentes nuevos y actualizados llegan con cadenas unificadas en inglés y español.

## [1.6.4] - 2026-08-03

### 🇺🇸 English

- The tray menu shows a subtle glowing emerald LED next to the app branding when a new version is detected.
- The update confirmation dialog details are translated into Spanish.
- The grab cursor on the confirm modes dock now appears only over the drag grip, keeping the standard arrow over empty padding and separators.

### 🇪🇸 Español

- El menú de la bandeja muestra un sutil LED verde esmeralda junto a la marca de la app cuando se detecta una versión nueva.
- Los detalles del diálogo de confirmación de actualización se traducen al español.
- El cursor de agarre del dock de modos de confirmación solo aparece sobre el asa de arrastre, manteniendo la flecha estándar sobre el relleno vacío y los separadores.

## [1.6.3] - 2026-07-19

### 🇺🇸 English

- Update checks prioritize the installer executable so updates no longer open the portable zip archive in WinRAR.

### 🇪🇸 Español

- Las comprobaciones de actualización priorizan el ejecutable del instalador para que las actualizaciones ya no abran el archivo zip portable en WinRAR.

## [1.6.2] - 2026-07-19

### 🇺🇸 English

- Installer shortcut tasks are localized to Spanish and English, checked by default, and assigned proper icons.

### 🇪🇸 Español

- Las tareas de accesos directos del instalador se traducen al inglés y al español, quedan marcadas por defecto y reciben iconos apropiados.

## [1.6.1] - 2026-07-19

### 🇺🇸 English

- Fixes a WPF exception when opening Configuration or the Gallery, and a SQLite insert column mismatch.

### 🇪🇸 Español

- Corrige una excepción de WPF al abrir Configuración o la Galería y un error de columnas en las inserciones de SQLite.

## [1.6.0] - 2026-07-19

### 🇺🇸 English

- Editor, Settings, and Gallery window titlebars drop redundant branding for clean localized titles, and Windows taskbar title formatting is standardized across all application windows.
- Exit and cancel menu items unify on the sign-out door icon across all forms and context menus, with a horizontally flipped version on the capture overlay toolbar exit button for a natural outward orientation.
- The capture overlay toolbar drags freely in real time from the position button, brand logo, or empty toolbar space.
- Working-area clamping keeps the toolbar fully within screen boundaries on single and multi-monitor setups.
- Layered window coordinate feedback loops and one-frame ghosting when toggling position or dragging at 150%+ DPI scaling are fixed.
- The Quick Start guide expands to a roomier bubble with padded keycap badges for Enter, Esc, Ctrl+Z, and Ctrl+Y, restores crisp vector icons, and Spanish phrasing is refined across capture banners, tooltips, and onboarding guides.

### 🇪🇸 Español

- Las barras de título del Editor, Ajustes y Galería eliminan la marca redundante para títulos limpios y localizados, y el formato de títulos en la barra de tareas de Windows se estandariza en todas las ventanas de la aplicación.
- Los elementos de menú de salir y cancelar unifican el icono de puerta sign-out en todos los formularios y menús contextuales, con una versión volteada horizontalmente en el botón de salida de la barra de la superposición de captura para una orientación natural hacia afuera.
- La barra de herramientas de la superposición de captura se arrastra libremente en tiempo real desde el botón de posición, el logo de la marca o el espacio vacío.
- El ajuste al área de trabajo mantiene la barra siempre dentro de los límites de la pantalla en configuraciones de uno o varios monitores.
- Se corrigen los bucles de coordenadas de ventanas por capas y el parpadeo de un fotograma al alternar posición o arrastrar con escalado de 150% o más.
- La guía de inicio rápido se amplía a una burbuja más espaciosa con insignias de tecla rellenas para Enter, Esc, Ctrl+Z y Ctrl+Y, recupera iconos vectoriales nítidos y se refina la redacción en español de banners de captura, tooltips y guías de bienvenida.

## [1.5.6] - 2026-06-20

### 🇺🇸 English

- Top bar actions (Undo, Redo, Copy, Save, Save As) match the zoom controls with rounded borders, cyan glowing active states, and unified typography.
- The Border toggle in the bottom status bar becomes a custom-drawn, smoothly animated pill-style switch.
- The status bar gains unified, higher-contrast coordinates and metadata text with vertical line separators, a wider zoom panel that avoids wrapping on zoom percentages, and responsive zoom controls docked to the far right on smaller widths.
- The top actions panel fills the remaining area, fixing the partially clipped Undo button, and crop button texts shorten to Confirm and Cancel.
- Canvas borders paint with concentric paths so the outer glow and borders no longer fade or clip at the rounded corners.
- Changing tools shows a fast fade-in/fade-out HUD banner in the canvas corner instead of permanent bottom-bar hints.
- A command stack supports multi-level undo and redo on the canvas.
- The installer checks Start with Windows by default, the portable archive gets the explicit CyberSnap-[version]-Portable-win-x64.zip name, auto-updater tests cover the GitHub Releases check, and translation schemas are synchronized.

### 🇪🇸 Español

- Las acciones de la barra superior (Deshacer, Rehacer, Copiar, Guardar, Guardar como) igualan a los controles de zoom con bordes redondeados, estados activos cian brillantes y tipografía unificada.
- El interruptor de Borde en la barra de estado inferior se convierte en un interruptor de pastilla dibujado a medida con animación suave.
- La barra de estado gana coordenadas y metadatos unificados y de mayor contraste con separadores de líneas verticales, un panel de zoom más ancho que evita el ajuste de línea en porcentajes y controles de zoom anclados a la derecha en anchos menores.
- El panel superior de acciones llena el resto del área, arreglando el botón Deshacer parcialmente recortado, y los textos de los botones de recorte se acortan a Confirmar y Cancelar.
- Los bordes del lienzo se dibujan con trazos concéntricos para que el brillo exterior y los bordes no se desvanezcan ni se recorten en las esquinas redondeadas.
- Al cambiar de herramienta aparece un banner HUD con fundido rápido en la esquina del lienzo en lugar de las pistas permanentes de la barra inferior.
- Una pila de comandos admite deshacer y rehacer multinivel en el lienzo.
- El instalador marca Iniciar con Windows por defecto, el archivo portable recibe el nombre explícito CyberSnap-[version]-Portable-win-x64.zip, se añaden pruebas del actualizador automático contra la comprobación de GitHub Releases y se sincronizan los esquemas de traducción.
