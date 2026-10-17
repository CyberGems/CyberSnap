# Trimmer tabs: feasibility

Status: code review completed on 2026-10-17. Tabs are implemented for OCR and QR; the Trimmer remains unchanged.

Tabs are feasible with the current WPF and FFmpeg implementation. The work has medium-to-high complexity because the window currently owns both the document state and the playback/export lifecycle. Adding the tab bar alone would risk losing trim selections or applying an export to the wrong file.

## Reusable components

- `PreviewTabStrip` supplies the existing visual treatment, overflow navigation and close buttons.
- `ResultTabHost<T>` supplies selection, per-tab closing, reopening the last result, keyboard navigation and close-all confirmation. The Trimmer needs a different close policy to account for unsaved edits and exports; its session type must also explicitly own disposable media resources.
- `LoadMediaFile`, `ReleaseDecodedMedia`, `CancelPreviewLoad` and the GIF/MP4 load-version checks already provide a basis for switching the active media safely.

## State that must belong to each tab

Create a `TrimmerSession` containing the full file path, media metadata, selection start/end, playhead and paused state, volume/export mute, loop and precise-cut choices, zoom/fit/pan state, and pending edits. Keep application defaults separate from the current document's choices.

`ApplyLoadedMp4Preview` and `ApplyLoadedGifPreview` currently reset the selection to the full duration, reset zoom and start playback from zero. They need an explicit initial-load versus session-restore path, with restoration after the asynchronous load completes. Load, waveform and filmstrip callbacks must check both session identity and load generation before changing the UI.

## Playback and memory

Use one active playback surface and audio player. Pause the outgoing tab, capture its playhead, cancel its pending preview work, release decoded media and restore the incoming tab. Only the active tab should participate in `CompositionTarget.Rendering`.

Avoid retaining an unrestricted decoder/cache for every open tab. `Mp4FrameSequence` extracts preview frames into a temporary directory and caches up to 60 decoded frames; `GifFrameSequence` holds encoded media and caches up to 150 decoded frames. Multiplying those resources by the number of tabs can be expensive. Start by retaining lightweight session state and rebuilding the active preview, then consider a bounded recent-preview cache after measuring switch latency and memory use. Temporary files must remain owned by the session/job that created them.

## Export correctness is the main prerequisite

`TrimBtn_Click` reads `_mediaFilePath` again after awaiting FFmpeg and after a player-release delay. `SaveAsNewBtn_Click` reloads the exported copy after asynchronous work. Simply allowing tab switches would let these continuations act on a different active document.

Capture an immutable export job containing the source session, source/destination paths, selected interval, audio options and cut mode. Associate cancellation, progress and completion with that job. The atomic overwrite (`File.Replace`) must always use the captured source path. Releasing file locks and reloading the result must affect the originating session only.

For an initial implementation, serialize exports and prevent switching/closing the exporting tab until completion or cancellation has fully settled. Queue incoming files as additional tabs without replacing the active exporting document. Supporting simultaneous background exports is a separate, larger change.

## Opening and closing behavior

Route all current entry points through one presentation method: recording completion in `App.Capture.cs`, the toast action, both History actions, and file drop. Reopening an already-open full path should focus its tab. A file drop should append/select a tab instead of discarding the current selection.

Closing one tab must run that session's discard/export checks. Closing the window must validate every session before disposing any of them, so canceling leaves all documents usable. The existing `Window_Closing` cancels export before asking about pending edits; a tab implementation must defer cancellation until the close decision is accepted. Keep “Save As New” loading the new copy into the originating tab to preserve current behavior.

## Acceptance checks for implementation

1. Open mixed MP4/GIF files from every entry point and confirm that only one Trimmer window opens.
2. Switch repeatedly while media, waveform and thumbnails are loading; no stale callback may overwrite the active tab.
3. Restore each tab's trim bounds, playhead, pause state, audio and zoom; only one tab may play audio.
4. Export, cancel export and overwrite while another file arrives. Every output must match its originating session and source file.
5. Cancel individual/all-tab close prompts and confirm that document state and ongoing work remain usable.
6. Close all tabs and verify that FFmpeg processes, rendering subscriptions, frame caches and owned temporary files are released.
7. Check English/Spanish, both themes, scaling and overflow navigation.

Recommended sequence: extract session state, implement safe activation/restoration, isolate export jobs, then connect tab UI and opening routes. This is an architectural change to the Trimmer; no additional framework or media engine is required by the design identified in this review. Runtime performance still needs measurement during implementation.
