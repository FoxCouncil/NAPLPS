// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using NAPLPS.Drawing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NAPLPSTests.Base;

/// <summary>
/// The streaming APNG writer stores only each frame's changed rectangle, so its bytes differ from
/// the in-memory encoder's by design. What must NOT differ is what a decoder sees: same frame
/// count, same pixels, frame for frame. These tests pin that equivalence on real corpus files,
/// because a dirty-rect bug shows up as a frame that silently keeps stale pixels rather than as
/// anything that throws.
/// </summary>
[TestClass]
public class ApngWriterTests
{
    private static readonly string[] Corpus =
    [
        "Anthony Wetzel/AUDI5.nap",          // long animation, small per-frame deltas
        "Screens From Preview Disks/place-settings.nap",
        "naplps04.nap",                       // Telidon, anti-aliased curves
        "Cyd Gorman 1/20.nap",
    ];

    private static string Render(string rel, out string streamed)
    {
        var full = Path.Combine(NAPLPSTests.Visual.VisualTestContext.ExamplesDir, rel.Replace('/', Path.DirectorySeparatorChar));
        var forced = NAPLPSTests.Visual.VisualTestContext.GetForcedSystemType(rel);

        var inMemoryPath = Path.Combine(Path.GetTempPath(), $"apng-mem-{Guid.NewGuid():N}.apng");
        streamed = Path.Combine(Path.GetTempPath(), $"apng-str-{Guid.NewGuid():N}.apng");

        var naplps = NaplpsFormat.FromFile(full, forced);

        using (var ctx = new DrawContext(naplps, new SixLabors.ImageSharp.Size(1024, 768)))
        using (var apng = ctx.RenderToApng())
        {
            apng.SaveAsPng(inMemoryPath);
        }

        var naplps2 = NaplpsFormat.FromFile(full, forced);

        using (var ctx2 = new DrawContext(naplps2, new SixLabors.ImageSharp.Size(1024, 768)))
        {
            ctx2.RenderApngToFile(streamed);
        }

        return inMemoryPath;
    }

    [TestMethod]
    public void StreamedApngDecodesIdenticallyToInMemory()
    {
        foreach (var rel in Corpus)
        {
            var memPath = Render(rel, out var strPath);

            try
            {
                using var mem = Image.Load<Rgba32>(memPath);
                using var str = Image.Load<Rgba32>(strPath);

                Assert.AreEqual(mem.Frames.Count, str.Frames.Count, $"{rel}: frame count");

                var a = new byte[mem.Width * mem.Height * 4];
                var b = new byte[str.Width * str.Height * 4];

                for (int f = 0; f < mem.Frames.Count; f++)
                {
                    using var fa = mem.Frames.CloneFrame(f);
                    using var fb = str.Frames.CloneFrame(f);

                    fa.CopyPixelDataTo(a);
                    fb.CopyPixelDataTo(b);

                    Assert.IsTrue(a.AsSpan().SequenceEqual(b), $"{rel}: frame {f} differs");
                }

                // Encoded size is NOT where dirty-rect pays off, and it is worth recording why:
                // deflate already collapses the unchanged parts of a full-canvas frame to almost
                // nothing, so the two files come out within a fraction of a percent of each other.
                // The win is memory - the in-memory encoder holds every frame at full canvas size
                // until the end (gigabytes for the longer files), while the streaming writer holds
                // the current canvas and the previous one, regardless of frame count.
                // Guard only against size regressing badly, so a future filtering change that
                // bloated the output would still be caught.
                long memSize = new FileInfo(memPath).Length;
                long strSize = new FileInfo(strPath).Length;

                Assert.IsLessThan(memSize * 1.1, (double)strSize, $"{rel}: streamed ({strSize}) regressed badly vs in-memory ({memSize})");
            }
            finally
            {
                System.IO.File.Delete(memPath);
                System.IO.File.Delete(strPath);
            }
        }
    }

    /// <summary>
    /// Blink frames were the one thing the streaming path could not do, which kept the app's export
    /// on the in-memory encoder. Pin that they now agree frame for frame, on a file that actually
    /// blinks - otherwise this passes trivially.
    /// </summary>
    [TestMethod]
    public void StreamedBlinkFramesMatchInMemory()
    {
        const string rel = "blinky.nap";
        const int cycles = 2;

        var full = Path.Combine(NAPLPSTests.Visual.VisualTestContext.ExamplesDir, rel);
        var memPath = Path.Combine(Path.GetTempPath(), $"blink-mem-{Guid.NewGuid():N}.apng");
        var strPath = Path.Combine(Path.GetTempPath(), $"blink-str-{Guid.NewGuid():N}.apng");

        try
        {
            int withoutBlink;
            int withBlink;

            using (var ctx = new DrawContext(NaplpsFormat.FromFile(full, null), new SixLabors.ImageSharp.Size(1024, 768)))
            using (var apng = ctx.RenderToApng(5, false, cycles))
            {
                withBlink = apng.Frames.Count;
                apng.SaveAsPng(memPath);
            }

            using (var ctx = new DrawContext(NaplpsFormat.FromFile(full, null), new SixLabors.ImageSharp.Size(1024, 768)))
            {
                withoutBlink = ctx.RenderApngToFile(strPath, 5, false, 0);
            }

            Assert.IsGreaterThan(withoutBlink, withBlink, $"{rel} must actually produce blink frames or this test proves nothing");

            using (var ctx = new DrawContext(NaplpsFormat.FromFile(full, null), new SixLabors.ImageSharp.Size(1024, 768)))
            {
                ctx.RenderApngToFile(strPath, 5, false, cycles);
            }

            using var mem = Image.Load<Rgba32>(memPath);
            using var str = Image.Load<Rgba32>(strPath);

            Assert.AreEqual(mem.Frames.Count, str.Frames.Count, "blink frame count");

            var a = new byte[mem.Width * mem.Height * 4];
            var b = new byte[str.Width * str.Height * 4];

            for (int f = 0; f < mem.Frames.Count; f++)
            {
                using var fa = mem.Frames.CloneFrame(f);
                using var fb = str.Frames.CloneFrame(f);

                fa.CopyPixelDataTo(a);
                fb.CopyPixelDataTo(b);

                Assert.IsTrue(a.AsSpan().SequenceEqual(b), $"blink frame {f} differs");
            }
        }
        finally
        {
            System.IO.File.Delete(memPath);
            System.IO.File.Delete(strPath);
        }
    }

