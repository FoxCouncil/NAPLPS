// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using System.Globalization;
using System.Text;

namespace NAPLPS.Telidraw;

/// <summary>
/// Walks a <see cref="NaplpsFormat"/>'s command stream and emits flat Telidraw source
/// text. Per D2, no attempt is made to reconstruct <c>with</c> blocks, loops, or procs —
/// those are authoring constructs that don't exist in the NAPLPS byte stream. The output
/// is a linear sequence of command calls that, when fed back through the <see cref="Compiler"/>,
/// produces byte-identical NAPLPS output.
///
/// The <see cref="NaplpsFormat.New"/> header (CAN + NSR sentinels) is recognized and skipped
/// because the compiler re-emits it automatically. This makes decompile→compile→decompile
/// stable for any compiler-produced file.
/// </summary>
public static class Decompiler
{
    /// <summary>Decompile an in-memory NaplpsFormat to a Telidraw source string.</summary>
    public static string Decompile(NaplpsFormat format)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Decompiled from .nap by Telidraw decompiler");
        sb.AppendLine("#coord fractions");

        // Emit a #bits directive so the compiler round-trips the original file's
        // numerical-data base (0x40 for 7-bit, 0xC0 for 8-bit). Without this, high-bit
        // differences break byte-for-byte equality even though the low 6 data bits match.
        sb.AppendLine($"#bits {(format.Is7Bit ? 7 : 8)}");
        sb.AppendLine();

        // Set encoder bit-width to match source file for the duration of this decompile.
        // The verifier calls the compiler per-candidate; compiled bytes must match the
        // original file's bit mode so high-bit comparison succeeds.
        var priorMode = NaplpsEncoder.Use7BitMode;
        NaplpsEncoder.Use7BitMode = format.Is7Bit;

        // Reset cross-file mode tracker; mixed-mode files emit `#bits N` directives at
        // section boundaries inside EmitCommand based on this.
        _lastEmittedBitMode = format.Is7Bit ? 7 : 8;

        bool inTextRun = false;
        var textBuffer = new StringBuilder();

        for (int i = 0; i < format.Commands.Count; i++)
        {
            var seq = format.Commands[i];
            var cmd = seq.Command;

            // Synthetic sequences (macro expansions) are presentation output; the coded
            // stream carries only the invocation byte, preserved as a raw command.
            if (seq.IsSynthetic)
            {
                continue;
            }

            // Group basic ASCII text characters into a single text "..." statement.
            // ONLY when the opcode byte IS the character code (Primary Character Set in GL).
            // Supplementary chars may have ASCII-range Unicode values (e.g., '/' at opcode 0x49)
            // that would produce wrong bytes if grouped — those go through raw.
            if (cmd is AsciiCharCommand ascii && cmd.OpCode == (byte)ascii.AsciiCharacter && ascii.AsciiCharacter >= 0x20 && ascii.AsciiCharacter <= 0x7E)
            {
                if (!inTextRun)
                {
                    inTextRun = true;
                    textBuffer.Clear();
                }
                textBuffer.Append(ascii.AsciiCharacter);
                continue;
            }

            // Flush any pending text run before the next non-text command.
            if (inTextRun)
            {
                sb.Append("text \"").Append(EscapeString(textBuffer.ToString())).AppendLine("\"");
                inTextRun = false;
            }

            EmitCommand(sb, cmd, seq.State);
            _lastDecompileState = seq.State;
        }

        // Flush final text run.
        if (inTextRun)
        {
            sb.Append("text \"").Append(EscapeString(textBuffer.ToString())).AppendLine("\"");
        }

        // Restore prior bit mode so nested/concurrent decompiles don't bleed state.
        NaplpsEncoder.Use7BitMode = priorMode;

