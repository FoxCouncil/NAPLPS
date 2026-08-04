# Ideas & Open TODOs ✨

A scratch pad for stuff that isn't a bug and isn't scoped into a release, but shouldn't get lost.

## Pending doc TODOs 📝

- **Installer links** (from `docs/app.md`). Once there's a tagged release with installers published (MSIX for Windows, DMG for macOS, `.deb` / AppImage for Linux), wire the download links into the editor guide's intro.

## Housekeeping 🧹

- **Re-enable `Avalonia.Diagnostics` 12.x** once it ships on NuGet. Currently commented out in `Telidraw/Telidraw.csproj`; the 11.3.14 release is the latest at time of bump and isn't ABI-compatible with Avalonia 12.

## Ideas worth considering 💡

- Networked multiplayer drawing on the canvas 🎨
- Macro recorder for scripted edit sequences 🎬
- Extra Telidraw syntax sugar (mosaic high-level form, incremental high-level forms)
- Accessibility pass (keyboard-only authoring, screen reader labels)
- More export formats (SVG, animated GIF, legacy CPT)
- Contribution sprint on the long-tail items in [`docs/gaps.md`](docs/gaps.md) 🎯

Add to this file instead of leaving TODOs scattered through the docs.
