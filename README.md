# NAPLPS 📺✨

A modern .NET 10 toolkit for the **North American Presentation Level Protocol Syntax**, ANSI X3.110-1983 — with live-rendering examples from the historical corpus. Click any tile below to open the full-size APNG; each is a bit-identical render from a real `.nap` file in `Examples/`.

### 🖼️ [**Browse the full visual corpus →**](https://foxcouncil.github.io/NAPLPS/)

All 372 renders in one gallery, with transport controls to scrub through the drawing sequence frame by frame, and before/after diffs for every recent change to the renderer. For background on the format, Prodigy, and Telidon, see the Wikipedia articles: [NAPLPS](https://en.wikipedia.org/wiki/NAPLPS), [Prodigy (online service)](https://en.wikipedia.org/wiki/Prodigy_(online_service)), [Telidon](https://en.wikipedia.org/wiki/Telidon). ⚠️WARNING:⚠️ These are various amazing rabbit holes, be warned! 🐇🕳️

<table>
  <tr>
    <td align="center"><a href="NAPLPSTests/Visual/Baselines/girl.nap.apng"><img src="NAPLPSTests/Visual/Baselines/girl.nap.apng" width="240"/><br/><sub>girl.nap</sub></a></td>
    <td align="center"><a href="NAPLPSTests/Visual/Baselines/dragon.nap.apng"><img src="NAPLPSTests/Visual/Baselines/dragon.nap.apng" width="240"/><br/><sub>dragon.nap</sub></a></td>
    <td align="center"><a href="NAPLPSTests/Visual/Baselines/seaport.nap.apng"><img src="NAPLPSTests/Visual/Baselines/seaport.nap.apng" width="240"/><br/><sub>seaport.nap</sub></a></td>
  </tr>
  <tr>
    <td align="center"><a href="NAPLPSTests/Visual/Baselines/earth.nap.apng"><img src="NAPLPSTests/Visual/Baselines/earth.nap.apng" width="240"/><br/><sub>earth.nap</sub></a></td>
    <td align="center"><a href="NAPLPSTests/Visual/Baselines/SAIL.NAP.apng"><img src="NAPLPSTests/Visual/Baselines/SAIL.NAP.apng" width="240"/><br/><sub>SAIL.NAP</sub></a></td>
    <td align="center"><a href="NAPLPSTests/Visual/Baselines/santa.nap.apng"><img src="NAPLPSTests/Visual/Baselines/santa.nap.apng" width="240"/><br/><sub>santa.nap</sub></a></td>
  </tr>
</table>

A from-scratch, spec-focused parser, renderer, encoder, and authoring toolchain, with 375+ historical example files. The 1983 BYTE Magazine NAPLPS articles and historical spec documents are bundled in [`docs/`](docs/) as reference. 📚

| | |
|--------|-------|
| Current version | 0.12.0 |
| Tests | 800+ unit tests, plus round-trip Telidraw and visual regression baselines across the corpus |
| Spec coverage | 98% (see [`docs/gaps.md`](docs/gaps.md) for needs, and [`docs/naplps.md`](docs/naplps.md) for the knowns) |

## What's in the box 📦

- 🧩 **`NAPLPS/`**. The library: parser, renderer, encoder, command builder. Reads and writes `.nap` byte streams, renders to ImageSharp `Image<Rgba32>`, and is AOT-compatible (`<IsAotCompatible>true</IsAotCompatible>`).
- 🖥️ **`Telidraw/`**. The multi-platform Avalonia desktop editor, viewer, and exporter. Canvas drawing tools, attribute editors, DRCS / Texture designers, sequence inspector, a Telidraw text-source pane (AvaloniaEdit-backed), TCP networking, and APNG export.
- ✍️ **`Telidraw`**. A word-number DSL (`.td` files) that compiles to `.nap` and decompiles back byte-identical. The canonical human-editable source format. See [docs/telidraw.md](docs/telidraw.md) for the language reference.
- 🧪 **`NAPLPSTests/`**. Full suite: per-command unit tests, Telidraw lexer/parser/compiler tests, the headline round-trip test across every example, and the visual regression suite (renders each example to APNG and byte-compares against a curated baseline).

## Quick start 🚀

```bash
# Build everything
dotnet build NAPLPS.sln

# Launch the editor
dotnet run --project Telidraw

# Compile a .td source file to .nap
dotnet run --project Telidraw -- compile path/to/scene.td -o path/to/scene.nap

# Export .nap as APNG
dotnet run --project Telidraw -- export path/to/scene.nap --format=apng

# Dump file metadata
dotnet run --project Telidraw -- info path/to/scene.nap
```

Open any file from `Examples/` to see it render. The repo includes 375+ historical NAPLPS files, plus hand-authored Telidraw templates in `Examples/telidraw/` (hello, house, star, spirograph, snowflake, clock, palette demo, menu page). 🎨

Toggle **View > Toolbox** to enter authoring mode. Toggle **View > Properties** for the attribute panel. The Telidraw source pane is accessible from the main canvas area when a file is loaded; edits sync back to the visual canvas.

## Documentation map 🗺️

| Doc | What's in it |
|---|---|
| [README.md](README.md) | You are here. Cliff notes and pointers. |
| [Visual corpus](https://foxcouncil.github.io/NAPLPS/) | 🖼️ Every render in the corpus, frame-by-frame playback, and the change history of the renderer |
| [docs/app.md](docs/app.md) | 🖥️ The editor: tools, panels, menus, keyboard shortcuts, CLI commands, export formats, network mode, macro recorder, DRCS / Texture designers |
| [docs/naplps.md](docs/naplps.md) | 📖 The spec: byte-stream layout, G-set invocation, ESC sequences, PDI command family, coordinate encoding, text / color / texture semantics, mosaic + DRCS, plus how the parser implements the state machine |
| [docs/telidraw.md](docs/telidraw.md) | ✍️ The DSL: keyword reference, directives, block structure, expression grammar, round-trip guarantees |
| [docs/quickref.md](docs/quickref.md) | 🔖 Normative reference: opcode tables, operand layouts, in-use table rules |
| [docs/gaps.md](docs/gaps.md) | 🕳️ Spec gaps and deliberate deviations, by category |
| [docs/tools.md](docs/tools.md) | 🔧 Developer workflow: running tests, visual regression workflow, AOT status, dev commands, native interop examples |
| [tools/aot/README.md](tools/aot/README.md) | 🌐 Ten native interop demos (C, C++, Rust, Python, Node.js, Go, Zig, PHP, Ruby, Swift) that link against the AOT-published library |
| [IDEAS.md](IDEAS.md) | 💡 Ideas and open TODOs |
| [docs/](docs/) | 📚 Original source material: 1983 BYTE Magazine NAPLPS articles, ANSI X3.110-1983 standard PDF |

## Key invariants ✅

Every commit must maintain:

1. ✅ **All unit tests pass** (spec, commands, DSL, encoder, decoder, Telidraw compiler, renderer)
2. 🔁 **Telidraw round-trip byte-identical** across the full corpus (`.nap` → decompile → recompile → same bytes)
3. 💾 **Disk-honest** (`FromFile → ToBytes` matches source bytes; the parser preserves every byte, even spec-undefined ones, for lossless round-trip)
4. 🖼️ **Visual regression baselines pass** (APNG bit-identical to a curated corpus)
5. 🧪 **Editor launches, renders, saves** (smoke test)

Running the full suite:

```bash
dotnet test NAPLPSTests/NAPLPSTests.csproj
```

## License 📄

MIT. See [`LICENSE`](LICENSE).

## Contributing 🤝

Pull requests welcome, especially for:

- Long-tail spec items in [docs/gaps.md](docs/gaps.md) (text rendering, G2 visual accent composition on opt-in, proportional spacing spec-strict mode)
- Additional BBS / Telidon / Prodigy file format support in the parser
- Fresh ideas for the editor (see [IDEAS.md](IDEAS.md) for the current wishlist: networked multiplayer drawing, macro recording, Telidraw extensions, export formats, accessibility features)

Please run the full test suite before submitting. The round-trip and visual regression tests catch most regressions that type-level unit tests miss. 🎯