        return sb.ToString();
    }

    /// <summary>Decompile a .nap file on disk to a Telidraw source string.</summary>
    public static string DecompileFile(string napFilePath)
    {
        var format = NaplpsFormat.FromFile(napFilePath);
        return Decompile(format);
    }

    // ---- Per-command emission ------------------------------------------

    /// <summary>
    /// Try every high-level form for this command in priority order. For each candidate,
    /// verify byte-identity by recompiling the proposed Telidraw line and comparing the
    /// emitted opcode+operands to the original command. The first form that round-trips
    /// wins. Falls back to <see cref="EmitRaw"/> when no high-level form matches —
    /// guaranteeing 100% byte fidelity while producing readable output everywhere it's safe.
    /// </summary>
    /// <summary>
    /// Tracks the bit mode of the most-recently emitted high-level command so we can insert
    /// a `#bits N` directive when the next command crosses bit-mode boundaries (mixed-mode
    /// files like ec1060.nap have 7-bit and 8-bit sections interleaved).
    /// </summary>
    [System.ThreadStatic]
    private static int _lastEmittedBitMode;

    [System.ThreadStatic]
    private static NaplpsState? _lastDecompileState;

    private static void EmitCommand(StringBuilder sb, NaplpsCommand cmd, NaplpsState stateBefore)
    {
        foreach (var candidate in HighLevelCandidates(cmd, stateBefore))
        {
            if (RoundTripsExactly(candidate, cmd, stateBefore))
            {
                // Original opcode bit 7 tells us which transmission base this command used.
                // If it differs from the prior emitted high-level form, emit a `#bits N` so
                // the compiler flips Use7BitMode for this section.
                int thisMode = cmd.OpCode < 0x80 ? 7 : 8;
                if (_lastEmittedBitMode != 0 && _lastEmittedBitMode != thisMode)
                {
                    sb.AppendLine($"#bits {thisMode}");
                }
                _lastEmittedBitMode = thisMode;

                sb.AppendLine(candidate);
                return;
            }
        }

        // Raw passes the byte verbatim — bit mode doesn't matter, so don't update tracker.
        EmitRaw(sb, cmd);
    }

    /// <summary>
    /// Generate plausible high-level Telidraw lines for a given NAPLPS command. Each
    /// returned string must be a complete one-line statement. Order matters — return the
    /// most readable form first; the verifier picks the first one that round-trips.
    /// </summary>
    private static IEnumerable<string> HighLevelCandidates(NaplpsCommand cmd, NaplpsState stateBefore)
    {
        switch (cmd)
        {
            // Geometric — decode the operand bytes directly so we never reinvent coordinates.
            case PointSetAbsoluteCommand:
            {
                var (x, y) = NaplpsEncoder.DecodeVertex2D(cmd.Operands);
                yield return $"move {Fmt(x)} {Fmt(y)}";
                break;
            }

            case PointAbsoluteCommand:
            {
                var (x, y) = NaplpsEncoder.DecodeVertex2D(cmd.Operands);
                yield return $"point {Fmt(x)} {Fmt(y)}";
                break;
            }

            case PointSetRelativeCommand:
            {
                var (dx, dy) = NaplpsEncoder.DecodeVertex2D(cmd.Operands);
                yield return $"move-rel {Fmt(dx)} {Fmt(dy)}";
                break;
            }

            case PointRelativeCommand:
            {
                var (dx, dy) = NaplpsEncoder.DecodeVertex2D(cmd.Operands);
                yield return $"point-rel {Fmt(dx)} {Fmt(dy)}";
                break;
            }

            // LINE ABSOLUTE/RELATIVE — a series of coordinate blocks chained into a polyline
            // (X3.110 5.3.3.2, issue #56). Emit every pair; single-block commands come out
            // as the familiar `line x y` form.
            case LineAbsoluteCommand when TryReconstructLineSet(cmd, stateBefore, out var linePts):
            {
                yield return "line " + FormatPolygonArgs(linePts);
                break;
            }

            case LineRelativeCommand when TryReconstructLineSet(cmd, stateBefore, out var lineDeltas):
            {
                yield return "line-rel " + FormatPolygonArgs(lineDeltas);
                break;
            }

            case RectangleFilledCommand:
            {
                var (w, h) = NaplpsEncoder.DecodeVertex2D(cmd.Operands);
                yield return $"rect {Fmt(w)} {Fmt(h)}";
                break;
            }

            case RectangleOutlinedCommand:
            {
                var (w, h) = NaplpsEncoder.DecodeVertex2D(cmd.Operands);
                yield return $"rect-outline {Fmt(w)} {Fmt(h)}";
                break;
            }

            case ResetCommand:
            {
                yield return "reset";
                break;
            }

            case NonSelectiveResetCommand:
            {
                yield return "nsr";
                break;
            }

            // RECTANGLE SET — (x, y) absolute + (w, h) absolute. Two independent vertex2D
            // encodings, no chain, so byte-exact round-trip without an exact form needed.
            case RectangleSetFilledCommand when TryReconstructRectSet(cmd, stateBefore, out var r):
            {
                yield return $"rect-set {Fmt(r.x)} {Fmt(r.y)} {Fmt(r.w)} {Fmt(r.h)}";
                break;
            }

            case RectangleSetOutlinedCommand when TryReconstructRectSet(cmd, stateBefore, out var r):
            {
                yield return $"rect-set-outline {Fmt(r.x)} {Fmt(r.y)} {Fmt(r.w)} {Fmt(r.h)}";
                break;
            }

            // ARC SET — start absolute + relative mid + relative end. Try the all-absolute
            // form first (readable); fall back to the abs-marker form (decoded relatives).
            case ArcSetFilledCommand when TryReconstructArcSet(cmd, stateBefore, out var asf):
            {
                yield return $"arc-set {Fmt(asf.sx)} {Fmt(asf.sy)} {Fmt(asf.mx)} {Fmt(asf.my)} {Fmt(asf.ex)} {Fmt(asf.ey)}";
                yield return $"arc-set abs {Fmt(asf.sx)} {Fmt(asf.sy)} {Fmt(asf.dmx)} {Fmt(asf.dmy)} {Fmt(asf.dex)} {Fmt(asf.dey)}";
                break;
            }

            case ArcSetOutlinedCommand when TryReconstructArcSet(cmd, stateBefore, out var asf):
            {
                yield return $"arc-set-outline {Fmt(asf.sx)} {Fmt(asf.sy)} {Fmt(asf.mx)} {Fmt(asf.my)} {Fmt(asf.ex)} {Fmt(asf.ey)}";
                yield return $"arc-set-outline abs {Fmt(asf.sx)} {Fmt(asf.sy)} {Fmt(asf.dmx)} {Fmt(asf.dmy)} {Fmt(asf.dex)} {Fmt(asf.dey)}";
                break;
            }

            // LINE SET ABSOLUTE — N independent absolute vertices. No chain.
            case LineSetAbsoluteCommand when TryReconstructLineSet(cmd, stateBefore, out var pts):
            {
                yield return "line-set " + FormatPolygonArgs(pts);
                break;
            }

            // LINE SET RELATIVE — emit the decoded deltas as a `line-set-rel` directly.
            // Bytes are the deltas; the compiler re-encodes them as deltas. Round-trip exact.
            case LineSetRelativeCommand when TryReconstructLineSet(cmd, stateBefore, out var deltas):
            {
                yield return "line-set-rel " + FormatPolygonArgs(deltas);
                break;
            }

            // POLYGON SET — first mv bytes = absolute start, remaining = N relative deltas.
            // We emit the FULLY-ABSOLUTE form first (most readable: every vertex is a real
            // point on screen). If float-rounding in the decode→add→subtract→encode chain
            // breaks byte equality, fall through to the relative form which decodes the
            // exact bytes back into pen-relative deltas — guaranteed to round-trip.
            case PolygonSetFilledCommand when TryReconstructPolygonSet(cmd, stateBefore, out var verts, out var startAbs, out var rels):
            {
                yield return "polygon-set " + FormatPolygonArgs(verts);
                yield return $"polygon-set abs {Fmt(startAbs.x)} {Fmt(startAbs.y)} " + FormatRelArgs(rels);
                break;
            }

            case PolygonSetOutlinedCommand when TryReconstructPolygonSet(cmd, stateBefore, out var verts, out var startAbs, out var rels):
            {
                yield return "polygon-set-outline " + FormatPolygonArgs(verts);
                yield return $"polygon-set-outline abs {Fmt(startAbs.x)} {Fmt(startAbs.y)} " + FormatRelArgs(rels);
                break;
            }

            case WaitCommand wc when wc.IsValid:
            {
                yield return $"wait {wc.WaitTime}";
                break;
            }

            case IncrementalFieldCommand ifc when ifc.Operands.Count == 0:
            {
                yield return "field";
                break;
            }

            // FIELD with origin+dimensions: 4 vertex2D-encoded values (mv*2 bytes total = 6 default).
            // The compiler accepts `field originX originY dimX dimY` for this form.
            case IncrementalFieldCommand ifc2 when TryReconstructField(ifc2, stateBefore, out var f):
            {
                yield return $"field {Fmt(f.ox)} {Fmt(f.oy)} {Fmt(f.dx)} {Fmt(f.dy)}";
                break;
            }

            // SELECT COLOR — palette index lives in bits 3-6 of the first operand byte.
            // For mode 2 (two operand groups, single-byte width = 1) propose `color fg bg` first;
            // mode 1 falls through to `color fg`. Verifier picks whichever round-trips.
            case SelectColorCommand sc when sc.Operands.Count >= 1:
            {
                int svBytes = stateBefore.SingleByteValue;
                int values = sc.Operands.Count / Math.Max(1, svBytes);
                byte fg = NaplpsUtils.ConvertBitsToByte([sc.Operands[0, 3], sc.Operands[0, 4], sc.Operands[0, 5], sc.Operands[0, 6]]);

                if (values >= 2 && svBytes == 1 && sc.Operands.Count >= 2)
                {
                    byte bg = NaplpsUtils.ConvertBitsToByte([sc.Operands[1, 3], sc.Operands[1, 4], sc.Operands[1, 5], sc.Operands[1, 6]]);
                    yield return $"color {fg} {bg}";
                }
                yield return $"color {fg}";
                break;
            }

            // DOMAIN — fixed byte packs (singleByte, multiByte, dimensionality).
            case DomainCommand dc when dc.Operands.Count >= 1:
            {
                var (sv, mv, dim) = DomainCommand.ProcessFixedByte(dc.Operands);
                yield return $"domain {sv} {mv} {dim}";
                break;
            }

            // TEXTURE — first operand byte packs (linePattern, highlight, fillPattern).
            case TextureCommand tc when tc.Operands.Count >= 1:
            {
                yield return $"texture {(byte)tc.LineTexture} {(tc.ShouldHighlight ? 1 : 0)} {(byte)tc.TexturePattern}";
                break;
            }
        }
    }

    /// <summary>
    /// Compile a candidate Telidraw line in isolation and verify the resulting opcode +
    /// operand bytes match the original command. Comparison is on the lower 6 data bits
    /// of each operand byte — the only bits the NAPLPS parser reads — so a 7-bit source
    /// (0x40-0x7F operand bytes) and the compiler's 8-bit output (0xC0-0xFF) compare equal
    /// when their data nibbles match. The raw-byte file-level round-trip test still proves
    /// EXACT byte preservation; this verifier guarantees SEMANTIC equivalence per command.
    /// </summary>
    private static bool RoundTripsExactly(string candidateLine, NaplpsCommand original, NaplpsState stateBefore)
    {
        try
        {
            var tokens = new Lexer(candidateLine).Tokenize();
            var parser = new Parser(tokens);
            var ast = parser.Parse();

            if (parser.Diagnostics.Count > 0)
            {
                return false;
            }

            // Per-command bit-mode: if the original opcode has bit 7 clear (0x20-0x7F),
            // it came from a 7-bit transmission section and the compiler must reproduce
            // those low-base bytes (0x40 numerical-data base, no bit 7 on the opcode).
            // This is the per-byte refinement of an earlier file-level approach that broke
            // mixed-mode files.
            var savedBitMode = NaplpsEncoder.Use7BitMode;
            NaplpsEncoder.Use7BitMode = original.OpCode < 0x80;

            NaplpsFormat format;
            Compiler compiler;
            try
            {
                // Seed the compiler with the full pre-state so CurrentMv / sv / dim match
                // what the original command saw. Otherwise any file with `domain mv=2 ...`
                // re-encodes at the builder's default mv=3 → wrong byte count → raw fallback.
                compiler = new Compiler(ast)
                {
                    BareFormat = true,
                    InitialPenPosition = stateBefore.Pen,
                    InitialState = stateBefore,
                };
                format = compiler.Compile();
            }
            finally
            {
                NaplpsEncoder.Use7BitMode = savedBitMode;
            }

            if (compiler.Diagnostics.Count > 0 || format.Commands.Count != 1)
            {
                return false;
            }

            var emitted = format.Commands[0].Command;

            if (emitted.OpCode != original.OpCode || emitted.Operands.Count != original.Operands.Count)
            {
                return false;
            }

            for (int i = 0; i < emitted.Operands.Count; i++)
            {
                if (emitted.Operands[i] != original.Operands[i])
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Universal lossless fallback: emit `raw OPCODE OP1 OP2 ...` with a trailing comment
    /// showing the command name for readability. The compiler's CompileRaw passes the
    /// opcode + operand bytes through verbatim, guaranteeing byte-identity round-trip.
    /// </summary>
    private static void EmitRaw(StringBuilder sb, NaplpsCommand cmd)
    {
        // Canonical raw-byte form: `UPPERCASE-MNEMONIC bytes...`.
        // Uppercase disambiguates from lowercase high-level keywords with the SAME name
        // (e.g. lowercase `domain sv mv dim` = high-level decoded; uppercase `DOMAIN bytes`
        // = raw byte pass-through). The parser is case-insensitive at lookup time but the
        // lexer emits a KEYWORD token for lowercase matches, and an Identifier for others —
        // which is how the parser routes high-level vs raw. Every command gets both forms.
        //
        // Priority: ANSI per-opcode mnemonic (NUL/CAN/ESC/NSR/...) beats kebab name because
        // it's shorter and more idiomatic for the C0 range. For PDI commands we fall back
        // to the registry name kebab-cased then uppercased.
        if (CommandRegistry.OpcodeMnemonics.TryGetValue(cmd.OpCode, out var mnemonic))
        {
            sb.Append(mnemonic);
            foreach (var b in cmd.Operands) { sb.Append(' ').Append(b); }
            sb.AppendLine();
            return;
        }

        var descriptor = CommandRegistry.GetByType(cmd.GetType());
        if (descriptor != null)
        {
            // Emit UPPERCASE kebab so the lexer produces an Identifier token (keywords
            // are lowercase-only) and the parser's mnemonic-statement path takes over.
            // Round-trips to the same opcode via case-insensitive registry lookup.
            var kebab = descriptor.Name.ToUpperInvariant().Replace(' ', '-');
            byte registryOp = CommandRegistry.GetOpcodeByKebabName(kebab);
            // Match either the 8-bit (0xA0+) or the 7-bit (0x20-0x7F) presentation. The
            // compiler's CompileRaw strips bit 7 when Use7BitMode is active, so the same
            // kebab text encodes correctly into either mode based on the surrounding
            // `#bits N` directive.
            //
            // registryOp == 0 means the kebab is ambiguous (e.g. ControlCommand has 64
            // opcodes spanning C0 + C1, no single canonical byte) — fall through to the
            // legacy `raw N // Comment` form so the literal opcode byte is preserved.
            if (registryOp != 0 && (registryOp == cmd.OpCode || (registryOp & 0x7F) == (cmd.OpCode & 0x7F)))
            {
                // Same mode-tracking as the high-level path: the kebab form parses to
                // the 8-bit opcode 0xA0+ and only becomes 7-bit at compile time when
                // Use7BitMode is active. So we MUST emit `#bits N` before crossing into
                // a section whose bit mode differs from the previously-emitted line.
                int thisMode = cmd.OpCode < 0x80 ? 7 : 8;
                if (_lastEmittedBitMode != 0 && _lastEmittedBitMode != thisMode)
                {
                    sb.AppendLine($"#bits {thisMode}");
                }
                _lastEmittedBitMode = thisMode;

                sb.Append(kebab);
                foreach (var b in cmd.Operands) { sb.Append(' ').Append(b); }
                sb.AppendLine();
                return;
            }
        }

        // Unknown-opcode last resort: bare `raw <opcode> <bytes>` with trailing comment.
        sb.Append("raw ").Append(cmd.OpCode);
        foreach (var b in cmd.Operands) { sb.Append(' ').Append(b); }
        if (descriptor != null) { sb.Append("  // ").Append(descriptor.Name); }
        sb.AppendLine();
    }

    /// <summary>Convert a registry display name like "Polygon Set Filled" to "polygon-set-filled".</summary>
    private static string KebabCase(string name) =>
        name.ToLowerInvariant().Replace(' ', '-');


    // ---- Formatting helpers -------------------------------------------

    /// <summary>
    /// Format a float for Telidraw source. Uses invariant culture, strips trailing zeros,
    /// and snaps to common NAPLPS fractions (1/40, 5/128, etc.) when exact.
    /// </summary>
    /// <summary>
    /// Decode an arc command's relative mid+end operands into absolute world coordinates,
    /// using the pen position from <paramref name="stateBefore"/> as the anchor. Returns
    /// false if the operand byte count doesn't match the expected mv*2 (split layout).
    /// </summary>
    private static bool TryReconstructArc(NaplpsCommand cmd, NaplpsState stateBefore, out (float mx, float my, float ex, float ey) abs)
    {
        abs = default;
        int mv = Math.Max(1, (int)stateBefore.MultiByteValue);

        if (cmd.Operands.Count < mv * 2)
        {
            return false;
        }

        var midOps = new NaplpsOperands(cmd.Operands[0..mv]);
        var endOps = new NaplpsOperands(cmd.Operands[mv..(mv * 2)]);
        var (midDx, midDy) = NaplpsEncoder.DecodeVertex2D(midOps);
        var (endDx, endDy) = NaplpsEncoder.DecodeVertex2D(endOps);

        float mx = stateBefore.Pen.X + midDx;
        float my = stateBefore.Pen.Y + midDy;
        float ex = mx + endDx;
        float ey = my + endDy;

        abs = (mx, my, ex, ey);
        return true;
    }

    /// <summary>
    /// Decode a polygon command's N relative vertex operands into absolute coordinates by
    /// accumulating from the pen. Each vertex consumes mv bytes. Returns the absolute
    /// vertices in draw order; false if operand count isn't a multiple of mv.
    /// </summary>
    private static bool TryReconstructRectSet(NaplpsCommand cmd, NaplpsState stateBefore, out (float x, float y, float w, float h) r)
    {
        r = default;
        int mv = Math.Max(1, (int)stateBefore.MultiByteValue);

        if (cmd.Operands.Count < mv * 2)
        {
            return false;
        }

        var posSlice = new NaplpsOperands(cmd.Operands[0..mv]);
        var sizeSlice = new NaplpsOperands(cmd.Operands[mv..(mv * 2)]);
        var (x, y) = NaplpsEncoder.DecodeVertex2D(posSlice);
        var (w, h) = NaplpsEncoder.DecodeVertex2D(sizeSlice);
        r = (x, y, w, h);
        return true;
    }

    private static bool TryReconstructArcSet(NaplpsCommand cmd, NaplpsState stateBefore, out (float sx, float sy, float mx, float my, float ex, float ey, float dmx, float dmy, float dex, float dey) a)
    {
        a = default;
        int mv = Math.Max(1, (int)stateBefore.MultiByteValue);

        if (cmd.Operands.Count < mv * 3)
        {
            return false;
        }

        var (sx, sy) = NaplpsEncoder.DecodeVertex2D(new NaplpsOperands(cmd.Operands[0..mv]));
        var (dmx, dmy) = NaplpsEncoder.DecodeVertex2D(new NaplpsOperands(cmd.Operands[mv..(mv * 2)]));
        var (dex, dey) = NaplpsEncoder.DecodeVertex2D(new NaplpsOperands(cmd.Operands[(mv * 2)..(mv * 3)]));

        float mx = sx + dmx;
        float my = sy + dmy;
        float ex = mx + dex;
        float ey = my + dey;

        a = (sx, sy, mx, my, ex, ey, dmx, dmy, dex, dey);
        return true;
    }

    /// <summary>
    /// Decode an IncrementalField with bounds: origin (mv bytes) + dimensions (mv bytes).
    /// Two independent vertex2D encodings, no chain — exact round-trip via `field x y w h`.
    /// </summary>
    private static bool TryReconstructField(NaplpsCommand cmd, NaplpsState stateBefore, out (float ox, float oy, float dx, float dy) f)
    {
        f = default;
        int mv = Math.Max(1, (int)stateBefore.MultiByteValue);

        if (cmd.Operands.Count < mv * 2)
        {
            return false;
        }

        var (ox, oy) = NaplpsEncoder.DecodeVertex2D(new NaplpsOperands(cmd.Operands[0..mv]));
        var (dx, dy) = NaplpsEncoder.DecodeVertex2D(new NaplpsOperands(cmd.Operands[mv..(mv * 2)]));
        f = (ox, oy, dx, dy);
        return true;
    }

    private static bool TryReconstructLineSet(NaplpsCommand cmd, NaplpsState stateBefore, out List<(float x, float y)> verts)
    {
        verts = new List<(float, float)>();
        int mv = Math.Max(1, (int)stateBefore.MultiByteValue);

        if (cmd.Operands.Count < mv || cmd.Operands.Count % mv != 0)
        {
            return false;
        }

        for (int i = 0; i < cmd.Operands.Count; i += mv)
        {
            var slice = new NaplpsOperands(cmd.Operands[i..(i + mv)]);
            var (x, y) = NaplpsEncoder.DecodeVertex2D(slice);
            verts.Add((x, y));
        }

        return true;
    }

    /// <summary>
    /// Decode a PolygonSet*'s operands. Returns three views of the same data:
    /// <paramref name="absVerts"/> — absolute coords for start + every vertex (readable form);
    /// <paramref name="startAbs"/> — just the absolute start vertex;
    /// <paramref name="relTail"/> — the relative deltas after the start (byte-exact form).
    /// The dual view lets the verifier try the readable form first and fall back to the
    /// exact form when float math breaks round-trip.
    /// </summary>
    private static bool TryReconstructPolygonSet(NaplpsCommand cmd, NaplpsState stateBefore, out List<(float x, float y)> absVerts, out (float x, float y) startAbs, out List<(float dx, float dy)> relTail)
    {
        absVerts = new List<(float, float)>();
        relTail = new List<(float, float)>();
        startAbs = (0f, 0f);
        int mv = Math.Max(1, (int)stateBefore.MultiByteValue);

        if (cmd.Operands.Count < mv * 2 || cmd.Operands.Count % mv != 0)
        {
            return false;
        }

        var startSlice = new NaplpsOperands(cmd.Operands[0..mv]);
        var (sx, sy) = NaplpsEncoder.DecodeVertex2D(startSlice);
        startAbs = (sx, sy);
        absVerts.Add((sx, sy));

        float px = sx, py = sy;

        for (int i = mv; i < cmd.Operands.Count; i += mv)
        {
            var slice = new NaplpsOperands(cmd.Operands[i..(i + mv)]);
            var (dx, dy) = NaplpsEncoder.DecodeVertex2D(slice);
            relTail.Add((dx, dy));
            px += dx;
            py += dy;
            absVerts.Add((px, py));
        }

        return true;
    }

    private static bool TryReconstructPolygon(NaplpsCommand cmd, NaplpsState stateBefore, out List<(float x, float y)> verts)
    {
        verts = new List<(float, float)>();
        int mv = Math.Max(1, (int)stateBefore.MultiByteValue);

        if (cmd.Operands.Count < mv || cmd.Operands.Count % mv != 0)
        {
            return false;
        }

        float px = stateBefore.Pen.X;
        float py = stateBefore.Pen.Y;

        for (int i = 0; i < cmd.Operands.Count; i += mv)
        {
            var slice = new NaplpsOperands(cmd.Operands[i..(i + mv)]);
            var (dx, dy) = NaplpsEncoder.DecodeVertex2D(slice);
            px += dx;
            py += dy;
            verts.Add((px, py));
        }

        return true;
    }

    private static string FormatPolygonArgs(List<(float x, float y)> verts)
    {
        var parts = new List<string>(verts.Count * 2);

        foreach (var (x, y) in verts)
        {
            parts.Add(Fmt(x));
            parts.Add(Fmt(y));
        }

        return string.Join(' ', parts);
    }

    private static string FormatRelArgs(List<(float dx, float dy)> rels)
    {
        var parts = new List<string>(rels.Count * 2);

        foreach (var (dx, dy) in rels)
        {
            parts.Add(Fmt(dx));
            parts.Add(Fmt(dy));
        }

        return string.Join(' ', parts);
    }

    private static string Fmt(float value)
    {
        // Try the ENCODER's actual denominators FIRST so any value the encoder can hit
        // round-trips byte-exact via the fraction literal `N/D`. NAPLPS encodes coords as
        // 1 sign bit + (totalBits-1) fraction bits at weights 1/2, 1/4, ..., 1/2^(totalBits-1).
        // Default mv=3 → 9 bits → 8 fraction bits → denominator 256.
        // mv=2 → 6 bits → 5 fraction → 32. mv=4 → 12 bits → 11 fraction → 2048.
        if (TryFormatAsFraction(value, 256, out var frac)) { return frac; }
        if (TryFormatAsFraction(value, 2048, out frac)) { return frac; }
        if (TryFormatAsFraction(value, 32, out frac)) { return frac; }

        // Pretty-printable fractions for human-readable values that happen to align.
        if (TryFormatAsFraction(value, 40, out frac)) { return frac; }
        if (TryFormatAsFraction(value, 128, out frac)) { return frac; }
        if (TryFormatAsFraction(value, 80, out frac)) { return frac; }

        // "R" is .NET's round-trip format — guarantees Parse(ToString(x)) == x for floats.
        // Earlier "0.########" was lossy at 8 digits (float32 needs 9 to round-trip), which
        // broke the decompiler verifier on values that didn't align to one of the fraction
        // denominators above. Concrete failure: 567/2048 = 0.27685546875 → "0.27685547" →
        // parse → 0.27685547f (not the same float32) → re-encode → wrong byte. With "R" the
        // text round-trips bit-exact, so the verifier accepts every high-level candidate that
        // the decoder/encoder pair would normally produce identically.
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static bool TryFormatAsFraction(float value, int denominator, out string result)
    {
        float numerator = value * denominator;
        int intNumerator = (int)MathF.Round(numerator);

        // Tighter tolerance — the previous 0.01 was sloppy enough to false-positive on
        // values that don't actually align to the denominator, producing wrong bytes after
        // round-trip. 1e-4 catches genuine fraction matches without false positives.
        if (MathF.Abs(numerator - intNumerator) < 1e-4f && intNumerator != 0)
        {
            result = $"{intNumerator}/{denominator}";
            return true;
        }

        result = string.Empty;
        return false;
    }

    private static string EscapeString(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
    }
}
