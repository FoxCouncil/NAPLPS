// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using NAPLPS.Drawing;

namespace NAPLPS;

/// <summary>
/// A stateful decode-and-paint session over an append-only NAPLPS byte stream: the managed
/// core behind the C ABI's naplps_ctx_* entry points (see NativeExportsCtx), and equally
/// usable from managed code and tests.
///
/// Model: forward-only. The session owns one <see cref="NaplpsDecoder"/> and one canvas for
/// its whole life. An append hands the chunk to the decoder, which returns the commands that
/// chunk COMPLETED; those are added to the command list and painted, once, by exec_to /
/// exec_next. Nothing is re-parsed and nothing is repainted, so an append costs the size of
/// the chunk rather than the size of the session. The byte history is not retained - the
/// decoder's live state is the whole of the memory.
///
/// Chunks may split anywhere, including mid-command. A command whose operand list reaches the
/// end of the received bytes is WITHHELD, not half-emitted: an X3.110 operand list is
/// terminated by the next non-numeric byte, never by a length, so a complete command ending at
/// the frontier is byte-identical to a truncated one. It is released as soon as the byte that
/// terminates it arrives, or by <see cref="Flush"/> for a stream that really has ended.
/// Pixels therefore never change retroactively.
///
/// Failure model: an append parses but does not paint, and the parse layer records stream
/// errors rather than throwing, so a bad stream leaves the canvas untouched. A render failure
/// (a library bug, not a stream condition) surfaces from exec_to / exec_next and may leave the
/// surface partially painted at the reported command index.
///
/// Thread model: instances are not internally synchronized; use one session per thread
/// or synchronize externally. A disposed session throws ObjectDisposedException from
/// every method; property getters keep their last values.
/// </summary>
public sealed class NaplpsStreamSession : IDisposable
{
    /// <summary>Bytes held back until the system type is decidable; see
    /// <see cref="TryEstablish"/>. Empty once the decoder exists.</summary>
    private readonly List<byte> _header = [];

    private NaplpsDecoder? _decoder;
    private DrawContext? _draw;
    private bool _appended;

    public NaplpsStreamSession(int width, int height, bool prodigy, bool transparentBackground = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);
        Width = width;
        Height = height;
        Prodigy = prodigy;
        TransparentBackground = transparentBackground;

        // Forcing Prodigy decides the system type with no bytes at all, so the client path
        // has its decoder and canvas from the outset.
        TryEstablish(atStreamEnd: false);
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Force the Prodigy pipeline (canonical CLUT, MVDI text, authentic
    /// geometry, Prodigy display ratio) regardless of stream auto-detection.</summary>
    public bool Prodigy { get; }

    /// <summary>When true the canvas clears to fully transparent (0,0,0,0) instead of
    /// opaque black, and only painted pixels carry alpha 255 - the window-overlay model:
    /// composite by alpha and the page below shows through everything the window stream
    /// did not paint. Replay-safe by construction (replays repaint over the same
    /// transparent base). A stream that wants an opaque backdrop draws one.</summary>
    public bool TransparentBackground { get; }

    public NaplpsFormat? Format { get; private set; }

    /// <summary>Count of commands already painted onto the canvas.</summary>
    public int Cursor { get; private set; }

    public int CommandCount => Format?.Commands.Count ?? 0;

    /// <summary>
    /// Append bytes to the stream. Returns the total count of COMPLETE commands decoded so
    /// far: a chunk ending mid-command leaves that command out until the byte terminating it
    /// arrives (or <see cref="Flush"/> declares the stream over). Callers appending whole
    /// streams and flushing never see the difference. Painting is exec_to / exec_next's job;
    /// this only decodes.
    /// </summary>
    public int Append(byte[] chunk)
    {
        ThrowIfDisposed();

        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.Length == 0) { throw new ArgumentException("empty chunk", nameof(chunk)); }

        _appended = true;

        if (_decoder is null)
        {
            _header.AddRange(chunk);

            // Still short of the two bytes DetectSystemType may need; nothing can be decoded
            // until the answer is known, because it selects the CLUT and the PDI table.
            if (!TryEstablish(atStreamEnd: false)) { return 0; }

            return CommandCount;
        }

        return Ingest(_decoder.Feed(chunk));
    }

