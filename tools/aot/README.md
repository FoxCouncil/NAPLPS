# NAPLPS Native Interop Examples 🌐✨

Nine small programs (C, C++, Rust, Python, Node.js, Go, Zig, PHP, Ruby) plus a Swift scaffold that link against the AOT-published NAPLPS library and render a `.nap` file to a `.png`. Each demonstrates the four core entry points: `naplps_version`, `naplps_command_count`, `naplps_error_count`, `naplps_render_png`. (The library exports more - see the Exported API table.)

All ten produce a **byte-identical 6709-byte PNG** (MD5 `9a431be59694112d90d9e33297efbe46`) from the same `Examples/telidraw/hello.nap` input. The table below is exhaustive: toolchain, FFI mechanism, build command, runtime setup, and the size of each demo's source file.

| Language | Tested version | FFI mechanism | Compile step | Runtime setup | Demo LOC |
|---|---|---|---|---|---|
| **C** 🅒 | MSVC 14.44 (VS 2026) | Direct C ABI against `NAPLPS.lib` import library | `cl main.c ... NAPLPS.lib` via `build-msvc.ps1` | DLL next to `.exe` | 95 |
| **C++** | MSVC 14.44, C++17 | Same as C, with `std::vector` / `fstream` | `cl /EHsc /std:c++17 main.cpp ...` via `build-msvc.ps1` | DLL next to `.exe` | 83 |
| **Rust** 🦀 | 1.95 | `#[link(name="NAPLPS", kind="dylib")]` plus raw `unsafe extern "C"` | `cargo build --release` | DLL next to `.exe` (`build.rs` sets link-search) | 96 |
| **Python** 🐍 | 3.13 | `ctypes.CDLL` with `.argtypes`/`.restype` | (interpreted, no build) | DLL in `../publish/` | 106 |
| **Node.js** 🟢 | 22 LTS | `koffi.load` plus `lib.func('prototype')` (dynamic FFI, no build step) | `npm install koffi` (single native addon, prebuilt for win/mac/linux) | DLL in `../publish/` | 82 |
| **Go** 🐹 | 1.26 | `purego.RegisterLibFunc` (no cgo, no C compiler required at build time) | `go build` (platform split file for `Dlopen` vs `LoadLibrary`) | DLL next to `.exe` | 121 |
| **Zig** ⚡ | 0.16 | `extern fn` declarations at top-level; libc `fopen`/`fread`/`fwrite` via `@cImport` for file I/O (avoids churn in Zig 0.16 `std.fs`) | `zig build` | DLL next to `.exe` | 100 |
| **PHP** 🐘 | 8.1 | Built-in `FFI::cdef` with inline C prototypes | (interpreted; needs `extension=ffi` enabled via `php.ini` or `-d`) | DLL in script dir | 102 |
| **Ruby** 💎 | 4.0 | `ffi` gem with `attach_function` declarations | `gem install ffi` (one-time) | DLL next to script | 86 |
| **Swift** 🦅 | 6.3.1 | SwiftPM `systemLibrary` target plus `module.modulemap` (brings C header into Swift as a module) | Windows: `cmd /c build-windows.bat` (vcvars64 + SDKROOT + runtime DLLs). Mac/Linux: `swift build -c release` | Runtime DLLs on PATH plus DLL next to `.exe` | 87 |

**Takeaways:** 🔍

- 🟢 **Zero-compile FFI**: Python, Node.js, PHP, Ruby. Dynamic loaders, no build step for the binding itself.
- 🟢 **Zero-C-compiler**: Python, Node.js, Go (via purego), Ruby. No C toolchain needed to consume the library.
- 📏 **Smallest LOC**: Node.js (82). Most others land in the 80-100 range; Go is largest at 121 because of the platform-split loader helpers.
- 🧩 **Trickiest setup**: Swift on Windows (three env vars to align) and C/C++ (MSVC path gymnastics). Everything else is one command.

## Layout 📁

