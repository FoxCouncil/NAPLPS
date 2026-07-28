// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using NAPLPSTests.Visual;

namespace NAPLPSTests.Base;

/// <summary>
/// The report pages used to inline every frame of both animations as base64, which produced a
/// 267 MB HTML file for a test that PASSED and was the single largest consumer of memory in the
/// suite. Nothing about that failed loudly - the pages were simply unopenable - so these tests pin
/// the properties that matter: pages stay small, and the frames they reference actually exist.
/// </summary>
[TestClass]
public class VisualReportTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"naplps-report-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        return dir;
    }

    /// <summary>Renders two different corpus files, so nearly every frame differs.</summary>
    private static (string Baseline, string Actual) RenderMismatchedPair(string dir)
    {
        var baseline = Path.Combine(dir, "baseline.apng");
        var actual = Path.Combine(dir, "actual.apng");

        VisualTestContext.RenderApngToFile(Path.Combine(VisualTestContext.ExamplesDir, "bb8.nap"), baseline);
        VisualTestContext.RenderApngToFile(Path.Combine(VisualTestContext.ExamplesDir, "blocky.nap"), actual);

        return (baseline, actual);
    }

    [TestMethod]
    public void DiffPageReferencesFramesInsteadOfInliningThem()
    {
        var dir = TempDir();

        try
        {
            var (baseline, actual) = RenderMismatchedPair(dir);

            var htmlPath = Path.Combine(dir, "x.diff.html");
            var framesDir = Path.Combine(dir, "x.diff.frames");

            var comparison = VisualTestContext.CompareApngs(baseline, actual, framesDir);

            Assert.IsFalse(comparison.AreIdentical, "two different files should not compare equal");

            VisualTestContext.GenerateDiffHtml("x.nap", comparison, baseline, actual, htmlPath);

            var html = System.IO.File.ReadAllText(htmlPath);

            // The page is markup plus a small metadata array; frames live on disk beside it.
            Assert.IsLessThan(256 * 1024, new FileInfo(htmlPath).Length, "diff page should stay small");
            Assert.IsFalse(html.Contains("data:image/png;base64"), "diff page must not inline frames");

            // Every frame the page can navigate to has to exist, or the viewer shows broken images.
            var exported = comparison.FrameDiffs.Where(f => f.Exported).ToList();

            Assert.IsNotEmpty(exported);
            Assert.AreEqual(comparison.ExportedFrames, exported.Count);

            foreach (var frame in exported)
            {
                foreach (var kind in new[] { "b", "a", "d" })
                {
                    var png = Path.Combine(framesDir, $"{kind}{frame.FrameIndex:D6}.png");
                    Assert.IsTrue(System.IO.File.Exists(png), $"missing exported frame {png}");
                }
            }

            // Frames beyond the cap, and frames with no counterpart, must be disclosed rather than
            // quietly dropped - every changed frame has to land in exactly one bucket.
            int changed = comparison.FrameDiffs.Count(f => f.DiffPixelCount > 0);
            Assert.AreEqual(changed, comparison.ExportedFrames + comparison.SuppressedFrames + comparison.UnpairedFrames, "every changed frame is exported, suppressed or unpaired");
            Assert.IsLessThanOrEqualTo(VisualTestContext.MaxExportedFramesPerFile, comparison.ExportedFrames);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void MatchingFilesProduceATinyPageAndNoFrameExports()
    {
        var dir = TempDir();

        try
        {
            var baseline = Path.Combine(dir, "baseline.apng");
            var actual = Path.Combine(dir, "actual.apng");
            var source = Path.Combine(VisualTestContext.ExamplesDir, "bb8.nap");

            VisualTestContext.RenderApngToFile(source, baseline);
            VisualTestContext.RenderApngToFile(source, actual);

            var htmlPath = Path.Combine(dir, "x.diff.html");
            var framesDir = Path.Combine(dir, "x.diff.frames");

            var comparison = VisualTestContext.CompareApngs(baseline, actual, framesDir);

            Assert.IsTrue(comparison.AreIdentical, "the same file rendered twice must match");

            VisualTestContext.GenerateDiffHtml("x.nap", comparison, baseline, actual, htmlPath);

            Assert.IsLessThan(32 * 1024, new FileInfo(htmlPath).Length, "a passing page is just two <img> tags");
            Assert.IsFalse(Directory.Exists(framesDir), "a passing comparison should export no frames at all");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// Bounds drive the viewer's zoom-to-change, so a wrong box sends you looking at blank canvas.
    /// A single changed pixel should produce a 1x1 box exactly on it.
    /// </summary>
    [TestMethod]
    public void DiffBoundsLocateTheChangedRegion()
    {
        var dir = TempDir();

        try
        {
            var source = Path.Combine(VisualTestContext.ExamplesDir, "bb8.nap");
            var baseline = Path.Combine(dir, "baseline.apng");

            VisualTestContext.RenderApngToFile(source, baseline);

            // Rebuild the same animation with one pixel altered on the first frame.
            var actual = Path.Combine(dir, "actual.apng");
            const int px = 613;
            const int py = 271;

            using (var input = System.IO.File.OpenRead(baseline))
            using (var reader = new NAPLPS.Drawing.ApngReader(input, leaveOpen: true))
            using (var output = System.IO.File.Create(actual))
            using (var writer = new NAPLPS.Drawing.ApngWriter(output, reader.Width, reader.Height, 1))
            {
                var pixels = new byte[reader.Width * reader.Height * 4];
                bool first = true;

                while (reader.TryReadFrame(pixels))
                {
                    if (first)
                    {
                        int i = ((py * reader.Width) + px) * 4;
                        pixels[i] = (byte)(pixels[i] ^ 0xFF);
                    }

                    writer.WriteFrame(pixels, new SixLabors.ImageSharp.Rectangle(0, 0, reader.Width, reader.Height), 5, 1000);
                    first = false;
                }
            }

            var comparison = VisualTestContext.CompareApngs(baseline, actual);
            var changed = comparison.FrameDiffs.Where(f => f.DiffPixelCount > 0).ToList();

            Assert.IsNotEmpty(changed);
            Assert.AreEqual(1, changed[0].DiffPixelCount, "exactly one pixel was altered");

            var bounds = changed[0].DiffBounds;

            Assert.IsNotNull(bounds);
            Assert.AreEqual(px, bounds.Value.X);
            Assert.AreEqual(py, bounds.Value.Y);
            Assert.AreEqual(1, bounds.Value.Width);
            Assert.AreEqual(1, bounds.Value.Height);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
