# NAPLPS: Spec Overview 📖✨

A living and working guide to <a href="naplps_standard_-_ansi_x3.110-1983_smaller.pdf" target="_blank" rel="noopener">ANSI X3.110-1983</a>. Covers the byte stream, state machine, and command set, plus where our implementation in `NAPLPS/NaplpsFormat.cs` makes specific choices.

For the compressed opcode table see [quickref.md](quickref.md). For the Telidraw DSL see [telidraw.md](telidraw.md).

The <a href="naplps_standard_-_ansi_x3.110-1983_smaller.pdf" target="_blank" rel="noopener">ANSI X3.110-1983</a> PDF is bundled in `docs/` in different formats for supplementary reading.

## Byte stream 🧬

A NAPLPS file is a byte stream. No header, no length prefix, no magic number. The first byte is the first command. Decoding is a state machine.

The 256-byte code page has four regions:

| Bytes | Region | Purpose |
|---|---|---|
| 0x00-0x1F | C0 | 32 control codes (NUL, SOH, ..., SHIFT-OUT, ESC, NSR) |
| 0x20-0x7F | GL | 96-entry "graphic left", invoked from one of G0-G3 |
| 0x80-0x9F | C1 | 32 control codes (NAPLPS-specific: DEF MACRO, BLINK START, PROTECT, ...) |
| 0xA0-0xFF | GR | 96-entry "graphic right", invoked from one of G0-G3 |

In 7-bit transmission only 0x00-0x7F is addressable; 8-bit adds 0x80-0xFF. NAPLPS streams can be either, and can switch mid-stream via locking shifts. A well-formed file starts 7-bit and shifts to 8-bit through SHIFT-OUT and locking shifts, or stays 7-bit throughout.

**Numerical-data bytes**: bytes in the GL range (0x20-0x7F) or GR range (0xA0-0xFF), depending on mode, that aren't themselves opcodes. In 8-bit mode these have bit 7 set (base 0xC0, effective bytes 0xC0-0xFF); in 7-bit mode they have bit 7 clear (base 0x40, effective bytes 0x40-0x7F). Only the low 6 bits carry data. Every PDI command's operand data lives in these bytes.

## State machine 🧠

At any instant the decoder tracks:

- Four **G-sets** (G0-G3), each a table of up to 96 command or character definitions.
- Which G-set is **invoked** into GL, which into GR. Invocations can be locking or single-shift.
- The **in-use table**, a 256-entry array mapping each byte value to its current interpretation. Rebuilt whenever an invocation or designation changes.
- **Pen position**, current color, character size, text path, domain parameters, active field, pending accent, blink processes, palette.

Decode loop (see `NaplpsFormat.ReadStream`):

1. Read a byte.
2. Look it up in the in-use table.
3. If a C0 or C1 control code, run its handler (may mutate state).
4. If a graphic or character, look up its command class via the G-set.
5. If a numerical-data byte, it's operand data for the command in progress; the command class's `OperandType` tells the reader how many bytes to consume and how to pack them.
6. Instantiate the command, snapshot state, append, loop.

Each command is stored with its pre-command state snapshot in a `NaplpsSequence`, so rendering can replay deterministically.

## G-sets 🔠

Four slots, each holding a designated character or command set. Defaults:

| Slot | Default designation |
|---|---|
| G0 | Primary Character Set (ASCII + NAPLPS extras) |
| G1 | General PDI Set (Reset, Domain, Point, Line, Arc, Rectangle, Polygon, ...) |
| G2 | Supplementary Character Set (94 symbols including accents) |
| G3 | Mosaic Set (2x3 block graphics, 64+ cells) |

Only one G-set can be invoked into GL at a time, same for GR. Default: G0 in GL, G1 in GR.

### Invocation commands (all in C0)

| Byte | Mnemonic | Effect |
|---|---|---|
| 0x0E | SO (Shift Out) | Locking: invoke G1 into GL |
| 0x0F | SI (Shift In) | Locking: invoke G0 into GL |
| 0x19 | SS2 | Single-shift: use G2 for the next byte only |
| 0x1D | SS3 | Single-shift: use G3 for the next byte only |
| 0x1B 0x6E | LS2 | Locking shift: invoke G2 into GL |
| 0x1B 0x6F | LS3 | Locking shift: invoke G3 into GL |
| 0x1B 0x7E | LS1R | Locking shift: invoke G1 into GR |
| 0x1B 0x7D | LS2R | Locking shift: invoke G2 into GR |
| 0x1B 0x7C | LS3R | Locking shift: invoke G3 into GR |

After SS2 the next single byte is resolved via G2; after that byte, GL reverts to whatever was locking-shifted before.

