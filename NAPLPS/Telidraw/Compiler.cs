// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using System.Numerics;

namespace NAPLPS.Telidraw;

/// <summary>
/// Coordinate system for numeric literals in Telidraw source.
/// <list type="bullet">
///   <item><see cref="Fractions"/> \u2014 literals are already in unit-screen coords (0..1, 0..0.75).</item>
///   <item><see cref="Pixels"/> \u2014 literals are in the current domain's pixel space and get
///     divided by the domain resolution before being fed to <see cref="NaplpsCommandBuilder"/>.</item>
/// </list>
/// </summary>
public enum CoordMode
{
    Fractions,
    Pixels,
}

/// <summary>
/// Walks a Telidraw <see cref="ProgramNode"/> and emits NAPLPS commands into a
/// <see cref="NaplpsFormat"/> via <see cref="NaplpsCommandBuilder"/>. Per D2, <c>with</c>
/// blocks compile to explicit-restore: the compiler snapshots the relevant attribute,
/// emits the setter, walks the body, then emits a second setter to restore the previous
/// value. Byte round-trip is preserved; the decompiler (Phase 8) emits flat commands and
/// does not reconstruct <c>with</c> blocks.
/// </summary>
public sealed class Compiler
{
    private readonly ProgramNode _program;
    private NaplpsFormat _format;
    private readonly List<Diagnostic> _diagnostics = [];

    private readonly Dictionary<string, double> _scopeVars = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _paletteAliases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProcDeclNode> _procs = new(StringComparer.Ordinal);

    private Vector3 _pen = Vector3.Zero;

    /// <summary>
    /// Initial pen position for the pen tracker. Set by callers (e.g. the decompiler verifier)
    /// when compiling a single command in isolation, so relative-to-pen conversions match
    /// the position the original command was emitted from. Default (0,0) for full programs.
    /// </summary>
    public Vector3 InitialPenPosition { get; set; } = Vector3.Zero;

    /// <summary>
    /// Initial NaplpsState the compiler starts with (in BareFormat mode). Used by the
    /// decompiler verifier to seed per-command recompiles with the exact domain / color
    /// mode / texture attrs the original command saw. When null, defaults to a fresh
    /// <see cref="NaplpsState"/>. Only consulted when <see cref="BareFormat"/> is true —
    /// full compiles always get a proper NaplpsFormat.New header.
    /// </summary>
    public NaplpsState? InitialState { get; set; }

    private int CurrentMv => _format.State.MultiByteValue;

    // Currently-active foreground color index (0-15). Tracked so `with color X { }` can
    // emit a restore SelectColor on block exit even if the body changed colors.
    private byte _currentColor = 7; // nominal white

    private CoordMode _coordMode = CoordMode.Fractions;
    private int _domainPixelWidth = 256;
    private int _domainPixelHeight = 192;

    /// <summary>
    /// When true, the compiler creates a bare NaplpsFormat with no CAN+NSR sentinels.
    /// Used by the decompile→recompile round-trip path where the .td source is the
    /// complete byte specification. When false (default for human-authored .td files),
    /// FormatNew sentinels are included.
    /// </summary>
    public bool BareFormat { get; set; }

    public Compiler(ProgramNode program, NaplpsSystemType systemType = NaplpsSystemType.NAPLPS)
    {
        _program = program;
        _systemType = systemType;
        _format = null!; // set in Compile()
    }

    private readonly NaplpsSystemType _systemType;

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    public NaplpsFormat Compile()
    {
        // Snapshot encoder bit mode so a #bits directive doesn't bleed into other code.
        // Restored in the finally block at the end of the method body.
        var priorBitMode = NaplpsEncoder.Use7BitMode;

        try
        {
            return CompileInner();
        }
        finally
        {
            NaplpsEncoder.Use7BitMode = priorBitMode;
        }
    }

    private NaplpsFormat CompileInner()
    {
        if (BareFormat)
        {
            _format = new NaplpsFormat(InitialState?.Clone() ?? new NaplpsState());
        }
        else
        {
            _format = NaplpsFormat.New(_systemType);
        }

        // Apply caller-supplied initial pen so the compiler's relative-vertex math (used by
        // EmitPolygon*, EmitArc*) matches the position the original command was emitted from.
        _pen = InitialPenPosition;

        foreach (var d in _program.Directives)
        {
            ApplyDirective(d);
        }

        // Pre-pass: register all proc declarations so procs can call other procs declared later.
        foreach (var s in _program.Statements)
        {
            if (s is ProcDeclNode proc)
            {
                _procs[proc.Name] = proc;
            }
        }

        foreach (var s in _program.Statements)
        {
            CompileStatement(s);
        }

        // Raw statements compile to bare NaplpsCommand placeholders that keep the bytes
        // exact but neither apply state nor draw (issue #41: a decompiled LINE-RELATIVE
        // reloads without its lines). The serialized bytes are the source of truth:
        // re-parse them so every command comes back typed - rendering and state chaining
        // match a .nap load - while ToBytes stays byte-identical. Skipped when
        // InitialState seeds a mid-stream context (the decompiler verifier compiles
        // single commands in isolation, where a fresh parse would misread operands).
        if (_emittedRaw && InitialState is null)
        {
            return NaplpsFormat.FromBytes(_format.ToBytes(),
                _systemType == NaplpsSystemType.NAPLPS ? null : _systemType);
        }

        return _format;
    }

    // ---- Directives ----------------------------------------------------

