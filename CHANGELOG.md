# Changelog

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

[1.23.0]: https://github.com/CyberGems/CyberSnap/releases/tag/v1.23.0