    /// <summary>
    /// Declare the stream complete. At end of stream a command whose operand list runs to the
    /// last byte is byte-identical to a truncated one, so the decoder holds it; this is the
    /// caller asserting there is no more data, which releases it. Returns the total command
    /// count. Calling it on a stream that is merely paused would emit a truncated command as
    /// though it were whole.
    /// </summary>
    public int Flush()
    {
        ThrowIfDisposed();

        if (_decoder is null)
        {
            // An intentionally-empty stream is still a stream: flushing it establishes
            // (generic NAPLPS by the detection rules), so a caller can synthesize runs
            // on a session that never received wire bytes.
            _appended = true;

            if (!TryEstablish(atStreamEnd: true)) { return CommandCount; }
        }

        return Ingest(_decoder!.Flush());
    }

    /// <summary>Paint up through (and including) command <paramref name="cmdIndex"/>,
    /// clamped to the stream end. Idempotent for already-painted commands. Returns the
    /// highest painted index, or -1 when nothing has been painted.</summary>
    public int ExecTo(int cmdIndex)
    {
        ThrowIfDisposed();

        ArgumentOutOfRangeException.ThrowIfNegative(cmdIndex);
        if (!_appended) { throw new InvalidOperationException("no bytes appended"); }
        if (_draw is null) { return Cursor - 1; }

        var target = Math.Min(cmdIndex, CommandCount - 1);
        while (Cursor <= target)
        {
            _draw.RenderStep(Cursor);
            Cursor++;
        }

        return Cursor - 1;
    }

    /// <summary>Execute the next unpainted command. Returns its index, or null when the
    /// stream is exhausted.</summary>
    public int? ExecNext()
    {
        ThrowIfDisposed();

        if (!_appended) { throw new InvalidOperationException("no bytes appended"); }
        if (_draw is null || Cursor >= CommandCount) { return null; }

        _draw.RenderStep(Cursor);
        return Cursor++;
    }

    /// <summary>
    /// Decide the system type and stand up the decoder, format shell and canvas. Detection
    /// reads at most the first two bytes and is made ONCE: unlike the old re-parse-everything
    /// append it cannot be revised later, so a first chunk too short to decide holds its bytes
    /// rather than locking in the wrong answer. Returns false while still undecidable.
    /// </summary>
    private bool TryEstablish(bool atStreamEnd)
    {
        NaplpsSystemType systemType;

        if (Prodigy)
        {
            systemType = NaplpsSystemType.Prodigy;
        }
        else
        {
            // The same rules as the file path, incrementally: Telidon's 0x0E, the A1 C8
            // Prodigy marker possibly behind CAN/NSR sentinels, undecided while more
            // header bytes could still change the answer.
            var detected = NaplpsFormat.TryDetectSystemType(_header, atStreamEnd);

            if (detected is null)
            {
                return false;
            }

            systemType = detected.Value;
        }

        var state = new NaplpsState();
        NaplpsDecoder.ApplySystemDefaults(state, systemType);

        _decoder = new NaplpsDecoder(state);
        Format = new NaplpsFormat(_decoder, systemType);

        _draw = new DrawContext(Format, new SixLabors.ImageSharp.Size(Width, Height))
        {
            // Match naplps_render_png_prodigy: the ctor derives gun width / MVDI font /
            // display ratio from SystemType; authentic geometry is set explicitly.
            AuthenticGeometry = Prodigy || systemType == NaplpsSystemType.Prodigy,
        };

        _draw.ClearCanvas(TransparentBackground
            ? SixLabors.ImageSharp.Color.Transparent
            : SixLabors.ImageSharp.Color.Black);

        if (_header.Count > 0)
        {
            var held = _header.ToArray();
            _header.Clear();
            Ingest(_decoder.Feed(held));
        }

        return true;
    }