    private void ApplyDirective(DirectiveNode d)
    {
        switch (d.Name)
        {
            case "coord":
                if (d.Args.Count >= 1 && d.Args[0] is IdentifierNode id)
                {
                    _coordMode = id.Name switch
                    {
                        "fractions" => CoordMode.Fractions,
                        "pixels" => CoordMode.Pixels,
                        _ => _coordMode,
                    };
                }
                break;

            case "resolution":
                if (d.Args.Count >= 2)
                {
                    _domainPixelWidth = (int)Evaluate(d.Args[0]);
                    _domainPixelHeight = (int)Evaluate(d.Args[1]);
                }
                break;

            case "bits":
                // #bits 7 selects the 0x40 numerical-data base (7-bit transmission mode);
                // #bits 8 (default) uses 0x40. Drives NaplpsEncoder for byte-exact round-trip.
                if (d.Args.Count >= 1)
                {
                    var bits = (int)Evaluate(d.Args[0]);
                    NaplpsEncoder.Use7BitMode = bits == 7;
                }
                break;

            default:
                Diag(DiagnosticSeverity.Warning, d.Line, d.Column, $"Unknown directive '#{d.Name}' (ignored)");
                break;
        }
    }

    // ---- Statement dispatch --------------------------------------------

    private void CompileStatement(StatementNode stmt)
    {
        switch (stmt)
        {
            case CommandCallNode c: CompileCommandCall(c); break;
            case ProcCallNode p: CompileProcCall(p); break;
            case WithBlockNode w: CompileWithBlock(w); break;
            case RepeatNode r: CompileRepeat(r); break;
            case ForNode f: CompileFor(f); break;
            case IfNode i: CompileIf(i); break;
            case ProcDeclNode: break; // already registered in pre-pass
            case RawStatementNode raw: CompileRaw(raw); break;
            case PaletteAliasNode pa: _paletteAliases[pa.Name] = Evaluate(pa.Value); break;
            case LetNode l: _scopeVars[l.Name] = Evaluate(l.Value); break;
            case DirectiveNode d: ApplyDirective(d); break;
            default: Diag(DiagnosticSeverity.Error, stmt.Line, stmt.Column, $"Unhandled statement '{stmt.GetType().Name}'"); break;
        }
    }

    // ---- Command dispatch ----------------------------------------------

