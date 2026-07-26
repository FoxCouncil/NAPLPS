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
    /// How many files to render at once. This cap is about MEMORY, not CPU: a single render holds
    /// the whole animation in memory as an APNG, and the corpus contains multi-hundred-frame files
    /// — a 370-frame 1024x768 render is roughly 1.1 GB live. Unbounded, this has exhausted RAM on an
    /// 8 GB machine and taken the whole box down, so the default stays deliberately conservative.
    /// Raise it with NAPLPS_VR_PARALLELISM on a machine you know has the headroom.
    /// </summary>
    private static int RenderParallelism()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("NAPLPS_VR_PARALLELISM"), out var configured) && configured > 0)
        {
            return configured;
        }

        return Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
    }

    private static void ProcessFile(string relativePath, System.Collections.Concurrent.ConcurrentBag<string> failures)
    {
        var fullPath = Path.Combine(VisualTestContext.ExamplesDir, relativePath);
        var baselinePath = VisualTestContext.GetBaselinePath(relativePath);
        var actualPath = VisualTestContext.GetActualPath(relativePath);

        Image<Rgba32>? apng = null;

        try
        {
            apng = VisualTestContext.RenderApng(fullPath, VisualTestContext.GetForcedSystemType(relativePath));
        }
        catch (Exception ex)
        {
            VisualTestContext.Results[relativePath] = new VisualTestResult(relativePath, VisualTestStatus.Error, baselinePath, null, null, 0, 0, 0, ex.Message);
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(actualPath)!);
            apng.SaveAsPng(actualPath);
            var frameCount = apng.Frames.Count;
            apng.Dispose();

            var diffHtmlPath = VisualTestContext.GetDiffHtmlPath(relativePath);

            if (!System.IO.File.Exists(baselinePath))
            {
                VisualTestContext.GenerateViewHtml(relativePath, actualPath, frameCount, diffHtmlPath);
                VisualTestContext.Results[relativePath] = new VisualTestResult(relativePath, VisualTestStatus.New, null, actualPath, diffHtmlPath, frameCount, 0, 0, null);
                return;
            }

            var comparison = VisualTestContext.CompareApngs(baselinePath, actualPath);

            if (comparison.AreIdentical)
            {
                VisualTestContext.GenerateDiffHtml(relativePath, comparison, baselinePath, actualPath, diffHtmlPath);
                VisualTestContext.Results[relativePath] = new VisualTestResult(relativePath, VisualTestStatus.Pass, baselinePath, actualPath, diffHtmlPath, frameCount, 0, 0, null);
                return;
            }

            VisualTestContext.GenerateDiffHtml(relativePath, comparison, baselinePath, actualPath, diffHtmlPath);

            foreach (var fd in comparison.FrameDiffs)
            {
                fd.DiffImage?.Dispose();
            }

            VisualTestContext.Results[relativePath] = new VisualTestResult(relativePath, VisualTestStatus.Fail, baselinePath, actualPath, diffHtmlPath, frameCount, comparison.FrameDiffs.Count(f => f.DiffPixelCount > 0), comparison.TotalDiffPixels, null);
            failures.Add(relativePath);
        }
        catch (Exception ex)
        {
            VisualTestContext.Results[relativePath] = new VisualTestResult(relativePath, VisualTestStatus.Error, baselinePath, actualPath, null, 0, 0, 0, ex.Message);
        }
    }
}