    /// <summary>Take what a feed completed onto the command list.</summary>
    private int Ingest(List<NaplpsSequence> completed)
    {
        var commands = Format!.Commands;
        commands.AddRange(completed);

        // Keep the animation frame count meaningful even though stepping does not consult it.
        _draw!.TotalFrames = (uint)Math.Max(0, commands.Count - 1);

        return commands.Count;
    }

    /// <summary>
    /// A synthesized run is always coded 8-bit, whatever width the decoded stream is, so that
    /// its opcodes AND its operands live in GR (0xA0-0xFF) - which is the PDI set, and which
    /// no shift the caller's stream can perform will take away. The alternative is not
    /// available: <see cref="NaplpsEncoder.Use7BitMode"/> rebases only the OPERANDS, and
    /// <see cref="NaplpsCommandBuilder"/> has no 7-bit opcodes, so "7-bit" here could only
    /// ever mean opcodes in GR with operands in GL - and operands are recognized by a lookup
    /// in the in-use table, not by a range test, so with GL invoked with a character set every
    /// one of them decodes as a glyph and its command executes with no operands at all.
    ///
    /// Nothing is lost by forcing the width. <see cref="NaplpsFormat.Is7Bit"/> is a reporting
    /// flag - the Telidraw decompiler's #bits line, the app's properties panel - and is never
    /// consulted by the decoder; and the session retains no byte history, so these bytes are
    /// never re-emitted to anyone.
    /// </summary>
    private const bool EncodeSynthesized7Bit = false;

    /// <summary>SI: invoke G0 into GL, so bytes in 0x20-0x7F resolve as characters.</summary>
    private const byte ShiftIn = 0x0F;

    /// <summary>SO: invoke G1 into GL.</summary>
    private const byte ShiftOut = 0x0E;

    private const byte Escape = 0x1B;

    /// <summary>
    /// The bytes that re-invoke <paramref name="slot"/> into GL, for putting back the
    /// invocation a synthesized run had to change. Empty for G0, which the run's own SI
    /// already left in place.
    /// </summary>
    private static byte[] RestoreGraphicLeft(NaplpsState.GsetSlot slot) => slot switch
    {
        NaplpsState.GsetSlot.G1 => [ShiftOut],
        NaplpsState.GsetSlot.G2 => [Escape, 0x6E],  // LS2
        NaplpsState.GsetSlot.G3 => [Escape, 0x6F],  // LS3
        _ => [],
    };