```
tools/aot/
  include/
    naplps.h         - C header, shared by C and C++ examples
  publish/           - output of publish.ps1 (generated, gitignored)
  publish.ps1        - one-shot publish + import-lib copy
  c/
    main.c
    build-msvc.ps1   - Windows MSVC build (verified)
    Makefile         - GCC / MinGW build
  cpp/
    main.cpp
    build-msvc.ps1   - Windows MSVC build (verified)
    Makefile         - GCC / MinGW build
  rust/
    Cargo.toml
    build.rs
    src/main.rs
  python/
    naplps_demo.py   - stdlib ctypes, no package install needed
  node/
    package.json
    naplps_demo.js   - koffi-based FFI, npm install then node run
  go/
    go.mod
    main.go          - purego FFI (no cgo required)
    load_windows.go  - platform-specific library loader
    load_unix.go
  zig/
    build.zig
    main.zig         - extern fn declarations + libc stdio via @cImport
  php/
    naplps_demo.php  - FFI::cdef with inline C prototypes
  ruby/
    naplps_demo.rb   - ffi gem
  swift/
    Package.swift
    README.md
    Sources/
      CNaplps/
        module.modulemap
        shim.h       - includes ../include/naplps.h
      naplps_demo/
        main.swift
  README.md
```

## Step 1: Publish the NAPLPS library for NativeAOT 🚀

Pick your target runtime ID. Supported: `win-x64`, `linux-x64`, `osx-x64`, `osx-arm64`, `linux-arm64`.

```powershell
# Windows (uses vswhere + MSVC linker under the hood)
pwsh tools/aot/publish.ps1
# or for a different target
pwsh tools/aot/publish.ps1 -Rid linux-x64
```

Equivalent raw `dotnet` command:

```bash
dotnet publish NAPLPS/NAPLPS.csproj -c Release -r win-x64 \
    --property:PublishAot=true \
    -o tools/aot/publish
```

Output:

| Platform | Files |
|---|---|
| Windows | `NAPLPS.dll`, `NAPLPS.lib`, `NAPLPS.exp`, `NAPLPS.pdb` |
| Linux | `libNAPLPS.so` |
| macOS | `libNAPLPS.dylib` |

On Windows the `.lib` (import library) is NOT produced in the publish output by default. `publish.ps1` copies it from the intermediate `NAPLPS/bin/Release/.../native/` directory so C/C++ examples can link without extra path plumbing. If you run `dotnet publish` directly, you'll need to copy `NAPLPS.lib` and `NAPLPS.exp` manually.

### Prerequisites for `PublishAot=true`

- **Windows** 🪟. Visual Studio 2026 with the C++ workload, and `vswhere.exe` reachable. vswhere is installed at `C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe`. If `dotnet publish` fails with "vswhere not recognized", prepend that directory to PATH for the current shell:

  ```powershell
  $env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;" + $env:PATH
  ```

- **Linux** 🐧. `clang`, `zlib`, `libicu-dev`. See the .NET NativeAOT docs for the exact list.
- **macOS** 🍎. Xcode command-line tools.

## Step 2: Build an example 🛠️

### C (MSVC, verified)

```powershell
pwsh tools/aot/c/build-msvc.ps1
cd tools/aot/c
.\naplps_demo.exe ..\..\..\Examples\telidraw\hello.nap hello.png
```

### C++ (MSVC, verified)

```powershell
pwsh tools/aot/cpp/build-msvc.ps1
cd tools/aot/cpp
.\naplps_demo.exe ..\..\..\Examples\telidraw\hello.nap hello.png
```

### C / C++ (GCC or MinGW)

```bash
cd tools/aot/c     # or cpp
make run
```

The Makefile links against `libNAPLPS.so` (or `NAPLPS.lib` via MinGW import library on Windows).

### Rust 🦀

```bash
cd tools/aot/rust
cargo build --release
./target/release/naplps_demo ../../../Examples/telidraw/hello.nap hello.png
```

### Python 🐍

