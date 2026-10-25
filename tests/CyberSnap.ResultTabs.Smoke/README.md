# Capture tool regression checks

Run on Windows with .NET 10:

```powershell
dotnet run --project tests/CyberSnap.ResultTabs.Smoke -c Release -- --capture-tool-regressions-only
```

This targeted suite covers:

- **Hotkey scopes:** capture annotations and Editor shortcuts can share a key.
  Duplicates within a stage, Editor tool/view collisions, global collisions,
  restoring F6, aliases, and saved settings are also checked.
- **Pin imports:** real indexed BMP/GIF, animated GIF, RGB24 BMP, RGB48/RGBA64
  TIFF, and CMYK JPEG/TIFF fixtures. Checks verify colors, alpha, first-frame
  selection, released files, and localized errors for damaged images.
- **Zoom help:** English and Spanish hints include capture confirmation,
  direct copy/save, zoom controls, and exit. Layout is checked at multiple
  viewport sizes, including 320×240.

The `Verify capture tool regressions` workflow runs these checks and the strict
localization audit on relevant pushes and pull requests. It does not publish a
release. To save Zoom help renders, append a local output directory to the command.

For broader interactive checks on a Windows desktop:

```powershell
dotnet run --project tests/CyberSnap.ResultTabs.Smoke -c Release -- --hotkeys-only
dotnet run --project tests/CyberSnap.ResultTabs.Smoke -c Release -- --pin-only
dotnet run --project tests/CyberSnap.ResultTabs.Smoke -c Release -- --zoom-only
```

These fixtures use temporary preferences and synthetic windows. The Pin selector
checks native presentation and capture exclusion across the available monitors,
both UI languages, both themes, and five UI scales. They keep the original pixel
tolerance and require an inspectable desktop surface before checking for clipping.

See [Pin fixtures](Fixtures/Pin/README.md) for fixture details and regeneration.
