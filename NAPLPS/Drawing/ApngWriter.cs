// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using System.Buffers.Binary;
using System.IO.Compression;

namespace NAPLPS.Drawing;

/// <summary>
/// Writes an animated PNG one frame at a time, storing only the rectangle that actually changed.
///
/// The renderer used to build the whole animation as an ImageSharp <c>Image</c> with N full-canvas
/// frames and encode it at the end, which is O(frames) in memory: a 1462-frame 1024x768 file such
/// as farao.nap is ~4.6 GB on its own, and rendering the corpus three-at-a-time peaked at ~19.8 GB.
/// Measured across the corpus, consecutive frames differ over just 2.2% of the canvas, so writing
/// each frame's dirty bounding box instead of the whole canvas is roughly a 46x reduction in both
/// memory and encoded size.
///
/// APNG is designed for exactly this: every frame carries its own x/y/width/height in its fcTL
/// chunk. ImageSharp cannot ENCODE that - PngFrameMetadata exposes only delay, dispose and blend -
/// but it decodes it correctly, which is what the baseline comparison relies on.
///
/// Frames use dispose=NONE with blend=SOURCE, so each dirty rect simply replaces those pixels and
/// the rest of the canvas persists. That makes a frame's meaning independent of how the previous
/// one was composited.
/// </summary>
public sealed class ApngWriter : IDisposable
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly Stream _stream;
    private readonly int _width;
    private readonly int _height;
    private readonly long _actlCountOffset;

    private uint _sequence;
    private uint _frameCount;
    private bool _wroteDefaultImage;

    /// <param name="stream">Must be seekable: the frame count is patched into acTL on dispose.</param>
    public ApngWriter(Stream stream, int width, int height, uint repeatCount)
    {
        if (!stream.CanSeek)
        {
            throw new ArgumentException("APNG writing needs a seekable stream to patch the frame count.", nameof(stream));
        }

        _stream = stream;
        _width = width;
        _height = height;

        _stream.Write(Signature);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr[..4], width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.Slice(4, 4), height);
        ihdr[8] = 8;    // bit depth
        ihdr[9] = 6;    // colour type: RGBA
        ihdr[10] = 0;   // deflate
        ihdr[11] = 0;   // adaptive filtering
        ihdr[12] = 0;   // no interlace
        WriteChunk("IHDR"u8, ihdr);

        // Frame count is not known until the last frame is written, so remember where to patch it.
        var actl = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(actl.AsSpan(4, 4), repeatCount);
        _actlCountOffset = _stream.Position + 8;
        WriteChunk("acTL"u8, actl);
    }

    /// <summary>
    /// Appends a frame covering <paramref name="rect"/> of the canvas. <paramref name="pixels"/> is
    /// the full canvas; only the rectangle is read and stored.
    /// </summary>
    public void WriteFrame(ReadOnlySpan<byte> pixels, Rectangle rect, ushort delayNumerator, ushort delayDenominator)
    {
        // A frame that changed nothing still has to advance time, so keep a 1x1 rect rather than
        // emitting an empty one - fcTL forbids zero width or height.
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            rect = new Rectangle(0, 0, 1, 1);
        }

        Span<byte> fctl = stackalloc byte[26];
        BinaryPrimitives.WriteUInt32BigEndian(fctl[..4], _sequence++);
        BinaryPrimitives.WriteInt32BigEndian(fctl.Slice(4, 4), rect.Width);
        BinaryPrimitives.WriteInt32BigEndian(fctl.Slice(8, 4), rect.Height);
        BinaryPrimitives.WriteInt32BigEndian(fctl.Slice(12, 4), rect.X);
        BinaryPrimitives.WriteInt32BigEndian(fctl.Slice(16, 4), rect.Y);
        BinaryPrimitives.WriteUInt16BigEndian(fctl.Slice(20, 2), delayNumerator);
        BinaryPrimitives.WriteUInt16BigEndian(fctl.Slice(22, 2), delayDenominator);
        fctl[24] = 0; // dispose: NONE
        fctl[25] = 0; // blend: SOURCE
        WriteChunk("fcTL"u8, fctl);

        var deflated = Deflate(pixels, rect);

        if (!_wroteDefaultImage)
        {
            // The first frame is the PNG's own image, so it goes in IDAT and must be full canvas.
            WriteChunk("IDAT"u8, deflated);
            _wroteDefaultImage = true;
        }
        else
        {
            var fdat = new byte[4 + deflated.Length];
            BinaryPrimitives.WriteUInt32BigEndian(fdat.AsSpan(0, 4), _sequence++);
            deflated.CopyTo(fdat.AsSpan(4));
            WriteChunk("fdAT"u8, fdat);
        }

        _frameCount++;
    }

    /// <summary>The rectangle in which <paramref name="current"/> differs from <paramref name="previous"/>.</summary>
    public static Rectangle DirtyRect(ReadOnlySpan<byte> current, ReadOnlySpan<byte> previous, int width, int height)
    {
        int minX = width;
        int minY = height;
        int maxX = -1;
        int maxY = -1;

        for (int y = 0; y < height; y++)
        {
            int row = y * width * 4;
            var a = current.Slice(row, width * 4);
            var b = previous.Slice(row, width * 4);

            if (a.SequenceEqual(b))
            {
                continue;
            }

            if (y < minY) { minY = y; }
            if (y > maxY) { maxY = y; }

            for (int x = 0; x < width; x++)
            {
                int i = x * 4;

                if (!a.Slice(i, 4).SequenceEqual(b.Slice(i, 4)))
                {
                    if (x < minX) { minX = x; }
                    if (x > maxX) { maxX = x; }
                }
            }
        }

        return maxX < 0 ? Rectangle.Empty : new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    private byte[] Deflate(ReadOnlySpan<byte> pixels, Rectangle rect)
    {
        using var buffer = new MemoryStream();

        const int bpp = 4;
        int stride = rect.Width * bpp;

        using (var zlib = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            var raw = new byte[stride];
            var prior = new byte[stride];
            var candidate = new byte[5 * stride];
            var line = new byte[1 + stride];

            for (int y = 0; y < rect.Height; y++)
            {
                int src = (((rect.Y + y) * _width) + rect.X) * bpp;
                pixels.Slice(src, stride).CopyTo(raw);

                // Standard adaptive filtering: try all five and keep whichever has the smallest
                // sum of absolute signed deviations, which is the heuristic the PNG spec suggests
                // and correlates well with what deflate can then do. Skipping this entirely (always
                // filter 0) makes the encoded file LARGER than the whole-canvas encoder's output,
                // which would defeat the point of storing only the dirty rectangle.
                int best = 0;
                long bestScore = long.MaxValue;

                for (int f = 0; f < 5; f++)
                {
                    var dst = candidate.AsSpan(f * stride, stride);
                    long score = 0;

                    for (int i = 0; i < stride; i++)
                    {
                        byte a = i >= bpp ? raw[i - bpp] : (byte)0;
                        byte b = prior[i];
                        byte c = i >= bpp ? prior[i - bpp] : (byte)0;

                        byte v = f switch
                        {
                            0 => raw[i],
                            1 => (byte)(raw[i] - a),
                            2 => (byte)(raw[i] - b),
                            3 => (byte)(raw[i] - ((a + b) >> 1)),
                            _ => (byte)(raw[i] - Paeth(a, b, c)),
                        };

                        dst[i] = v;
                        score += (sbyte)v < 0 ? -(sbyte)v : (sbyte)v;
                    }

                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = f;
                    }
                }

                line[0] = (byte)best;
                candidate.AsSpan(best * stride, stride).CopyTo(line.AsSpan(1));
                zlib.Write(line);

                raw.CopyTo(prior.AsSpan());
            }
        }

        return buffer.ToArray();
    }

    private static byte Paeth(byte a, byte b, byte c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);

        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private void WriteChunk(ReadOnlySpan<byte> tag, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        _stream.Write(length);
        _stream.Write(tag);
        _stream.Write(data);

        uint crc = 0xFFFFFFFF;
        crc = UpdateCrc(crc, tag);
        crc = UpdateCrc(crc, data);

        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc ^ 0xFFFFFFFF);
        _stream.Write(crcBytes);
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];

        for (uint n = 0; n < 256; n++)
        {
            uint c = n;

            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    public void Dispose()
    {
        WriteChunk("IEND"u8, ReadOnlySpan<byte>.Empty);

        // Patch the real frame count into acTL now that it is known.
        long end = _stream.Position;
        _stream.Position = _actlCountOffset;

        Span<byte> count = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(count, _frameCount);
        _stream.Write(count);

        // The chunk's CRC covers the tag and data, so it has to be recomputed after the patch.
        _stream.Position = _actlCountOffset;
        Span<byte> actlData = stackalloc byte[8];
        _stream.ReadExactly(actlData);

        uint crc = 0xFFFFFFFF;
        crc = UpdateCrc(crc, "acTL"u8);
        crc = UpdateCrc(crc, actlData);

        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc ^ 0xFFFFFFFF);
        _stream.Write(crcBytes);

        _stream.Position = end;
        _stream.Flush();
    }
}
