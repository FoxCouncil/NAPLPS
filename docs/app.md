# NAPLPSApp: Editor and CLI Guide 🖥️✨

The NAPLPS desktop application is a multi-platform Avalonia-based editor, viewer, and exporter for NAPLPS files. It runs on Windows, macOS, and Linux. Launch it with `dotnet run --project NAPLPSApp`, or use it headless via the CLI subcommands documented at the bottom of this file.

TODO: Link to installers

## Surfaces 🪟

When you open a `.nap` file you get three optional panes around the render canvas:

- 🧰 **Toolbox** (left). 11 drawing tools, fill-mode toggle, color swatches.
- 🎛️ **Properties panel** (right). Context-sensitive attribute editors.
- ✍️ **Telidraw source pane** (bottom, toggleable). Live DSL view of the current file.

The **status bar** below the canvas shows the pointer coordinate in NAPLPS-normalized space (`X ∈ [0, 1]`, `Y ∈ [0, 0.75]` for the default 4:3 aspect). A parse-warning badge appears on the right when the loaded file has errors; click to jump to the first offending command.

## Drawing tools ✏️

Activate a tool by clicking its toolbar button or pressing its hotkey.

| Hotkey | Tool | Behavior |
|---|---|---|
| `V` | Select | Click to pick a command, Shift/Ctrl to add to selection, drag for marquee. Drag vertex handles on single-vertex commands to edit coordinates. |
| `M` | Move Pen | Click places the absolute pen position (emits `POINT SET ABSOLUTE`). |
| `L` | Line | Click-drag to draw a line from pen to release point. |
| `R` | Rectangle | Click-drag; chevron flyout toggles filled / outlined. |
| `P` | Polygon | Click vertices; double-click or Shift+click to close. Chevron flyout toggles filled / outlined. |
| `A` | Arc | Click three points: start, mid, end. Chevron flyout toggles filled / outlined. |
| `T` | Text | Click placement, type characters. The canvas uses the current Text Attributes section's char size / rotation / path / spacing. |
| `F` | Fill | Click an outlined shape to duplicate it as filled in the current foreground color. |
| · | Scribble (Line) | Freehand pen-drag emitting an `INCREMENTAL LINE` command with motion codes derived from the drag samples. |
| · | Scribble Filled | Same as Scribble Line but emits `INCREMENTAL POLYGON FILLED`. |
| · | Raster Paint | Drag to paint 1-bit pixels (`INCREMENTAL POINT`). |

**Fill mode toggle** (next to the Rectangle tool) makes the shape tools emit filled variants by default. The chevron flyout on it configures the current fill pattern (solid / vertical / horizontal / mesh) and pel size, then `Emit TEXTURE command` writes a `TEXTURE` command into the stream.

## Properties panel 🎛️

Right-side dock, scrollable, with collapsible sections. Only shows in editor mode. All sections are independent; activating one doesn't affect the others.

### Selection inspector 🔍

Appears only when a command is selected. Shows:

- Command index in the stream, name, opcode hex + decimal, raw operand bytes
- Type-specific decoded summary (e.g. `sv=1 mv=3 dim=2` for Domain, `fg=7 bg=0` for SelectColor)
- Editable fields for single-vertex geometric commands (`POINT SET ABSOLUTE`, `POINT ABSOLUTE`, `LINE ABSOLUTE`), `SELECT COLOR`, and `DOMAIN`. Edit and hit `Apply Edit` to re-emit the command in place. Undo preserves the exact prior bytes.
- `Delete` button removes the command from the stream.

For multi-vertex geometric commands (polygon sets, line sets, arc sets), use the drag-to-edit vertex handles on the canvas instead; they're shown as cyan squares when the command is selected.

**Shift+Arrow** nudges the selected command's coordinates by one pel in the corresponding direction. Plain arrow keys stay bound to frame navigation so animation playback isn't disrupted.

### Palette 🎨

Foreground / background palette index (0–15). These drive the default color for newly-drawn commands unless the current tool emits its own `SELECT COLOR`.

### Text Attributes 🔤

Configures the next `TEXT` command emission:

- Char Width / Height in unit-screen coords
- Rotation: 0 / 90 / 180 / 270
- Path: Right / Left / Up / Down
- Inter-character spacing: One / 5:4 / 3:2 / Proportional
- Inter-row spacing: One / 5:4 / 3:2 / Two

The live Text tool reads these values at each keystroke.

