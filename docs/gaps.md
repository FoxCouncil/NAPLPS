# Spec Gaps and Deviations 🕳️✨

Everything our implementation does not match the <a href="naplps_standard_-_ansi_x3.110-1983_smaller.pdf" target="_blank" rel="noopener">ANSI X3.110-1983</a> spec on, organized by whether the deviation is deliberate or outstanding work.

## Deferred (state machinery wired, rendering off by default) 🛠️

### G2 non-spacing accent composition (§5.3.2.1)

Spec: non-spacing accent characters in the G2 supplementary set (positions 0x40-0x4F) compose onto the following spacing character at the same field origin, producing composed glyphs like `é`, `ñ`, `ü`.

Us: the parser flags accents correctly (`AsciiCharCommand.IsNonSpacing`), tracks the pending accent on `NaplpsState.PendingAccentChar`, and attaches it to the next spacing char as `AsciiCharCommand.OverlayAccent`. The renderer in `DrawableAsciiChar.Draw` does NOT paint the overlay. One early-return line inside `Draw` enables the overlay path:

```csharp
if (_command.IsNonSpacing) { return; }
// ...later:
if (_command.OverlayAccent.HasValue)
{
    DrawBitmapFont(image, state, size, _command.OverlayAccent.Value);
}
```

Why it's off: enabling it changes the rendered output of every corpus file that uses G2 accents, which breaks the visual regression baselines.

## Intentional deviations from strict spec 🎯

### Proportional spacing (§5.3.2.3.4)

Spec: defines glyph advance as `charFieldWidth * displacement[widthClass][rowIndex] / n` using a width-class displacement table keyed by character.

Us: the displacement table in `DrawableAsciiChar.GetProportionalDisplacement` matches the visual output of the period renderers we compared against. The strict-spec formula produces visibly different spacing on some glyph combinations.

A spec-strict mode could be added behind a `NaplpsState.ProportionalSpacingMode` flag if needed; the existing table would stay as the default.

### DRCS nested-recursion cap (spec silent)

Spec: the `DEF DRCS` body is a NAPLPS stream that can itself contain any commands, including DEF DRCS. The spec does not set a recursion limit.

Us: `NaplpsFormat.ParseDrcsData` caps DRCS-defining-DRCS at 4 levels of recursion via `_drcsRecursionDepth`. Adversarial input would otherwise stack-overflow. No real-world file nests DRCS, so this cap is defensive only.

## Not yet implemented 🚧

### Backtracking word-wrap (§5.3.2.3.6)

Spec: when a character would overflow the field, the renderer tracks the last word-break position and, on overflow, rewinds to that position before performing the carriage-return + line-feed. This means the partial word at line end migrates to the next line intact, producing true word wrap.

Us: the wrap algorithm is char-by-char with space-discard. A character that overflows the field still renders at its current position (now off the edge), and the next character starts on a new line. The last-break position is tracked (`state.LastWordBreakPen`) but not used for rewinding. Hyphen is recognized as a word-break character but follows the same non-backtracking rule.

To implement properly: defer glyph rendering until the line is complete, accumulate glyphs with their tentative positions, decide wrap points based on the full line, then paint. That is a deferred-render pipeline refactor touching `AsciiCharCommand`, `DrawableAsciiChar`, and `DrawContext`.

### Extended 256-color palette wiring

Spec: systems capable of more than 16 palette entries use the algorithmic extended CLUT. Greyscale ramp in the first half, hue-circle in the second half (Blue=0°, Red=120°, Green=240°).

Us: `NaplpsState.GenerateDefaultPalette(int entryCount)` implements the spec algorithm. `new NaplpsState(256)` opts in. `NaplpsFormat.New(systemType, colorCapacity: 256)` threads the capacity through.

Gap: no corpus file exercises extended colors, so the path has no visual regression coverage. The color-command emitters also don't validate palette indices against the capacity; writing to index 200 in a 16-entry state is not checked.

## Known fringe cases (flagged but preserved)

### Undefined GR positions

Two corpus files contain single bytes (`0xCF` in `movie-search-frame.nap`, `0xD4` in `stephen-birnbaum-travel-column.nap`) that resolve to spec-undefined positions in the currently-invoked G-set. Per spec §4.3.3, "code positions that have no defined meaning shall be ignored."

Us: the parser preserves them as bare `NaplpsCommand` instances for disk-honest round-trip, records a `NaplpsErrorType.UnknownOpcode` error on `NaplpsState.Errors`, and surfaces them in the status-bar parse-warning badge. They render as no-ops.

This is a deliberate deviation from the ignore-silently spec rule, for round-trip integrity. Strict spec conformance would drop these bytes; we keep them.

## DSL decompiler gaps (readability only, not spec)

These don't break the round-trip guarantee or misrender anything. They affect only the human-friendliness of the generated Telidraw source.

### Mosaic Element high-level form

The decompiler emits `MOSAIC-ELEMENT N` raw-byte lines for every mosaic command. A high-level form (e.g. `mosaic pattern row col`) would be more readable. ~10% of the 3.7% raw lines across the corpus are mosaic commands.

### Control C1 codes

Some C1 codes (e.g. `REVERSE VIDEO`, `SCROLL ON`, `UNDERLINE START`) emit as raw bytes in the decompiler. A high-level form with keyword names would improve readability. `BlinkStart`, `BlinkStop`, `RepeatToEOL`, and some others are already at the high-level layer.

### Incremental commands

`INCREMENTAL POINT`, `INCREMENTAL LINE`, and `INCREMENTAL POLYGON FILLED` have byte-packing that depends on pel step, motion code density, and bit-per-pixel choices. The decompiler emits them as raw opcode + operand bytes. A high-level form would need to decode the motion codes back into a vertex list.

## Tracking 📍

When a gap is closed, delete it and update the README's spec coverage percentage. The visual regression + round-trip + unit test invariants must stay green through every change.