### Designation

ESC sequences also redesignate a G-set slot, loading a different character or command table. Syntax: `ESC` + one or two intermediate bytes + a final byte. Final bytes live in 0x30-0x7E (7-bit form) or 0xA0-0xFE (8-bit form, with bit 7 set). Our parser handles both ranges; an earlier version only handled 7-bit, which caused 82 stray `0xDF` bytes from 8-bit ESC final bytes to leak as separate commands.

If a redesignated slot is currently invoked, the in-use table rebuilds immediately (spec §4.3.2: "the new code interpretations are simultaneously invoked"). Implemented in `NaplpsState.DesignateGset`.

## PDI command family 🎨

Graphic commands living in the General PDI Set (G1 by default). At the default designations they occupy bytes 0xA0-0xBF in GR and 0x20-0x3F in GL (after SO invokes G1 into GL).

| Opcode (8-bit) | Command | Operands |
|---|---|---|
| 0xA0 | RESET | 0, 1, or 2 fixed bytes |
| 0xA1 | DOMAIN | 1 fixed byte (sv/mv/dim) + optional logical-pel vertex |
| 0xA2 | TEXT | 1-2 fixed attribute bytes |
| 0xA3 | TEXTURE | 1 fixed byte (linePattern, highlight, fillPattern) + optional mask-size vertex |
| 0xA4 | POINT SET ABSOLUTE | 1 vertex (mv bytes) |
| 0xA5 | POINT SET RELATIVE | 1 vertex (relative) |
| 0xA6 | POINT ABSOLUTE | 1 vertex, draws pixel at pen |
| 0xA7 | POINT RELATIVE | 1 vertex relative |
| 0xA8 | LINE ABSOLUTE | 1 vertex (endpoint) |
| 0xA9 | LINE RELATIVE | 1 vertex relative |
| 0xAA | LINE SET ABSOLUTE | N independent vertices |
| 0xAB | LINE SET RELATIVE | N relative deltas |
| 0xAC | ARC OUTLINED | mid-rel + end-rel (2 vertices) |
| 0xAD | ARC FILLED | mid-rel + end-rel |
| 0xAE | ARC SET OUTLINED | start-abs + mid-rel + end-rel (3 vertices) |
| 0xAF | ARC SET FILLED | same |
| 0xB0 | RECTANGLE OUTLINED | 1 vertex (width, height) |
| 0xB1 | RECTANGLE FILLED | 1 vertex |
| 0xB2 | RECTANGLE SET OUTLINED | 2 vertices: (x, y) + (w, h) |
| 0xB3 | RECTANGLE SET FILLED | same |
| 0xB4 | POLYGON OUTLINED | N relative vertices from pen |
| 0xB5 | POLYGON FILLED | same |
| 0xB6 | POLYGON SET OUTLINED | 1 absolute start + N relative tail |
| 0xB7 | POLYGON SET FILLED | same |
| 0xB8 | INCREMENTAL FIELD | 0, 1, or 2 vertices (empty = full screen) |
| 0xB9 | INCREMENTAL POINT | 1 header byte (bpp) + packed pixel data |
| 0xBA | INCREMENTAL LINE | start vertex + packed motion codes |
| 0xBB | INCREMENTAL POLYGON FILLED | start vertex + packed motion codes |
| 0xBC | SET COLOR | RGB fixed bytes, mode-dependent length |
| 0xBD | WAIT | 1 byte tenths-of-a-second; the renderer captures a frame here |
| 0xBE | SELECT COLOR | 1 or 2 palette-index bytes depending on color mode |
| 0xBF | BLINK | 1 target-index byte + triples of (on, off, start-delay) 1/10s bytes |

In 7-bit transmission these shift down to 0x20-0x3F. Our encoder strips bit 7 at emit time when `Use7BitMode` is active.

## Coordinate encoding 📐

Coordinates are signed binary fractions packed across a variable number of bytes (the `mv` from the current DOMAIN).

A 2D vertex with `mv=N` occupies N bytes. Each byte's low 6 bits carry 3 bits of X followed by 3 bits of Y, MSB-first. The first byte's first X bit is a sign bit; subsequent bits are fraction. For `mv=3` (default), 8 fraction bits per axis (1/256 precision). For `mv=4`, 11 fraction bits (1/2048). For `mv=2`, 5 fraction bits (1/32).

Unit-screen coords run X in [0, 1], Y in [0, 0.75] (4:3 aspect).