### Domain (geometry precision) 📐

Single-byte width, multi-byte width, dimensionality (2D / 3D), logical pel X/Y. `Emit DOMAIN command` writes the spec-correct fixed byte plus optional logical-pel vertex. After a DOMAIN emit, every subsequent geometric command re-encodes at the new byte widths; this is the core mechanism for varying coordinate precision across a file.

### Field

Origin X/Y plus Dimensions W/H. Emitting a `FIELD` command defines the active drawing / text region for subsequent incremental commands. Omit all four fields and the emitted command takes no operands, which resets the field to the full unit screen.

### Blink ✨

Target palette index (the color the foreground swaps TO during the on-interval), On / Off intervals in 1/10 second units, optional start delay. `Emit BLINK command` defines a blink process; `Stop all blink processes` emits a blink-with-no-operands which terminates every active process.

### Line / Texture

Line pattern (solid / dashed / dotted / dot-dash), highlight checkbox. `Emit TEXTURE (line) command` writes a `TEXTURE` command with the line-pattern fields set.

### Color Mode

Toggles color mode 0 (direct RGB) / 1 (palette) / 2 (palette with explicit FG+BG). Transparent checkbox emits `SET COLOR TRANSPARENT`. `Emit color-mode command` writes the appropriate control.

### Network 🌐

TCP listener plus sender, for driving a remote NAPLPS decoder or receiving a stream from one.

- **Listen on port** (default 5510). Start / Stop toggles the listener. When active, incoming bytes get parsed and rendered frame-by-frame on the local canvas.
- **Remote host / port**. `Send current document` pushes the loaded `.nap` byte stream to the remote listener. A simulated baud-rate (Menu > NAPLPS > Speed) can throttle the send for authentic period playback.

### Macro Recorder 🎬

Capture a sequence of drawing operations into a macro that can be invoked by name later.

1. Enter a slot character (A-Z) in the textbox
2. Click `Start Recording`. Red indicator appears, all emitted commands get buffered.
3. Draw normally
4. Click `Stop & Save`. Emits `DEF MACRO <slot>` plus buffered bytes plus `END`, added as a single undo step.
5. `Cancel Recording` discards the buffer without emitting anything

Invoke a recorded macro later via the DEF MACRO bytecode (the parser runs its bytes automatically when encountered during render).

## Menus 🧭

### File 📁

- **New**. Blank `.nap` canvas.
- **Open**. Load `.nap` or `.td` file.
- **Save** / **Save As** (Ctrl+S / Ctrl+Shift+S)
- **Close**. Close current document.
- **Import > SVG**. Convert SVG paths to Telidraw line segments, then compile.
- **Import > Bitmap**. Quantize raster to 16-color palette and emit as filled cells.
- **Export**. Open the Export dialog.
- **Quit**

### Edit

- **Undo / Redo** (Ctrl+Z / Ctrl+Y). Tool emissions that produce multiple commands (`MOVE` + `LINE`, pattern prefix + shape, etc.) are grouped into one undo step via `AddCommandsAction` / `CompositeAction`. Drag-to-edit and attribute-panel edits use `ReplaceCommandAction` so the prior bytes restore exactly.

### NAPLPS

- **Palette > Load NAPLPS Default / Load Prodigy**. Emit `SET COLOR` commands for the canonical 16-entry CLUT of the chosen profile.
- **DRCS Character**. Open the DRCS designer (see below).
- **Texture Mask**. Open the Texture designer.
- **Macro Recording > Start / Stop & Save / Cancel**. Same as the Properties-panel buttons.
- **Network > Start Listener / Stop Listener / Send to Remote**. Same as the Properties-panel buttons.
- **Re-render**. Force `DrawContext` rebuild (useful after a manual byte edit).
- **Animate / Loop**. Frame-by-frame playback with optional looping.
- **Palette Animation**. Visualize active BLINK processes as a color cycle.
- **Speed**. Simulated baud rate (460800, 230400, …, 1200, 0 = unlimited) for authentic period playback timing.

### View

- **Toolbox**. Show/hide the left dock.
- **Properties**. Show/hide the right dock.

### Help

- **Help (github.com)**. Opens the wiki.
- **GitHub Code**. Opens the repo.
- **About**. Version plus project description dialog.

## Asset designers 🎨

### DRCS Character Designer

**Menu > NAPLPS > DRCS Character**

