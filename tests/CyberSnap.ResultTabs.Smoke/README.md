# Capture tool regression checks

Run on Windows with .NET 10:

```powershell
dotnet run --project tests/CyberSnap.ResultTabs.Smoke -c Release -- --capture-tool-regressions-only
```

This targeted suite covers:

- **Hotkey scopes:** capture annotations and Editor shortcuts can share a key.
  Duplicates within a stage, Editor tool/view collisions, global collisions,
  restoring F6, aliases, and saved settings are also checked.
- **Capture destinations:** Pin and frozen Zoom selections hide capture tools.
  Confirmation restores annotations, retains its output purpose, rejects alternate
  destinations and shortcuts, and keeps a compact dock.
  Regular captures retain their destination switcher. Both UI languages are checked.
- **Confirmation options:** English and Spanish menus show only controls relevant to
  the capture purpose. Hiding annotations disables drawing and lets the crop move
  from inside, including over existing artwork. Visibility, custom tools, and the
  remembered tool survive a settings reload. Tab/Space, Enter, and Retry remain usable.
  Grip colors match across drawing surfaces. History animations reserve their native
  host, reuse the bitmap, and reach their endpoint even after a delayed frame.
  The dimensions pill is opaque and draggable, its separate grip follows annotation
  visibility without brightening on hover, and the Enter keycap matches Preview
  proportions. Dismissal clicks close More without reopening it or changing the
  selected area.
- **Confirmation help:** the toolbar logo and More open English/Spanish help for
  capture, Pin, and frozen Zoom. Content follows annotation visibility, Preview
  availability, and the pending destination. Checks cover stage shortcuts, fit at
  100/150% DPI and both UI scales, safe dismissal, and the original capture guide.
- **Pin imports:** real indexed BMP/GIF, animated GIF, RGB24 BMP, RGB48/RGBA64
  TIFF, and CMYK JPEG/TIFF fixtures. Checks verify colors, alpha, first-frame
  selection, released files, and localized errors for damaged images.
- **Zoom help:** English and Spanish hints include capture confirmation,
  direct copy/save, zoom controls, and exit. The responsive card is checked at
  multiple viewport sizes (including 320×240), DPI scales, UI scales, and themes.
  Desktop checks also compare CPU/GPU pixels on every available monitor.

The `Verify capture tool regressions` workflow runs these checks and the strict
localization audit on relevant pushes and pull requests. It does not publish a
release. To save Zoom help renders, append a local output directory to the command.

For broader interactive checks on a Windows desktop:

```powershell
dotnet run --project tests/CyberSnap.ResultTabs.Smoke -c Release -- --confirmation-options-only
dotnet run --project tests/CyberSnap.ResultTabs.Smoke -c Release -- --confirmation-chrome-only
dotnet run --project tests/CyberSnap.ResultTabs.Smoke -c Release -- --confirmation-guide-only
dotnet run --project tests/CyberSnap.ResultTabs.Smoke -c Release -- --hotkeys-only
dotnet run --project tests/CyberSnap.ResultTabs.Smoke -c Release -- --pin-capture-only
dotnet run --project tests/CyberSnap.ResultTabs.Smoke -c Release -- --pin-only
dotnet run --project tests/CyberSnap.ResultTabs.Smoke -c Release -- --zoom-help-only
dotnet run --project tests/CyberSnap.ResultTabs.Smoke -c Release -- --zoom-only
```

Append a local output directory to `--confirmation-options-only` to save menu renders.
This desktop check also opens the quick start guide while annotation controls are hidden.
`--confirmation-chrome-only` focuses on click-owned More menus, annotation popup bounds,
modal tooltip suppression, grip pixels, and history animation frames. It accepts the same
optional render directory. It also checks repeated annotation hide/show without moving
the selection, including after a frame gesture while annotations are hidden. Native
pixel inspection confirms the reshown toolbar is visible, not merely an open window
with zero opacity.
`--confirmation-guide-only` checks the contextual guide, including native placement
on every available monitor. It also accepts a render directory.

These fixtures use temporary preferences and synthetic windows. The Pin selector
checks native presentation and capture exclusion across the available monitors,
both UI languages, both themes, and five UI scales. They keep the original pixel
tolerance and require an inspectable desktop surface before checking for clipping.

See [Pin fixtures](Fixtures/Pin/README.md) for fixture details and regeneration.
