# C example 🅒

`main.c`, the reference binding. Reads a `.nap`, calls `naplps_render_png`, writes a `.png`. 95 lines including error handling.

## Prerequisites 📋

- NAPLPS library published to `../publish/` (run `pwsh ../publish.ps1` from the repo root).
- Windows: Visual Studio 2026 with the C++ workload.
- Linux / macOS: any C11 compiler (`gcc`, `clang`).

## Build + run 🛠️

Windows:

```powershell
pwsh build-msvc.ps1
.\naplps_demo.exe ..\..\..\Examples\telidraw\hello.nap hello.png
```

Linux / macOS:

```bash
make run
```

## Expected output ✅

```
NAPLPS library version: 0.12.0
Loaded ../../../Examples/telidraw/hello.nap (35 bytes)
Parsed 22 commands, 0 errors
Wrote hello.png (6709 bytes, 1024x768)
```

See [../README.md](../README.md) for the full comparison table across all languages.
