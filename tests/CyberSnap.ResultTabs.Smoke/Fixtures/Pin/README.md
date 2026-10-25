# Pin image format fixtures

These synthetic 32×32 images contain red, green, blue, and white quadrants.
They are generated locally and contain no third-party artwork or metadata.

| File | Purpose |
|---|---|
| `rgb24.bmp` | RGB bitmap with 24 bits per pixel |
| `indexed8.bmp` | Palette-based bitmap with 8 bits per pixel |
| `indexed.gif` | Palette-based GIF |
| `animated.gif` | Two-frame GIF; the first frame has the quadrants and the second is black |
| `rgb48.tiff` | RGB TIFF with 16 bits per channel |
| `rgba64.tiff` | RGBA TIFF with 16 bits per channel; the red quadrant has 50% straight alpha |
| `cmyk.jpg` | Adobe CMYK JPEG |
| `cmyk.tiff` | Uncompressed CMYK TIFF |

`generate.py` regenerates the fixtures with Python and Pillow. The TIFF writer
uses only Python's standard library. Tests consume the checked-in files, so
Python and Pillow are not required to build or run the smoke checks.

Run `dotnet run --project tests/CyberSnap.ResultTabs.Smoke -c Release -- --pin-formats-only`
from the repository root on Windows. Checks cover decoded pixels, straight alpha,
first-frame selection, released file handles, and localized errors for damaged files.
