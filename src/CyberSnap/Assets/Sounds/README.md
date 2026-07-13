# Sound Assets

Coloca aquí tus archivos MP3 con los siguientes nombres exactos.  
El proyecto embebe automáticamente todo `Assets/Sounds/*.mp3` (`EmbeddedResource` en el `.csproj`).

| Archivo               | Evento (`SoundEvent`) | Se reproduce cuando...                              |
|-----------------------|------------------------|-----------------------------------------------------|
| `capture.mp3`         | `Capture`              | Se completa una captura                             |
| `color.mp3`           | `Color`                | Se usa el color picker                              |
| `text.mp3`            | `Text`                 | Se copia texto OCR                                  |
| `scan.mp3`            | `Scan`                 | Se detecta un QR / código de barras                 |
| `record-start.mp3`    | `RecordStart`          | Inicia una grabación                                |
| `record-stop.mp3`     | `RecordStop`           | Termina una grabación                               |
| `upload.mp3`          | `Upload`               | Subida / Share exitoso (Editor o Galería)           |
| `error.mp3`           | `Error`                | Ocurre un error                                     |
| `startup.mp3`         | `Startup`              | Toast "CyberSnap ready" al iniciar la app           |
| `achievement.mp3`     | `Achievement`          | Se desbloquea un logro                              |

## Añadir un sonido nuevo (checklist)

1. **Archivo:** `src/CyberSnap/Assets/Sounds/<nombre>.mp3` (mismo nombre que en la tabla).
2. **Código (solo si es un evento nuevo):**
   - Añadir valor a `SoundEvent` en `Models/AppSettings.cs`.
   - Mapear el nombre en `SoundService.LoadEmbeddedMp3`.
   - Añadir fila en `SoundEventDefs` (`SettingsWindow.Recording.cs`) para la pestaña Sonidos.
   - Llamar `SoundService.Play…()` donde corresponda.
3. **Rebuild.** Si el MP3 falta, ese evento queda en silencio (sin error).

Para **upload**, el cableado ya está: solo falta soltar `upload.mp3` en esta carpeta y recompilar.

## Requisitos

- Formato: **MP3** (MPEG-1 Audio Layer III)
- Duración recomendada: **&lt; 1 segundo** (sonidos UI, no canciones)
- Bitrate: 128–192 kbps (mono o stereo)
- Frecuencia de muestreo: 44100 Hz

## Notas

- Los archivos se embeben como `EmbeddedResource` en el `.exe`.
- El usuario puede sobrescribir o silenciar cada sonido desde **Configuración → Sonidos**.
- Si un archivo falta, ese sonido no se reproduce (sin crashear).
- Fallos de subida usan el sonido de **Error**, no el de upload.