```bash
cd tools/aot/python
python naplps_demo.py ../../../Examples/telidraw/hello.nap hello.png
```

No install step. `ctypes` is stdlib.

### Node.js 🟢

```bash
cd tools/aot/node
npm install
node naplps_demo.js ../../../Examples/telidraw/hello.nap hello.png
```

Uses `koffi` for FFI. Works on Node 18+.

### Go 🐹

```bash
cd tools/aot/go
cp ../publish/NAPLPS.dll .     # Windows: DLL next to binary
go build -o naplps_demo .
./naplps_demo ../../../Examples/telidraw/hello.nap hello.png
```

Uses `github.com/ebitengine/purego`, a pure-Go FFI with no cgo and no C compiler required at build time. Cross-platform library loading is split into `load_windows.go` (uses `syscall.LoadLibrary`) and `load_unix.go` (uses `purego.Dlopen`).

### Zig ⚡

```bash
cd tools/aot/zig
cp ../publish/NAPLPS.dll .
zig build
cp zig-out/bin/naplps_demo.exe .
./naplps_demo.exe
```

Zig 0.16+. The input / output paths are hardcoded because Zig 0.16's `std.process.args` and `std.fs.cwd` APIs are in flux; libc `fopen`/`fread`/`fwrite` via `@cImport` is stable. `build.zig` links against `NAPLPS` from `../publish/`.

### PHP 🐘

```bash
cd tools/aot/php
cp ../publish/NAPLPS.dll .
php -d extension=ffi naplps_demo.php ../../../Examples/telidraw/hello.nap hello.png
```

PHP 7.4+. If `extension=ffi` is already set in your `php.ini`, the `-d` flag is unnecessary.

### Ruby 💎

```bash
cd tools/aot/ruby
gem install ffi       # one-time
cp ../publish/NAPLPS.dll .
ruby naplps_demo.rb ../../../Examples/telidraw/hello.nap hello.png
```

### Swift 🦅

Install the toolchain from https://www.swift.org/install/ (macOS ships via Xcode; Windows uses the installer from swift.org).

**Windows**. The environment needs the Swift runtime DLLs on PATH, `SDKROOT` pointed at the Platform SDK, and VS x64 linker env loaded. `tools/aot/swift/build-windows.bat` handles all three:

```powershell
cd tools/aot/swift
cmd /c build-windows.bat
cp ..\publish\NAPLPS.dll .build\x86_64-unknown-windows-msvc\release\
$env:PATH = "$env:LOCALAPPDATA\Programs\Swift\Runtimes\6.3.1\usr\bin;" + $env:PATH
.\.build\x86_64-unknown-windows-msvc\release\naplps_demo.exe ..\..\..\Examples\telidraw\hello.nap hello.png
```

**macOS / Linux**. The toolchain's installer should have set PATH correctly:

```bash
cd tools/aot/swift
swift build -c release
./.build/release/naplps_demo ../../../Examples/telidraw/hello.nap hello.png
```

See `tools/aot/swift/README.md` for how the SwiftPM system-library target is wired up.

On Windows the .exe needs `NAPLPS.dll` next to it or on PATH. On Linux/macOS the `build.rs` script sets an rpath to `../publish/` so the dylib is found automatically.

## Expected output ✅

```
NAPLPS library version: 0.11.0
Loaded ../../../Examples/telidraw/hello.nap (35 bytes)
Parsed 22 commands, 0 errors
Wrote hello.png (6709 bytes, 1024x768)
```

The output PNG is a valid 8-bit RGBA PNG. First bytes: `89 50 4E 47 0D 0A 1A 0A` (the PNG magic). 🖼️

## Exported API 🔌

The exports come from `NAPLPS/NativeExports.cs` (stateless) and `NAPLPS/NativeExportsCtx.cs` (stateful decoder contexts) via `[UnmanagedCallersOnly(EntryPoint = "...")]`. `dumpbin /EXPORTS NAPLPS.dll` confirms the symbols:

