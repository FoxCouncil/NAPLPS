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

    /// <summary>
    /// X3.110 6.2.2 chained definitions (a DEF terminated by the next DEF, no END): the
    /// terminating DEF byte hands off to normal command processing, which can defer on its
    /// name byte or operand list. That handoff must be deferred WHOLE - closing the
    /// definition first and unwinding on the handoff retracted the body commands while the
    /// close stood, so a retry skipped them forever.
    /// </summary>
    [TestMethod]
    public void ChainedDefinition_TerminatorAtTheFrontier_LosesNothing()
    {
        // DEF MACRO 'A' (name 0xAB), one raw body byte 0x0F, terminated by the next DEF
        // MACRO whose name byte is still in flight when the terminator arrives.
        byte[] chained = [0x80, 0xAB, 0x0F, 0x81];

        var oneShot = OneShot(chained, NaplpsSystemType.NAPLPS);
        var byteAtATime = Streamed(chained, NaplpsSystemType.NAPLPS, Fixed(1, chained.Length));

        Assert.AreEqual(oneShot.Commands, byteAtATime.Commands,
            FirstDifference(oneShot.Commands, byteAtATime.Commands));
        Assert.AreEqual(oneShot.State, byteAtATime.State);

        // The frontier variant: the terminator AND a numeric name byte arrive in one chunk,
        // but the name's operand list is still open - the handoff must defer then too.
        byte[] frontier = [0x80, 0xAB, 0x0F, 0x81, 0x22, 0x41];

        var reference = OneShot(frontier, NaplpsSystemType.NAPLPS);
        var split = Streamed(frontier, NaplpsSystemType.NAPLPS, [5, 1]);

        Assert.AreEqual(reference.Commands, split.Commands,
            FirstDifference(reference.Commands, split.Commands));
        Assert.AreEqual(reference.State, split.State);
    }

    /// <summary>
    /// The pixel-affecting shape of the same bug: a DEFP MACRO (define-and-display) chained
    /// into the next definition loses its entire drawn replay on the wire path - a session
    /// never paints what the one-shot path paints.
    /// </summary>
    [TestMethod]
    public void ChainedDefpReplay_SurvivesEveryChunking()
    {
        // DEFP MACRO '!' with a LINE SET body, terminated by DEF MACRO '"' then END.
        byte[] bytes = [0x81, 0x21, 0xA9, 0xC0, 0xC1, 0xC2, 0x80, 0x22, 0x85];

        var oneShot = OneShot(bytes, NaplpsSystemType.NAPLPS);

        StringAssert.Contains(oneShot.Commands, "~", "the DEFP replay must be present as synthetic output");

        for (int size = 1; size <= 3; size++)
        {
            var streamed = Streamed(bytes, NaplpsSystemType.NAPLPS, Fixed(size, bytes.Length));

            Assert.AreEqual(oneShot.Commands, streamed.Commands,
                $"[{size}-byte chunks] " + FirstDifference(oneShot.Commands, streamed.Commands));
            Assert.AreEqual(oneShot.State, streamed.State, $"[{size}-byte chunks] state diverged");
        }
    }

    /// <summary>
    /// X3.110 6.2.3: the direct 8-bit C1 DEF DRCS (0x83) consumes the start-code byte that
    /// follows and enters buffered definition mode, so the glyph body renders offscreen
    /// instead of executing as live drawing - and the glyph is stored under the raw start
    /// code, which is how text rendering looks it up.
    /// </summary>
    [TestMethod]
    public void DefDrcs_DirectC1_OpensBufferedDefinitionAndStoresTheGlyph()
    {
        // DEF DRCS 'A' whose glyph body is a filled rect, terminated by END.
        var buffer = new List<byte> { 0x83, 0x41 };
        var (op, ops) = NaplpsCommandBuilder.BuildRectangleSetFilled(0.1f, 0.1f, 0.8f, 0.8f, 3);
        buffer.Add(op);
        buffer.AddRange(ops);
        buffer.Add(0x85);
        byte[] bytes = [.. buffer];

        var decoder = MakeDecoder(NaplpsSystemType.NAPLPS);
        decoder.Feed(bytes);
        decoder.Flush();

        Assert.IsNull(decoder.State.DrcsStartCode, "definition mode left open");
        Assert.IsTrue(decoder.State.DrcsCharacters.ContainsKey(0x41), "glyph not stored under its start code");

        var oneShot = OneShot(bytes, NaplpsSystemType.NAPLPS);

        for (int size = 1; size <= 3; size++)
        {
            var streamed = Streamed(bytes, NaplpsSystemType.NAPLPS, Fixed(size, bytes.Length));

            Assert.AreEqual(oneShot.Commands, streamed.Commands,
                $"[{size}-byte chunks] " + FirstDifference(oneShot.Commands, streamed.Commands));
            Assert.AreEqual(oneShot.State, streamed.State, $"[{size}-byte chunks] state diverged");
        }
    }

    /// <summary>
    /// X3.110 6.2.4 via the direct 8-bit C1 DEF TEXTURE (0x84): the selector byte that
    /// follows picks mask A-D and the buffered body defines the same pattern the ESC-coded
    /// form defines; an out-of-range selector makes the whole command a null operation with
    /// the body still buffered (not executed live) until END.
    /// </summary>
    [TestMethod]
    public void DefTexture_DirectC1_MatchesTheEscForm()
    {
        // Mask A, one 8-byte body row pattern, END. ESC form: ESC 4/4 (0x1B 0x44).
        byte[] body = [0xAA, 0x55, 0xAA, 0x55, 0xAA, 0x55, 0xAA, 0x55];
        byte[] direct = [0x84, 0x41, .. body, 0x85];
        byte[] escForm = [0x1B, 0x44, 0x41, .. body, 0x85];

        var directDecoder = MakeDecoder(NaplpsSystemType.NAPLPS);
        directDecoder.Feed(direct);
        directDecoder.Flush();

        var escDecoder = MakeDecoder(NaplpsSystemType.NAPLPS);
        escDecoder.Feed(escForm);
        escDecoder.Flush();

        Assert.IsNull(directDecoder.State.TextureBeingDefined, "definition mode left open");
        Assert.IsNotNull(directDecoder.State.TextureMaskA, "mask A not stored");
        CollectionAssert.AreEqual(escDecoder.State.TextureMaskA, directDecoder.State.TextureMaskA,
            "direct C1 form and ESC form defined different patterns");

        var oneShot = OneShot(direct, NaplpsSystemType.NAPLPS);

        for (int size = 1; size <= 3; size++)
        {
            var streamed = Streamed(direct, NaplpsSystemType.NAPLPS, Fixed(size, direct.Length));

            Assert.AreEqual(oneShot.Commands, streamed.Commands,
                $"[{size}-byte chunks] " + FirstDifference(oneShot.Commands, streamed.Commands));
            Assert.AreEqual(oneShot.State, streamed.State, $"[{size}-byte chunks] state diverged");
        }
    }
}
