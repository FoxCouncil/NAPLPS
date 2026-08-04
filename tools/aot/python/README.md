# Python example 🐍

`naplps_demo.py`, pure stdlib `ctypes`, no `pip install` required. Great for Jupyter notebooks, quick scripting, and batch rendering. 106 lines.

## Prerequisites 📋

- NAPLPS library published to `../publish/` (run `pwsh ../publish.ps1`).
- Python 3.8+ (tested with 3.13).

## Run 🚀

```bash
python naplps_demo.py ../../../Examples/telidraw/hello.nap hello.png
```

Script auto-resolves `../publish/NAPLPS.dll` (Windows), `libNAPLPS.dylib` (macOS), or `libNAPLPS.so` (Linux).

## Expected output ✅

```
NAPLPS library version: 0.12.0
Loaded ../../../Examples/telidraw/hello.nap (35 bytes)
Parsed 22 commands, 0 errors
Wrote hello.png (6709 bytes, 1024x768)
```

## Use cases 💡

- Batch-render an archive of `.nap` files into PNGs with a one-line loop.
- Feed rendered frames into OpenCV or Pillow for further processing.
- Integrate with Jupyter / pandas / matplotlib for analysis of rendered output pixel histograms.

See [../README.md](../README.md) for the full comparison table.
