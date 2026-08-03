// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using System.Collections.Concurrent;
using System.Text;
using NAPLPSTests.Visual;

namespace NAPLPSTests.File;

/// <summary>
/// The codec-level equivalence gate: feeding a stream to <see cref="NaplpsDecoder"/> in any
/// chunking must produce the same commands, in the same order, with the same final decoder
/// state, as decoding the whole buffer in one call.
///
/// This is the direct test of the deferral machinery. Where the session-level gate in
/// <see cref="StreamSessionEquivalenceTests"/> compares rendered pixels and so can only afford
/// coarse chunkings over the corpus, this one compares command lists - no rasterization - so it
/// can afford ONE BYTE AT A TIME over every example file. Every operand list, escape sequence,
/// macro body and NSR cursor operand in the corpus therefore gets split at every interior
/// position, which is exactly where a wrong deferral point shows up.
///
/// See docs/plans/streaming-decode-and-surface-model.md.
/// </summary>
[TestClass]
public class DecoderFeedEquivalenceTests
{
    private static NaplpsDecoder MakeDecoder(NaplpsSystemType systemType)
    {
        var state = new NaplpsState();
        NaplpsDecoder.ApplySystemDefaults(state, systemType);

        return new NaplpsDecoder(state);
    }

    /// <summary>A stable fingerprint of what the decoder emitted: opcode, operand bytes and
    /// whether the sequence is coded input or presentation output.</summary>
    private static string Fingerprint(IEnumerable<NaplpsSequence> commands)
    {
        var sb = new StringBuilder();

        foreach (var seq in commands)
        {
            sb.Append(seq.IsSynthetic ? '~' : '.');
            sb.Append(seq.Command.OpCode.ToString("X2"));

            foreach (var operand in seq.Command.Operands)
            {
                sb.Append(' ').Append(operand.ToString("X2"));
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>One call, whole buffer: literally the path <see cref="NaplpsFormat"/> takes,
    /// splice reader included - the reference must not be a parse no consumer performs.</summary>
    private static (string Commands, string State) OneShot(byte[] bytes, NaplpsSystemType systemType)
    {
        var fmt = NaplpsFormat.FromBytes(bytes, systemType);

        return (Fingerprint(fmt.Commands), fmt.State.ToJson());
    }

    /// <summary>Fed in the given chunk sizes, then flushed: the path the wire takes.</summary>
    private static (string Commands, string State) Streamed(byte[] bytes, NaplpsSystemType systemType, IEnumerable<int> chunkLengths)
    {
        var decoder = MakeDecoder(systemType);
        var emitted = new List<NaplpsSequence>();
        var off = 0;

        foreach (var len in chunkLengths)
        {
            if (off >= bytes.Length) { break; }

            var end = Math.Min(bytes.Length, off + Math.Max(1, len));
            emitted.AddRange(decoder.Feed(bytes.AsSpan(off, end - off)));
            off = end;
        }

        if (off < bytes.Length)
        {
            emitted.AddRange(decoder.Feed(bytes.AsSpan(off)));
        }

        emitted.AddRange(decoder.Flush());

        Assert.AreEqual(0, decoder.PendingByteCount, "flush left bytes pending");

        return (Fingerprint(emitted), decoder.State.ToJson());
    }

    private static IEnumerable<int> Fixed(int size, int total)
    {
        for (var i = 0; i < total; i += size) { yield return size; }
    }

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

    /// <summary>Reports the first differing line rather than dumping two whole command lists.</summary>
    private static string FirstDifference(string expected, string actual)
    {
        var e = expected.Split('\n');
        var a = actual.Split('\n');

        for (var i = 0; i < Math.Min(e.Length, a.Length); i++)
        {
            if (e[i] != a[i])
            {
                return $"command {i}: expected '{e[i]}', got '{a[i]}'";
            }
        }

        return $"command count: expected {e.Length}, got {a.Length}";
    }

    [TestMethod]
    [TestCategory("Equivalence")]
    public void Corpus_FeedMatchesOneShot_ByteAtATime()
    {
        var files = VisualTestContext.DiscoverExampleFiles().ToList();
        Assert.IsTrue(files.Count > 0, "no example files discovered");

        var failures = new ConcurrentBag<string>();
        var checkedCount = 0;

        Parallel.ForEach(files, relativePath =>
        {
            var full = Path.Combine(VisualTestContext.ExamplesDir, relativePath);
            var bytes = System.IO.File.ReadAllBytes(full);
            var forced = VisualTestContext.GetForcedSystemType(relativePath);
            var systemType = forced ?? NaplpsFormat.FromBytes(bytes).SystemType;

            var expected = OneShot(bytes, systemType);

            var strategies = new List<(string Name, IEnumerable<int> Chunks)>
            {
                ("1-byte chunks", Fixed(1, bytes.Length)),
                ("3-byte chunks", Fixed(3, bytes.Length)),
                ("random splits", RandomSplits(SeedFor(relativePath), bytes.Length)),
            };

            foreach (var (name, chunks) in strategies)
            {
                try
                {
                    var actual = Streamed(bytes, systemType, chunks);

                    if (actual.Commands != expected.Commands)
                    {
                        failures.Add($"{relativePath} [{name}]: {FirstDifference(expected.Commands, actual.Commands)}");
                        continue;
                    }

                    if (actual.State != expected.State)
                    {
                        failures.Add($"{relativePath} [{name}]: final decoder state diverged");
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{relativePath} [{name}]: {ex.GetType().Name}: {ex.Message}");
                }
            }

            Interlocked.Increment(ref checkedCount);
        });

        Console.WriteLine($"decoder feed equivalence: {checkedCount} files x 3 chunkings");

        if (!failures.IsEmpty)
        {
            var list = string.Join(Environment.NewLine, failures.OrderBy(f => f).Take(40));
            Assert.Fail($"{failures.Count} failure(s):{Environment.NewLine}{list}");
        }
    }

    /// <summary>A command straddling the frontier must be withheld, not half-emitted: the
    /// decoder holds its bytes and emits it only once the terminating byte arrives.</summary>
    [TestMethod]
    public void PartialCommand_IsDeferredUntilItsTerminatorArrives()
    {
        var decoder = MakeDecoder(NaplpsSystemType.NAPLPS);

        // POINT SET ABS in the GR-invoked PDI set (0xA0 + 4) with two numerical-data operand
        // bytes (0xC0..0xFF in GR). Emitting now would be a guess: a third numeric byte could
        // still follow and extend the coordinate.
        var emitted = decoder.Feed(new byte[] { 0xA4, 0xC0, 0xC0 });

        Assert.AreEqual(0, emitted.Count, "a command whose operand list reaches the frontier must be deferred");
        Assert.AreEqual(3, decoder.PendingByteCount);

        // 'A' is not numerical data, so the operand list is now provably terminated. Both the
        // freed command and the character itself come out: a character takes no operands, so it
        // is complete the moment its byte lands and never needs deferring.
        emitted = decoder.Feed(new byte[] { 0x41 });

        Assert.AreEqual(2, emitted.Count, "the completed command should be emitted once terminated");
        Assert.AreEqual(0xA4, emitted[0].Command.OpCode);
        Assert.AreEqual(2, emitted[0].Command.Operands.Count);
        Assert.AreEqual(0x41, emitted[1].Command.OpCode);
        Assert.AreEqual(0, decoder.PendingByteCount);
    }

    /// <summary>Flush is the only thing that resolves a genuinely-final command: at end of
    /// stream a complete operand list is byte-identical to a truncated one.</summary>
    [TestMethod]
    public void Flush_EmitsTheCommandEndingAtTheLastByte()
    {
        var decoder = MakeDecoder(NaplpsSystemType.NAPLPS);

        Assert.AreEqual(0, decoder.Feed(new byte[] { 0xA4, 0xC0, 0xC0, 0xC0 }).Count);

        var emitted = decoder.Flush();

        Assert.AreEqual(1, emitted.Count);
        Assert.AreEqual(0xA4, emitted[0].Command.OpCode);
        Assert.AreEqual(3, emitted[0].Command.Operands.Count);
        Assert.AreEqual(0, decoder.PendingByteCount);
    }

    /// <summary>An ESC sequence split before its final byte must not be dispatched early.</summary>
    [TestMethod]
    public void EscapeSequence_SplitBeforeFinalByte_IsDeferred()
    {
        var decoder = MakeDecoder(NaplpsSystemType.NAPLPS);

        Assert.AreEqual(0, decoder.Feed(new byte[] { 0x1B }).Count, "a bare ESC has no final byte yet");
        Assert.AreEqual(1, decoder.PendingByteCount);

        var emitted = decoder.Feed(new byte[] { 0x28, 0x40 });

        Assert.AreEqual(1, emitted.Count);
        Assert.AreEqual(0x1B, emitted[0].Command.OpCode);
        Assert.AreEqual(0, decoder.PendingByteCount);
    }
}