    /// <summary>
    /// Append a field-text run built by the library's own encoder: Point Set Absolute,
    /// SELECT COLOR (mode-shaped), optional TEXT character size, then the text bytes.
    /// Coordinates and sizes are rounded to the coordinate wire grid.
    ///
    /// Independent of the decoder state it lands in, and neutral with respect to it. The
    /// drawing commands are coded 8-bit so they resolve through GR whatever the caller's
    /// stream invoked into GL (see <see cref="EncodeSynthesized7Bit"/>); an SI precedes the
    /// text so the payload always resolves as characters rather than executing as PDI
    /// commands; and the incoming GL invocation is restored afterwards, so a caller that
    /// paints a field between two chunks of one presentation gets its shift state back.
    /// The one state this does NOT re-establish is the G0 DESIGNATION: a stream that pointed
    /// G0 at some set other than the primary characters and left it there will have the
    /// payload drawn with that set's glyphs.
    ///
    /// Throws <see cref="InvalidOperationException"/> when the stream currently ends inside an
    /// unfinished macro / DRCS / texture definition (the bytes would be swallowed into
    /// the definition instead of drawing), is paused mid-command (any deferred tail -
    /// flush a finished stream first), or is not yet established.
    /// </summary>
    public int DrawText(double x, double y, int fg, int bg, double charW, double charH, byte[] ascii)
    {
        ThrowIfDisposed();

        ArgumentNullException.ThrowIfNull(ascii);
        if (ascii.Length == 0) { throw new ArgumentException("empty text", nameof(ascii)); }

        if (!double.IsFinite(x)) { throw new ArgumentOutOfRangeException(nameof(x), "non-finite position"); }
        if (!double.IsFinite(y)) { throw new ArgumentOutOfRangeException(nameof(y), "non-finite position"); }

        // A size is either a finite value >= 0 (applied only when BOTH are) or a negative
        // "keep current size" sentinel. NaN is neither: it would silently fall into the
        // keep branch (NaN >= 0 is false), so it is rejected like the infinities.
        if (double.IsNaN(charW) || charW is double.PositiveInfinity) { throw new ArgumentOutOfRangeException(nameof(charW), "non-finite size"); }
        if (double.IsNaN(charH) || charH is double.PositiveInfinity) { throw new ArgumentOutOfRangeException(nameof(charH), "non-finite size"); }

        var incomingGraphicLeft = Format?.State?.GraphicLeftInvocation ?? NaplpsState.GsetSlot.G0;

        return EmitSynthesizedRun(run =>
        {
            var mbv = run.MultiByteValue;
            var bytes = run.Bytes;
            double Quant(double v) => run.Quant(v);
            void Add((byte opcode, NaplpsOperands operands) cmd) => run.Add(cmd);

            Add(NaplpsCommandBuilder.BuildPointSetAbsolute((float)Quant(x), (float)Quant(y), mbv));

            var f = (byte)Math.Clamp(fg, 0, 15);
            if (bg >= 0)
            {
                var b = (byte)Math.Clamp(bg, 0, 15);
                if (f == b)
                {
                    // Spec decoders treat the two-operand SELECT COLOR form with IDENTICAL
                    // operands as a background-only change; set an interim background first
                    // so the foreground lands too.
                    Add(NaplpsCommandBuilder.BuildSelectColor(f, (byte)(b == 0 ? 7 : 0)));
                }

                Add(NaplpsCommandBuilder.BuildSelectColor(f, b));
            }
            else
            {
                Add(NaplpsCommandBuilder.BuildSelectColor(f));
            }

            if (charW >= 0 && charH >= 0)
            {
                Add(NaplpsCommandBuilder.BuildText((float)Quant(charW), (float)Quant(charH), multiByteValue: mbv));
            }

            // The payload has to resolve through a character set. It goes here rather than at
            // the head of the run so the run's FIRST byte stays a PDI opcode - see the note on
            // AppendComplete about terminating the caller's pending operand list.
            bytes.Add(ShiftIn);
            bytes.AddRange(ascii);
            bytes.AddRange(RestoreGraphicLeft(incomingGraphicLeft));
        });
    }

    /// <summary>
    /// Append a solid filled rectangle: TEXTURE (solid fill), SELECT COLOR (foreground
    /// form), RECTANGLE SET FILLED. Position and size are rounded to the coordinate wire
    /// grid (size floored at one grid step). Alignment guarantee: a rect at the SAME
    /// quantized position/size as a DrawText cell covers that cell exactly - address
    /// cells as x_q + i * cw_q (quantized values), never i * nominal_pitch, which is not
    /// grid-representable. Decoder-state footprint of the emitted commands: texture
    /// becomes solid fill / solid line / no highlight / zero mask size, color mode
    /// becomes 1 (foreground) with the given color, and the pen ends at (x + w, y) per
    /// the X3.110 rectangle pen advance. Shift state is not in that footprint and is not
    /// depended on either: coded 8-bit (see <see cref="EncodeSynthesized7Bit"/>) the run
    /// neither reads nor writes a byte in GL. Throws <see cref="InvalidOperationException"/>
    /// inside an unfinished definition, is
    /// paused mid-command (any deferred tail - flush a finished stream first), or is not
    /// yet established.
    /// </summary>
    public int FillRect(double x, double y, double w, double h, int color)
    {
        ThrowIfDisposed();

        if (!double.IsFinite(x)) { throw new ArgumentOutOfRangeException(nameof(x), "non-finite position"); }
        if (!double.IsFinite(y)) { throw new ArgumentOutOfRangeException(nameof(y), "non-finite position"); }
        if (!double.IsFinite(w) || w <= 0) { throw new ArgumentOutOfRangeException(nameof(w), "non-positive size"); }
        if (!double.IsFinite(h) || h <= 0) { throw new ArgumentOutOfRangeException(nameof(h), "non-positive size"); }

        return EmitSynthesizedRun(run =>
        {
            run.Add(NaplpsCommandBuilder.BuildTexture(0, false, 0, multiByteValue: run.MultiByteValue));
            run.Add(NaplpsCommandBuilder.BuildSelectColor((byte)Math.Clamp(color, 0, 15)));
            run.Add(NaplpsCommandBuilder.BuildRectangleSetFilled(
                (float)run.Quant(x), (float)run.Quant(y), (float)run.QuantSize(w), (float)run.QuantSize(h), run.MultiByteValue));
        });
    }