    private void CompileCommandCall(CommandCallNode c)
    {
        switch (c.Command)
        {
            case TokenKind.Move:
            case TokenKind.Goto:
                ExpectArgs(c, 2);
                EmitMove((float)NormX(Evaluate(c.Args[0])), (float)NormY(Evaluate(c.Args[1])));
                break;

            case TokenKind.Point:
                ExpectArgs(c, 2);
                EmitPoint((float)NormX(Evaluate(c.Args[0])), (float)NormY(Evaluate(c.Args[1])));
                break;

            case TokenKind.Line:
                ExpectArgs(c, 2);
                EmitLine((float)NormX(Evaluate(c.Args[0])), (float)NormY(Evaluate(c.Args[1])));
                break;

            case TokenKind.Rect:
                ExpectArgs(c, 2);
                EmitRectFilled((float)NormW(Evaluate(c.Args[0])), (float)NormH(Evaluate(c.Args[1])));
                break;

            case TokenKind.Arc:
                ExpectArgs(c, 4);
                EmitArcFilled(
                    (float)NormX(Evaluate(c.Args[0])), (float)NormY(Evaluate(c.Args[1])),
                    (float)NormX(Evaluate(c.Args[2])), (float)NormY(Evaluate(c.Args[3])));
                break;

            case TokenKind.Polygon:
                if (c.Args.Count < 2 || c.Args.Count % 2 != 0)
                {
                    Diag(DiagnosticSeverity.Error, c.Line, c.Column, $"polygon needs pairs of x,y coords (got {c.Args.Count})");
                    break;
                }
                EmitPolygonFilled(c);
                break;

            case TokenKind.Color:
                if (c.Args.Count == 1)
                {
                    EmitCommand(NaplpsCommandBuilder.BuildSelectColor((byte)Evaluate(c.Args[0])));
                    _currentColor = (byte)Evaluate(c.Args[0]);
                }
                else if (c.Args.Count == 2)
                {
                    EmitCommand(NaplpsCommandBuilder.BuildSelectColor((byte)Evaluate(c.Args[0]), (byte)Evaluate(c.Args[1])));
                    _currentColor = (byte)Evaluate(c.Args[0]);
                }
                else
                {
                    Diag(DiagnosticSeverity.Error, c.Line, c.Column, "color takes 1 (fg) or 2 (fg, bg) args");
                }
                break;

            case TokenKind.Domain:
                // domain singleByteWidth multiByteWidth [dimensionality]
                if (c.Args.Count < 2) { Diag(DiagnosticSeverity.Error, c.Line, c.Column, "'domain' needs at least 2 args"); break; }
                var sv = (byte)Evaluate(c.Args[0]);
                var mv = (byte)Evaluate(c.Args[1]);
                var dim = c.Args.Count >= 3 ? (byte)Evaluate(c.Args[2]) : (byte)2;
                EmitCommand(NaplpsCommandBuilder.BuildDomain(sv, mv, dim));
                break;

            case TokenKind.Texture:
                // texture linePattern highlight fillPattern
                ExpectArgs(c, 3);
                EmitCommand(NaplpsCommandBuilder.BuildTexture(
                    (byte)Evaluate(c.Args[0]),
                    Evaluate(c.Args[1]) != 0,
                    (byte)Evaluate(c.Args[2])));
                break;

            case TokenKind.Wait:
                ExpectArgs(c, 1);
                EmitCommand(NaplpsCommandBuilder.BuildWait((byte)Evaluate(c.Args[0])));
                break;

            case TokenKind.Reset:
                EmitCommand(NaplpsCommandBuilder.BuildReset());
                break;

            case TokenKind.Nsr:
                EmitCommand(NaplpsCommandBuilder.BuildNonSelectiveReset());
                break;

            case TokenKind.MoveRel:
                ExpectArgs(c, 2);
                EmitCommand(NaplpsCommandBuilder.BuildPointSetRelative(
                    (float)NormW(Evaluate(c.Args[0])), (float)NormH(Evaluate(c.Args[1])), CurrentMv));
                break;

            case TokenKind.PointRel:
                ExpectArgs(c, 2);
                EmitCommand(NaplpsCommandBuilder.BuildPointRelative(
                    (float)NormW(Evaluate(c.Args[0])), (float)NormH(Evaluate(c.Args[1])), CurrentMv));
                break;

            case TokenKind.LineRel:
                ExpectArgs(c, 2);
                EmitCommand(NaplpsCommandBuilder.BuildLineRelative(
                    (float)NormW(Evaluate(c.Args[0])), (float)NormH(Evaluate(c.Args[1])), CurrentMv));
                break;

            case TokenKind.RectOutline:
                ExpectArgs(c, 2);
                EmitCommand(NaplpsCommandBuilder.BuildRectangleOutlined(
                    (float)NormW(Evaluate(c.Args[0])), (float)NormH(Evaluate(c.Args[1])), CurrentMv));
                break;

            case TokenKind.ArcOutline:
                ExpectArgs(c, 4);
                EmitArcOutlined(
                    (float)NormX(Evaluate(c.Args[0])), (float)NormY(Evaluate(c.Args[1])),
                    (float)NormX(Evaluate(c.Args[2])), (float)NormY(Evaluate(c.Args[3])));
                break;

            case TokenKind.PolyOutline:
                if (c.Args.Count < 2 || c.Args.Count % 2 != 0)
                {
                    Diag(DiagnosticSeverity.Error, c.Line, c.Column, $"polygon-outline needs pairs of x,y coords (got {c.Args.Count})");
                    break;
                }
                EmitPolygonOutlined(c);
                break;

            case TokenKind.LineSet:
            case TokenKind.LineSetRel:
            {
                bool relative = c.Command == TokenKind.LineSetRel;
                string verb = relative ? "line-set-rel" : "line-set";

                if (c.Args.Count < 2 || c.Args.Count % 2 != 0)
                {
                    Diag(DiagnosticSeverity.Error, c.Line, c.Column, $"{verb} needs pairs of coords (got {c.Args.Count})");
                    break;
                }
                EmitLineSet(c, relative);
                break;
            }

            case TokenKind.RectSet:
            case TokenKind.RectSetOutline:
            {
                bool filled = c.Command == TokenKind.RectSet;
                string verb = filled ? "rect-set" : "rect-set-outline";

                ExpectArgs(c, 4);
                float x = (float)NormX(Evaluate(c.Args[0]));
                float y = (float)NormY(Evaluate(c.Args[1]));
                float w = (float)NormW(Evaluate(c.Args[2]));
                float h = (float)NormH(Evaluate(c.Args[3]));

                EmitCommand(filled
                    ? NaplpsCommandBuilder.BuildRectangleSetFilled(x, y, w, h, CurrentMv)
                    : NaplpsCommandBuilder.BuildRectangleSetOutlined(x, y, w, h, CurrentMv));
                break;
            }

            case TokenKind.ArcSet:
            case TokenKind.ArcSetOutline:
            {
                bool filled = c.Command == TokenKind.ArcSet;
                string verb = filled ? "arc-set" : "arc-set-outline";

                // Two forms: `arc-set sx sy mx my ex ey` (all absolute, readable) or
                // `arc-set abs sx sy dmx dmy dex dey` (start absolute + relative deltas, exact).
                if (c.Args.Count >= 1 && c.Args[0] is IdentifierNode { Name: "abs" })
                {
                    ExpectArgs(c, 7);
                    EmitArcSetExact(c, filled);
                }
                else
                {
                    ExpectArgs(c, 6);
                    EmitArcSet(c, filled);
                }
                break;
            }

            case TokenKind.PolySet:
            case TokenKind.PolySetOutline:
            {
                bool filled = c.Command == TokenKind.PolySet;
                string verb = filled ? "polygon-set" : "polygon-set-outline";

                // Two forms: `polygon-set abs sx sy dx1 dy1 ...` (decompiler's exact form,
                // start absolute + relative tail) or `polygon-set sx sy v1x v1y ...` (all
                // absolute, more readable). The `abs` keyword is a literal IdentifierNode.
                if (c.Args.Count >= 1 && c.Args[0] is IdentifierNode { Name: "abs" })
                {
                    if (c.Args.Count < 5 || c.Args.Count % 2 != 1)
                    {
                        Diag(DiagnosticSeverity.Error, c.Line, c.Column, $"{verb} abs needs (sx sy dx1 dy1 ...), got {c.Args.Count - 1}");
                        break;
                    }
                    EmitPolygonSetExact(c, filled);
                }
                else
                {
                    if (c.Args.Count < 4 || c.Args.Count % 2 != 0)
                    {
                        Diag(DiagnosticSeverity.Error, c.Line, c.Column, $"{verb} needs at least 4 coords (start_x start_y v1x v1y ...), got {c.Args.Count}");
                        break;
                    }
                    EmitPolygonSet(c, filled);
                }
                break;
            }

            case TokenKind.SetColor:
                // set-color g r b — defines RGB at the currently-pointed palette entry.
                ExpectArgs(c, 3);
                EmitCommand(NaplpsCommandBuilder.BuildSetColorRgb(
                    (byte)Evaluate(c.Args[0]),
                    (byte)Evaluate(c.Args[1]),
                    (byte)Evaluate(c.Args[2])));
                break;

            case TokenKind.Blink:
                // blink blinkToIndex onInterval offInterval [startDelay]
                if (c.Args.Count < 3)
                {
                    Diag(DiagnosticSeverity.Error, c.Line, c.Column, "blink needs at least (toIndex, onInterval, offInterval)");
                    break;
                }
                var delay = c.Args.Count >= 4 ? (byte)Evaluate(c.Args[3]) : (byte)0;
                EmitCommand(NaplpsCommandBuilder.BuildBlink(
                    (byte)Evaluate(c.Args[0]),
                    ((byte)Evaluate(c.Args[1]), (byte)Evaluate(c.Args[2]), delay)));
                break;

            case TokenKind.Field:
                // field originX originY dimsX dimsY (all optional — empty args = full screen)
                if (c.Args.Count == 0)
                {
                    EmitCommand(NaplpsCommandBuilder.BuildField());
                }
                else if (c.Args.Count == 4)
                {
                    EmitCommand(NaplpsCommandBuilder.BuildField(
                        origin: new Vector3((float)NormX(Evaluate(c.Args[0])), (float)NormY(Evaluate(c.Args[1])), 0),
                        dimensions: new Vector3((float)NormW(Evaluate(c.Args[2])), (float)NormH(Evaluate(c.Args[3])), 0),
                        multiByteValue: CurrentMv));
                }
                else
                {
                    Diag(DiagnosticSeverity.Error, c.Line, c.Column, "field takes 0 (full-screen) or 4 (originX originY dimsX dimsY) args");
                }
                break;

            case TokenKind.Text:
                EmitText(c);
                break;

            case TokenKind.Close:
                // Close current polygon path — emit a line back to the last move position.
                // For Phase 7 MVP we approximate as "line to (0,0) if at start"; the compiler
                // doesn't track polygon-start so this is a no-op hint until decompiler adds
                // path-close semantics.
                break;

            default:
                Diag(DiagnosticSeverity.Error, c.Line, c.Column, $"Command '{c.Command}' not yet supported by the compiler");
                break;
        }
    }

