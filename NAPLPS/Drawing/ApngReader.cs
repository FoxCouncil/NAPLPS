// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using System.Buffers.Binary;
using System.IO.Compression;

namespace NAPLPS.Drawing;

/// <summary>
/// Decodes an animated PNG one composited frame at a time.
///
/// The counterpart to <see cref="ApngWriter"/>, and needed for the same reason: loading an APNG
/// through a general-purpose decoder materialises every frame at full canvas size, so comparing two
/// long animations costs O(frames) twice over. A 1429-frame 1024x768 file is ~4.3 GB per side.
/// This holds one canvas (plus one more only if a frame asks for DISPOSE_OP_PREVIOUS), regardless
/// of how many frames the file has.
///
/// Callers see the same thing a viewer would: each <see cref="TryReadFrame"/> returns the fully
/// composited canvas at that point in the animation, not the frame's stored sub-rectangle.
///
/// Scope is deliberately narrow - 8-bit RGBA, non-interlaced, which is what <see cref="ApngWriter"/>
/// produces. Anything else throws rather than silently decoding to something plausible but wrong.
/// </summary>
public sealed class ApngReader : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;

    private readonly byte[] _canvas;
    private byte[]? _previousCanvas;

    private MemoryStream? _data;
    private bool _havePending;
    private Rectangle _pendingRect;
    private byte _pendingDispose;
    private byte _pendingBlend;

    public int Width { get; }

    public int Height { get; }

    /// <summary>Frame count as declared in acTL; 0 when the file carries no animation.</summary>
    public uint FrameCount { get; }

    public ApngReader(Stream stream, bool leaveOpen = false)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;

        Span<byte> signature = stackalloc byte[8];
        _stream.ReadExactly(signature);

        ReadOnlySpan<byte> expected = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

        if (!signature.SequenceEqual(expected))
        {
            throw new InvalidDataException("Not a PNG stream.");
        }

        // IHDR is required to be the first chunk, so read it directly rather than scanning.
        if (!TryReadChunkHeader(out var tag, out int length) || !tag.SequenceEqual("IHDR"u8.ToArray()))
        {
            throw new InvalidDataException("PNG stream does not start with IHDR.");
        }

        var ihdr = new byte[length];
        _stream.ReadExactly(ihdr);
        SkipCrc();

        Width = BinaryPrimitives.ReadInt32BigEndian(ihdr.AsSpan(0, 4));
        Height = BinaryPrimitives.ReadInt32BigEndian(ihdr.AsSpan(4, 4));

        if (ihdr[8] != 8 || ihdr[9] != 6)
        {
            throw new NotSupportedException($"Only 8-bit RGBA PNG is supported (got bit depth {ihdr[8]}, colour type {ihdr[9]}).");
        }

        if (ihdr[12] != 0)
        {
            throw new NotSupportedException("Interlaced PNG is not supported.");
        }

        _canvas = new byte[Width * Height * 4];

        // acTL, when present, precedes the first frame. Peek for it without consuming anything else.
        long mark = _stream.Position;

        while (TryReadChunkHeader(out var t, out int len))
        {
            if (t.SequenceEqual("acTL"u8.ToArray()))
            {
                var actl = new byte[len];
                _stream.ReadExactly(actl);
                SkipCrc();
                FrameCount = BinaryPrimitives.ReadUInt32BigEndian(actl.AsSpan(0, 4));
                mark = _stream.Position;
                break;
            }

            if (t.SequenceEqual("IDAT"u8.ToArray()) || t.SequenceEqual("fcTL"u8.ToArray()))
            {
                // Reached image data with no acTL: a still PNG. Rewind so the frame loop sees it.
                _stream.Position = mark;
                break;
            }

            _stream.Position += len + 4;
            mark = _stream.Position;
        }
    }

    /// <summary>
    /// Composites the next frame into <paramref name="canvas"/> (which must be Width*Height*4 bytes)
    /// and returns true, or returns false once the animation is exhausted.
    /// </summary>
    public bool TryReadFrame(Span<byte> canvas)
    {
        if (canvas.Length != _canvas.Length)
        {
            throw new ArgumentException($"Canvas must be {_canvas.Length} bytes.", nameof(canvas));
        }

        while (TryReadChunkHeader(out var tag, out int length))
        {
            if (tag.SequenceEqual("fcTL"u8.ToArray()))
            {
                var fctl = new byte[length];
                _stream.ReadExactly(fctl);
                SkipCrc();

                // A pending frame's data ends where the next frame's control chunk begins.
                bool completed = _havePending && _data is not null;

                if (completed)
                {
                    Compose();
                }
                else if (_data is not null)
                {
                    // Default image that is not itself an animation frame: it seeds the canvas.
                    ComposeRaw(new Rectangle(0, 0, Width, Height), blend: 0);
                }

                _pendingRect = new Rectangle(
                    BinaryPrimitives.ReadInt32BigEndian(fctl.AsSpan(12, 4)),
                    BinaryPrimitives.ReadInt32BigEndian(fctl.AsSpan(16, 4)),
                    BinaryPrimitives.ReadInt32BigEndian(fctl.AsSpan(4, 4)),
                    BinaryPrimitives.ReadInt32BigEndian(fctl.AsSpan(8, 4)));

                _pendingDispose = fctl[24];
                _pendingBlend = fctl[25];
                _havePending = true;

                if (completed)
                {
                    _canvas.CopyTo(canvas);
                    return true;
                }

                continue;
            }

            if (tag.SequenceEqual("IDAT"u8.ToArray()))
            {
                AppendData(length, skip: 0);
                continue;
            }

            if (tag.SequenceEqual("fdAT"u8.ToArray()))
            {
                // First four bytes are the sequence number, not image data.
                AppendData(length, skip: 4);
                continue;
            }

            if (tag.SequenceEqual("IEND"u8.ToArray()))
            {
                _stream.Position += length + 4;

                if (_havePending && _data is not null)
                {
                    Compose();
                    _havePending = false;
                    _canvas.CopyTo(canvas);
                    return true;
                }

                return false;
            }

            _stream.Position += length + 4;
        }

        return false;
    }

    private void AppendData(int length, int skip)
    {
        _data ??= new MemoryStream();

        if (skip > 0)
        {
            _stream.Position += skip;
            length -= skip;
        }

        var buffer = new byte[length];
        _stream.ReadExactly(buffer);
        SkipCrc();
        _data.Write(buffer);
    }

    /// <summary>Applies the pending frame to the canvas, honouring its blend and dispose ops.</summary>
    private void Compose()
    {
        // DISPOSE_OP_PREVIOUS means the canvas must be restored afterwards, so snapshot it first.
        if (_pendingDispose == 2)
        {
            _previousCanvas ??= new byte[_canvas.Length];
            _canvas.CopyTo(_previousCanvas.AsSpan());
        }

        ComposeRaw(_pendingRect, _pendingBlend);

        switch (_pendingDispose)
        {
            case 1:
            {
                // DISPOSE_OP_BACKGROUND: the frame's own region reverts to transparent black.
                for (int y = 0; y < _pendingRect.Height; y++)
                {
                    int row = (((_pendingRect.Y + y) * Width) + _pendingRect.X) * 4;
                    Array.Clear(_canvas, row, _pendingRect.Width * 4);
                }
            }
            break;

            case 2:
            {
                _previousCanvas!.CopyTo(_canvas.AsSpan());
            }
            break;
        }
    }

    /// <summary>Inflates and defilters the buffered frame data, then blends it into the canvas.</summary>
    private void ComposeRaw(Rectangle rect, byte blend)
    {
        const int bpp = 4;

        int stride = rect.Width * bpp;
        var current = new byte[stride];
        var prior = new byte[stride];

        _data!.Position = 0;

        using (var zlib = new ZLibStream(_data, CompressionMode.Decompress, leaveOpen: true))
        {
            for (int y = 0; y < rect.Height; y++)
            {
                int filter = zlib.ReadByte();

                if (filter < 0)
                {
                    throw new InvalidDataException("Truncated APNG frame data.");
                }

                zlib.ReadExactly(current, 0, stride);
                Defilter((byte)filter, current, prior, bpp);

                int dst = (((rect.Y + y) * Width) + rect.X) * bpp;

                if (blend == 0)
                {
                    // BLEND_OP_SOURCE: the frame's pixels replace what was there, alpha included.
                    current.CopyTo(_canvas.AsSpan(dst, stride));
                }
                else
                {
                    BlendOver(current, _canvas.AsSpan(dst, stride));
                }

                (prior, current) = (current, prior);
            }
        }

        _data.Dispose();
        _data = null;
    }

    private static void Defilter(byte filter, byte[] line, byte[] prior, int bpp)
    {
        switch (filter)
        {
            case 0:
            break;

            case 1:
            {
                for (int i = bpp; i < line.Length; i++)
                {
                    line[i] = (byte)(line[i] + line[i - bpp]);
                }
            }
            break;

            case 2:
            {
                for (int i = 0; i < line.Length; i++)
                {
                    line[i] = (byte)(line[i] + prior[i]);
                }
            }
            break;

            case 3:
            {
                for (int i = 0; i < line.Length; i++)
                {
                    int a = i >= bpp ? line[i - bpp] : 0;
                    line[i] = (byte)(line[i] + ((a + prior[i]) >> 1));
                }
            }
            break;

            case 4:
            {
                for (int i = 0; i < line.Length; i++)
                {
                    byte a = i >= bpp ? line[i - bpp] : (byte)0;
                    byte c = i >= bpp ? prior[i - bpp] : (byte)0;
                    line[i] = (byte)(line[i] + Paeth(a, prior[i], c));
                }
            }
            break;

            default:
            {
                throw new InvalidDataException($"Unknown PNG filter type {filter}.");
            }
        }
    }

    private static byte Paeth(byte a, byte b, byte c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);

        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>BLEND_OP_OVER: standard non-premultiplied source-over compositing, 8-bit.</summary>
    private static void BlendOver(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        for (int i = 0; i < src.Length; i += 4)
        {
            int sa = src[i + 3];

            if (sa == 255)
            {
                src.Slice(i, 4).CopyTo(dst.Slice(i, 4));
                continue;
            }

            if (sa == 0)
            {
                continue;
            }

            int da = dst[i + 3];
            int outA = sa + (da * (255 - sa) / 255);

            for (int c = 0; c < 3; c++)
            {
                int s = src[i + c] * sa;
                int d = dst[i + c] * da * (255 - sa) / 255;
                dst[i + c] = (byte)(outA == 0 ? 0 : (s + d) / outA);
            }

            dst[i + 3] = (byte)outA;
        }
    }

    private bool TryReadChunkHeader(out byte[] tag, out int length)
    {
        Span<byte> header = stackalloc byte[8];
        int read = _stream.ReadAtLeast(header, 8, throwOnEndOfStream: false);

        if (read < 8)
        {
            tag = [];
            length = 0;

            return false;
        }

        length = BinaryPrimitives.ReadInt32BigEndian(header[..4]);
        tag = header.Slice(4, 4).ToArray();

        return true;
    }

    private void SkipCrc()
    {
        _stream.Position += 4;
    }

    public void Dispose()
    {
        _data?.Dispose();

        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }
}