    /// <summary>
    /// Append a one-pel rectangle OUTLINE: TEXTURE (solid line), SELECT COLOR (foreground
    /// form), RECTANGLE SET OUTLINED. Unlike <see cref="FillRect"/>, this draws the four
    /// edges as X3.110 lines, which are exactly one device pel wide and carry no fill halo,
    /// so a focus/cursor border is a true hairline rather than the >=2-pel + boundary-pel
    /// footprint of a filled rect. Position and size are rounded to the coordinate wire grid
    /// (size floored at one grid step). Decoder-state footprint matches FillRect except the
    /// texture's line form stays solid; the pen ends at (x + w, y). Throws
    /// <see cref="InvalidOperationException"/> inside an unfinished definition, is
    /// paused mid-command (any deferred tail - flush a finished stream first), or is not
    /// yet established.
    /// </summary>
    public int StrokeRect(double x, double y, double w, double h, int color)
    {
        ThrowIfDisposed();

        if (!double.IsFinite(x)) { throw new ArgumentOutOfRangeException(nameof(x), "non-finite position"); }
        if (!double.IsFinite(y)) { throw new ArgumentOutOfRangeException(nameof(y), "non-finite position"); }
        if (!double.IsFinite(w) || w <= 0) { throw new ArgumentOutOfRangeException(nameof(w), "non-positive size"); }
        if (!double.IsFinite(h) || h <= 0) { throw new ArgumentOutOfRangeException(nameof(h), "non-positive size"); }

        return EmitSynthesizedRun(run =>
        {
            run.Add(NaplpsCommandBuilder.BuildTexture(0, false, 0, multiByteValue: run.MultiByteValue));
            run.Add(NaplpsCommandBuilder.BuildSelectColor((byte)Math.Clamp(color, 0, 15)));
            run.Add(NaplpsCommandBuilder.BuildRectangleSetOutlined(
                (float)run.Quant(x), (float)run.Quant(y), (float)run.QuantSize(w), (float)run.QuantSize(h), run.MultiByteValue));
        });
    }