    /// <summary>
    /// The export dialog clips to a frame range and scales; both used to happen by mutating a fully
    /// materialised animation. Streaming applies them per frame instead, so pin the result.
    /// </summary>
    [TestMethod]
    public void FrameRangeAndScaleApplyWhileStreaming()
    {
        const string rel = "bb8.nap";

        var full = Path.Combine(NAPLPSTests.Visual.VisualTestContext.ExamplesDir, rel);
        var allPath = Path.Combine(Path.GetTempPath(), $"range-all-{Guid.NewGuid():N}.apng");
        var cutPath = Path.Combine(Path.GetTempPath(), $"range-cut-{Guid.NewGuid():N}.apng");

        try
        {
            int total;

            using (var ctx = new DrawContext(NaplpsFormat.FromFile(full, null), new SixLabors.ImageSharp.Size(1024, 768)))
            {
                total = ctx.RenderApngToFile(allPath);
            }

            Assert.IsGreaterThan(6, total, "need enough frames to clip meaningfully");

            // Keep frames 3..6 (1-based inclusive) at half size.
            using (var ctx = new DrawContext(NaplpsFormat.FromFile(full, null), new SixLabors.ImageSharp.Size(1024, 768)))
            {
                int written = ctx.RenderApngToFile(cutPath, 5, false, 0, 3, 6, new SixLabors.ImageSharp.Size(512, 384));

                Assert.AreEqual(4, written, "frames 3..6 inclusive is four frames");
            }

            using var all = Image.Load<Rgba32>(allPath);
            using var cut = Image.Load<Rgba32>(cutPath);

            Assert.AreEqual(4, cut.Frames.Count);
            Assert.AreEqual(512, cut.Width);
            Assert.AreEqual(384, cut.Height);

            // The kept range must be the requested one, not just any four frames: frame 0 of the
            // clipped file is the scaled frame 2 (0-based) of the full render.
            using var expected = all.Frames.CloneFrame(2);
            expected.Mutate(c => c.Resize(512, 384));

            using var actual = cut.Frames.CloneFrame(0);

            var e = new byte[512 * 384 * 4];
            var g = new byte[512 * 384 * 4];
            expected.CopyPixelDataTo(e);
            actual.CopyPixelDataTo(g);

            Assert.IsTrue(e.AsSpan().SequenceEqual(g), "clipped output should start at the requested frame");
        }
        finally
        {
            System.IO.File.Delete(allPath);
            System.IO.File.Delete(cutPath);
        }
    }

    /// <summary>
    /// The visual suite compares baselines through <see cref="ApngReader"/> rather than ImageSharp,
    /// so a decoder that quietly composited differently would not fail loudly - it would just make
    /// the baselines mean something slightly different. Pin it against ImageSharp on real files,
    /// including the in-memory encoder's output so the reader is not merely self-consistent with
    /// our own writer.
    /// </summary>
    [TestMethod]
    public void ReaderCompositesIdenticallyToImageSharp()
    {
        foreach (var rel in Corpus)
        {
            var memPath = Render(rel, out var strPath);

            try
            {
                foreach (var path in new[] { memPath, strPath })
                {
                    using var expected = Image.Load<Rgba32>(path);

                    using var stream = System.IO.File.OpenRead(path);
                    using var reader = new ApngReader(stream, leaveOpen: true);

                    var mine = new byte[reader.Width * reader.Height * 4];
                    var theirs = new byte[reader.Width * reader.Height * 4];

                    int count = 0;

                    while (reader.TryReadFrame(mine))
                    {
                        Assert.IsLessThan(expected.Frames.Count, count, $"{rel} ({Path.GetFileName(path)}): reader produced more frames than ImageSharp");

                        using var frame = expected.Frames.CloneFrame(count);
                        frame.CopyPixelDataTo(theirs);

                        Assert.IsTrue(mine.AsSpan().SequenceEqual(theirs), $"{rel} ({Path.GetFileName(path)}): frame {count} composited differently");

                        count++;
                    }

                    Assert.AreEqual(expected.Frames.Count, count, $"{rel} ({Path.GetFileName(path)}): frame count");
                }
            }
            finally
            {
                System.IO.File.Delete(memPath);
                System.IO.File.Delete(strPath);
            }
        }
    }
}
