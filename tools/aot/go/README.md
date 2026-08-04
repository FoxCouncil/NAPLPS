# Go example 🐹

`main.go`, uses [`purego`](https://github.com/ebitengine/purego) for FFI without cgo. No C compiler required at build time. 121 lines (main.go plus two small platform-split loader files).

## Prerequisites 📋

- NAPLPS library published to `../publish/` (run `pwsh ../publish.ps1`).
- Go 1.22 or later (tested with 1.26).

## Build + run 🛠️

```bash
cp ../publish/NAPLPS.dll .        # Windows only
go build -o naplps_demo.exe .
./naplps_demo.exe ../../../Examples/telidraw/hello.nap hello.png
```

## How it works 🔍

Go's traditional FFI is cgo, which requires a C compiler (MinGW on Windows, `gcc`/`clang` elsewhere). `purego` bypasses cgo entirely: it loads the shared library at runtime and generates per-function trampolines that marshal between Go types and the C ABI. Build step is plain `go build`.

`load_windows.go` and `load_unix.go` wrap the platform-specific library-loading calls (`syscall.LoadLibrary` vs `purego.Dlopen`) behind a single `loadLibrary` function.

## Expected output ✅

```
NAPLPS library version: 0.12.0
Loaded ../../../Examples/telidraw/hello.nap (35 bytes)
Parsed 22 commands, 0 errors
Wrote hello.png (6709 bytes, 1024x768)
```

## Use cases 💡

- A NAPLPS-to-PNG HTTP microservice (`net/http` plus `naplps_render_png`).
- CLI batch tools.
- Embedded into existing Go services without adding a C toolchain dependency.

See [../README.md](../README.md) for the full comparison table.
