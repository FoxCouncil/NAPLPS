# Zig example ⚡

`main.zig`, raw `extern fn` declarations for the four C exports, plus `@cImport` for libc `fopen` / `fread` / `fwrite`. Zig 0.16's `std.fs` API is in flux; libc stdio is stable and already linked via `linkLibC`. 100 lines.

## Prerequisites 📋

- NAPLPS library published to `../publish/` (run `pwsh ../publish.ps1`).
- Zig 0.16 or later.

## Build + run 🛠️

```bash
zig build
cp ../publish/NAPLPS.dll zig-out/bin/   # Windows only
zig-out/bin/naplps_demo
```

Input and output paths are hardcoded (`../../../Examples/telidraw/hello.nap` to `hello.png`) to keep the demo focused on FFI rather than argv parsing. Adapt `main.zig` if you want a real CLI.

## Expected output ✅

```
NAPLPS library version: 0.12.0
Loaded ../../../Examples/telidraw/hello.nap (35 bytes)
Parsed 22 commands, 0 errors
Wrote hello.png (6709 bytes, 1024x768)
```

## Why Zig 🏆

Best-in-class C interop: `@cImport` literally reads C headers at compile time. The binding is one file, no wrappers. Great for systems-level embedding where you don't want a runtime.

See [../README.md](../README.md) for the full comparison table.
