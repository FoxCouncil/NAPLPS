// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using NAPLPS.Drawing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NAPLPSTests.Visual;

public enum VisualTestStatus
{
    Pass,
    Fail,
    New,
    Error
}

public record VisualTestResult(
    string RelativePath,
    VisualTestStatus Status,
    string? BaselinePath,
    string? ActualPath,
    string? DiffHtmlPath,
    int FrameCount,
    int DiffFrameCount,
    long TotalDiffPixels,
    string? ErrorMessage
);

public record FrameDiffResult(
    int FrameIndex,
    long DiffPixelCount,
    long TotalPixels,
    Image<Rgba32>? DiffImage
);

public record ComparisonResult(
    bool AreIdentical,
    int BaselineFrameCount,
    int ActualFrameCount,
    List<FrameDiffResult> FrameDiffs,
    long TotalDiffPixels
);

public static class VisualTestContext
{
    public const int CanvasWidth = 1024;
    public const int CanvasHeight = 768;

    // `.td` files are Telidraw source, not NAPLPS binary. They round-trip as ASCII text
    // (every byte 0x20-0x7E maps to AsciiCharCommand) so the round-trip test treats them
    // fine, but rendering them as NAPLPS just prints the source text onto the canvas — not
    // meaningful for visual regression.
    private static readonly string[] SkipExtensions = [".jpg", ".png", ".txt", ".exe", ".td"];

    public static readonly ConcurrentDictionary<string, VisualTestResult> Results = new();

    public static string SourceDir { get; } = ResolveSourceDir();

    public static string BaselinesDir => Path.Combine(SourceDir, "Visual", "Baselines");

    public static string OutputDir { get; set; } = Path.Combine(AppContext.BaseDirectory, "VisualRegression");

    public static string ActualsDir => Path.Combine(OutputDir, "Actuals");

    public static string DiffsDir => Path.Combine(OutputDir, "Diffs");

    public static string ReportPath => Path.Combine(OutputDir, "VisualRegressionReport.html");

    public static string ExamplesDir => Path.Combine(AppContext.BaseDirectory, "examples");

    public static IEnumerable<string> DiscoverExampleFiles()
    {
        return Directory.GetFiles(ExamplesDir, "*", SearchOption.AllDirectories)
            .Where(f => !SkipExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Select(f => Path.GetRelativePath(ExamplesDir, f))
            .OrderBy(f => f);
    }

    public static string GetBaselinePath(string relativePath)
    {
        return Path.Combine(BaselinesDir, relativePath + ".apng");
    }

    public static string GetActualPath(string relativePath)
    {
        return Path.Combine(ActualsDir, relativePath + ".apng");
    }

    public static string GetDiffHtmlPath(string relativePath)
    {
        return Path.Combine(DiffsDir, relativePath + ".diff.html");
    }

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(ActualsDir);
        Directory.CreateDirectory(DiffsDir);
    }

    public static void CleanOutputDirs()
    {
        if (Directory.Exists(ActualsDir))
        {
            Directory.Delete(ActualsDir, true);
        }

        if (Directory.Exists(DiffsDir))
        {
            Directory.Delete(DiffsDir, true);
        }

        EnsureDirectories();
    }