Design a custom 8×10 bitmap character to upload as a DRCS (Dynamically Redefinable Character Set) entry. The spec actually allows the body to be a full NAPLPS stream that renders to an offscreen bitmap (see naplps.md §DRCS), but the designer uses the simplified grid form.

- 8 columns × 10 rows toggle grid
- `Clear Grid` / `Invert Grid` buttons
- Slot character input (1 char, A-Z plus some punctuation)
- `Commit DRCS` writes a `DEF DRCS` command into the current stream

### Texture Mask Designer

**Menu > NAPLPS > Texture Mask**

Design a fill pattern plus mask for the `TEXTURE` command.

- Dual 8×8 grids: **PATTERN** (yellow when on) for the fill bits, **MASK** (cyan when on) for the stencil bits
- Slot selector A–D
- `Clear Pattern` / `Clear Mask` buttons
- `Commit Texture` writes a `DEF TEXTURE` command

## Keyboard shortcuts ⌨️

| Gesture | Action |
|---|---|
| `V` / `M` / `L` / `R` / `P` / `A` / `T` / `F` | Activate Select / MovePen / Line / Rectangle / Polygon / Arc / Text / Fill tool |
| `Esc` | Cancel in-progress draw (e.g. mid-polygon click sequence) |
| `Ctrl+Z` / `Ctrl+Y` | Undo / Redo |
| `Ctrl+S` / `Ctrl+Shift+S` | Save / Save As |
| `Delete` | Delete selected command |
| `Shift+←` / `Shift+→` / `Shift+↑` / `Shift+↓` | Nudge selected command's coordinates by 1 pel |
| `←` / `→` | Previous / Next animation frame |
| `Home` / `End` | First / Last frame |
| `F5` | Force Telidraw recompile (when auto-recompile is racing) |

## Export dialog 📤

**File > Export** (or `export` CLI command) opens a dialog with format-dependent options.

| Format | Options |
|---|---|
| **PNG** | Scale (0.5×–8×), transparent background |
| **JPEG** | Scale, quality slider (1–100, default 90) |
| **BMP** | Scale |
| **GIF** | Scale, transparent background, loop, frame delay (1/100s) |
| **APNG** | Scale, frame delay (ms), blink cycles to append, loop, frame range (start / end, 1-based, 0 = no clip) |

The dialog displays the estimated APNG frame count (= WAIT commands + 1) so you know what you're getting before committing.

## CLI reference 💻

Invoke as `dotnet run --project NAPLPSApp -- <command> [args]`, or once published, `Telidraw.exe <command> [args]`.

### `info <file> [--format=text|json]`

Print file metadata: detected system type, byte count, command count, parse errors, validation warnings. JSON output is suitable for piping into a tool.

### `compile <file.td> [-o output.nap]`

Compile a Telidraw source to binary. Lex, parse, and compile phases each emit their diagnostics to stderr with source context. Exits non-zero on any error. Success prints `Compiled: <N> commands, <M> bytes` to stdout.

### `export <file> [output] [options]`

Export `.nap` to raster format.

- `--format=png|jpeg|bmp|gif|apng` (default: png)
- `--size=WxH` (default: 1024x768)
- `--at=FRAMES`. Printer-style frame range expression for PNG output: `1,2-5,500` exports individual frames by index.
- `--stdout` / `-`. Pipe to stdout instead of writing a file.
- `--loop` (GIF/APNG only)
- `--delay=N`. Frame delay in 1/100s (default: 5).
- `--palette-anim`. Export blink-process animation as GIF.
- `--frames=N`. Frame count for palette-anim export (default: 120).
- `--blink-cycles=N`. Additional blink cycles appended to APNG.

### `export --batch <dir> [--output-dir=path]`

Parallel multi-file export. Every `.nap` under `<dir>` gets rendered concurrently. Progress logs stream to stderr.

### `diff <file1> <file2> [--mode=text|visual]`

Compare two files. Text mode shows command-level differences; visual mode renders both and writes a side-by-side PNG highlighting diff pixels.

### `help` / `-h` / `--version`

Help text and version.

## Tips 💡

- The editor auto-saves drafts to a temporary file; `File > Open` lets you recover after a crash.
- When `IsFileDirty` is true, the title bar suffixes an asterisk.
- The sequence inspector (`View > Sequence`) uses a `DataGrid` that's virtualized, so large files like `canada1.nap` (2140 commands) stay scrollable.
- Network mode is super fun 🎉