Decode: for each axis, bit 0 is sign. If 0 (positive), the remaining N-1 bits are summed at weights 1/2, 1/4, ..., 1/2^(N-1). If 1 (negative), the same sum is computed then -1 added. See `NaplpsEncoder.DecodeVertex2D` and `NaplpsUtils.ConvertBitsToFraction`; encode is the exact inverse.

Telidraw's `Fmt` helper uses `"R"` round-trip float format for any value that doesn't align to one of the common denominators (256, 2048, 32, 40, 128, 80). This ensures bit-exact round-trip across all corpus files regardless of mv.

## Color modes 🌈

Three modes:

| Mode | SELECT COLOR operand | SET COLOR operand | Behavior |
|---|---|---|---|
| 0 | unused | RGB triplet | Direct RGB, no palette |
| 1 | 1 byte (4-bit FG index) | RGB triplet at current pointer | 16-color palette; SELECT chooses FG, SET redefines entry |
| 2 | 2 bytes (FG, BG indices) | 2 RGB triplets (FG + BG) | 16-color palette with explicit BG for every text cell |

Mode is set by a `SET COLOR MODE` command with an ordinal operand. Our default start state is mode 2.

Transparency is a special case of SET COLOR: `SET COLOR TRANSPARENT` emits a control that shows the underlying canvas instead of a fill color.

Systems capable of more than 16 colors can construct an extended CLUT via `NaplpsState.GenerateDefaultPalette(N)`. First half is a greyscale ramp (R=G=B uniformly spaced), second half is hues walking the hue circle (Blue=0°, Red=120°, Green=240°). Opt in via `new NaplpsState(256)` or `NaplpsFormat.New(systemType, colorCapacity: 256)`.

## Text and glyphs 🔤

The `TEXT` command carries attribute bytes for the character cell:

- Character size (implicit from DOMAIN or explicit from TEXT)
- Rotation: 0°, 90°, 180°, 270°
- Path: Right, Left, Up, Down (cursor advance direction)
- Inter-character spacing: One, 5:4, 3:2, Proportional
- Inter-row spacing: One, 5:4, 3:2, Two
- Cursor style, move attributes (protected / unprotected)

### Proportional spacing

Uses a width-class displacement table: glyph advance = `charFieldWidth * displacement[widthClass][rowIndex] / n`. Our implementation matches the visual output of period renderers we compared against; the strict-spec formula produces visibly different spacing. See `DrawableAsciiChar.GetProportionalDisplacement`.

### Word wrap (§5.3.2.3.6)

When a character would advance the pen past the field boundary, the renderer performs an automatic carriage-return + line-feed. In word-wrap mode, trailing spaces are discarded. Hyphen is recognized as a mid-word break point (updates `state.LastWordBreakPen`).

The current implementation is char-by-char with space-discard. A full backtracking algorithm (redraw the partial word on the new line) is the biggest remaining long-tail item.

### Non-spacing accents

