# Developer Tooling 🔧

Running the test suite, the visual regression workflow, and common dev commands.

## Prerequisites 📋

- .NET 10 SDK (target framework is `net10.0`)
- Windows, macOS, or Linux (any Avalonia 12 platform)
- git

```bash
git clone https://github.com/FoxCouncil/NAPLPS
cd NAPLPS
dotnet build NAPLPS.sln
```

## Running tests 🧪

The test project is `NAPLPSTests/`. The suite at v0.10.0 is 800+ unit tests, plus a visual-regression test and a round-trip test covering the 375+ `.nap` examples.

```bash
# Full suite (unit tests + round-trip + visual regression). ~6 minutes.
dotnet test NAPLPSTests/NAPLPSTests.csproj

# Unit tests + round-trip, skip VR (fast, ~20s)
dotnet test NAPLPSTests/NAPLPSTests.csproj --filter "TestCategory!=VR&TestCategory!=Diag"

# Just the round-trip headline test
dotnet test NAPLPSTests/NAPLPSTests.csproj --filter "TestCategory=RoundTrip"

# Just visual regression (~5 minutes)
dotnet test NAPLPSTests/NAPLPSTests.csproj --filter "TestCategory=VR"

# A specific test by name
dotnet test NAPLPSTests/NAPLPSTests.csproj --filter "FullyQualifiedName~WordWrapTests"
```

Test categories:

| Category | Purpose |
|---|---|
| *(default)* | Unit tests: parse, encode, decode, Telidraw compile, each opcode |
| `RoundTrip` | Headline: for every `.nap` in `Examples/`, load, decompile to `.td`, recompile, assert byte-equal to original |
| `VR` | Visual regression: render every example to APNG, byte-compare against the baseline in `NAPLPSTests/Visual/Baselines/` |
| `Diag` | Exploratory one-off probes, always skipped in normal runs. Don't commit long-term Diag tests; they're scratch. |

## The 5 invariants ✅

Every commit keeps these green:

1. ✅ All unit tests pass
2. 🔁 Telidraw round-trip is byte-identical across the corpus (`.nap` → decompile → recompile → same bytes)
3. 💾 Disk-honest (`FromFile` → `ToBytes` matches the source bytes exactly, preserving undefined opcodes)
4. 🖼️ Visual regression baselines pass (APNG renders match the baseline corpus bit-for-bit)
5. 🧪 Editor launches, renders, saves (smoke test)

`dotnet test` exits non-zero on any failure.

## Visual regression workflow 🖼️

The VR suite renders each example to APNG and compares frame-by-frame against a curated baseline.

### Directory layout

```
NAPLPSTests/
  Visual/
    VisualRegressionTest.cs       - the test itself
    VisualTestContext.cs          - rendering + comparison
    Baselines/                    - curated APNG reference images
      <filename>.nap.apng         - one per example
    (generated at test time:)
    bin/Debug/net10.0/VisualRegression/
      Actuals/                    - newest rendered output
      Diffs/                      - diff HTML per file
      VisualRegressionReport.html - summary dashboard
      Viewers/                    - side-by-side comparison pages
```

### Running and inspecting

```bash
dotnet test NAPLPSTests/NAPLPSTests.csproj --filter "TestCategory=VR"
```

If any file's render differs, the test fails and `VisualRegressionReport.html` lists the offenders. Open the per-file diff HTML to see baseline, actual, and diff side-by-side with highlighted pixels.

If the test reports `Skipped` or `Inconclusive` instead of Failed: a new file in `Examples/` has no baseline. The report page lists it under "new". Review the rendered output and, if correct, copy it to `Baselines/`.

### Accepting new baselines

When adding a new example or intentionally changing rendering:

```bash
cp NAPLPSTests/bin/Debug/net10.0/VisualRegression/Actuals/<filename>.nap.apng \
   NAPLPSTests/Visual/Baselines/<filename>.nap.apng

git add NAPLPSTests/Visual/Baselines/<filename>.nap.apng
git commit -m "VR: accept baseline for <filename>"
```

Don't blanket-accept all new baselines. Review each one. Committing a regression as a new baseline silently breaks future comparisons. ⚠️

### When you change rendering

Changes that touch `NAPLPS/Drawing/*.cs` will probably affect the VR baselines. Run VR locally before commit, fix any regressions, and commit baseline updates alongside the rendering change in the same commit.

## Round-trip test 🔁

The headline correctness test:

> For every `.nap` in `Examples/`, the byte sequence survives a full decompile-to-recompile cycle unchanged.

