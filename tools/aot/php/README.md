# PHP example 🐘

`naplps_demo.php`, uses PHP's built-in FFI (PHP 7.4+) via `FFI::cdef`. Inline C prototypes; no extension install beyond the bundled `ext-ffi`. 102 lines.

## Prerequisites 📋

- NAPLPS library published to `../publish/` (run `pwsh ../publish.ps1`).
- PHP 7.4 or later with `ext-ffi` enabled (tested with 8.1).

## Enable ext-ffi 🔌

Check `php.ini` for:

```ini
extension=ffi
```

or pass on the command line:

```bash
php -d extension=ffi naplps_demo.php <input.nap> <output.png>
```

## Run 🚀

```bash
php naplps_demo.php ../../../Examples/telidraw/hello.nap hello.png
```

## Expected output ✅

```
NAPLPS library version: 0.12.0
Loaded ../../../Examples/telidraw/hello.nap (35 bytes)
Parsed 22 commands, 0 errors
Wrote hello.png (6709 bytes, 1024x768)
```

## Use cases 💡

- Render `.nap` files on-demand in a legacy PHP web stack (Laravel, Symfony, WordPress plugin).
- Batch convert archived `.nap` collections into PNG galleries served statically.

See [../README.md](../README.md) for the full comparison table.
