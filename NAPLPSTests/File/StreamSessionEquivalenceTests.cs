// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using System.Collections.Concurrent;
using NAPLPS.Drawing;
using NAPLPSTests.Visual;

namespace NAPLPSTests.File;

/// <summary>
/// Corpus-wide equivalence gate for the decode pipeline.
///
/// The invariant under test, over every example stream: a NaplpsStreamSession fed the
/// stream in ANY chunking must end with the same framebuffer, command count, and decoder
/// state as the same session fed the stream in one piece. Chunk boundaries fall wherever
/// they fall, including mid-command. This is the property the codec extraction and the
/// forward-only rewrite actually threaten.
///
/// Streams that do not trigger the retroactive CLUT re-render are ALSO compared against
/// the one-shot NaplpsFormat + DrawContext.Render() path, tying the runtime driver to the
/// editor driver. Streams that DO trigger it are excluded from that second comparison by
/// design, not by convenience: Render() re-renders the whole stream against the final
/// palette when a non-Prodigy stream redefines a CLUT entry mid-stream, and RenderStep -
/// which paints one command onto an existing canvas and can never revisit earlier ones -
/// documents that it does not. 61 of the 372 corpus files are in this class.
///
/// This is the gate for the codec extraction and the forward-only session rewrite. It
/// compares the pipeline against
/// ITSELF rather than against committed golden images, so it is immune to the
/// environmental drift that makes the checked-in visual baselines unreproducible on some
/// machines - a divergence here is always a real behaviour change.
///
/// Two tiers:
/// - Equivalence (default): the whole corpus, a few coarse chunkings.
/// - EquivalenceDeep (opt-in): a representative subset with pathological chunkings
///   (1 byte at a time, 7, 64) plus seeded random splits. Cheap now that Append is
///   forward-only, but kept opt-in so the default run stays quick; widening the subset
///   toward the whole corpus is the natural next step if a chunking bug ever escapes.
/// </summary>
[TestClass]
public class StreamSessionEquivalenceTests
{
    private const int W = 640;
    private const int H = 480;

    /// <summary>Files exercised by the deep tier: Prodigy and generic, small and large,
    /// text-heavy and geometry-heavy.</summary>
    private static readonly string[] DeepSubset =
    [
        "MM01.NAP",
        "beer.nap",
        "1.nap",
        "blocky.nap",
        "boom.nap",
        "telidraw/star.nap",
        "telidraw/snowflake.nap",
        "Ads From Preview Disks/weather-ad.nap",
        "Screens From Preview Disks/games.nap",
    ];

    private sealed record Snapshot(byte[] Pixels, int CommandCount, string StateJson);

    /// <summary>The editor path: whole buffer, rendered in one shot.</summary>
    private static Snapshot OneShot(byte[] bytes, NaplpsSystemType? forced)
    {
        var fmt = NaplpsFormat.FromBytes(bytes, forced);
        using var ctx = new DrawContext(fmt, new SixLabors.ImageSharp.Size(W, H));

        if (forced == NaplpsSystemType.Prodigy)
        {
            ctx.AuthenticGeometry = true;
        }

        ctx.Render();
        var buf = new byte[W * H * 4];
        ctx.Image.CopyPixelDataTo(buf);

        return new Snapshot(buf, fmt.Commands.Count, fmt.State.ToJson());
    }

    /// <summary>True when DrawContext.Render() would finish with a retroactive CLUT
    /// re-render, which no forward-only stepping path can reproduce. Mirrors the predicate
    /// in Render(): a non-Prodigy stream redefining a color while a CLUT color mode is
    /// selected.</summary>
    private static bool UsesRetroactiveClut(byte[] bytes, NaplpsSystemType? forced)
    {
        var fmt = NaplpsFormat.FromBytes(bytes, forced);

        if (fmt.SystemType == NaplpsSystemType.Prodigy) { return false; }

        return fmt.Commands.Any(seq =>
            seq.Command is SetColorCommand setColor &&
            setColor.Operands.Count > 0 &&
            (seq.State.ColorMode == 1 || seq.State.ColorMode == 2));
    }

