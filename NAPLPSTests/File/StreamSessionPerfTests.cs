// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using System.Diagnostics;
using NAPLPSTests.Visual;

namespace NAPLPSTests.File;

/// <summary>
/// Performance shape of the append path, for the interactive C ABI consumer.
///
/// The contract is not "fast" but "flat": appending a small transient drawing (a cursor
/// face, a caret cell, a keystroke echo) must cost the same whether the session has
/// decoded nothing or a whole page. A reception-system client repaints on a 400 ms blink
/// for the lifetime of a page, so any dependence on history length is quadratic over the
/// session.
///
/// These were the gate for the forward-only session rewrite, and were RED before it: the old Append concatenated onto the full byte history,
/// re-parsed it, and replayed every executed command onto a fresh canvas, so a transient
/// paint cost O(page) rather than O(30 bytes). The forward-only session turned them
/// green. They stay out of the default CI run by category (timing-based; run them isolated).
/// </summary>
[TestClass]
public class StreamSessionPerfTests
{
    private const int W = 640;
    private const int H = 480;

    /// <summary>A transient paint onto a loaded session may cost this much more than the
    /// same paint onto an empty one. Forward-only decoding makes the true ratio ~1;
    /// the allowance covers canvas-size effects and scheduling noise.</summary>
    private const double MaxLoadedToEmptyRatio = 4.0;

    private static byte[] Page(string name)
    {
        var path = Path.Combine(VisualTestContext.ExamplesDir, name);
        Assert.IsTrue(System.IO.File.Exists(path), $"corpus page missing: {path}");

        return System.IO.File.ReadAllBytes(path);
    }

    /// <summary>Blink one cell <paramref name="ticks"/> times and return the per-tick
    /// milliseconds, discarding the first tick as warmup.</summary>
    private static double BlinkCostMs(byte[]? preload, int ticks)
    {
        using var session = new NaplpsStreamSession(W, H, prodigy: true);

        if (preload is not null)
        {
            session.Append(preload);
            session.Flush();
            while (session.ExecNext() is not null) { }
        }

        var first = session.FillRect(0.25, 0.5, 1.0 / 40, 0.0390625, 6);
        session.ExecTo(first - 1);

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < ticks; i++)
        {
            var count = session.FillRect(0.25, 0.5, 1.0 / 40, 0.0390625, (i % 2 == 0) ? 0 : 6);
            session.ExecTo(count - 1);
        }

        sw.Stop();

        return sw.Elapsed.TotalMilliseconds / ticks;
    }

    /// <summary>The discriminating gate: the SAME transient paint, onto an empty session
    /// and onto one holding a decoded page. A replay-based Append would make the loaded case
    /// cost a full page re-parse plus re-render; forward-only Append makes the two equal.</summary>
    [TestMethod]
    [TestCategory("Perf")]
    public void TransientPaintCost_IsIndependentOfSessionContents()
    {
        var page = Page("canada1.nap");

        BlinkCostMs(null, 4); // warm the JIT for both paths
        BlinkCostMs(page, 2);

        var empty = BlinkCostMs(null, 40);
        var loaded = BlinkCostMs(page, 40);
        var ratio = loaded / empty;

        Console.WriteLine($"page: canada1.nap ({page.Length} bytes)");
        Console.WriteLine($"transient paint on empty session:  {empty:F3} ms");
        Console.WriteLine($"transient paint on loaded session: {loaded:F3} ms");
        Console.WriteLine($"ratio: {ratio:F1}x (flat = ~1.0)");

        Assert.IsTrue(
            ratio <= MaxLoadedToEmptyRatio,
            $"a transient paint costs {ratio:F1}x more on a loaded session " +
            $"({empty:F3} ms -> {loaded:F3} ms); append still re-does the session history");
    }

    /// <summary>Cost per tick must not climb as transient paints accumulate. Each tick adds
    /// bytes and commands that a replay-based Append would re-do on every later tick.</summary>
    [TestMethod]
    [TestCategory("Perf")]
    public void RepeatedTransientPaint_CostDoesNotGrowWithTickCount()
    {
        const int ticks = 120;

        using var session = new NaplpsStreamSession(W, H, prodigy: true);
        session.Append(Page("MM01.NAP"));
        session.Flush();
        while (session.ExecNext() is not null) { }

        var timings = new double[ticks];
        for (var i = 0; i < ticks; i++)
        {
            var sw = Stopwatch.StartNew();
            var count = session.FillRect(0.25, 0.5, 1.0 / 40, 0.0390625, (i % 2 == 0) ? 6 : 0);
            session.ExecTo(count - 1);
            sw.Stop();
            timings[i] = sw.Elapsed.TotalMilliseconds;
        }

        var q = ticks / 4;
        var first = timings.Skip(1).Take(q).Average();
        var last = timings.Skip(ticks - q).Average();
        var ratio = last / first;

        Console.WriteLine($"ticks: {ticks}, commands after: {session.CommandCount}");
        Console.WriteLine($"first quartile: {first:F2} ms, last quartile: {last:F2} ms, ratio: {ratio:F2}");
        Console.WriteLine($"total: {timings.Sum():F0} ms");

        Assert.IsTrue(
            ratio <= 1.5,
            $"per-tick cost grew {ratio:F2}x over {ticks} ticks ({first:F2} ms -> {last:F2} ms)");
    }
}
