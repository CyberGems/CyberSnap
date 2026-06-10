# Sound Assets

Coloca aquí tus archivos MP3 con los siguientes nombres exactos:

| Archivo               | Se reproduce cuando...                        |
|-----------------------|-----------------------------------------------|
| `capture.mp3`         | Se completa una captura                       |
| `color.mp3`           | Se usa el color picker                        |
| `text.mp3`            | Se copia texto OCR                            |
| `scan.mp3`            | Se detecta un QR/barcode                      |
| `record-start.mp3`    | Inicia una grabación                          |
| `record-stop.mp3`     | Termina una grabación                         |
| `error.mp3`           | Ocurre un error                               |
| `startup.mp3`         | Toast "CyberSnap ready" al iniciar la app     |

## Requisitos

- Formato: **MP3** (MPEG-1 Audio Layer III)
- Duración recomendada: **< 1 segundo** (son sonidos UI, no canciones)
- Bitrate: 128-192 kbps (mono o stereo, da igual)
- Frecuencia de muestreo: 44100 Hz

## Notas

- Los archivos se embeberán como `EmbeddedResource` en el .exe
- El usuario podrá sobrescribir cada sonido individualmente desde Settings > General
- El usuario podrá mutear sonidos individualmente desde Settings > General
- Si un archivo falta, ese sonido simplemente no se reproducirá (sin errores)
