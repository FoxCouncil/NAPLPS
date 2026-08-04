# C++ example ⚙️

`main.cpp`, a C++17 port of the C demo. Same behavior, uses `std::vector` plus `std::fstream` instead of raw `malloc` plus `FILE*`. 83 lines.

## Prerequisites 📋

- NAPLPS library published to `../publish/` (run `pwsh ../publish.ps1`).
- Windows: Visual Studio 2026 with the C++ workload.
- Linux / macOS: `g++` or `clang++` with C++17 support.

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

See [../README.md](../README.md) for the full comparison table.