```
naplps_command_count
naplps_ctx_append
naplps_ctx_command_count
naplps_ctx_create
naplps_ctx_destroy
naplps_ctx_draw_text
naplps_ctx_exec_next
naplps_ctx_exec_to
naplps_ctx_framebuffer
naplps_ctx_reset
naplps_error_count
naplps_render_png
naplps_render_png_prodigy
naplps_version
```

Stateless functions:

| Symbol | Purpose | Returns |
|---|---|---|
| `naplps_version(buf, len)` | Library version string | bytes written (excl. null), or required size if `len == 0`, or negative error |
| `naplps_command_count(bytes, len)` | Parsed NAPLPS command count | count, or negative error |
| `naplps_error_count(bytes, len)` | Parse error count | count (0 = clean), or negative error |
| `naplps_render_png(bytes, len, w, h, buf, buf_len)` | Render .nap to PNG | PNG bytes written, required size if `buf_len == 0`, or negative error |
| `naplps_render_png_prodigy(...)` | Same, forcing the Prodigy pipeline | as above |

Stateful decoder contexts (`naplps_ctx_*`): an opaque handle owning a persistent decoder and a raw RGBA8888 framebuffer, for consumers that append bytes over time, paint command-by-command, and blit without PNG round-trips. Full semantics in `include/naplps.h`.

| Symbol | Purpose |
|---|---|
| `naplps_ctx_create(w, h, flags)` / `naplps_ctx_destroy(ctx)` | Lifecycle; flags bit 0 = Prodigy pipeline |
| `naplps_ctx_reset(ctx)` | Fresh page: clears bytes, decoder state, framebuffer |
| `naplps_ctx_append(ctx, bytes, len)` | Append stream bytes (transactional); returns command count |
| `naplps_ctx_command_count(ctx)` | Parsed command count |
| `naplps_ctx_exec_to(ctx, idx)` / `naplps_ctx_exec_next(ctx, dirty)` | Paint commands; step returns -4 at stream end |
| `naplps_ctx_draw_text(ctx, x, y, fg, bg, cw, ch, text, len)` | Append a field-text run via the library encoder |
| `naplps_ctx_framebuffer(ctx, &w, &h, &stride)` | Pinned RGBA8888 pixels, valid for the context lifetime |

Error codes (negative return values):

| Code | Meaning |
|---|---|
| -1 | Parse error or exception (context calls leave the context unchanged) |
| -2 | Output buffer too small |
| -3 | Invalid input (null pointer, non-positive length, bad argument or state) |
| -4 | Stream exhausted (`naplps_ctx_exec_next` only; a status, not an error) |
| -5 | Bad context handle |

The stateless functions are thread-safe: each call builds its own render state. Context handles are different: creating/destroying/looking up handles is safe from any thread, but a given context must not be used from two threads at the same time, and its framebuffer pointer is only coherent between the caller's own calls.

## Adding a new language binding ➕

The ABI is standard C: plain `int32_t` returns, raw `uint8_t*` buffers, no callbacks, no manual memory allocation crossing the boundary (the only struct is the optional 4-int `NaplpsRect` out-parameter). Any language with a C FFI should work. `tools/aot/include/naplps.h` is the source of truth for signatures.

Strong candidates for further bindings:

| Language | Why | FFI layer |
|---|---|---|
| **Kotlin Native** | Android archival apps | cinterop + def file |
| **Java** (via JNI or JNA) | Enterprise / JVM integration | JNA: declare interface; JNI: heavier |
| **Lua** | LuaJIT `ffi.cdef` for game-engine embedding | LuaJIT FFI |
| **Nim / D** | Systems languages with first-class C ABI | direct `extern "C"` |
| **Elixir / Erlang** | NIF or ranch for a .nap render service | `erl_nif.h` or port driver |
| **Crystal** | Ruby-like syntax, compiled | `@[Link]` attribute |

When you add one, drop a new subdirectory here with a small program, a build script, and a section in this README. 🎉