Implementation: `NAPLPSTests/Telidraw/ExamplesRoundTripTests.cs`. It loads each file, runs it through `Decompiler.Decompile` to produce Telidraw text, parses and compiles that text back through the full Telidraw pipeline, and asserts byte-equality with the original.

A failure here means one of:

- The decompiler emitted something the parser can't consume (broken grammar or precedence bug)
- The decompiler lost precision in a coordinate or operand
- The compiler re-emitted a different byte sequence than the original (encoding mismatch, bit-mode error, mv-tracking error)
- The parser lost a byte during the first load (disk-honest regression)

`td_roundtrip_report.txt` (in the test `bin/` dir) lists per-file pass or fail after a run.

## AOT compatibility ⚡

As of v0.10.0, the **NAPLPS library** is AOT-compatible. `<IsAotCompatible>true</IsAotCompatible>` is enabled, with zero trimmer or AOT warnings and analyzer output surfaced via `EnableTrimAnalyzer=true`.

Two suppressions in `NAPLPS/GlobalSuppressions.cs` handle the source gen emitting dead-code metadata for `NaplpsCommandReference`. NCR[] round-trips entirely through the custom converter at runtime; the generated metadata is never executed.

A working `PublishAot` example will land here once we have one.

## Native interop examples (tools/aot/) 🌐

Ten small programs that link against the AOT-published library and render a `.nap` to PNG. Each produces the same 6709-byte PNG (MD5 `9a431be59694112d90d9e33297efbe46`). The library exports its C API via `[UnmanagedCallersOnly]`: the stateless `naplps_version`, `naplps_command_count`, `naplps_error_count`, `naplps_render_png`, `naplps_render_png_prodigy`, plus the stateful `naplps_ctx_*` decoder-context family (see `tools/aot/include/naplps.h`).

| Language | Directory | FFI mechanism |
|---|---|---|
| C | [tools/aot/c/](../tools/aot/c/) | direct C ABI against `NAPLPS.lib` |
| C++ | [tools/aot/cpp/](../tools/aot/cpp/) | same as C, with `std::vector` / `fstream` |
| Rust | [tools/aot/rust/](../tools/aot/rust/) | `#[link]` + cargo |
| Python | [tools/aot/python/](../tools/aot/python/) | stdlib `ctypes`, no install |
| Node.js | [tools/aot/node/](../tools/aot/node/) | `koffi` npm package |
| Go | [tools/aot/go/](../tools/aot/go/) | `purego` (no cgo) |
| Zig | [tools/aot/zig/](../tools/aot/zig/) | `extern fn` + `@cImport` libc |
| PHP | [tools/aot/php/](../tools/aot/php/) | built-in `FFI::cdef` |
| Ruby | [tools/aot/ruby/](../tools/aot/ruby/) | `ffi` gem |
| Swift | [tools/aot/swift/](../tools/aot/swift/) | SwiftPM `systemLibrary` target |

Each directory has its own `README.md` with prerequisites, build command, expected output, and representative use cases. The top-level [tools/aot/README.md](../tools/aot/README.md) has a detailed comparison table (tested versions, compile steps, runtime setup, demo LOC).

To publish the library for native consumers:

```powershell
pwsh tools/aot/publish.ps1          # Windows (vswhere + VS toolchain required)
# or
dotnet publish NAPLPS/NAPLPS.csproj -c Release -r <rid> --property:PublishAot=true -o tools/aot/publish
```

Output: `NAPLPS.dll` + `NAPLPS.lib` + `NAPLPS.exp` (Windows) or `libNAPLPS.{so,dylib}` (Linux / macOS).

## Common commands 🛠️

```bash
# Run everything (full regression)
dotnet test NAPLPSTests/NAPLPSTests.csproj

# Just unit tests (fast)
dotnet test NAPLPSTests/NAPLPSTests.csproj --filter "TestCategory!=VR&TestCategory!=Diag"

# Build the library in AOT-analyzer mode (catches trimmer issues at build time)
dotnet build NAPLPS/NAPLPS.csproj

# Launch the editor
dotnet run --project NAPLPSApp

# Compile + preview a Telidraw source
dotnet run --project NAPLPSApp -- compile Examples/telidraw/hello.td -o /tmp/hello.nap
dotnet run --project NAPLPSApp -- export /tmp/hello.nap --format=apng

# Batch-export every file in the corpus to PNG
dotnet run --project NAPLPSApp -- export --batch Examples/ --output-dir=/tmp/naplps-renders/

# Diff two files (text mode)
dotnet run --project NAPLPSApp -- diff file1.nap file2.nap --mode=text

# Visual diff to PNG
dotnet run --project NAPLPSApp -- diff file1.nap file2.nap --mode=visual
```
