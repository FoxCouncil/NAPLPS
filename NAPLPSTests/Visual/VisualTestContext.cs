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

/// <summary>
/// A single frame's comparison outcome. Deliberately holds no image: differing frames are written
/// straight to disk as they are compared, because keeping them meant a live <see cref="Image{TPixel}"/>
/// per differing frame and a corpus-wide regression would hold thousands at once.
/// <paramref name="DiffBounds"/> is the bounding box of the changed pixels, which is what tells you
/// at a glance whether a regression is "one glyph moved" or "the whole canvas".
/// </summary>
public record FrameDiffResult(
    int FrameIndex,
    long DiffPixelCount,
    long TotalPixels,
    SixLabors.ImageSharp.Rectangle? DiffBounds,
    bool Exported
);

public record ComparisonResult(
    bool AreIdentical,
    int BaselineFrameCount,
    int ActualFrameCount,
    List<FrameDiffResult> FrameDiffs,
    long TotalDiffPixels,
    int ExportedFrames = 0,
    int SuppressedFrames = 0,
    int UnpairedFrames = 0
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
    /// Upper bound on how many differing frames get written out for the viewer. A corpus-wide
    /// regression can make thousands of frames differ across hundreds of files; past this point the
    /// extra PNGs tell you nothing new. Anything skipped is reported, never silently dropped.
    /// </summary>
    public const int MaxExportedFramesPerFile = 400;

    /// <summary>
    /// Compares two APNGs frame by frame without ever holding more than one canvas from each.
    /// <see cref="Image.Load{TPixel}(string)"/> materialises every frame at full canvas size, which
    /// for the longest corpus files is gigabytes per side and was what set the suite's parallelism
    /// ceiling; <see cref="ApngReader"/> composites on the fly instead.
    /// </summary>
    /// <param name="frameExportDir">
    /// When set, each differing frame is written here as a baseline/actual/diff PNG triplet for the
    /// viewer to load on demand.
    /// </param>
    public static ComparisonResult CompareApngs(string baselinePath, string actualPath, string? frameExportDir = null)
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
        int exported = 0;
        int suppressed = 0;
        int unpaired = 0;

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
            // the reported frame counts stay accurate. These frames have no counterpart to sit
            // beside, so they are not exported - counted separately so the page can say so rather
            // than leave them unaccounted for.
            if (!haveBaseline || !haveActual)
            {
                long missingFramePixels = (long)CanvasWidth * CanvasHeight;
                totalDiffPixels += missingFramePixels;
                frameDiffs.Add(new FrameDiffResult(index, missingFramePixels, missingFramePixels, null, false));
                allIdentical = false;
                unpaired++;
                index++;
                continue;
            }

            var diff = CompareFrames(index, baselinePixels, actualPixels);
            totalDiffPixels += diff.DiffPixelCount;

            if (diff.DiffPixelCount > 0)
            {
                allIdentical = false;

                if (frameExportDir is not null)
                {
                    if (exported < MaxExportedFramesPerFile)
                    {
                        ExportFrameTriplet(frameExportDir, index, baselinePixels, actualPixels, diff.DiffBounds);
                        diff = diff with { Exported = true };
                        exported++;
                    }
                    else
                    {
                        suppressed++;
                    }
                }
            }

            frameDiffs.Add(diff);
            index++;
        }

        return new ComparisonResult(allIdentical, baselineCount, actualCount, frameDiffs, totalDiffPixels, exported, suppressed, unpaired);
    }

    /// <summary>
    /// Writes the baseline, actual and diff images for one differing frame as PNGs the viewer can
    /// load on demand. Inlining these as base64 instead is what produced 267 MB HTML pages.
    /// </summary>
    private static void ExportFrameTriplet(string dir, int index, byte[] baseline, byte[] actual, SixLabors.ImageSharp.Rectangle? bounds)
    {
        Directory.CreateDirectory(dir);

        using (var image = Image.LoadPixelData<Rgba32>(baseline, CanvasWidth, CanvasHeight))
        {
            image.SaveAsPng(Path.Combine(dir, $"b{index:D6}.png"));
        }

        using (var image = Image.LoadPixelData<Rgba32>(actual, CanvasWidth, CanvasHeight))
        {
            image.SaveAsPng(Path.Combine(dir, $"a{index:D6}.png"));
        }

        using var diff = BuildDiffImage(baseline, actual, bounds);
        diff.SaveAsPng(Path.Combine(dir, $"d{index:D6}.png"));
    }

    /// <summary>
    /// Differing pixels in magenta over a dimmed copy of the baseline, with the change's bounding
    /// box outlined so a single-pixel difference is still findable on a 1024x768 canvas.
    /// </summary>
    private static Image<Rgba32> BuildDiffImage(byte[] baseline, byte[] actual, SixLabors.ImageSharp.Rectangle? bounds)
    {
        var image = new Image<Rgba32>(CanvasWidth, CanvasHeight);

        image.Frames.RootFrame.ProcessPixelRows(accessor =>
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
                        row[x] = new Rgba32(255, 0, 255, 255);
                    }
                    else
                    {
                        row[x] = new Rgba32((byte)(baseline[i] / 4), (byte)(baseline[i + 1] / 4), (byte)(baseline[i + 2] / 4), 255);
                    }
                }
            }

            if (bounds is not { } box)
            {
                return;
            }

            var outline = new Rgba32(0, 255, 128, 255);

            // Rectangle.Right/Bottom are exclusive, so step back one for the drawn edge.
            int left = box.Left;
            int right = box.Right - 1;
            int top = box.Top;
            int bottom = box.Bottom - 1;

            for (int x = left; x <= right; x++)
            {
                accessor.GetRowSpan(top)[x] = outline;
                accessor.GetRowSpan(bottom)[x] = outline;
            }

            for (int y = top; y <= bottom; y++)
            {
                var row = accessor.GetRowSpan(y);
                row[left] = outline;
                row[right] = outline;
            }
        });

        return image;
    }

    private static FrameDiffResult CompareFrames(int index, byte[] baseline, byte[] actual)
    {
        long totalPixels = (long)CanvasWidth * CanvasHeight;

        // Most frames match, and the whole-buffer compare settles that in one pass. The old code
        // built an Image per frame regardless and dropped it unreferenced when nothing differed.
        if (baseline.AsSpan().SequenceEqual(actual))
        {
            return new FrameDiffResult(index, 0, totalPixels, null, false);
        }

        long diffCount = 0;
        int minX = CanvasWidth;
        int minY = CanvasHeight;
        int maxX = -1;
        int maxY = -1;

        for (int y = 0; y < CanvasHeight; y++)
        {
            int offset = y * CanvasWidth * 4;

            // Skip whole rows that match rather than testing pixel by pixel; on a typical
            // regression the change touches a handful of scanlines.
            if (baseline.AsSpan(offset, CanvasWidth * 4).SequenceEqual(actual.AsSpan(offset, CanvasWidth * 4)))
            {
                continue;
            }

            if (y < minY) { minY = y; }
            if (y > maxY) { maxY = y; }

            for (int x = 0; x < CanvasWidth; x++)
            {
                int i = offset + (x * 4);

                if (baseline.AsSpan(i, 4).SequenceEqual(actual.AsSpan(i, 4)))
                {
                    continue;
                }

                diffCount++;

                if (x < minX) { minX = x; }
                if (x > maxX) { maxX = x; }
            }
        }

        var bounds = maxX < 0 ? (SixLabors.ImageSharp.Rectangle?)null : new SixLabors.ImageSharp.Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);

        return new FrameDiffResult(index, diffCount, totalPixels, bounds, false);
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

        var dir = Path.GetDirectoryName(outputPath)!;
        var framesRelative = Path.GetFileNameWithoutExtension(outputPath) + ".frames";
        var reportRelative = Path.GetRelativePath(dir, ReportPath).Replace('\\', '/');
        var baselineRelative = Path.GetRelativePath(dir, baselinePath).Replace('\\', '/');
        var actualRelative = Path.GetRelativePath(dir, actualPath).Replace('\\', '/');

        var exportedFrames = comparison.FrameDiffs.Where(f => f.Exported).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html><head><meta charset='utf-8'>");
        sb.AppendLine($"<title>Diff: {HtmlEncode(relativePath)}</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(DiffPageCss());
        sb.AppendLine("</style></head><body>");
        sb.AppendLine($"<div class='breadcrumb'><a href='{HtmlEncode(reportRelative)}'>&larr; Back to Report</a></div>");
        sb.AppendLine($"<h1>Visual Diff: {HtmlEncode(relativePath)}</h1>");

        var changedFrames = comparison.FrameDiffs.Count(f => f.DiffPixelCount > 0);
        sb.AppendLine($"<div class='summary'>Baseline {comparison.BaselineFrameCount} frames | Actual {comparison.ActualFrameCount} frames | {changedFrames} changed frame{(changedFrames == 1 ? "" : "s")} | {comparison.TotalDiffPixels:N0} diff pixels</div>");

        if (comparison.SuppressedFrames > 0)
        {
            sb.AppendLine($"<div class='warn'>Showing the first {comparison.ExportedFrames:N0} changed frames; {comparison.SuppressedFrames:N0} more were left out of the stepper. The Animation view still plays the complete files.</div>");
        }

        if (comparison.UnpairedFrames > 0)
        {
            var longer = comparison.ActualFrameCount > comparison.BaselineFrameCount ? "actual" : "baseline";
            sb.AppendLine($"<div class='warn'>Frame counts differ: {comparison.UnpairedFrames:N0} frame(s) exist only in the {longer}. Those have nothing to sit beside, so they are not in the stepper &mdash; use the Animation view to see them.</div>");
        }

        sb.AppendLine($"<div class='timestamp'>Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}</div>");

        sb.AppendLine("<div class='tabs'>");
        sb.AppendLine("<button class='tab active' data-view='sidebyside' onclick='setView(\"sidebyside\")'>Side by Side</button>");
        sb.AppendLine("<button class='tab' data-view='baseline' onclick='setView(\"baseline\")'>Baseline</button>");
        sb.AppendLine("<button class='tab' data-view='actual' onclick='setView(\"actual\")'>Actual</button>");
        sb.AppendLine("<button class='tab' data-view='diff' onclick='setView(\"diff\")'>Diff</button>");
        sb.AppendLine("<button class='tab' data-view='toggle' onclick='setView(\"toggle\")'>Toggle</button>");
        sb.AppendLine("<button class='tab' data-view='animation' onclick='setView(\"animation\")'>Animation</button>");
        sb.AppendLine("<span class='nav-sep'>|</span>");
        sb.AppendLine("<button class='tab' id='zoomTab' onclick='toggleZoom()'>Zoom to change</button>");
        sb.AppendLine("</div>");

        sb.AppendLine("<div class='frame-nav'>");
        sb.AppendLine("<button class='nav-btn' onclick='prevFrame()' title='Left Arrow'>&larr; Prev</button>");
        sb.AppendLine("<span id='frameCounter'></span>");
        sb.AppendLine("<button class='nav-btn' onclick='nextFrame()' title='Right Arrow'>Next &rarr;</button>");
        sb.AppendLine("<span class='nav-sep'>|</span>");
        sb.AppendLine("<span id='frameStats'></span>");
        sb.AppendLine("</div>");

        sb.AppendLine("<div id='sparkline' class='sparkline' title='Changed pixels per frame - click to jump'></div>");
        sb.AppendLine("<div id='viewer' class='viewer'></div>");

        sb.AppendLine("<script>");
        sb.AppendLine($"const framesDir = {JsonString(framesRelative)};");
        sb.AppendLine($"const baselineApng = {JsonString(baselineRelative)};");
        sb.AppendLine($"const actualApng = {JsonString(actualRelative)};");
        sb.AppendLine($"const canvasW = {CanvasWidth}, canvasH = {CanvasHeight};");

        // Only changed frames are exported, so the stepper walks those rather than every frame of
        // the animation - on a regression the identical frames in between are not what you need.
        sb.AppendLine($"const frames = [{string.Join(",", exportedFrames.Select(FrameJson))}];");
        sb.AppendLine($"const totalPixels = {(long)CanvasWidth * CanvasHeight};");
        sb.AppendLine("let currentFrame = 0;");
        sb.AppendLine("let currentView = 'sidebyside';");
        sb.AppendLine("let zoomed = false;");
        sb.AppendLine(DiffPageJs());
        sb.AppendLine("</script>");

        sb.AppendLine("</body></html>");

        System.IO.File.WriteAllText(outputPath, sb.ToString());
    }

    private static string FrameJson(FrameDiffResult frame)
    {
        var bounds = frame.DiffBounds is { } b
            ? $"[{b.X},{b.Y},{b.Width},{b.Height}]"
            : "null";

        return $"{{i:{frame.FrameIndex},d:{frame.DiffPixelCount},b:{bounds}}}";
    }

    private static string JsonString(string value)
    {
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    /// <summary>
    /// Page for a file with no baseline yet. There is nothing to compare, so it just plays the
    /// render - which the browser does natively from the APNG, at no extraction cost.
    /// </summary>
    public static void GenerateViewHtml(string relativePath, string actualPath, int frameCount, string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var dir = Path.GetDirectoryName(outputPath)!;
        var reportRelative = Path.GetRelativePath(dir, ReportPath).Replace('\\', '/');
        var actualRelative = Path.GetRelativePath(dir, actualPath).Replace('\\', '/');

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html><head><meta charset='utf-8'>");
        sb.AppendLine($"<title>New: {HtmlEncode(relativePath)}</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(DiffPageCss());
        sb.AppendLine("</style></head><body>");
        sb.AppendLine($"<div class='breadcrumb'><a href='{HtmlEncode(reportRelative)}'>&larr; Back to Report</a></div>");
        sb.AppendLine($"<h1>New: {HtmlEncode(relativePath)}</h1>");
        sb.AppendLine($"<div class='summary'>{frameCount} frame{(frameCount != 1 ? "s" : "")} | No baseline &mdash; accept it to make this the reference</div>");
        sb.AppendLine($"<div class='timestamp'>Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}</div>");
        sb.AppendLine("<div class='viewer'>");
        sb.AppendLine($"<div class='panel solo'><h3>Render</h3><img src='{HtmlEncode(actualRelative)}' alt='render'></div>");
        sb.AppendLine("</div>");
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
        sb.AppendLine("<button class='filter' onclick='filterBy(\"all\", this)'>All</button>");
        sb.AppendLine("<button class='filter active' onclick='filterBy(\"fail\", this)'>Failed</button>");
        sb.AppendLine("<button class='filter' onclick='filterBy(\"pass\", this)'>Passed</button>");
        sb.AppendLine("<button class='filter' onclick='filterBy(\"new\", this)'>New</button>");
        sb.AppendLine("<button class='filter' onclick='filterBy(\"reviewed\", this)'>Reviewed</button>");
        sb.AppendLine("<button class='filter' onclick='filterBy(\"unreviewed\", this)'>Unreviewed</button>");
        if (errors > 0)
        {
            sb.AppendLine("<button class='filter' onclick='filterBy(\"error\", this)'>Errors</button>");
        }

        // 373 rows is too many to scan by eye when you are looking for one file.
        sb.AppendLine("<input id='search' class='search' type='search' placeholder='Filter by name...' oninput='searchBy(this.value)'>");
        sb.AppendLine("<span id='shownCount' class='shown-count'></span>");
        sb.AppendLine("</div>");

        sb.AppendLine("<table id='results'>");
        sb.AppendLine("<thead><tr><th>Status</th><th>File</th><th class='sortable' onclick='sortBy(\"frames\")'>Frames &#8645;</th><th class='sortable' onclick='sortBy(\"diffframes\")'>Changed Frames &#8645;</th><th class='sortable' onclick='sortBy(\"diffpixels\")'>Diff Pixels &#8645;</th><th>Details</th><th>Reviewed</th></tr></thead>");
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
            sb.AppendLine($"<tr class='row {statusClass}' data-status='{statusClass}' data-file='{fileKey}' data-frames='{result.FrameCount}' data-diffframes='{result.DiffFrameCount}' data-diffpixels='{result.TotalDiffPixels}'>");
            sb.AppendLine($"<td class='status {statusClass}'>{statusIcon}</td>");
            sb.AppendLine($"<td>{HtmlEncode(result.RelativePath)}</td>");
            sb.AppendLine($"<td>{result.FrameCount}</td>");
            // Already computed per result but never surfaced: how much of the animation moved,
            // which separates a one-frame blip from a regression running through the whole file.
            sb.AppendLine($"<td>{(result.DiffFrameCount > 0 ? $"{result.DiffFrameCount:N0} / {result.FrameCount:N0}" : "")}</td>");
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
                sb.AppendLine("<pre class='command'>powershell -File tools/accept-baselines.ps1 -NewOnly</pre>");
            }

            if (failed > 0)
            {
                sb.AppendLine("<h3>Accept All Changed</h3>");
                sb.AppendLine("<pre class='command'>powershell -File tools/accept-baselines.ps1 -All</pre>");
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

    private static string HtmlEncode(string text)
    {
        return System.Net.WebUtility.HtmlEncode(text);
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
        .filters { margin-bottom: 16px; display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
        .search { margin-left: auto; padding: 6px 10px; border: 1px solid #30363d; background: #0d1117; color: #c9d1d9; border-radius: 6px; min-width: 220px; }
        .search:focus { outline: none; border-color: #58a6ff; }
        .shown-count { color: #8b949e; font-size: 13px; min-width: 70px; text-align: right; }
        th.sortable { cursor: pointer; user-select: none; }
        th.sortable:hover { color: #58a6ff; }
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

        let currentStatus = 'fail';
        let currentSearch = '';

        // Takes the clicked button explicitly. It used to read the global `event`, which is
        // undefined when this is called directly on load - that threw, and everything registered
        // after it (the click-to-copy handlers) silently never ran.
        function filterBy(status, btn) {
            currentStatus = status;

            if (btn) {
                document.querySelectorAll('.filter').forEach(b => b.classList.remove('active'));
                btn.classList.add('active');
            }

            applyFilters();
        }

        function searchBy(text) {
            currentSearch = text.trim().toLowerCase();
            applyFilters();
        }

        function applyFilters() {
            let shown = 0;

            document.querySelectorAll('.row').forEach(row => {
                let show;
                if (currentStatus === 'all') show = true;
                else if (currentStatus === 'reviewed') show = row.dataset.reviewed === 'true';
                else if (currentStatus === 'unreviewed') show = row.dataset.reviewed !== 'true' && row.dataset.status === 'fail';
                else show = row.dataset.status === currentStatus;

                if (show && currentSearch) {
                    show = row.dataset.file.toLowerCase().includes(currentSearch);
                }

                row.classList.toggle('hidden', !show);
                if (show) shown++;
            });

            const count = document.getElementById('shownCount');
            if (count) count.textContent = `${shown} shown`;
        }

        // Sorts by a numeric data attribute, descending first so the worst regression is on top.
        let sortState = {};
        function sortBy(key) {
            const tbody = document.querySelector('#results tbody');
            const rows = Array.from(tbody.querySelectorAll('.row'));
            const desc = sortState[key] !== 'desc';
            sortState = { [key]: desc ? 'desc' : 'asc' };

            rows.sort((a, b) => {
                const av = +(a.dataset[key] || 0), bv = +(b.dataset[key] || 0);
                return desc ? bv - av : av - bv;
            });

            rows.forEach(r => tbody.appendChild(r));
        }

        // Init review buttons on load
        renderReviewButtons();

        // Default to showing failed
        filterBy('fail', document.querySelector('.filter.active'));

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
        .warn { background: #2d2416; border: 1px solid #7d5c1e; color: #d29922; padding: 8px 12px; border-radius: 6px; margin-bottom: 12px; font-size: 13px; }
        .imgwrap { position: relative; border: 1px solid #30363d; border-radius: 4px; overflow: hidden; background: #010409; }
        .imgwrap img { display: block; border: none; border-radius: 0; }
        .sparkline { display: flex; align-items: flex-end; gap: 1px; height: 32px; margin-bottom: 16px; padding: 2px; background: #161b22; border: 1px solid #30363d; border-radius: 4px; overflow-x: auto; }
        .spark { flex: 0 0 3px; background: #f85149; opacity: 0.55; cursor: pointer; border-radius: 1px; }
        .spark:hover { opacity: 1; }
        .spark.on { background: #58a6ff; opacity: 1; }
        """;

    private static string DiffPageJs() => """
        const pad = n => String(n).padStart(6, '0');
        const srcFor = (kind, i) => `${framesDir}/${kind}${pad(i)}.png`;

        function prevFrame() { if (currentFrame > 0) { currentFrame--; updateView(); } }
        function nextFrame() { if (currentFrame < frames.length - 1) { currentFrame++; updateView(); } }

        function setView(v) {
            currentView = v;
            document.querySelectorAll('.tab[data-view]').forEach(t => t.classList.toggle('active', t.dataset.view === v));
            updateView();
        }

        function toggleZoom() {
            zoomed = !zoomed;
            document.getElementById('zoomTab').classList.toggle('active', zoomed);
            updateView();
        }

        // Scales the changed region up to fill the panel. A handful of differing pixels on a
        // 1024x768 canvas is otherwise invisible at page scale, which is the common case for the
        // regressions worth chasing.
        function zoomStyle() {
            const box = frames[currentFrame] && frames[currentFrame].b;
            if (!zoomed || !box) { return { wrap: '', img: '' }; }

            const [x, y, w, h] = box;
            // Pad the box so the change has visible context around it.
            const padPx = Math.max(16, Math.round(Math.max(w, h) * 0.25));
            const zx = Math.max(0, x - padPx), zy = Math.max(0, y - padPx);
            const zw = Math.min(canvasW - zx, w + padPx * 2), zh = Math.min(canvasH - zy, h + padPx * 2);
            const scale = Math.min(8, Math.max(1, 480 / Math.max(zw, zh)));

            return {
                wrap: `overflow:hidden;width:${Math.round(zw * scale)}px;height:${Math.round(zh * scale)}px;`,
                img: `width:${canvasW * scale}px;height:${canvasH * scale}px;max-width:none;margin-left:${-zx * scale}px;margin-top:${-zy * scale}px;`
            };
        }

        function panel(title, kind, extraClass) {
            const f = frames[currentFrame];
            const z = zoomStyle();
            return `<div class='panel ${extraClass || ''}'><h3>${title}</h3>
                <div class='imgwrap' style='${z.wrap}'><img loading='lazy' src='${srcFor(kind, f.i)}' style='${z.img}'></div></div>`;
        }

        function updateView() {
            const f = frames[currentFrame];

            document.getElementById('frameCounter').textContent =
                frames.length ? `Change ${currentFrame + 1} / ${frames.length} (frame ${f.i})` : 'No exported frames';

            const fs = document.getElementById('frameStats');
            if (f) {
                const pct = ((f.d / totalPixels) * 100).toFixed(3);
                const box = f.b ? ` in ${f.b[2]}x${f.b[3]} at ${f.b[0]},${f.b[1]}` : '';
                fs.textContent = `${f.d.toLocaleString()} pixels differ (${pct}%)${box}`;
            } else {
                fs.textContent = '';
            }

            const btns = document.querySelectorAll('.nav-btn');
            btns[0].disabled = currentFrame <= 0;
            btns[1].disabled = currentFrame >= frames.length - 1;

            const viewer = document.getElementById('viewer');

            if (currentView === 'animation') {
                // Browsers animate APNG natively, so the whole file costs one <img> each and needs
                // no per-frame export at all.
                viewer.innerHTML = `<div class='row-pair'>
                    <div class='panel'><h3>Baseline</h3><img src='${baselineApng}'></div>
                    <div class='panel'><h3>Actual</h3><img src='${actualApng}'></div></div>`;
                return;
            }

            if (!f) { viewer.innerHTML = `<div class='panel solo'><h3>Nothing to show</h3></div>`; return; }

            if (currentView === 'sidebyside') {
                viewer.innerHTML = `<div class='row-pair'>${panel('Baseline', 'b')}${panel('Actual', 'a')}</div>
                    <div class='row-diff'>${panel('Diff', 'd')}</div>`;
            } else if (currentView === 'baseline') {
                viewer.innerHTML = panel('Baseline', 'b', 'solo');
            } else if (currentView === 'actual') {
                viewer.innerHTML = panel('Actual', 'a', 'solo');
            } else if (currentView === 'diff') {
                viewer.innerHTML = panel('Diff', 'd', 'solo');
            } else {
                const z = zoomStyle();
                viewer.innerHTML = `<div class='panel'><h3 id='toggleLabel'>Baseline (click to toggle)</h3>
                    <div class='imgwrap' style='${z.wrap}'><img id='toggleImg' src='${srcFor('b', f.i)}' style='cursor:pointer;${z.img}' onclick='toggleImage()'></div></div>`;
                window._toggleState = false;
            }

            markCurrent();
        }

        function toggleImage() {
            window._toggleState = !window._toggleState;
            const f = frames[currentFrame];
            document.getElementById('toggleImg').src = srcFor(window._toggleState ? 'a' : 'b', f.i);
            document.getElementById('toggleLabel').textContent =
                window._toggleState ? 'Actual (click to toggle)' : 'Baseline (click to toggle)';
        }

        // Bar per changed frame, height proportional to how much changed - shows at a glance
        // whether a regression is one blip or a drift running through the whole animation.
        function buildSparkline() {
            const el = document.getElementById('sparkline');
            if (!frames.length) { el.style.display = 'none'; return; }

            const max = Math.max(...frames.map(f => f.d));
            el.innerHTML = frames.map((f, n) =>
                `<span class='spark' data-n='${n}' title='frame ${f.i}: ${f.d.toLocaleString()} px'
                    style='height:${Math.max(2, Math.round((f.d / max) * 28))}px'></span>`).join('');

            el.addEventListener('click', e => {
                const n = e.target.dataset.n;
                if (n !== undefined) { currentFrame = +n; updateView(); }
            });
        }

        function markCurrent() {
            document.querySelectorAll('.spark').forEach((s, n) => s.classList.toggle('on', n === currentFrame));
        }

        document.addEventListener('keydown', function(e) {
            switch (e.key) {
                case 'ArrowLeft': e.preventDefault(); prevFrame(); break;
                case 'ArrowRight': e.preventDefault(); nextFrame(); break;
                case 'Home': e.preventDefault(); currentFrame = 0; updateView(); break;
                case 'End': e.preventDefault(); currentFrame = frames.length - 1; updateView(); break;
                case 'z': case 'Z': toggleZoom(); break;
            }
        });

        buildSparkline();
        updateView();
        """;
}
