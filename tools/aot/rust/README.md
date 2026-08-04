# Rust example 🦀

`src/main.rs`, native Rust FFI via `#[link(name = "NAPLPS")]` plus `unsafe extern "C"`. No bindgen, no build.rs wrapper; the four C functions are declared inline. 96 lines.

## Prerequisites 📋

- NAPLPS library published to `../publish/` (run `pwsh ../publish.ps1`).
- Rust toolchain (stable): `rustup` from https://rustup.rs.

## Build + run 🛠️

```bash
cargo build --release
cp ../publish/NAPLPS.dll target/release/   # Windows only
./target/release/naplps_demo ../../../Examples/telidraw/hello.nap hello.png
```

`build.rs` adds `../publish` to the linker search path automatically, and sets rpath on Unix. On Windows the `.dll` has to sit next to the `.exe` at runtime (or on `PATH`).

## Expected output ✅

```
NAPLPS library version: 0.12.0
Loaded ../../../Examples/telidraw/hello.nap (35 bytes)
Parsed 22 commands, 0 errors
Wrote hello.png (6709 bytes, 1024x768)
```

See [../README.md](../README.md) for the full comparison table.
