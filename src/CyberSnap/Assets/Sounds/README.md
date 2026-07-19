# Sound Assets

Place your MP3 files here using the exact names below.
The project automatically embeds every `Assets/Sounds/*.mp3` (`EmbeddedResource` in the `.csproj`).

| File                  | Event (`SoundEvent`) | Plays when...                                   |
|-----------------------|----------------------|-------------------------------------------------|
| `capture.mp3`         | `Capture`            | A capture is completed                           |
| `color.mp3`           | `Color`              | The color picker is used                         |
| `text.mp3`            | `Text`               | OCR text is copied                               |
| `scan.mp3`            | `Scan`               | A QR / barcode is detected                       |
| `record-start.mp3`    | `RecordStart`        | A recording starts                               |
| `record-stop.mp3`     | `RecordStop`         | A recording ends                                 |
| `upload.mp3`          | `Upload`             | Successful upload / Share (Editor or Gallery)    |
| `system.mp3`          | `System`             | System notices (e.g. "Sent to editor")           |
| `error.mp3`           | `Error`              | An error occurs                                  |
| `startup.mp3`         | `Startup`            | "CyberSnap ready" toast on app launch            |
| `achievement.mp3`     | `Achievement`        | An achievement is unlocked                       |

## Add a new sound (checklist)

1. **File:** `src/CyberSnap/Assets/Sounds/<name>.mp3` (same name as in the table).
2. **Code (only if it is a new event):**
   - Add a value to `SoundEvent` in `Models/AppSettings.cs`.
   - Map the name in `SoundService.LoadEmbeddedMp3`.
   - Add a row in `SoundEventDefs` (`SettingsWindow.Recording.cs`) for the Sounds tab.
   - Call `SoundService.Play…()` where appropriate.
3. **Rebuild.** If the MP3 is missing, that event stays silent (no error).

For **upload**, the wiring is already in place: just drop `upload.mp3` in this folder and rebuild.

## Requirements

- Format: **MP3** (MPEG-1 Audio Layer III)
- Recommended duration: **< 1 second** (UI sounds, not songs)
- Bitrate: 128–192 kbps (mono or stereo)
- Sample rate: 44100 Hz

## Notes

- Files are embedded as `EmbeddedResource` in the `.exe`.
- Users can override or mute each sound from **Settings → Sounds**.
- If a file is missing, that sound does not play (no crash).
- Upload failures use the **Error** sound, not the upload one.