    // ---- Emission helpers ----------------------------------------------

    private void EmitMove(float x, float y)
    {
        EmitCommand(NaplpsCommandBuilder.BuildPointSetAbsolute(x, y, CurrentMv));
        _pen = new Vector3(x, y, 0);
    }

    private void EmitPoint(float x, float y)
    {
        EmitCommand(NaplpsCommandBuilder.BuildPointAbsolute(x, y, CurrentMv));
        _pen = new Vector3(x, y, 0);
    }

    private void EmitLine(float x, float y)
    {
        EmitCommand(NaplpsCommandBuilder.BuildLineAbsolute(x, y, CurrentMv));
        _pen = new Vector3(x, y, 0);
    }

    private void EmitRectFilled(float w, float h)
    {
        EmitCommand(NaplpsCommandBuilder.BuildRectangleFilled(w, h, CurrentMv));
        _pen = new Vector3(_pen.X + w, _pen.Y, 0);
    }

    private void EmitArcFilled(float midX, float midY, float endX, float endY)
    {
        // Command operands are (mid relative, end relative) in NAPLPS convention. The
        // Telidraw surface gives absolutes; convert by subtracting pen position.
        float penX = _pen.X;
        float penY = _pen.Y;
        EmitCommand(NaplpsCommandBuilder.BuildArcFilled(midX - penX, midY - penY, endX - midX, endY - midY, CurrentMv));
        _pen = new Vector3(endX, endY, 0);
    }

    private void EmitArcOutlined(float midX, float midY, float endX, float endY)
    {
        float penX = _pen.X;
        float penY = _pen.Y;
        EmitCommand(NaplpsCommandBuilder.BuildArcOutlined(midX - penX, midY - penY, endX - midX, endY - midY, CurrentMv));
        _pen = new Vector3(endX, endY, 0);
    }

    private void EmitPolygonFilled(CommandCallNode c)
    {
        var verts = new Vector3[c.Args.Count / 2];

        for (int i = 0; i < verts.Length; i++)
        {
            verts[i] = new Vector3(
                (float)NormX(Evaluate(c.Args[2 * i])),
                (float)NormY(Evaluate(c.Args[2 * i + 1])),
                0);
        }

        // Convert absolute vertices into relative displacements from the pen — that's the
        // operand shape for PolygonFilled.
        var relVerts = new Vector3[verts.Length];
        var pen = _pen;

        for (int i = 0; i < verts.Length; i++)
        {
            relVerts[i] = verts[i] - pen;
            pen = verts[i];
        }

        EmitCommand(NaplpsCommandBuilder.BuildPolygonFilled(relVerts, CurrentMv));
        _pen = verts[^1];
    }

    private void EmitPolygonOutlined(CommandCallNode c)
    {
        var verts = new Vector3[c.Args.Count / 2];

        for (int i = 0; i < verts.Length; i++)
        {
            verts[i] = new Vector3(
                (float)NormX(Evaluate(c.Args[2 * i])),
                (float)NormY(Evaluate(c.Args[2 * i + 1])),
                0);
        }

        var relVerts = new Vector3[verts.Length];
        var pen = _pen;

        for (int i = 0; i < verts.Length; i++)
        {
            relVerts[i] = verts[i] - pen;
            pen = verts[i];
        }

        EmitCommand(NaplpsCommandBuilder.BuildPolygonOutlined(relVerts, CurrentMv));
        _pen = verts[^1];
    }

    private void EmitLineSet(CommandCallNode c, bool relative)
    {
        var pts = new Vector3[c.Args.Count / 2];

        for (int i = 0; i < pts.Length; i++)
        {
            pts[i] = new Vector3(
                (float)NormX(Evaluate(c.Args[2 * i])),
                (float)NormY(Evaluate(c.Args[2 * i + 1])),
                0);
        }

        if (relative)
        {
            // line-set-rel emits the bytes verbatim — the args are already deltas.
            EmitCommand(NaplpsCommandBuilder.BuildLineSetRelative(pts, CurrentMv));
        }
        else
        {
            // Each absolute vertex is independently encoded; no chain, no precision drift.
            EmitCommand(NaplpsCommandBuilder.BuildLineSetAbsolute(pts, CurrentMv));
            _pen = pts[^1];
        }
    }

