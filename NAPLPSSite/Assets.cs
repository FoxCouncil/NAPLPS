// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using System.Security.Cryptography;
using NAPLPS.Drawing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NAPLPSSite;

/// <summary>
/// Content-addressed asset store. Every asset is named by the hash of its bytes, so a baseline that
/// is unchanged across several covered commits is written once and referenced from each - which is
/// what keeps the history section from multiplying the corpus by the number of commits.
/// </summary>
public sealed class Assets(string outputRoot)
{
    private readonly HashSet<string> _written = [];

    public long BytesWritten { get; private set; }

    public int Count => _written.Count;

    private static string Hash(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexStringLower(SHA256.HashData(bytes))[..16];
    }

    /// <summary>Stores raw bytes under <paramref name="folder"/> and returns the site-relative path.</summary>
    public string Store(byte[] bytes, string folder, string extension)
    {
        var name = $"{Hash(bytes)}{extension}";
        var relative = $"assets/{folder}/{name}";

        if (_written.Add(relative))
        {
            var full = Path.Combine(outputRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
            BytesWritten += bytes.Length;
        }

        return relative;
    }

    /// <summary>
    /// A representative composited frame of an APNG as PNG bytes, at native size and as a
    /// thumbnail. Used for the gallery grid and for og:image - social crawlers and search engines
    /// will not animate an APNG, so a still is what actually shows up in a preview card.
    ///
    /// These animations are drawing sequences, so the LAST frame is the finished artwork and is
    /// what should represent the file - the first frame is whatever the first command painted, which
    /// on most of the corpus is a nearly blank canvas.
    ///
    /// The exception is a sequence that clears, scrolls away or fades at the end, where the final
    /// frame is empty and would make a useless thumbnail. So the final frame is used unless it
    /// holds substantially less ink than the animation's fullest frame, in which case the fullest
    /// frame wins.
    /// </summary>
    public static (byte[] Poster, byte[] Thumb, int Width, int Height, uint Frames) RepresentativeFrame(byte[] apngBytes)
    {
        using var input = new MemoryStream(apngBytes);
        using var reader = new ApngReader(input, leaveOpen: true);

        var pixels = new byte[reader.Width * reader.Height * 4];

        if (!reader.TryReadFrame(pixels))
        {
            throw new InvalidDataException("APNG contained no frames");
        }

        var last = (byte[])pixels.Clone();
        var fullest = (byte[])pixels.Clone();
        long lastInk = Ink(pixels);
        long fullestInk = lastInk;

        while (reader.TryReadFrame(pixels))
        {
            lastInk = Ink(pixels);
            pixels.CopyTo(last.AsSpan());

            if (lastInk > fullestInk)
            {
                fullestInk = lastInk;
                pixels.CopyTo(fullest.AsSpan());
            }
        }

        // A final frame holding less than 60% of the peak ink means the sequence ended on a clear
        // or a transition rather than on the finished picture.
        var chosen = fullestInk > 0 && lastInk < fullestInk * 0.6 ? fullest : last;

        using var image = Image.LoadPixelData<Rgba32>(chosen, reader.Width, reader.Height);

        using var posterStream = new MemoryStream();
        image.SaveAsPng(posterStream);

        using var thumb = image.Clone(c => c.Resize(320, 240));
        using var thumbStream = new MemoryStream();
        thumb.SaveAsPng(thumbStream);

        return (posterStream.ToArray(), thumbStream.ToArray(), reader.Width, reader.Height, reader.FrameCount);
    }

    /// <summary>
    /// How much of the canvas is drawn on: opaque pixels that are not near-black. The corpus is
    /// overwhelmingly artwork on a black or transparent field, so this separates "the picture is
    /// here" from "the screen has been cleared".
    /// </summary>
    private static long Ink(ReadOnlySpan<byte> pixels)
    {
        long n = 0;

        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] > 0 && (pixels[i] > 8 || pixels[i + 1] > 8 || pixels[i + 2] > 8))
            {
                n++;
            }
        }

        return n;
    }

    /// <summary>
    /// Compares two baselines and renders the single most-changed frame as a diff image: changed
    /// pixels in magenta over a dimmed copy of the old render, with the change outlined. One
    /// representative still per file keeps the history section bounded no matter how long the
    /// animations are.
    /// </summary>
    public static (byte[]? Diff, long TotalDiffPixels, int ChangedFrames, uint BeforeFrames, uint AfterFrames) Compare(byte[] before, byte[] after)
    {
        using var beforeStream = new MemoryStream(before);
        using var afterStream = new MemoryStream(after);
        using var beforeReader = new ApngReader(beforeStream, leaveOpen: true);
        using var afterReader = new ApngReader(afterStream, leaveOpen: true);

        int w = afterReader.Width;
        int h = afterReader.Height;

        if (beforeReader.Width != w || beforeReader.Height != h)
        {
            return (null, 0, 0, beforeReader.FrameCount, afterReader.FrameCount);
        }

        var a = new byte[w * h * 4];
        var b = new byte[w * h * 4];

        byte[]? worstBefore = null;
        byte[]? worstAfter = null;
        long worstCount = 0;
        long total = 0;
        int changedFrames = 0;

        while (true)
        {
            bool haveBefore = beforeReader.TryReadFrame(a);
            bool haveAfter = afterReader.TryReadFrame(b);

            if (!haveBefore || !haveAfter)
            {
                break;
            }

            if (a.AsSpan().SequenceEqual(b))
            {
                continue;
            }

            long count = 0;

            for (int i = 0; i < a.Length; i += 4)
            {
                if (!a.AsSpan(i, 4).SequenceEqual(b.AsSpan(i, 4)))
                {
                    count++;
                }
            }

            changedFrames++;
            total += count;

            if (count > worstCount)
            {
                worstCount = count;
                worstBefore = (byte[])a.Clone();
                worstAfter = (byte[])b.Clone();
            }
        }

        if (worstBefore is null || worstAfter is null)
        {
            return (null, total, changedFrames, beforeReader.FrameCount, afterReader.FrameCount);
        }

        using var diff = new Image<Rgba32>(w, h);
        int minX = w, minY = h, maxX = -1, maxY = -1;

        diff.Frames.RootFrame.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < h; y++)
            {
                var row = accessor.GetRowSpan(y);

                for (int x = 0; x < w; x++)
                {
                    int i = ((y * w) + x) * 4;

                    if (!worstBefore.AsSpan(i, 4).SequenceEqual(worstAfter.AsSpan(i, 4)))
                    {
                        row[x] = new Rgba32(255, 0, 255, 255);

                        if (x < minX) { minX = x; }
                        if (x > maxX) { maxX = x; }
                        if (y < minY) { minY = y; }
                        if (y > maxY) { maxY = y; }
                    }
                    else
                    {
                        row[x] = new Rgba32((byte)(worstBefore[i] / 4), (byte)(worstBefore[i + 1] / 4), (byte)(worstBefore[i + 2] / 4), 255);
                    }
                }
            }

            if (maxX < 0)
            {
                return;
            }

            var outline = new Rgba32(0, 255, 128, 255);

            for (int x = minX; x <= maxX; x++)
            {
                accessor.GetRowSpan(minY)[x] = outline;
                accessor.GetRowSpan(maxY)[x] = outline;
            }

            for (int y = minY; y <= maxY; y++)
            {
                var row = accessor.GetRowSpan(y);
                row[minX] = outline;
                row[maxX] = outline;
            }
        });

        using var ms = new MemoryStream();
        diff.SaveAsPng(ms);

        return (ms.ToArray(), total, changedFrames, beforeReader.FrameCount, afterReader.FrameCount);
    }
}
