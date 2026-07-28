// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NAPLPSTests.Visual;

[TestClass]
public class VisualRegressionTest
{
    [TestMethod]
    [TestCategory("VR")]
    public void VisualBaselines()
    {
        VisualTestContext.CleanOutputDirs();

        var files = VisualTestContext.DiscoverExampleFiles().ToList();
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();

        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = RenderParallelism() }, relativePath =>
        {
            ProcessFile(relativePath, failures);
        });

        VisualTestContext.GenerateReport(VisualTestContext.Results);

        if (failures.Count > 0)
        {
            Assert.Fail($"{failures.Count} visual regression(s) detected. See report: {VisualTestContext.ReportPath}");
        }

        var newCount = VisualTestContext.Results.Values.Count(r => r.Status == VisualTestStatus.New);

        if (newCount > 0)
        {
            Assert.Inconclusive($"{newCount} new baseline(s) need to be accepted. See report: {VisualTestContext.ReportPath}");
        }
    }

    /// <summary>
    /// How many files to render at once. This used to be a hard MEMORY cap - rendering held the
    /// whole animation in memory, so a single multi-thousand-frame file cost gigabytes and the
    /// suite peaked at ~19.8 GB, which had already taken a machine down once. Rendering, comparing
    /// and reporting now all stream (see ApngWriter/ApngReader), and the measured peak is ~0.34 GB
    /// regardless of how many frames a file has, so this is an ordinary CPU-bound cap again.
    /// Override with NAPLPS_VR_PARALLELISM.
    /// </summary>
    private static int RenderParallelism()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("NAPLPS_VR_PARALLELISM"), out var configured) && configured > 0)
        {
            return configured;
        }

        return Math.Max(1, Environment.ProcessorCount);
    }

    private static void ProcessFile(string relativePath, System.Collections.Concurrent.ConcurrentBag<string> failures)
    {
        var fullPath = Path.Combine(VisualTestContext.ExamplesDir, relativePath);
        var baselinePath = VisualTestContext.GetBaselinePath(relativePath);
        var actualPath = VisualTestContext.GetActualPath(relativePath);

        int frameCount;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(actualPath)!);
            frameCount = VisualTestContext.RenderApngToFile(fullPath, actualPath, VisualTestContext.GetForcedSystemType(relativePath));
        }
        catch (Exception ex)
        {
            VisualTestContext.Results[relativePath] = new VisualTestResult(relativePath, VisualTestStatus.Error, baselinePath, null, null, 0, 0, 0, ex.Message);
            return;
        }

        try
        {
            var diffHtmlPath = VisualTestContext.GetDiffHtmlPath(relativePath);

            if (!System.IO.File.Exists(baselinePath))
            {
                VisualTestContext.GenerateViewHtml(relativePath, actualPath, frameCount, diffHtmlPath);
                VisualTestContext.Results[relativePath] = new VisualTestResult(relativePath, VisualTestStatus.New, null, actualPath, diffHtmlPath, frameCount, 0, 0, null);
                return;
            }

            // Differing frames are written next to the diff page as it compares, so the page can
            // reference them instead of inlining every frame as base64.
            var framesDir = Path.Combine(Path.GetDirectoryName(diffHtmlPath)!, Path.GetFileNameWithoutExtension(diffHtmlPath) + ".frames");
            var comparison = VisualTestContext.CompareApngs(baselinePath, actualPath, framesDir);

            if (comparison.AreIdentical)
            {
                VisualTestContext.GenerateDiffHtml(relativePath, comparison, baselinePath, actualPath, diffHtmlPath);
                VisualTestContext.Results[relativePath] = new VisualTestResult(relativePath, VisualTestStatus.Pass, baselinePath, actualPath, diffHtmlPath, frameCount, 0, 0, null);
                return;
            }

            VisualTestContext.GenerateDiffHtml(relativePath, comparison, baselinePath, actualPath, diffHtmlPath);

            VisualTestContext.Results[relativePath] = new VisualTestResult(relativePath, VisualTestStatus.Fail, baselinePath, actualPath, diffHtmlPath, frameCount, comparison.FrameDiffs.Count(f => f.DiffPixelCount > 0), comparison.TotalDiffPixels, null);
            failures.Add(relativePath);
        }
        catch (Exception ex)
        {
            VisualTestContext.Results[relativePath] = new VisualTestResult(relativePath, VisualTestStatus.Error, baselinePath, actualPath, null, 0, 0, 0, ex.Message);
        }
    }
}