    private void EmitArcSet(CommandCallNode c, bool filled)
    {
        // All-absolute readable form. Convert to (start, midRel, endRel).
        float sx = (float)NormX(Evaluate(c.Args[0]));
        float sy = (float)NormY(Evaluate(c.Args[1]));
        float mx = (float)NormX(Evaluate(c.Args[2]));
        float my = (float)NormY(Evaluate(c.Args[3]));
        float ex = (float)NormX(Evaluate(c.Args[4]));
        float ey = (float)NormY(Evaluate(c.Args[5]));

        EmitCommand(filled
            ? NaplpsCommandBuilder.BuildArcSetFilled(sx, sy, mx - sx, my - sy, ex - mx, ey - my, CurrentMv)
            : NaplpsCommandBuilder.BuildArcSetOutlined(sx, sy, mx - sx, my - sy, ex - mx, ey - my, CurrentMv));

        _pen = new Vector3(ex, ey, 0);
    }

    private void EmitArcSetExact(CommandCallNode c, bool filled)
    {
        // Args[0] is the `abs` marker. Args[1..2] = start abs; [3..4] = mid rel; [5..6] = end rel.
        float sx = (float)NormX(Evaluate(c.Args[1]));
        float sy = (float)NormY(Evaluate(c.Args[2]));
        float dmx = (float)NormW(Evaluate(c.Args[3]));
        float dmy = (float)NormH(Evaluate(c.Args[4]));
        float dex = (float)NormW(Evaluate(c.Args[5]));
        float dey = (float)NormH(Evaluate(c.Args[6]));

        EmitCommand(filled
            ? NaplpsCommandBuilder.BuildArcSetFilled(sx, sy, dmx, dmy, dex, dey, CurrentMv)
            : NaplpsCommandBuilder.BuildArcSetOutlined(sx, sy, dmx, dmy, dex, dey, CurrentMv));

        _pen = new Vector3(sx + dmx + dex, sy + dmy + dey, 0);
    }

    /// <summary>
    /// Exact form: `polygon-set abs sx sy dx1 dy1 dx2 dy2 ...`. Args[0] is the literal
    /// `abs` marker; args[1..2] are the absolute start; remaining pairs are pre-computed
    /// relative deltas. The decompiler emits this when the absolute form would lose bytes
    /// to float-rounding in the decode→add→subtract→encode chain.
    /// </summary>
    private void EmitPolygonSetExact(CommandCallNode c, bool filled)
    {
        float sx = (float)NormX(Evaluate(c.Args[1]));
        float sy = (float)NormY(Evaluate(c.Args[2]));
        int relCount = (c.Args.Count - 3) / 2;
        var rels = new Vector3[relCount];

        for (int i = 0; i < relCount; i++)
        {
            rels[i] = new Vector3(
                (float)NormW(Evaluate(c.Args[3 + 2 * i])),
                (float)NormH(Evaluate(c.Args[3 + 2 * i + 1])),
                0);
        }

        EmitCommand(filled
            ? NaplpsCommandBuilder.BuildPolygonSetFilled(new Vector3(sx, sy, 0), rels, CurrentMv)
            : NaplpsCommandBuilder.BuildPolygonSetOutlined(new Vector3(sx, sy, 0), rels, CurrentMv));

        var pen = new Vector3(sx, sy, 0);
        foreach (var r in rels) { pen += r; }
        _pen = pen;
    }

    /// <summary>
    /// Readable form: `polygon-set sx sy v1x v1y v2x v2y ...` — all absolute. The first
    /// pair is the absolute start vertex; subsequent pairs are absolute too. The builder
    /// expects start absolute + relative tail, so we subtract here. Susceptible to
    /// float-rounding for some byte patterns; fall back to EmitPolygonSetExact if needed.
    /// </summary>
    private void EmitPolygonSet(CommandCallNode c, bool filled)
    {
        int vertCount = c.Args.Count / 2;
        var verts = new Vector3[vertCount];

        for (int i = 0; i < vertCount; i++)
        {
            verts[i] = new Vector3(
                (float)NormX(Evaluate(c.Args[2 * i])),
                (float)NormY(Evaluate(c.Args[2 * i + 1])),
                0);
        }

        var start = verts[0];
        var relTail = new Vector3[vertCount - 1];

        for (int i = 1; i < vertCount; i++)
        {
            relTail[i - 1] = verts[i] - verts[i - 1];
        }

        EmitCommand(filled
            ? NaplpsCommandBuilder.BuildPolygonSetFilled(start, relTail, CurrentMv)
            : NaplpsCommandBuilder.BuildPolygonSetOutlined(start, relTail, CurrentMv));

        _pen = verts[^1];
    }

    private void EmitText(CommandCallNode c)
    {
        if (c.Args.Count == 0 || c.Args[0] is not StringLiteralNode str)
        {
            Diag(DiagnosticSeverity.Error, c.Line, c.Column, "text expects a string literal as first arg");
            return;
        }

        // Optional [size] argument emits a TEXT command sized accordingly.
        if (c.Args.Count >= 3)
        {
            EmitCommand(NaplpsCommandBuilder.BuildText(
                (float)Evaluate(c.Args[1]),
                (float)Evaluate(c.Args[2]),
                multiByteValue: CurrentMv));
        }

        foreach (var ch in str.Value)
        {
            if (ch >= 0x20 && ch <= 0x7E)
            {
                _format.AddCommand((byte)ch, []);
            }
        }
    }

    // ---- With / Repeat / For / If --------------------------------------