G2 supplementary positions 0x40-0x4F: ´ ` ¨ etc. They don't advance the pen; they compose onto the next character at the same field origin. State machinery captures this via `NaplpsState.PendingAccentChar` and `AsciiCharCommand.OverlayAccent`. Visual rendering of the overlay is currently disabled to preserve visual regression baselines; one line in `DrawableAsciiChar.Draw` enables it.

## Mosaic graphics 🧱

The Mosaic Set (G3 by default) contains 2x3 block graphics. Each byte 0x20-0x7F (when G3 is invoked) selects one of 64 patterns where a character cell is divided into 6 rectangles and each bit of the byte paints one. This is how block graphics render without true pixel ops.

The parser parses mosaic commands correctly; the Telidraw decompiler doesn't emit a high-level DSL form. Mosaic commands go out as raw `MOSAIC-ELEMENT N` lines (preserving bytes). This is the largest remaining category of raw lines (~10% of the 3.7% overall raw rate).

## DRCS ✍️

`DEF DRCS` (C1 opcode 0x83) defines a custom glyph in a user-assigned slot. The operand is a start-code byte plus a body. The body is, per spec, a NAPLPS stream that renders to an offscreen bitmap. Our implementation (`NaplpsFormat.ParseDrcsData`) runs the body through a recursive ReadStream call, captures the render to a 32xN monochrome bitmap (aspect ratio derived from the current CharSize), and stores it in `State.DrcsCharacters[startCode]`.

A recursion depth guard caps DRCS-inside-DRCS at 4 levels to prevent pathological input from stack-overflowing. Real files don't nest DRCS.

## Incremental commands ⚡

For raster-style paint operations (large solid fills, scribbles, bitmaps), the incremental commands pack many operations into one opcode:

- **INCREMENTAL POINT**: bits-per-pixel header + packed pixel values. Each value is MSB-first in the 6-bit data area of each operand byte.
- **INCREMENTAL LINE**: start vertex + packed motion codes. Each motion code is 2 bits: step (00 = meta), advance-and-draw variants (01/10/11). Three codes pack into each operand byte.
- **INCREMENTAL POLYGON FILLED**: same as incremental line but the closed shape gets filled.
- **INCREMENTAL FIELD**: not a paint op; defines the active field region for subsequent incremental ops.

## C1 control codes 🎛️

In 8-bit transmission these sit at bytes 0x80-0x9F. In 7-bit they're accessed via ESC + 0x40-0x5F.

| Opcode | Mnemonic | Purpose |
|---|---|---|
| 0x80 | DEF MACRO | Begin macro definition (body until END) |
| 0x81 | DEF P-MACRO | Protected macro variant |
| 0x82 | DEF T-MACRO | Transient macro |
| 0x83 | DEF DRCS | Dynamically Redefinable Character Set entry |
| 0x84 | DEF TEXTURE | Custom texture mask |
| 0x85 | END | Terminates a DEF block |
| 0x86 | REPEAT | Repeat next character N times |
| 0x87 | REPEAT TO EOL | Repeat last character until end of line |
| 0x88 | REVERSE VIDEO | FG / BG swap |
| 0x89 | NORMAL VIDEO | Cancel reverse |
| 0x8A-0x8F | SMALL / MED / NORMAL / DOUBLE-HEIGHT / BLINK-START / DOUBLE-SIZE TEXT | Text size / attributes |
| 0x90 | PROTECT | Mark field protected |
| 0x91-0x94 | EDC1-EDC4 | End of Data Codes (reserved) |
| 0x95 | WORD WRAP ON | Enable auto-wrap |
| 0x96 | WORD WRAP OFF | Disable auto-wrap |
| 0x97 | SCROLL ON | Enable scroll-up on LF past bottom |
| 0x98 | SCROLL OFF | Disable |
| 0x99-0x9C | UNDERLINE / CURSOR controls | Visual attributes |
| 0x9D | BLINK STOP | End blink processes |
| 0x9F | UNPROTECT | Remove field protection |

Most parse as `ControlCommand` with a specific `Command` enum value.

## Parser structure 🔬

Entry point is `NAPLPS/NaplpsFormat.cs`:

1. **`NaplpsFormat.FromFile`** loads bytes, detects system type from the first byte, applies system-specific defaults, then runs `ReadStream`.
2. **`ReadStream`** is the main decode loop. Unknown bytes get a bare `NaplpsCommand` plus a recorded error, and are preserved for round-trip.
3. **`ControlCommandEscape`** handles ESC sequences, accepting intermediate bytes (0x20-0x2F) and final bytes in both 7-bit (0x30-0x7E) and 8-bit (0xA0-0xFE) ranges.
4. **`TryInstantiateCommand`** uses `Activator.CreateInstance` with DAM annotations so the trimmer preserves command subclass constructors under AOT.
5. **`NaplpsSequence`** bundles `(preCommandState, command)`. The command list is the authoritative representation; rendering walks it, Telidraw compilation produces it, decompilation reads it.
6. **`NaplpsFormat.ToBytes`** serializes back. Every parsed byte round-trips exactly.

Each command stores pre-command state attached to its sequence entry. The renderer iterates the command list and uses each snapshot to decide how to draw. Given the same command sequence and starting state, the renderer is deterministic. Editing a command mid-file doesn't corrupt downstream snapshots at parse time; the editor calls `BuildDrawContext` after every edit, which re-parses the command list from scratch so the canvas stays current.

## Gaps 🕳️

The gaps we believe exist in the current implementation are documented here [docs/gaps.md](gaps.md).

All other documented spec items are implemented and tested.

## Pointers 📍

- `NAPLPS/NaplpsFormat.cs`: parser, entry point
- `NAPLPS/NaplpsState.cs`: state machine, G-sets, palette, field, pen
- `NAPLPS/NaplpsEncoder.cs`: byte-level encoding for vertex 2D, fixed bytes, palette indices
- `NAPLPS/Commands/*.cs`: one class per PDI / C0 / C1 opcode
- `NAPLPS/CommandRegistry.KnownTypes.cs`: static list of every `[AddCommand]` class; keep in sync with new additions for AOT correctness
- `NAPLPS/Drawing/DrawContext.cs`: renderer
- `NAPLPS/Drawing/DrawableAsciiChar.cs`: text glyph rendering
- `docs/naplps_standard_-_ansi_x3.110-1983_smaller.pdf`: the standard
- `docs/NAPLPS.ASC`: supplementary reading