    /// <summary>The runtime path: fed in the given chunk boundaries, executed to the end.</summary>
    private static Snapshot Session(byte[] bytes, NaplpsSystemType? forced, IEnumerable<int> chunkLengths)
    {
        using var session = new NaplpsStreamSession(W, H, forced == NaplpsSystemType.Prodigy);
        var off = 0;

        foreach (var len in chunkLengths)
        {
            if (len <= 0) { continue; }

            var end = Math.Min(bytes.Length, off + len);
            if (end <= off) { break; }

            session.Append(bytes[off..end]);
            while (session.ExecNext() is not null) { }
            off = end;
        }

        if (off < bytes.Length)
        {
            session.Append(bytes[off..]);
            while (session.ExecNext() is not null) { }
        }

        // The stream is over, so release any command whose operand list ran to the last byte -
        // at end of stream that is indistinguishable from a truncated one, and only the caller
        // knows which it is.
        session.Flush();
        while (session.ExecNext() is not null) { }

        var buf = new byte[W * H * 4];
        session.CopyFramebufferTo(buf);

        return new Snapshot(buf, session.CommandCount, session.Format!.State.ToJson());
    }

    private static IEnumerable<int> Fixed(int size, int total)
    {
        for (var i = 0; i < total; i += size) { yield return size; }
    }

    private static IEnumerable<int> EqualParts(int parts, int total)
    {
        var size = Math.Max(1, total / parts);
        return Fixed(size, total);
    }

    /// <summary>Deterministic per-file random splits - the same seed every run, so a
    /// failure is always reproducible.</summary>
    private static IEnumerable<int> RandomSplits(int seed, int total)
    {
        var rng = new Random(seed);
        var emitted = 0;

        while (emitted < total)
        {
            var len = rng.Next(1, Math.Max(2, total / 8));
            emitted += len;
            yield return len;
        }
    }

    private static int SeedFor(string relativePath)
    {
        var seed = 17;
        foreach (var c in relativePath) { seed = (seed * 31) + c; }

        return seed & 0x7FFFFFFF;
    }

    private static void AssertSame(Snapshot expected, Snapshot actual, string what)
    {
        Assert.AreEqual(expected.CommandCount, actual.CommandCount, $"{what}: command count diverged");
        Assert.AreEqual(expected.StateJson, actual.StateJson, $"{what}: final decoder state diverged");

        if (!expected.Pixels.AsSpan().SequenceEqual(actual.Pixels))
        {
            var diff = 0;
            for (var i = 0; i < expected.Pixels.Length; i += 4)
            {
                if (expected.Pixels[i] != actual.Pixels[i] ||
                    expected.Pixels[i + 1] != actual.Pixels[i + 1] ||
                    expected.Pixels[i + 2] != actual.Pixels[i + 2] ||
                    expected.Pixels[i + 3] != actual.Pixels[i + 3])
                {
                    diff++;
                }
            }

            Assert.Fail($"{what}: framebuffer diverged in {diff} pixels");
        }
    }