    private void CompileWithBlock(WithBlockNode w)
    {
        // D2: emit the new attribute value, walk body, emit the previous value to restore.
        switch (w.Attribute)
        {
            case TokenKind.Color:
            {
                if (w.AttributeArgs.Count == 0)
                {
                    Diag(DiagnosticSeverity.Error, w.Line, w.Column, "with color needs a color index or alias");
                    return;
                }

                var prev = _currentColor;
                var newColor = (byte)Evaluate(w.AttributeArgs[0]);
                EmitCommand(NaplpsCommandBuilder.BuildSelectColor(newColor));
                _currentColor = newColor;

                foreach (var s in w.Body) { CompileStatement(s); }

                EmitCommand(NaplpsCommandBuilder.BuildSelectColor(prev));
                _currentColor = prev;
                break;
            }

            case TokenKind.Texture:
            {
                if (w.AttributeArgs.Count < 3)
                {
                    Diag(DiagnosticSeverity.Error, w.Line, w.Column, "with texture needs (linePattern highlight fillPattern)");
                    return;
                }

                EmitCommand(NaplpsCommandBuilder.BuildTexture(
                    (byte)Evaluate(w.AttributeArgs[0]),
                    Evaluate(w.AttributeArgs[1]) != 0,
                    (byte)Evaluate(w.AttributeArgs[2])));

                foreach (var s in w.Body) { CompileStatement(s); }

                // Restore: default texture (line=solid, no highlight, pattern=0)
                EmitCommand(NaplpsCommandBuilder.BuildTexture(0, false, 0));
                break;
            }

            case TokenKind.Domain:
            {
                if (w.AttributeArgs.Count < 2)
                {
                    Diag(DiagnosticSeverity.Error, w.Line, w.Column, "with domain needs (singleByte multiByte)");
                    return;
                }

                var prevSv = 1; // default single-byte value
                var prevMv = 3; // default multi-byte value

                EmitCommand(NaplpsCommandBuilder.BuildDomain(
                    (byte)Evaluate(w.AttributeArgs[0]),
                    (byte)Evaluate(w.AttributeArgs[1]),
                    w.AttributeArgs.Count >= 3 ? (byte)Evaluate(w.AttributeArgs[2]) : (byte)2));

                foreach (var s in w.Body) { CompileStatement(s); }

                EmitCommand(NaplpsCommandBuilder.BuildDomain((byte)prevSv, (byte)prevMv, 2));
                break;
            }

            default:
                Diag(DiagnosticSeverity.Error, w.Line, w.Column, $"'with {w.Attribute}' not supported");
                break;
        }
    }

    private void CompileRepeat(RepeatNode r)
    {
        var n = (int)Evaluate(r.Count);
        for (int i = 0; i < n; i++)
        {
            foreach (var s in r.Body) { CompileStatement(s); }
        }
    }

    private void CompileFor(ForNode f)
    {
        var from = (int)Evaluate(f.From);
        var to = (int)Evaluate(f.To);
        var previousValue = _scopeVars.TryGetValue(f.Variable, out var prev) ? (double?)prev : null;

        for (int i = from; i <= to; i++)
        {
            _scopeVars[f.Variable] = i;
            foreach (var s in f.Body) { CompileStatement(s); }
        }

        if (previousValue.HasValue)
        {
            _scopeVars[f.Variable] = previousValue.Value;
        }
        else
        {
            _scopeVars.Remove(f.Variable);
        }
    }

    private void CompileIf(IfNode i)
    {
        var cond = Evaluate(i.Condition);
        var body = cond != 0 ? i.Then : i.Else;

        if (body == null)
        {
            return;
        }

        foreach (var s in body) { CompileStatement(s); }
    }

    // ---- Proc calls (inline substitution) -----------------------------

    private void CompileProcCall(ProcCallNode call)
    {
        if (!_procs.TryGetValue(call.Name, out var proc))
        {
            Diag(DiagnosticSeverity.Error, call.Line, call.Column, $"Unknown procedure '{call.Name}'");
            return;
        }

        if (call.Args.Count != proc.Parameters.Count)
        {
            Diag(DiagnosticSeverity.Error, call.Line, call.Column, $"'{call.Name}' expects {proc.Parameters.Count} args, got {call.Args.Count}");
            return;
        }

        // Snapshot any outer let-bindings we're about to shadow, then reinstate them on exit.
        var shadowed = new Dictionary<string, double?>();

        for (int i = 0; i < proc.Parameters.Count; i++)
        {
            var name = proc.Parameters[i];
            shadowed[name] = _scopeVars.TryGetValue(name, out var prev) ? (double?)prev : null;
            _scopeVars[name] = Evaluate(call.Args[i]);
        }

        // @macro procs would wrap the body with DefMacro + End; for Phase 7 MVP we always
        // inline. The AsMacro flag stays on the node for future compile-as-macro support.
        foreach (var s in proc.Body) { CompileStatement(s); }

        foreach (var (name, prev) in shadowed)
        {
            if (prev.HasValue) { _scopeVars[name] = prev.Value; }
            else { _scopeVars.Remove(name); }
        }
    }

    // ---- Expression evaluator -----------------------------------------

    private double Evaluate(ExpressionNode expr)
    {
        switch (expr)
        {
            case NumberLiteralNode n:
                return n.Value;

            case FractionLiteralNode f:
                return f.AsDouble;

            case StringLiteralNode:
                Diag(DiagnosticSeverity.Error, expr.Line, expr.Column, "Cannot use a string in a numeric context");
                return 0;

            case IdentifierNode id:
                if (_scopeVars.TryGetValue(id.Name, out var v)) { return v; }
                if (_paletteAliases.TryGetValue(id.Name, out var p)) { return p; }
                Diag(DiagnosticSeverity.Error, id.Line, id.Column, $"Unknown identifier '{id.Name}'");
                return 0;

            case UnaryOpNode u:
                var operand = Evaluate(u.Operand);
                return u.Op switch { "-" => -operand, _ => operand };

            case BinaryOpNode b:
                var left = Evaluate(b.Left);
                var right = Evaluate(b.Right);
                return b.Op switch
                {
                    "+" => left + right,
                    "-" => left - right,
                    "*" => left * right,
                    "/" => right == 0 ? 0 : left / right,
                    "%" => right == 0 ? 0 : left % right,
                    _ => 0,
                };

            case CallExpressionNode:
                Diag(DiagnosticSeverity.Error, expr.Line, expr.Column, "Function-call expressions not yet evaluated at compile time");
                return 0;

            default:
                return 0;
        }
    }

