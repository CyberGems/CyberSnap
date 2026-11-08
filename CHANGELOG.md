# Changelog

## [1.24.0] - 2026-11-08

### Added

- Achievement progress now shows its start date and can be reset for a fresh run. Progress is stored separately from general settings, with existing history migrated once.
- The Gallery can be opened independently of the Editor.
- An option can hide the floating widget during captures.

### Improved

- Editor controls, zoom actions, and the annotation palette adapt more consistently to display scaling.
- Window scaling and placement respond more reliably when moving between monitors with different DPI settings.
- Capture overlays recover from paint issues, retry black frames once, and keep the capture toolbar hidden during region selection.
- Preview actions and scrollbars have clearer spacing, while the widget restores more smoothly after captures.
- Recording duration detection can use bundled or system FFmpeg tools, and release packages now include `ffprobe.exe`.

### Fixed

- Tray menus, notifications, color picker windows, tooltips, and dialogs retain their size and placement across monitor DPI changes.
- The widget no longer reappears prematurely during capture transitions, and its topmost behavior no longer interferes with menus or captures.
- Single-instance cleanup only releases a mutex owned by the current process.

## [1.23.0] - 2026-10-27

### Added

- Checkerboard or solid capture-preview backgrounds, custom colors per theme, and a reset to the theme default.
- Configurable outlines (1–32 output pixels) and solid-color margins (1–256 pixels per side), included when copying or saving captures.
- Per-tab image-path copying and export to Paint from Preview's More menu.
- Optional cursor-following widget placement across monitors, available in the widget menu and Settings and remembered between sessions.
- Contextual confirmation guides for screenshots, Pin and Zoom.

### Improved

- Preview action layout, label readability, compact menus, final image-size display and toolbar wrapping.
- Color-picker scrolling at high DPI, fixed Reset/Accept actions, slider interaction, editable pixel values and the standard palette layout.
- Widget capture-button hit area and hover background, with more vertical space around the capture mark.
- Confirmation options, selection grips, draggable dimension readout, annotation history and stage-specific shortcut validation.
- Pin image imports, including transparency, the first frame of animated GIFs, and high-bit-depth or CMYK sources.
- About icon synchronization, winget verification retries and automated capture/widget regression coverage.

### Fixed

- Capture preview losing its center after maximizing and restoring the window, while preserving manual zoom.
- Pin and Zoom confirmation inheriting an ordinary screenshot destination.
- Capture toolbars appearing during Pin/Zoom region selection, selection toolbar visibility and repeated More-menu reopening.

Earlier versions are documented in the [GitHub release history](https://github.com/CyberGems/CyberSnap/releases).

[1.24.0]: https://github.com/CyberGems/CyberSnap/releases/tag/v1.24.0
[1.23.0]: https://github.com/CyberGems/CyberSnap/releases/tag/v1.23.0