    /// <summary>
    /// Corpus directories (top-level under Examples/) whose files are known-Prodigy regardless of
    /// header detection. About a third of the preview-disk corpus lacks the A1 C8 domain marker
    /// (two Ads files even start with 0x0E and would misdetect as Telidon); forcing the system
    /// type at parse routes them through the authentic Prodigy pipeline so its rendering is
    /// baseline-protected too. Forcing is a no-op for files that already carry the marker.
    /// </summary>
    private static readonly HashSet<string> ForcedProdigyDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Ads From Preview Disks",
        "Screens From Preview Disks",
        "Anthony Wetzel",
        "Cyd Gorman 1",
        "Cyd Gorman 2",
    };

    /// <summary>Forced system type for a corpus file, keyed on its top-level directory; null = autodetect.</summary>
    public static NaplpsSystemType? GetForcedSystemType(string relativePath)
    {
        var topDir = relativePath.Replace('\\', '/').Split('/')[0];

        return ForcedProdigyDirs.Contains(topDir) ? NaplpsSystemType.Prodigy : null;
    }

    public static Image<Rgba32> RenderApng(string exampleFilePath, NaplpsSystemType? forcedSystemType = null)
    {
        var naplps = NaplpsFormat.FromFile(exampleFilePath, forcedSystemType);

        using var drawContext = new DrawContext(naplps, new SixLabors.ImageSharp.Size(CanvasWidth, CanvasHeight));

        return drawContext.RenderToApng();
    }

    /// <summary>
    /// Renders straight to <paramref name="outputPath"/> and returns the frame count. The suite runs
    /// several files at once and the longest animations are thousands of full-canvas frames, so
    /// holding a whole APNG in memory is what set the parallelism ceiling. Streaming makes a file's
    /// cost independent of its frame count.
    /// </summary>
    public static int RenderApngToFile(string exampleFilePath, string outputPath, NaplpsSystemType? forcedSystemType = null)
    {
        var naplps = NaplpsFormat.FromFile(exampleFilePath, forcedSystemType);

        using var drawContext = new DrawContext(naplps, new SixLabors.ImageSharp.Size(CanvasWidth, CanvasHeight));

        return drawContext.RenderApngToFile(outputPath);
    }

    /// <summary>
    /// Compares two APNGs frame by frame without ever holding more than one canvas from each.
    /// <see cref="Image.Load{TPixel}(string)"/> materialises every frame at full canvas size, which
    /// for the longest corpus files is gigabytes per side and was what set the suite's parallelism
    /// ceiling; <see cref="ApngReader"/> composites on the fly instead.
    /// </summary>
    public static ComparisonResult CompareApngs(string baselinePath, string actualPath)
    {
        using var baselineStream = System.IO.File.OpenRead(baselinePath);
        using var actualStream = System.IO.File.OpenRead(actualPath);
        using var baselineReader = new ApngReader(baselineStream, leaveOpen: true);
        using var actualReader = new ApngReader(actualStream, leaveOpen: true);

        var baselinePixels = new byte[CanvasWidth * CanvasHeight * 4];
        var actualPixels = new byte[CanvasWidth * CanvasHeight * 4];

        var frameDiffs = new List<FrameDiffResult>();
        long totalDiffPixels = 0;
        bool allIdentical = true;
        int baselineCount = 0;
        int actualCount = 0;
        int index = 0;

        while (true)
        {
            bool haveBaseline = baselineReader.TryReadFrame(baselinePixels);
            bool haveActual = actualReader.TryReadFrame(actualPixels);

            if (!haveBaseline && !haveActual)
            {
                break;
            }

            if (haveBaseline)
            {
                baselineCount++;
            }

            if (haveActual)
            {
                actualCount++;
            }

            // One side ran out: count the whole canvas as different and keep draining the other so
            // the reported frame counts stay accurate.
            if (!haveBaseline || !haveActual)
            {
                long missingFramePixels = (long)CanvasWidth * CanvasHeight;
                totalDiffPixels += missingFramePixels;
                frameDiffs.Add(new FrameDiffResult(index, missingFramePixels, missingFramePixels, null));
                allIdentical = false;
                index++;
                continue;
            }

            var diff = CompareFrames(index, baselinePixels, actualPixels);
            frameDiffs.Add(diff);
            totalDiffPixels += diff.DiffPixelCount;

            if (diff.DiffPixelCount > 0)
            {
                allIdentical = false;
            }

            index++;
        }

        return new ComparisonResult(allIdentical, baselineCount, actualCount, frameDiffs, totalDiffPixels);
    }

    private static FrameDiffResult CompareFrames(int index, byte[] baseline, byte[] actual)
    {
        long totalPixels = (long)CanvasWidth * CanvasHeight;

        // Most frames match, and building a diff image for those was pure waste - the old code
        // allocated one per frame and dropped it on the floor unreferenced when nothing differed.
        if (baseline.AsSpan().SequenceEqual(actual))
        {
            return new FrameDiffResult(index, 0, totalPixels, null);
        }

        long diffCount = 0;
        var diffImage = new Image<Rgba32>(CanvasWidth, CanvasHeight);

        diffImage.Frames.RootFrame.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < CanvasHeight; y++)
            {
                var row = accessor.GetRowSpan(y);
                int offset = y * CanvasWidth * 4;

                for (int x = 0; x < CanvasWidth; x++)
                {
                    int i = offset + (x * 4);

                    if (!baseline.AsSpan(i, 4).SequenceEqual(actual.AsSpan(i, 4)))
                    {
                        diffCount++;
                        row[x] = new Rgba32(255, 0, 255, 255);
                    }
                    else
                    {
                        row[x] = new Rgba32((byte)(baseline[i] / 4), (byte)(baseline[i + 1] / 4), (byte)(baseline[i + 2] / 4), 255);
                    }
                }
            }
        });

        return new FrameDiffResult(index, diffCount, totalPixels, diffImage);
    }

    public static void GenerateDiffHtml(string relativePath, ComparisonResult comparison, string baselinePath, string actualPath, string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        // A file that matched has nothing to step through, and building the frame-by-frame viewer
        // for it was most of the suite's cost: two full Image.Load calls plus a base64 PNG per
        // frame, for every file in the corpus. That produced 3.4 GB of Diffs/ - a single 267 MB
        // HTML page for a test that PASSED, which no browser will open anyway. Browsers animate
        // APNG natively, so a passing file just needs the two files side by side.
        if (comparison.AreIdentical)
        {
            GeneratePassHtml(relativePath, comparison, baselinePath, actualPath, outputPath);

            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html><head><meta charset='utf-8'>");
        sb.AppendLine($"<title>Diff: {HtmlEncode(relativePath)}</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(DiffPageCss());
        sb.AppendLine("</style></head><body>");
        var reportRelative = Path.GetRelativePath(Path.GetDirectoryName(outputPath)!, ReportPath).Replace('\\', '/');
        sb.AppendLine($"<div class='breadcrumb'><a href='{HtmlEncode(reportRelative)}'>&larr; Back to Report</a></div>");
        sb.AppendLine($"<h1>Visual Diff: {HtmlEncode(relativePath)}</h1>");
        sb.AppendLine($"<div class='summary'>Baseline frames: {comparison.BaselineFrameCount} | Actual frames: {comparison.ActualFrameCount} | Diff pixels: {comparison.TotalDiffPixels:N0}</div>");
        sb.AppendLine($"<div class='timestamp'>Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}</div>");

        sb.AppendLine("<div class='tabs'>");
        sb.AppendLine("<button class='tab active' onclick='setView(\"sidebyside\")'>Side by Side</button>");
        sb.AppendLine("<button class='tab' onclick='setView(\"baseline\")'>Baseline</button>");
        sb.AppendLine("<button class='tab' onclick='setView(\"actual\")'>Actual</button>");
        sb.AppendLine("<button class='tab' onclick='setView(\"diff\")'>Diff</button>");
        sb.AppendLine("<button class='tab' onclick='setView(\"overlay\")'>Overlay</button>");
        sb.AppendLine("<button class='tab' onclick='setView(\"toggle\")'>Toggle</button>");
        sb.AppendLine("</div>");

        var baselineFrames = ExtractFramesAsBase64(baselinePath);
        var actualFrames = ExtractFramesAsBase64(actualPath);
        var diffFrames = new List<string>();

        foreach (var fd in comparison.FrameDiffs)
        {
            if (fd.DiffImage != null)
            {
                diffFrames.Add(ImageToBase64(fd.DiffImage));
                fd.DiffImage.Dispose();
            }
            else
            {
                diffFrames.Add("");
            }
        }

        sb.AppendLine("<script>");
        sb.AppendLine($"const baselineFrames = {JsonArrayFromList(baselineFrames)};");
        sb.AppendLine($"const actualFrames = {JsonArrayFromList(actualFrames)};");
        sb.AppendLine($"const diffFrames = {JsonArrayFromList(diffFrames)};");
        sb.AppendLine($"const frameDiffs = [{string.Join(",", comparison.FrameDiffs.Select(f => f.DiffPixelCount))}];");
        sb.AppendLine($"const frameTotals = [{string.Join(",", comparison.FrameDiffs.Select(f => f.TotalPixels))}];");
        sb.AppendLine($"let currentFrame = 0;");
        sb.AppendLine($"let currentView = 'sidebyside';");
        sb.AppendLine(DiffPageJs());
        sb.AppendLine("</script>");

        sb.AppendLine("<div class='frame-nav'>");
        sb.AppendLine("<button class='nav-btn' onclick='prevFrame()' title='Left Arrow'>&larr; Prev</button>");
        sb.AppendLine("<span id='frameCounter'>Frame 1 / 1</span>");
        sb.AppendLine("<button class='nav-btn' onclick='nextFrame()' title='Right Arrow'>Next &rarr;</button>");
        sb.AppendLine("<span class='nav-sep'>|</span>");
        sb.AppendLine("<button class='nav-btn diff-btn' onclick='prevDiff()' title='PgUp'>&laquo; Prev Change</button>");
        sb.AppendLine("<span id='diffCounter'></span>");
        sb.AppendLine("<button class='nav-btn diff-btn' onclick='nextDiff()' title='PgDown'>Next Change &raquo;</button>");
        sb.AppendLine("<span class='nav-sep'>|</span>");
        sb.AppendLine("<span id='frameStats'></span>");
        sb.AppendLine("</div>");

        sb.AppendLine("<div id='viewer' class='viewer'></div>");
        sb.AppendLine("<script>updateView();</script>");
        sb.AppendLine("</body></html>");

        System.IO.File.WriteAllText(outputPath, sb.ToString());
    }

    public static void GenerateViewHtml(string relativePath, string actualPath, int frameCount, string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var actualFrames = ExtractFramesAsBase64(actualPath);

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html><head><meta charset='utf-8'>");
        sb.AppendLine($"<title>New: {HtmlEncode(relativePath)}</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(DiffPageCss());
        sb.AppendLine("</style></head><body>");
        var reportRelative = Path.GetRelativePath(Path.GetDirectoryName(outputPath)!, ReportPath).Replace('\\', '/');
        sb.AppendLine($"<div class='breadcrumb'><a href='{HtmlEncode(reportRelative)}'>&larr; Back to Report</a></div>");
        sb.AppendLine($"<h1>New: {HtmlEncode(relativePath)}</h1>");
        sb.AppendLine($"<div class='summary'>{frameCount} frame{(frameCount != 1 ? "s" : "")} | No baseline</div>");
        sb.AppendLine($"<div class='timestamp'>Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}</div>");

        sb.AppendLine("<script>");
        sb.AppendLine($"const baselineFrames = [];");
        sb.AppendLine($"const actualFrames = {JsonArrayFromList(actualFrames)};");
        sb.AppendLine($"const diffFrames = [];");
        sb.AppendLine($"const frameDiffs = [{string.Join(",", Enumerable.Repeat("0", frameCount))}];");
        sb.AppendLine($"const frameTotals = [{string.Join(",", Enumerable.Repeat("0", frameCount))}];");
        sb.AppendLine($"let currentFrame = 0;");
        sb.AppendLine($"let currentView = 'actual';");
        sb.AppendLine(DiffPageJs());
        sb.AppendLine("</script>");

        sb.AppendLine("<div class='frame-nav'>");
        sb.AppendLine("<button class='nav-btn' onclick='prevFrame()' title='Left Arrow'>&larr; Prev</button>");
        sb.AppendLine("<span id='frameCounter'>Frame 1 / 1</span>");
        sb.AppendLine("<button class='nav-btn' onclick='nextFrame()' title='Right Arrow'>Next &rarr;</button>");
        sb.AppendLine("<span class='nav-sep'>|</span>");
        sb.AppendLine("<span id='frameStats'></span>");
        sb.AppendLine("</div>");

        sb.AppendLine("<div id='viewer' class='viewer'></div>");
        sb.AppendLine("<script>updateView();</script>");
        sb.AppendLine("</body></html>");

        System.IO.File.WriteAllText(outputPath, sb.ToString());
    }

    public static void GenerateReport(ConcurrentDictionary<string, VisualTestResult> results)
    {
        Directory.CreateDirectory(OutputDir);

        var sorted = results.Values.OrderBy(r => r.Status).ThenBy(r => r.RelativePath).ToList();
        var passed = sorted.Count(r => r.Status == VisualTestStatus.Pass);
        var failed = sorted.Count(r => r.Status == VisualTestStatus.Fail);
        var newCount = sorted.Count(r => r.Status == VisualTestStatus.New);
        var errors = sorted.Count(r => r.Status == VisualTestStatus.Error);

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html><head><meta charset='utf-8'>");
        sb.AppendLine("<title>Visual Regression Report</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(ReportCss());
        sb.AppendLine("</style></head><body>");

        sb.AppendLine("<h1>Visual Regression Report</h1>");
        sb.AppendLine($"<div class='timestamp'>Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}</div>");

        sb.AppendLine("<div class='stats'>");
        sb.AppendLine($"<span class='stat pass'>{passed} Passed</span>");
        sb.AppendLine($"<span class='stat fail'>{failed} Failed</span>");
        sb.AppendLine($"<span class='stat new'>{newCount} New</span>");
        if (errors > 0)
        {
            sb.AppendLine($"<span class='stat error'>{errors} Errors</span>");
        }
        sb.AppendLine($"<span class='stat total'>{sorted.Count} Total</span>");
        sb.AppendLine("</div>");

        sb.AppendLine("<div class='filters'>");
        sb.AppendLine("<button class='filter' onclick='filterBy(\"all\")'>All</button>");
        sb.AppendLine("<button class='filter active' onclick='filterBy(\"fail\")'>Failed</button>");
        sb.AppendLine("<button class='filter' onclick='filterBy(\"pass\")'>Passed</button>");
        sb.AppendLine("<button class='filter' onclick='filterBy(\"new\")'>New</button>");
        sb.AppendLine("<button class='filter' onclick='filterBy(\"reviewed\")'>Reviewed</button>");
        sb.AppendLine("<button class='filter' onclick='filterBy(\"unreviewed\")'>Unreviewed</button>");
        if (errors > 0)
        {
            sb.AppendLine("<button class='filter' onclick='filterBy(\"error\")'>Errors</button>");
        }
        sb.AppendLine("</div>");

        sb.AppendLine("<table id='results'>");
        sb.AppendLine("<thead><tr><th>Status</th><th>File</th><th>Frames</th><th>Diff Pixels</th><th>Details</th><th>Reviewed</th></tr></thead>");
        sb.AppendLine("<tbody>");

        foreach (var result in sorted)
        {
            var statusClass = result.Status.ToString().ToLowerInvariant();
            var statusIcon = result.Status switch
            {
                VisualTestStatus.Pass => "&#10004;",
                VisualTestStatus.Fail => "&#10008;",
                VisualTestStatus.New => "&#9733;",
                VisualTestStatus.Error => "&#9888;",
                _ => "?"
            };

            var fileKey = HtmlEncode(result.RelativePath).Replace("\\", "/");
            sb.AppendLine($"<tr class='row {statusClass}' data-status='{statusClass}' data-file='{fileKey}'>");
            sb.AppendLine($"<td class='status {statusClass}'>{statusIcon}</td>");
            sb.AppendLine($"<td>{HtmlEncode(result.RelativePath)}</td>");
            sb.AppendLine($"<td>{result.FrameCount}</td>");
            sb.AppendLine($"<td>{(result.TotalDiffPixels > 0 ? result.TotalDiffPixels.ToString("N0") : "")}</td>");

            if (result.DiffHtmlPath != null)
            {
                var diffRelative = Path.GetRelativePath(OutputDir, result.DiffHtmlPath);
                var linkText = result.Status == VisualTestStatus.Fail ? "View Diff" : "View";
                sb.AppendLine($"<td><a href='{HtmlEncode(diffRelative.Replace('\\', '/'))}'>{linkText}</a></td>");
            }
            else if (result.Status == VisualTestStatus.Error)
            {
                sb.AppendLine($"<td>{HtmlEncode(result.ErrorMessage ?? "")}</td>");
            }
            else
            {
                sb.AppendLine("<td></td>");
            }

            sb.AppendLine($"<td class='review-cell' data-file='{fileKey}'></td>");
            sb.AppendLine("</tr>");
        }

        sb.AppendLine("</tbody></table>");

        if (failed > 0 || newCount > 0)
        {
            sb.AppendLine("<div class='accept-section'>");
            sb.AppendLine("<h2>Accept Baselines</h2>");

            if (newCount > 0)
            {
                sb.AppendLine("<h3>Accept All New</h3>");
                sb.AppendLine("<pre class='command'>powershell -File accept-baselines.ps1 -NewOnly</pre>");
            }

            if (failed > 0)
            {
                sb.AppendLine("<h3>Accept All Changed</h3>");
                sb.AppendLine("<pre class='command'>powershell -File accept-baselines.ps1 -All</pre>");
            }

            sb.AppendLine("<h3>Git Commands</h3>");
            sb.AppendLine($"<pre class='command'>git add NAPLPSTests/Visual/Baselines/\ngit commit -m \"Update visual regression baselines\"</pre>");
            sb.AppendLine("</div>");
        }

        sb.AppendLine("<script>");
        sb.AppendLine($"const RUN_ID = '{DateTime.Now:yyyyMMddHHmmss}';");
        sb.AppendLine(ReportJs());
        sb.AppendLine("</script>");
        sb.AppendLine("</body></html>");

        System.IO.File.WriteAllText(ReportPath, sb.ToString());
    }

    private static string ResolveSourceDir([CallerFilePath] string? callerPath = null)
    {
        return Path.GetDirectoryName(Path.GetDirectoryName(callerPath!))!;
    }

    /// <summary>
    /// Lightweight page for a file that matched its baseline: the two APNGs referenced by path and
    /// left to the browser to animate. No frame extraction, no base64.
    /// </summary>
    private static void GeneratePassHtml(string relativePath, ComparisonResult comparison, string baselinePath, string actualPath, string outputPath)
    {
        var dir = Path.GetDirectoryName(outputPath)!;
        var reportRelative = Path.GetRelativePath(dir, ReportPath).Replace('\\', '/');
        var baselineRelative = Path.GetRelativePath(dir, baselinePath).Replace('\\', '/');
        var actualRelative = Path.GetRelativePath(dir, actualPath).Replace('\\', '/');

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html><head><meta charset='utf-8'>");
        sb.AppendLine($"<title>Match: {HtmlEncode(relativePath)}</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(DiffPageCss());
        sb.AppendLine("</style></head><body>");
        sb.AppendLine($"<div class='breadcrumb'><a href='{HtmlEncode(reportRelative)}'>&larr; Back to Report</a></div>");
        sb.AppendLine($"<h1>Visual Match: {HtmlEncode(relativePath)}</h1>");
        sb.AppendLine($"<div class='summary'>Identical &mdash; {comparison.BaselineFrameCount} frames, no differing pixels</div>");
        sb.AppendLine($"<div class='timestamp'>Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}</div>");
        sb.AppendLine("<div class='viewer'>");
        sb.AppendLine($"<div><h3>Baseline</h3><img src='{HtmlEncode(baselineRelative)}' alt='baseline'></div>");
        sb.AppendLine($"<div><h3>Actual</h3><img src='{HtmlEncode(actualRelative)}' alt='actual'></div>");
        sb.AppendLine("</div>");
        sb.AppendLine("</body></html>");

        System.IO.File.WriteAllText(outputPath, sb.ToString());
    }

    /// <summary>
    /// Streams frames out of an APNG as base64 PNGs. Uses <see cref="ApngReader"/> rather than
    /// <see cref="Image.Load{TPixel}(string)"/> so only one canvas is live at a time; loading the
    /// whole animation costs gigabytes on the longer corpus files.
    /// </summary>
    private static List<string> ExtractFramesAsBase64(string apngPath)
    {
        var frames = new List<string>();

        using var stream = System.IO.File.OpenRead(apngPath);
        using var reader = new ApngReader(stream, leaveOpen: true);

        var pixels = new byte[reader.Width * reader.Height * 4];

        while (reader.TryReadFrame(pixels))
        {
            using var singleFrame = Image.LoadPixelData<Rgba32>(pixels, reader.Width, reader.Height);

            frames.Add(ImageToBase64(singleFrame));
        }

        return frames;
    }

    private static string ImageToBase64(Image<Rgba32> image)
    {
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return Convert.ToBase64String(ms.ToArray());
    }

    private static string HtmlEncode(string text)
    {
        return System.Net.WebUtility.HtmlEncode(text);
    }

    private static string JsonArrayFromList(List<string> items)
    {
        var sb = new StringBuilder("[");

        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append('"');
            sb.Append(items[i]);
            sb.Append('"');
        }

        sb.Append(']');
        return sb.ToString();
    }

    private static string ReportCss() => """
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif; padding: 24px; background: #0d1117; color: #c9d1d9; }
        h1 { margin-bottom: 8px; color: #f0f6fc; }
        .timestamp { color: #8b949e; margin-bottom: 16px; }
        .stats { display: flex; gap: 12px; margin-bottom: 16px; flex-wrap: wrap; }
        .stat { padding: 6px 14px; border-radius: 6px; font-weight: 600; font-size: 14px; }
        .stat.pass { background: #0d2818; color: #3fb950; }
        .stat.fail { background: #3d1417; color: #f85149; }
        .stat.new { background: #2e2a00; color: #d29922; }
        .stat.error { background: #3d1417; color: #f85149; }
        .stat.total { background: #161b22; color: #8b949e; }
        .filters { margin-bottom: 16px; display: flex; gap: 8px; }
        .filter { padding: 6px 12px; border: 1px solid #30363d; background: #161b22; color: #c9d1d9; border-radius: 6px; cursor: pointer; }
        .filter.active { background: #1f6feb; border-color: #1f6feb; color: #fff; }
        table { width: 100%; border-collapse: collapse; background: #161b22; border-radius: 8px; overflow: hidden; }
        th { text-align: left; padding: 10px 14px; background: #21262d; color: #8b949e; font-size: 13px; text-transform: uppercase; }
        td { padding: 8px 14px; border-top: 1px solid #21262d; font-size: 14px; }
        .status { font-size: 16px; width: 30px; text-align: center; }
        .status.pass { color: #3fb950; }
        .status.fail { color: #f85149; }
        .status.new { color: #d29922; }
        .status.error { color: #f85149; }
        tr:hover { background: #1c2128; }
        a { color: #58a6ff; text-decoration: none; }
        a:hover { text-decoration: underline; }
        .accept-section { margin-top: 32px; padding: 20px; background: #161b22; border-radius: 8px; border: 1px solid #30363d; }
        .accept-section h2 { color: #f0f6fc; margin-bottom: 12px; }
        .accept-section h3 { color: #c9d1d9; margin: 12px 0 6px; font-size: 14px; }
        .command { background: #0d1117; padding: 10px 14px; border-radius: 6px; font-family: 'Cascadia Code', 'Fira Code', monospace; font-size: 13px; color: #79c0ff; overflow-x: auto; white-space: pre; cursor: pointer; border: 1px solid #30363d; }
        .command:hover { border-color: #58a6ff; }
        .row.hidden { display: none; }
        .review-btn { padding: 2px 8px; border: 1px solid #30363d; background: #161b22; color: #8b949e; border-radius: 4px; cursor: pointer; font-size: 12px; }
        .review-btn:hover { border-color: #58a6ff; }
        .review-btn.reviewed { background: #0d2818; color: #3fb950; border-color: #238636; }
        .review-time { color: #8b949e; font-size: 11px; display: block; margin-top: 2px; }
        """;

    private static string ReportJs() => """
        const STORAGE_KEY = 'vr_reviewed_' + RUN_ID;

        // Clean up reviewed state from previous runs
        for (let i = localStorage.length - 1; i >= 0; i--) {
            const key = localStorage.key(i);
            if (key && key.startsWith('vr_reviewed_') && key !== STORAGE_KEY) {
                localStorage.removeItem(key);
            }
        }

        function getReviewed() {
            try { return JSON.parse(localStorage.getItem(STORAGE_KEY) || '{}'); } catch { return {}; }
        }

        function setReviewed(file, timestamp) {
            const data = getReviewed();
            data[file] = timestamp;
            localStorage.setItem(STORAGE_KEY, JSON.stringify(data));
        }

        function removeReviewed(file) {
            const data = getReviewed();
            delete data[file];
            localStorage.setItem(STORAGE_KEY, JSON.stringify(data));
        }

        function toggleReview(file) {
            const reviewed = getReviewed();
            if (reviewed[file]) {
                removeReviewed(file);
            } else {
                setReviewed(file, new Date().toISOString());
            }
            renderReviewButtons();
        }

        function renderReviewButtons() {
            const reviewed = getReviewed();
            document.querySelectorAll('.review-cell').forEach(cell => {
                const file = cell.dataset.file;
                const ts = reviewed[file];
                if (ts) {
                    const date = new Date(ts);
                    const timeStr = date.toLocaleString(undefined, { month:'short', day:'numeric', hour:'2-digit', minute:'2-digit' });
                    cell.innerHTML = `<button class='review-btn reviewed' onclick='toggleReview("${file}")'>&#10004; Reviewed</button><span class='review-time'>${timeStr}</span>`;
                    cell.closest('tr').dataset.reviewed = 'true';
                } else {
                    cell.innerHTML = `<button class='review-btn' onclick='toggleReview("${file}")'>Mark Reviewed</button>`;
                    cell.closest('tr').dataset.reviewed = 'false';
                }
            });
        }

        function filterBy(status) {
            document.querySelectorAll('.filter').forEach(b => b.classList.remove('active'));
            event.target.classList.add('active');
            document.querySelectorAll('.row').forEach(row => {
                let show = false;
                if (status === 'all') show = true;
                else if (status === 'reviewed') show = row.dataset.reviewed === 'true';
                else if (status === 'unreviewed') show = row.dataset.reviewed !== 'true' && row.dataset.status === 'fail';
                else show = row.dataset.status === status;

                row.classList.toggle('hidden', !show);
            });
        }

        // Init review buttons on load
        renderReviewButtons();

        // Default to showing failed
        filterBy('fail');

        document.querySelectorAll('.command').forEach(el => {
            el.title = 'Click to copy';
            el.addEventListener('click', () => {
                navigator.clipboard.writeText(el.textContent);
                const orig = el.style.borderColor;
                el.style.borderColor = '#3fb950';
                setTimeout(() => el.style.borderColor = orig, 1000);
            });
        });
        """;

    private static string DiffPageCss() => """
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif; padding: 24px; background: #0d1117; color: #c9d1d9; }
        h1 { margin-bottom: 8px; color: #f0f6fc; font-size: 20px; }
        .summary { color: #8b949e; margin-bottom: 16px; font-size: 14px; }
        .tabs { display: flex; gap: 8px; margin-bottom: 16px; }
        .tab { padding: 6px 12px; border: 1px solid #30363d; background: #161b22; color: #c9d1d9; border-radius: 6px; cursor: pointer; }
        .tab.active { background: #1f6feb; border-color: #1f6feb; color: #fff; }
        .breadcrumb { margin-bottom: 12px; }
        .breadcrumb a { color: #58a6ff; text-decoration: none; font-size: 14px; }
        .breadcrumb a:hover { text-decoration: underline; }
        .timestamp { color: #8b949e; font-size: 13px; margin-bottom: 16px; }
        .frame-nav { display: flex; align-items: center; gap: 8px; margin-bottom: 16px; flex-wrap: wrap; }
        .nav-btn { padding: 6px 12px; border: 1px solid #30363d; background: #161b22; color: #c9d1d9; border-radius: 6px; cursor: pointer; font-size: 13px; }
        .nav-btn:hover { border-color: #58a6ff; }
        .nav-btn:disabled { opacity: 0.4; cursor: default; border-color: #30363d; }
        .diff-btn { background: #1c1e2a; border-color: #444c8c; color: #a5b4fc; }
        .diff-btn:hover:not(:disabled) { border-color: #818cf8; }
        .nav-sep { color: #30363d; font-size: 14px; user-select: none; }
        #frameCounter { color: #8b949e; font-size: 14px; min-width: 120px; text-align: center; }
        #diffCounter { color: #d29922; font-size: 14px; min-width: 120px; text-align: center; }
        #frameStats { color: #f85149; font-size: 14px; }
        .kbd { display: inline-block; padding: 1px 5px; font-size: 11px; color: #8b949e; background: #161b22; border: 1px solid #30363d; border-radius: 3px; font-family: 'Cascadia Code', monospace; margin-left: 4px; }
        .viewer { display: flex; flex-direction: column; gap: 16px; }
        .viewer img { max-width: 100%; border: 1px solid #30363d; border-radius: 4px; image-rendering: pixelated; }
        .viewer .row-pair { display: flex; gap: 16px; }
        .viewer .row-pair .panel { flex: 1; min-width: 300px; }
        .viewer .row-diff { }
        .viewer .row-diff .panel { max-width: 50%; }
        .viewer .panel { min-width: 300px; }
        .viewer .panel.solo { max-width: 100%; }
        .viewer .panel h3 { color: #8b949e; font-size: 13px; text-transform: uppercase; margin-bottom: 6px; }
        """;

    private static string DiffPageJs() => """
        // Build index of frames where the diff PATTERN changes — not just every differing frame.
        // A "change point" is where the diff pixel count transitions (0→N, N→0, or N→M different).
        const changeIndices = [];
        let prevDiffCount = -1;
        for (let i = 0; i < frameDiffs.length; i++) {
            if (frameDiffs[i] !== prevDiffCount) {
                changeIndices.push(i);
                prevDiffCount = frameDiffs[i];
            }
        }

        function maxFrames() { return Math.max(baselineFrames.length, actualFrames.length, diffFrames.length); }
        function prevFrame() { if (currentFrame > 0) { currentFrame--; updateView(); } }
        function nextFrame() { if (currentFrame < maxFrames() - 1) { currentFrame++; updateView(); } }

        function prevDiff() {
            for (let i = changeIndices.length - 1; i >= 0; i--) {
                if (changeIndices[i] < currentFrame) {
                    currentFrame = changeIndices[i];
                    updateView();
                    return;
                }
            }
        }

        function nextDiff() {
            for (let i = 0; i < changeIndices.length; i++) {
                if (changeIndices[i] > currentFrame) {
                    currentFrame = changeIndices[i];
                    updateView();
                    return;
                }
            }
        }

        function setView(v) {
            currentView = v;
            document.querySelectorAll('.tab').forEach(t => t.classList.remove('active'));
            event.target.classList.add('active');
            updateView();
        }

        function updateView() {
            const fc = document.getElementById('frameCounter');
            fc.textContent = `Frame ${currentFrame + 1} / ${maxFrames()}`;

            const dc = document.getElementById('diffCounter');
            const changeIdx = changeIndices.indexOf(currentFrame);
            if (changeIdx >= 0) {
                dc.textContent = `Change ${changeIdx + 1} / ${changeIndices.length}`;
                dc.style.color = '#f85149';
            } else {
                dc.textContent = `${changeIndices.length} change${changeIndices.length !== 1 ? 's' : ''}`;
                dc.style.color = '#d29922';
            }

            const fs = document.getElementById('frameStats');
            if (frameDiffs[currentFrame] > 0) {
                const pct = ((frameDiffs[currentFrame] / frameTotals[currentFrame]) * 100).toFixed(2);
                fs.textContent = `${frameDiffs[currentFrame].toLocaleString()} pixels differ (${pct}%)`;
                fs.style.color = '#f85149';
            } else {
                fs.textContent = 'Identical';
                fs.style.color = '#3fb950';
            }

            // Update button disabled states
            const btns = document.querySelectorAll('.nav-btn');
            btns[0].disabled = currentFrame <= 0;
            btns[1].disabled = currentFrame >= maxFrames() - 1;
            btns[2].disabled = !changeIndices.some(i => i < currentFrame);
            btns[3].disabled = !changeIndices.some(i => i > currentFrame);

            const viewer = document.getElementById('viewer');
            const bSrc = currentFrame < baselineFrames.length ? `data:image/png;base64,${baselineFrames[currentFrame]}` : '';
            const aSrc = currentFrame < actualFrames.length ? `data:image/png;base64,${actualFrames[currentFrame]}` : '';
            const dSrc = currentFrame < diffFrames.length && diffFrames[currentFrame] ? `data:image/png;base64,${diffFrames[currentFrame]}` : '';

            if (currentView === 'sidebyside') {
                viewer.innerHTML = `
                    <div class='row-pair'>
                        <div class='panel'><h3>Baseline</h3><img src='${bSrc}'></div>
                        <div class='panel'><h3>Actual</h3><img src='${aSrc}'></div>
                    </div>
                    ${dSrc ? `<div class='row-diff'><div class='panel'><h3>Diff</h3><img src='${dSrc}'></div></div>` : ''}`;
            } else if (currentView === 'baseline') {
                viewer.innerHTML = `<div class='panel solo'><h3>Baseline</h3><img src='${bSrc}'></div>`;
            } else if (currentView === 'actual') {
                viewer.innerHTML = `<div class='panel solo'><h3>Actual</h3><img src='${aSrc}'></div>`;
            } else if (currentView === 'diff') {
                viewer.innerHTML = dSrc
                    ? `<div class='panel solo'><h3>Diff</h3><img src='${dSrc}'></div>`
                    : `<div class='panel solo'><h3>No differences</h3></div>`;
            } else if (currentView === 'overlay') {
                viewer.innerHTML = dSrc
                    ? `<div class='panel'><h3>Diff Overlay</h3><img src='${dSrc}'></div>`
                    : `<div class='panel'><h3>No differences</h3></div>`;
            } else {
                // Toggle mode: preserve toggle state across frame changes
                const toggleImg = document.getElementById('toggleImg');
                if (toggleImg) {
                    // Just update the sources without rebuilding the DOM
                    window._toggleBaseline = bSrc;
                    window._toggleActual = aSrc;
                    toggleImg.src = window._toggleState ? aSrc : bSrc;
                } else {
                    // First render of toggle mode
                    viewer.innerHTML = `<div class='panel'><h3 id='toggleLabel'>Baseline (click to toggle)</h3><img id='toggleImg' src='${bSrc}' style='cursor:pointer' onclick='toggleImage()'></div>`;
                    window._toggleState = false;
                    window._toggleBaseline = bSrc;
                    window._toggleActual = aSrc;
                }
            }
        }

        function toggleImage() {
            window._toggleState = !window._toggleState;
            document.getElementById('toggleImg').src = window._toggleState ? window._toggleActual : window._toggleBaseline;
            document.getElementById('toggleLabel').textContent = window._toggleState ? 'Actual (click to toggle)' : 'Baseline (click to toggle)';
        }

        // Keyboard navigation
        document.addEventListener('keydown', function(e) {
            switch (e.key) {
                case 'ArrowLeft':
                    e.preventDefault();
                    prevFrame();
                    break;
                case 'ArrowRight':
                    e.preventDefault();
                    nextFrame();
                    break;
                case 'PageUp':
                    e.preventDefault();
                    prevDiff();
                    break;
                case 'PageDown':
                    e.preventDefault();
                    nextDiff();
                    break;
                case 'Home':
                    e.preventDefault();
                    currentFrame = 0;
                    updateView();
                    break;
                case 'End':
                    e.preventDefault();
                    currentFrame = maxFrames() - 1;
                    updateView();
                    break;
            }
        });

        // Jump to first change point on load if frame 0 is identical
        if (changeIndices.length > 0 && frameDiffs[0] === 0) {
            currentFrame = changeIndices.find(i => frameDiffs[i] > 0) ?? changeIndices[0];
        }
        """;
}