    /// <summary>
    /// Emit raw opcode + operand bytes verbatim. First byte is the opcode; rest are operands.
    /// This is the lossless escape hatch: any NAPLPS byte sequence that the decompiler
    /// can't express in higher-level DSL gets round-tripped through raw.
    /// </summary>
    private bool _emittedRaw;

    private void CompileRaw(RawStatementNode raw)
    {
        if (raw.Bytes.Count == 0)
        {
            return;
        }

        _emittedRaw = true;

        // Bypass AddCommand entirely — raw statements must NOT be instantiated through
        // InUseTable (which runs constructors that move the pen, change state, etc.).
        // Instead, create a bare NaplpsCommand and append it directly to the command list.
        // This preserves exact bytes without side effects.
        // ONLY the logical kebab form (POLYGON-SET-FILLED ...) gets bit-7 stripped under
        // 7-bit mode. The literal `raw N N N` form must preserve the opcode byte verbatim,
        // because that's what disk-honest round-trip relies on for unhandled opcodes —
        // including ambiguous cases like SS3-prefixed Mosaic Element at 0xA1, where 0xA1
        // means "Mosaic Element via G3" and stripping to 0x21 corrupts the stream.
        byte opcode = (raw.IsLogicalForm && NaplpsEncoder.Use7BitMode && raw.Bytes[0] >= 0xA0)
            ? (byte)(raw.Bytes[0] & 0x7F)
            : raw.Bytes[0];
        var operands = new NaplpsOperands();

        for (int i = 1; i < raw.Bytes.Count; i++)
        {
            operands.Add(raw.Bytes[i]);
        }

        var bareCmd = new NaplpsCommand(null, opcode, operands);
        _format.Commands.Add(new NaplpsSequence(_format.State.Clone(), bareCmd));

        // Pen tracking: when raw bytes encode a position-changing PDI command, mirror its
        // pen-end side effect onto the pen tracker so that subsequent high-level commands
        // (polygon, arc) compute relative deltas from the right anchor.
        UpdatePenFromRaw(opcode, operands);

        // State mutation from raw bytes: Domain changes sv/mv/dim, which every subsequent
        // geometric command reads via CurrentMv. Without this, raw-emitted Domain leaves
        // the compiler's state stuck at defaults (mv=3) even though downstream commands
        // were authored against the raw-set mv. Mirror the real command's fixed-byte decode.
        ApplyStateMutationFromRaw(opcode, operands);
    }

    /// <summary>
    /// Apply the state-mutating side effect of a raw-emitted command to <c>_format.State</c>.
    /// Currently handles Domain (sv/mv/dim from the first fixed byte). The compiler's
    /// <see cref="CurrentMv"/> read-through ensures any subsequent geometric emit uses
    /// the updated mv — matching how the real stream parser propagates Domain downstream.
    /// </summary>
    private void ApplyStateMutationFromRaw(byte opcode, NaplpsOperands operands)
    {
        // C0 shift codes determine which G-set is invoked into GL, and therefore whether
        // a subsequent 0x20-0x7F byte dispatches to the PDI set (Domain, PointSetAbsolute,
        // ...) or the primary character set (ASCII). Without mirroring these shifts onto
        // the compiler's state, AddCommand(0x21, ...) would look up InUseTable[0x21] and
        // find '!' instead of Domain — so the Domain constructor never runs, and
        // MultiByteValue never updates. This cascades: every subsequent geometric command
        // re-encodes at the builder's default mv=3 instead of the file's actual mv.
        switch (opcode)
        {
            case 0x0E: _format.State.DoShiftOut(); return;
            case 0x0F: _format.State.DoShiftIn(); return;
            case 0x19: _format.State.DoSingleShiftTwo(); return;
            case 0x1D: _format.State.DoSingleShiftThree(); return;
            case 0x1B: _format.State.DoEscape(operands); return;
        }

        byte normalized = (byte)(opcode & 0x7F);

        if (normalized == (NaplpsCommandBuilder.OpDomain & 0x7F) && operands.Count >= 1)
        {
            var (sv, mv, dim) = DomainCommand.ProcessFixedByte(operands);
            _format.State.SingleByteValue = sv;
            _format.State.MultiByteValue = mv;
            _format.State.Dimensionality = dim;
        }
    }