    /// <summary>
    /// Shared scaffold for the synthesized-run primitives (draw-text, fill-rect,
    /// stroke-rect): the definition and mid-command guards, the wire-grid quantizer,
    /// the synthesized-encoding pinning, single-shift neutrality, and the complete-run
    /// append. Every primitive gets identical safety semantics from one place.
    /// </summary>
    private int EmitSynthesizedRun(Action<SynthesizedRun> build)
    {
        // Before establishment the run's bytes would join the held header and pollute
        // system-type detection (an A1 mid-marker plus run bytes locks the wrong system
        // type for the session's whole life). The caller establishes first - append the
        // page's opening bytes, or flush an intentionally-empty stream.
        if (_decoder is null)
        {
            throw new InvalidOperationException("system type not yet established; append stream bytes (or flush) before synthesizing runs");
        }

        var state = Format?.State;

        if (state is not null &&
            (state.MacroBeingDefined is not null || state.DrcsStartCode is not null || state.TextureBeingDefined is not null))
        {
            throw new InvalidOperationException("stream ends inside an unfinished definition");
        }

        // A deferred tail is a partial command - an ESC missing its final byte, a DEF
        // missing its name byte, an operand list still open, a macro expansion awaiting
        // its next byte. Interposing a run there would splice the run's first bytes into
        // the caller's command (the run's opcode becomes the ESC final, the macro NAME,
        // or a truncated operand list's terminator). Same rule as naplps.h gives for
        // flush: not on a stream merely paused mid-command.
        if (_decoder.HasDeferredTail)
        {
            throw new InvalidOperationException("stream is paused mid-command; a synthesized run would corrupt it");
        }

        var run = new SynthesizedRun((int)(state?.MultiByteValue ?? 3));
        var prior = NaplpsEncoder.Use7BitMode;
        NaplpsEncoder.Use7BitMode = EncodeSynthesized7Bit;

        try
        {
            build(run);
        }
        finally
        {
            NaplpsEncoder.Use7BitMode = prior;
        }

        // A pending SS2/SS3 belongs to the CALLER's next byte, not to the run's leading
        // PDI opcode (which would otherwise resolve through G2/G3 as a character). Park
        // it across the run and put it back for the caller's stream.
        var pendingShift = state?.PendingSingleShift;

        if (state is not null)
        {
            state.PendingSingleShift = null;
        }

        try
        {
            return AppendComplete(run.Bytes);
        }
        finally
        {
            if (state is not null && pendingShift is not null)
            {
                state.PendingSingleShift = pendingShift;
            }
        }
    }

    /// <summary>A synthesized byte run under construction, with the wire-grid quantizers.</summary>
    private sealed class SynthesizedRun
    {
        public SynthesizedRun(int multiByteValue)
        {
            MultiByteValue = multiByteValue;
            Grid = 1 << (multiByteValue * 3 - 1);
        }

        public int MultiByteValue { get; }

        public int Grid { get; }

        public List<byte> Bytes { get; } = [];

        public double Quant(double v) => Math.Round(v * Grid) / Grid;

        public double QuantSize(double v) => Math.Max(1.0 / Grid, Quant(v));

        public void Add((byte opcode, NaplpsOperands operands) cmd)
        {
            Bytes.Add(cmd.opcode);
            Bytes.AddRange(cmd.operands);
        }
    }

    /// <summary>
    /// Append a run this session built itself. Such a run is complete by construction, so it
    /// ends with a flush: its last command's operands would otherwise sit at the frontier and
    /// the drawing would not appear until some later byte terminated it. (Its FIRST byte is
    /// always a PDI opcode, which terminates any operand list the caller's own stream left
    /// pending - exactly as the plain append would have.)
    /// </summary>
    private int AppendComplete(List<byte> bytes)
    {
        Append([.. bytes]);

        return Flush();
    }

    /// <summary>Copy the canvas into an RGBA8888 buffer of exactly Width*Height*4 bytes.
    /// Before any append (and after Reset) the buffer is filled with the mode's clear color (opaque black, or transparent when TransparentBackground is set).</summary>
    public void CopyFramebufferTo(byte[] destination)
    {
        ThrowIfDisposed();

        ArgumentNullException.ThrowIfNull(destination);
        if (destination.Length < Width * Height * 4) { throw new ArgumentException("buffer too small", nameof(destination)); }

        if (_draw is null)
        {
            var alpha = TransparentBackground ? (byte)0 : (byte)255;
            for (var i = 0; i < Width * Height * 4; i += 4)
            {
                destination[i] = 0;
                destination[i + 1] = 0;
                destination[i + 2] = 0;
                destination[i + 3] = alpha;
            }

            return;
        }

        _draw.Image.CopyPixelDataTo(destination.AsSpan(0, Width * Height * 4));
    }

    /// <summary>Clear the decoder state, command list, and canvas for a fresh page.</summary>
    public void Reset()
    {
        ThrowIfDisposed();

        _header.Clear();
        _draw?.Dispose();
        _draw = null;
        _decoder = null;
        _appended = false;
        Format = null;
        Cursor = 0;

        TryEstablish(atStreamEnd: false);
    }

    public void Dispose()
    {
        _disposed = true;
        _draw?.Dispose();
        _draw = null;
    }

    private bool _disposed;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
