// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using NAPLPS.Drawing;

namespace NAPLPS;

/// <summary>
/// A stateful decode-and-paint session over an append-only NAPLPS byte stream: the managed
/// core behind the C ABI's naplps_ctx_* entry points (see NativeExportsCtx), and equally
/// usable from managed code and tests.
///
/// Model: the accumulated BYTES are the source of truth. Each append re-parses the whole
/// buffer (parsing is deterministic, so decoder state - character sets, DRCS, position,
/// attributes - matches an incremental decode) and replays the already-executed command
/// prefix onto a fresh canvas, then execution continues from the cursor. Appends are
/// transactional: on any parse or replay failure the session is left exactly as it was.
///
/// Chunks may split anywhere, including mid-command. A trailing partial command parses
/// from its truncated operands and, once completed by a later append, is repainted
/// correctly by the replay - so already-blitted pixels can change across an append.
/// Callers that append complete streams never observe this.
///
/// Thread model: instances are not internally synchronized; use one session per thread
/// or synchronize externally. A disposed session throws ObjectDisposedException from
/// every method; property getters keep their last values.
/// </summary>
public sealed class NaplpsStreamSession : IDisposable
{
    private readonly List<byte> _bytes = [];
    private DrawContext? _draw;

    public NaplpsStreamSession(int width, int height, bool prodigy)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);
        Width = width;
        Height = height;
        Prodigy = prodigy;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Force the Prodigy pipeline (canonical CLUT, MVDI text, authentic
    /// geometry, Prodigy display ratio) regardless of stream auto-detection.</summary>
    public bool Prodigy { get; }

    public NaplpsFormat? Format { get; private set; }

    /// <summary>Count of commands already painted onto the canvas.</summary>
    public int Cursor { get; private set; }

    public int CommandCount => Format?.Commands.Count ?? 0;

    /// <summary>
    /// Append bytes to the stream and re-establish the canvas. Transactional: on failure
    /// the session state (bytes, parse, canvas, cursor) is unchanged and the exception
    /// propagates. Returns the new total command count.
    /// </summary>
    public int Append(byte[] chunk)
    {
        ThrowIfDisposed();

        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.Length == 0) { throw new ArgumentException("empty chunk", nameof(chunk)); }

        var combined = new byte[_bytes.Count + chunk.Length];
        _bytes.CopyTo(combined);
        chunk.CopyTo(combined, _bytes.Count);

        var format = NaplpsFormat.FromBytes(combined, Prodigy ? NaplpsSystemType.Prodigy : null);

        // Rebuild the canvas up to the cursor on a fresh context; only after the replay
        // succeeds does the new state get committed.
        var draw = new DrawContext(format, new SixLabors.ImageSharp.Size(Width, Height));
        try
        {
            if (Prodigy)
            {
                // Match naplps_render_png_prodigy: the ctor derives gun width / MVDI font /
                // display ratio from SystemType; authentic geometry is set explicitly.
                draw.AuthenticGeometry = true;
            }

            draw.ClearCanvas();
            var replayTo = Math.Min(Cursor, format.Commands.Count);
            for (var i = 0; i < replayTo; i++)
            {
                draw.RenderStep(i);
            }

            _bytes.AddRange(chunk);
            _draw?.Dispose();
            _draw = draw;
            Format = format;
            Cursor = replayTo;
            return format.Commands.Count;
        }
        catch
        {
            draw.Dispose();
            throw;
        }
    }

    /// <summary>Paint up through (and including) command <paramref name="cmdIndex"/>,
    /// clamped to the stream end. Idempotent for already-painted commands. Returns the
    /// highest painted index, or -1 when nothing has been painted.</summary>
    public int ExecTo(int cmdIndex)
    {
        ThrowIfDisposed();

        ArgumentOutOfRangeException.ThrowIfNegative(cmdIndex);
        if (_draw is null) { throw new InvalidOperationException("no bytes appended"); }

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

        if (_draw is null) { throw new InvalidOperationException("no bytes appended"); }
        if (Cursor >= CommandCount) { return null; }

        _draw.RenderStep(Cursor);
        return Cursor++;
    }

    /// <summary>
    /// Append a field-text run built by the library's own encoder: Point Set Absolute,
    /// SELECT COLOR (mode-shaped), optional TEXT character size, then the text bytes.
    /// Coordinates and sizes are rounded to the coordinate wire grid. Throws
    /// <see cref="InvalidOperationException"/> when the stream currently ends inside an
    /// unfinished macro / DRCS / texture definition (the bytes would be swallowed into
    /// the definition instead of drawing).
    /// </summary>
    public int DrawText(double x, double y, int fg, int bg, double charW, double charH, byte[] ascii)
    {
        ThrowIfDisposed();

        ArgumentNullException.ThrowIfNull(ascii);
        if (ascii.Length == 0) { throw new ArgumentException("empty text", nameof(ascii)); }

        var state = Format?.State;
        if (state is not null &&
            (state.MacroBeingDefined is not null || state.DrcsStartCode is not null || state.TextureBeingDefined is not null))
        {
            throw new InvalidOperationException("stream ends inside an unfinished definition");
        }

        var mbv = (int)(state?.MultiByteValue ?? 3);
        var grid = 1 << (mbv * 3 - 1);
        double Quant(double v) => Math.Round(v * grid) / grid;

        var prior = NaplpsEncoder.Use7BitMode;
        NaplpsEncoder.Use7BitMode = Format?.Is7Bit ?? false;
        var bytes = new List<byte>();
        void Add((byte opcode, NaplpsOperands operands) cmd)
        {
            bytes.Add(cmd.opcode);
            bytes.AddRange(cmd.operands);
        }

        try
        {
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

            bytes.AddRange(ascii);
        }
        finally
        {
            NaplpsEncoder.Use7BitMode = prior;
        }

        return Append([.. bytes]);
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
    /// the X3.110 rectangle pen advance. Throws <see cref="InvalidOperationException"/>
    /// inside an unfinished definition.
    /// </summary>
    public int FillRect(double x, double y, double w, double h, int color)
    {
        if (!double.IsFinite(x)) { throw new ArgumentOutOfRangeException(nameof(x), "non-finite position"); }
        if (!double.IsFinite(y)) { throw new ArgumentOutOfRangeException(nameof(y), "non-finite position"); }
        if (!double.IsFinite(w) || w <= 0) { throw new ArgumentOutOfRangeException(nameof(w), "non-positive size"); }
        if (!double.IsFinite(h) || h <= 0) { throw new ArgumentOutOfRangeException(nameof(h), "non-positive size"); }

        var state = Format?.State;
        if (state is not null &&
            (state.MacroBeingDefined is not null || state.DrcsStartCode is not null || state.TextureBeingDefined is not null))
        {
            throw new InvalidOperationException("stream ends inside an unfinished definition");
        }

        var mbv = (int)(state?.MultiByteValue ?? 3);
        var grid = 1 << (mbv * 3 - 1);
        double Quant(double v) => Math.Round(v * grid) / grid;
        double QuantSize(double v) => Math.Max(1.0 / grid, Quant(v));

        var prior = NaplpsEncoder.Use7BitMode;
        NaplpsEncoder.Use7BitMode = Format?.Is7Bit ?? false;
        var bytes = new List<byte>();
        void Add((byte opcode, NaplpsOperands operands) cmd)
        {
            bytes.Add(cmd.opcode);
            bytes.AddRange(cmd.operands);
        }

        try
        {
            Add(NaplpsCommandBuilder.BuildTexture(0, false, 0, multiByteValue: mbv));
            Add(NaplpsCommandBuilder.BuildSelectColor((byte)Math.Clamp(color, 0, 15)));
            Add(NaplpsCommandBuilder.BuildRectangleSetFilled(
                (float)Quant(x), (float)Quant(y), (float)QuantSize(w), (float)QuantSize(h), mbv));
        }
        finally
        {
            NaplpsEncoder.Use7BitMode = prior;
        }

        return Append([.. bytes]);
    }

    /// <summary>Copy the canvas into an RGBA8888 buffer of exactly Width*Height*4 bytes.
    /// Before any append (and after Reset) the buffer is filled opaque black.</summary>
    public void CopyFramebufferTo(byte[] destination)
    {
        ThrowIfDisposed();

        ArgumentNullException.ThrowIfNull(destination);
        if (destination.Length < Width * Height * 4) { throw new ArgumentException("buffer too small", nameof(destination)); }

        if (_draw is null)
        {
            for (var i = 0; i < Width * Height * 4; i += 4)
            {
                destination[i] = 0;
                destination[i + 1] = 0;
                destination[i + 2] = 0;
                destination[i + 3] = 255;
            }

            return;
        }

        _draw.Image.CopyPixelDataTo(destination.AsSpan(0, Width * Height * 4));
    }

    /// <summary>Clear the byte stream, decoder state, and canvas for a fresh page.</summary>
    public void Reset()
    {
        ThrowIfDisposed();

        _bytes.Clear();
        _draw?.Dispose();
        _draw = null;
        Format = null;
        Cursor = 0;
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