    /// <summary>
    /// Mirror the pen-end side effect of a raw-emitted PDI command. The compiler doesn't
    /// instantiate raw commands through their real classes, so the pen tracker would otherwise
    /// drift away from the actual pen position the renderer ends up at. Only handle the
    /// opcodes whose normal constructor moves the pen — others (Reset, Color, Texture, etc.)
    /// are no-ops here.
    /// </summary>
    private void UpdatePenFromRaw(byte opcode, NaplpsOperands operands)
    {
        // Strip bit 7 to normalize 7-bit (0x20-0x7F) and 8-bit (0xA0-0xFF) presentations.
        byte normalized = (byte)(opcode & 0x7F);
        int mv = 3;

        switch (normalized)
        {
            // Absolute pen sets — pen lands exactly at the decoded vertex.
            case NaplpsCommandBuilder.OpPointSetAbsolute & 0x7F:
            case NaplpsCommandBuilder.OpPointAbsolute & 0x7F:
            case NaplpsCommandBuilder.OpLineAbsolute & 0x7F:
            {
                if (operands.Count >= mv)
                {
                    var (x, y) = NaplpsEncoder.DecodeVertex2D(new NaplpsOperands(operands[0..mv]));
                    _pen = new Vector3(x, y, 0);
                }
                break;
            }

            // Relative pen offsets — pen advances by the decoded delta.
            case NaplpsCommandBuilder.OpPointSetRelative & 0x7F:
            case NaplpsCommandBuilder.OpPointRelative & 0x7F:
            case NaplpsCommandBuilder.OpLineRelative & 0x7F:
            {
                if (operands.Count >= mv)
                {
                    var (dx, dy) = NaplpsEncoder.DecodeVertex2D(new NaplpsOperands(operands[0..mv]));
                    _pen += new Vector3(dx, dy, 0);
                }
                break;
            }

            // Polygon — pen ends at the LAST vertex (cumulative relative).
            case NaplpsCommandBuilder.OpPolygonFilled & 0x7F:
            case NaplpsCommandBuilder.OpPolygonOutlined & 0x7F:
            {
                if (operands.Count >= mv && operands.Count % mv == 0)
                {
                    var pen = _pen;
                    for (int i = 0; i < operands.Count; i += mv)
                    {
                        var (dx, dy) = NaplpsEncoder.DecodeVertex2D(new NaplpsOperands(operands[i..(i + mv)]));
                        pen += new Vector3(dx, dy, 0);
                    }
                    _pen = pen;
                }
                break;
            }

            // LineSetAbsolute — pen ends at the last absolute vertex.
            case NaplpsCommandBuilder.OpLineSetAbsolute & 0x7F:
            {
                if (operands.Count >= mv && operands.Count % mv == 0)
                {
                    int lastStart = operands.Count - mv;
                    var (x, y) = NaplpsEncoder.DecodeVertex2D(new NaplpsOperands(operands[lastStart..(lastStart + mv)]));
                    _pen = new Vector3(x, y, 0);
                }
                break;
            }

            // LineSetRelative — pen advances cumulatively.
            case NaplpsCommandBuilder.OpLineSetRelative & 0x7F:
            {
                if (operands.Count >= mv && operands.Count % mv == 0)
                {
                    var pen = _pen;
                    for (int i = 0; i < operands.Count; i += mv)
                    {
                        var (dx, dy) = NaplpsEncoder.DecodeVertex2D(new NaplpsOperands(operands[i..(i + mv)]));
                        pen += new Vector3(dx, dy, 0);
                    }
                    _pen = pen;
                }
                break;
            }

            // ArcSet — pen ends at end vertex (start + dmid + dend).
            case NaplpsCommandBuilder.OpArcSetFilled & 0x7F:
            case NaplpsCommandBuilder.OpArcSetOutlined & 0x7F:
            {
                if (operands.Count >= mv * 3)
                {
                    var (sx, sy) = NaplpsEncoder.DecodeVertex2D(new NaplpsOperands(operands[0..mv]));
                    var (dmx, dmy) = NaplpsEncoder.DecodeVertex2D(new NaplpsOperands(operands[mv..(mv * 2)]));
                    var (dex, dey) = NaplpsEncoder.DecodeVertex2D(new NaplpsOperands(operands[(mv * 2)..(mv * 3)]));
                    _pen = new Vector3(sx + dmx + dex, sy + dmy + dey, 0);
                }
                break;
            }

            // PolygonSet — first mv bytes are absolute start; remaining are relative-from-prev.
            case NaplpsCommandBuilder.OpPolygonSetFilled & 0x7F:
            case NaplpsCommandBuilder.OpPolygonSetOutlined & 0x7F:
            {
                if (operands.Count >= mv * 2 && operands.Count % mv == 0)
                {
                    var (sx, sy) = NaplpsEncoder.DecodeVertex2D(new NaplpsOperands(operands[0..mv]));
                    var pen = new Vector3(sx, sy, 0);
                    for (int i = mv; i < operands.Count; i += mv)
                    {
                        var (dx, dy) = NaplpsEncoder.DecodeVertex2D(new NaplpsOperands(operands[i..(i + mv)]));
                        pen += new Vector3(dx, dy, 0);
                    }
                    _pen = pen;
                }
                break;
            }

            // Arc — pen ends at end-vertex (mid then end, both relative).
            case NaplpsCommandBuilder.OpArcFilled & 0x7F:
            case NaplpsCommandBuilder.OpArcOutlined & 0x7F:
            {
                if (operands.Count >= mv * 2)
                {
                    var (mdx, mdy) = NaplpsEncoder.DecodeVertex2D(new NaplpsOperands(operands[0..mv]));
                    var (edx, edy) = NaplpsEncoder.DecodeVertex2D(new NaplpsOperands(operands[mv..(mv * 2)]));
                    _pen += new Vector3(mdx + edx, mdy + edy, 0);
                }
                break;
            }
        }
    }

    // ---- Coord-mode projection ----------------------------------------

    private double NormX(double v) => _coordMode == CoordMode.Fractions ? v : v / _domainPixelWidth;
    private double NormY(double v) => _coordMode == CoordMode.Fractions ? v : v / _domainPixelHeight;
    private double NormW(double v) => _coordMode == CoordMode.Fractions ? v : v / _domainPixelWidth;
    private double NormH(double v) => _coordMode == CoordMode.Fractions ? v : v / _domainPixelHeight;

    // ---- Utilities -----------------------------------------------------

    private void EmitCommand((byte opcode, NaplpsOperands operands) cmd)
    {
        // 7-bit transmission strips bit 7 from PDI opcodes (0xA0-0xFF → 0x20-0x7F).
        // The encoder already emits operands at the right base; the opcode byte itself
        // gets shifted here so the AddCommand path stores the actual on-the-wire byte.
        byte opcode = (NaplpsEncoder.Use7BitMode && cmd.opcode >= 0xA0)
            ? (byte)(cmd.opcode & 0x7F)
            : cmd.opcode;

        _format.AddCommand(opcode, cmd.operands);
    }

    private void ExpectArgs(CommandCallNode c, int minCount)
    {
        if (c.Args.Count < minCount)
        {
            Diag(DiagnosticSeverity.Error, c.Line, c.Column, $"'{c.Command}' needs at least {minCount} args, got {c.Args.Count}");
        }
    }

    private void Diag(DiagnosticSeverity severity, int line, int col, string message)
    {
        _diagnostics.Add(new Diagnostic(severity, line, col, message));
    }
}