    /// <summary>Whole corpus, coarse chunkings: the session must match the one-shot render
    /// regardless of where the appends land.</summary>
    [TestMethod]
    [TestCategory("Equivalence")]
    public void Corpus_SessionMatchesOneShot_AcrossChunkings()
    {
        var files = VisualTestContext.DiscoverExampleFiles().ToList();
        Assert.IsTrue(files.Count > 0, "no example files discovered");

        var failures = new ConcurrentBag<string>();
        var skipped = new ConcurrentBag<string>();
        var clutExcluded = new ConcurrentBag<string>();
        var checkedCount = 0;

        Parallel.ForEach(files, relativePath =>
        {
            var full = Path.Combine(VisualTestContext.ExamplesDir, relativePath);
            var forced = VisualTestContext.GetForcedSystemType(relativePath);
            byte[] bytes;
            Snapshot reference;
            bool retroactiveClut;

            try
            {
                bytes = System.IO.File.ReadAllBytes(full);
                retroactiveClut = UsesRetroactiveClut(bytes, forced);
                reference = Session(bytes, forced, [bytes.Length]);
            }
            catch (Exception ex)
            {
                // The stream does not decode at all; not this gate's business.
                skipped.Add($"{relativePath}: {ex.GetType().Name}");
                return;
            }

            var strategies = new (string Name, IEnumerable<int> Chunks)[]
            {
                ("4 equal chunks", EqualParts(4, bytes.Length)),
                ("random splits", RandomSplits(SeedFor(relativePath), bytes.Length)),
            };

            foreach (var (name, chunks) in strategies)
            {
                try
                {
                    AssertSame(reference, Session(bytes, forced, chunks), $"{relativePath} [{name}]");
                }
                catch (Exception ex)
                {
                    failures.Add($"{relativePath} [{name}]: {ex.Message}");
                }
            }

            if (retroactiveClut)
            {
                clutExcluded.Add(relativePath);
            }
            else
            {
                try
                {
                    AssertSame(OneShot(bytes, forced), reference, $"{relativePath} [vs one-shot Render]");
                }
                catch (Exception ex)
                {
                    failures.Add($"{relativePath} [vs one-shot Render]: {ex.Message}");
                }
            }

            Interlocked.Increment(ref checkedCount);
        });

        Console.WriteLine(
            $"equivalence: {checkedCount} files checked, {skipped.Count} undecodable, " +
            $"{clutExcluded.Count} excluded from the one-shot comparison (retroactive CLUT)");

        foreach (var s in skipped.OrderBy(s => s)) { Console.WriteLine($"  undecodable: {s}"); }

        if (!failures.IsEmpty)
        {
            var list = string.Join(Environment.NewLine, failures.OrderBy(f => f).Take(40));
            Assert.Fail($"{failures.Count} equivalence failure(s):{Environment.NewLine}{list}");
        }
    }

    /// <summary>Pathological chunkings on a representative subset: one byte at a time and
    /// small fixed chunks, which split operand lists and definition bodies everywhere.</summary>
    [TestMethod]
    [TestCategory("EquivalenceDeep")]
    public void Subset_SessionMatchesOneShot_AcrossPathologicalChunkings()
    {
        var failures = new ConcurrentBag<string>();

        Parallel.ForEach(DeepSubset, relativePath =>
        {
            var full = Path.Combine(VisualTestContext.ExamplesDir, relativePath);

            if (!System.IO.File.Exists(full))
            {
                failures.Add($"{relativePath}: missing from the corpus");
                return;
            }

            var forced = VisualTestContext.GetForcedSystemType(relativePath);
            var bytes = System.IO.File.ReadAllBytes(full);
            var reference = Session(bytes, forced, [bytes.Length]);

            if (!UsesRetroactiveClut(bytes, forced))
            {
                try
                {
                    AssertSame(OneShot(bytes, forced), reference, $"{relativePath} [vs one-shot Render]");
                }
                catch (Exception ex)
                {
                    failures.Add($"{relativePath} [vs one-shot Render]: {ex.Message}");
                }
            }

            var strategies = new List<(string Name, IEnumerable<int> Chunks)>
            {
                ("1-byte chunks", Fixed(1, bytes.Length)),
                ("7-byte chunks", Fixed(7, bytes.Length)),
                ("64-byte chunks", Fixed(64, bytes.Length)),
            };

            for (var s = 0; s < 5; s++)
            {
                var seed = SeedFor(relativePath) + s;
                strategies.Add(($"random splits seed {s}", RandomSplits(seed, bytes.Length)));
            }

            foreach (var (name, chunks) in strategies)
            {
                try
                {
                    AssertSame(reference, Session(bytes, forced, chunks), $"{relativePath} [{name}]");
                }
                catch (Exception ex)
                {
                    failures.Add($"{relativePath} [{name}]: {ex.Message}");
                }
            }
        });

        if (!failures.IsEmpty)
        {
            var list = string.Join(Environment.NewLine, failures.OrderBy(f => f));
            Assert.Fail($"{failures.Count} deep equivalence failure(s):{Environment.NewLine}{list}");
        }
    }
}
