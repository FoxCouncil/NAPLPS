// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using NAPLPS.Drawing;
using SixLabors.ImageSharp;

namespace NAPLPSTests.File;

/// <summary>
/// NaplpsStreamSession (the managed core behind the naplps_ctx_* C ABI): stepped
/// execution must be pixel-identical to a one-shot render, decoder state (DRCS,
/// character sets, position) must carry across appends including mid-command chunk
/// splits, draw_text must emit well-formed commands, and appends must be transactional.
/// </summary>
[TestClass]
public class StreamSessionTests
{
    private const int W = 640;
    private const int H = 480;

    private static string Example(string name) =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Examples", name);

    private static byte[] OneShot(byte[] bytes, bool prodigy)
    {
        var fmt = NaplpsFormat.FromBytes(bytes, prodigy ? NaplpsSystemType.Prodigy : null);
        using var ctx = new DrawContext(fmt, new SixLabors.ImageSharp.Size(W, H));
        if (prodigy) { ctx.AuthenticGeometry = true; }
        ctx.Render();
        var buf = new byte[W * H * 4];
        ctx.Image.CopyPixelDataTo(buf);
        return buf;
    }

    private static byte[] Stepped(byte[] bytes, bool prodigy, int chunkSize)
    {
        using var session = new NaplpsStreamSession(W, H, prodigy);
        for (var off = 0; off < bytes.Length; off += chunkSize)
        {
            session.Append(bytes[off..Math.Min(bytes.Length, off + chunkSize)]);
            while (session.ExecNext() is not null) { }
        }

        var buf = new byte[W * H * 4];
        session.CopyFramebufferTo(buf);
        return buf;
    }

    /// <summary>Stepping a complete stream command-by-command must equal Render().</summary>
    [TestMethod]
    public void SteppedExecution_MatchesOneShotRender()
    {
        foreach (var (file, prodigy) in new[] { ("MM01.NAP", true), ("beer.nap", false), ("1.nap", true) })
        {
            var bytes = System.IO.File.ReadAllBytes(Example(file));
            var expected = OneShot(bytes, prodigy);
            var actual = Stepped(bytes, prodigy, chunkSize: bytes.Length);
            CollectionAssert.AreEqual(expected, actual, $"{file}: stepped render diverged");
        }
    }

    /// <summary>Chunked appends that split mid-command must converge to the same pixels
    /// once the stream is complete (the replay repaints the completed commands).</summary>
    [TestMethod]
    public void SplitAppends_ConvergeToOneShotPixels()
    {
        foreach (var (file, prodigy) in new[] { ("MM01.NAP", true), ("beer.nap", false) })
        {
            var bytes = System.IO.File.ReadAllBytes(Example(file));
            var expected = OneShot(bytes, prodigy);
            foreach (var chunk in new[] { 64, 7 })
            {
                var actual = Stepped(bytes, prodigy, chunk);
                CollectionAssert.AreEqual(expected, actual, $"{file}: {chunk}-byte chunking diverged");
            }
        }
    }

    /// <summary>Decoder state set up by one append must shape what a LATER append draws -
    /// the persistent-state contract the C consumer depends on. (The scenario's DEF DRCS
    /// bytes exercise the definition-command path; note the 8-bit C1 DEF DRCS cannot open
    /// buffered DRCS mode from the wire today - a pre-existing upstream gap - so what this
    /// pins is state carriage, asserted by the pixel differential.)</summary>
    [TestMethod]
    public void DecoderState_CarriesAcrossAppends()
    {
        // DEF DRCS for 'A' whose glyph body is a filled rect (a solid block).
        var def = new List<byte> { 0x83, 0x41 };
        var (op, ops) = NaplpsCommandBuilder.BuildRectangleSetFilled(0.1f, 0.1f, 0.8f, 0.8f, 3);
        def.Add(op);
        def.AddRange(ops);

        var text = new List<byte>();
        var (pop, pops) = NaplpsCommandBuilder.BuildPointSetAbsolute(0.25f, 0.5f, 3);
        text.Add(pop);
        text.AddRange(pops);
        text.AddRange("AAA"u8.ToArray());

        long Lit(NaplpsStreamSession s)
        {
            var buf = new byte[W * H * 4];
            s.CopyFramebufferTo(buf);
            long lit = 0;
            for (var i = 0; i < buf.Length; i += 4)
            {
                if (buf[i] > 8 || buf[i + 1] > 8 || buf[i + 2] > 8) { lit++; }
            }

            return lit;
        }

        using var withDef = new NaplpsStreamSession(W, H, prodigy: true);
        withDef.Append([.. def]);
        withDef.Append([.. text]);
        while (withDef.ExecNext() is not null) { }
        var customLit = Lit(withDef);

        using var withoutDef = new NaplpsStreamSession(W, H, prodigy: true);
        withoutDef.Append([.. text]);
        while (withoutDef.ExecNext() is not null) { }
        var fontLit = Lit(withoutDef);

        Assert.IsTrue(customLit > 0, "custom glyph drew nothing");
        Assert.AreNotEqual(fontLit, customLit, "DRCS definition from the earlier append had no effect");
    }

    /// <summary>draw_text emits Point Set Absolute + SELECT COLOR + TEXT + chars that
    /// parse back with the requested attributes.</summary>
    [TestMethod]
    public void DrawText_EmitsWellFormedCommands()
    {
        using var session = new NaplpsStreamSession(W, H, prodigy: true);
        var count = session.DrawText(0.25, 0.5, fg: 7, bg: 3, charW: 0.025, charH: 0.0390625, "HI"u8.ToArray());
        Assert.AreEqual(count, session.CommandCount);

        var cmds = session.Format!.Commands;
        Assert.IsInstanceOfType<PointSetAbsoluteCommand>(cmds[0].Command);
        Assert.IsInstanceOfType<SelectColorCommand>(cmds[1].Command);
        Assert.IsInstanceOfType<TextCommand>(cmds[2].Command);
        Assert.IsInstanceOfType<AsciiCharCommand>(cmds[3].Command);
        Assert.IsInstanceOfType<AsciiCharCommand>(cmds[4].Command);

        var final = session.Format.State;
        Assert.AreEqual(7, final.ColorMapForeground);
        Assert.AreEqual(3, final.ColorMapBackground);
        // Sizes round to the wire grid: 0.025 -> nearest 1/256 step.
        Assert.AreEqual(Math.Round(0.025 * 256) / 256, final.CharSize.X, 0.0001);
        Assert.AreEqual(Math.Round(0.0390625 * 256) / 256, final.CharSize.Y, 0.0001);
    }

    /// <summary>draw_text must refuse to append into an unfinished definition, where the
    /// bytes would be swallowed as definition body instead of drawing.</summary>
    [TestMethod]
    public void DrawText_RejectsUnfinishedDefinition()
    {
        using var session = new NaplpsStreamSession(W, H, prodigy: true);
        // DEF MACRO (ESC 4/0 'A') with no END: the stream now buffers into the macro.
        session.Append([0xA1, 0xC8, 0x1B, 0x40, 0x41, 0x20, 0x21]);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            session.DrawText(0.5, 0.5, 7, -1, -1, -1, "X"u8.ToArray()));
    }

    /// <summary>fill_rect paints a solid block in the requested color - the block-cursor
    /// primitive - even when the stream left a fill PATTERN active. The DOMAIN here sets a
    /// nonzero logical pel so patterns actually render patterned (with pel (0,0) every
    /// pattern degenerates to solid and the scenario proves nothing): without the emitted
    /// solid TEXTURE this hash-textured block drops below the threshold.</summary>
    [TestMethod]
    public void FillRect_PaintsSolidBlock_DespiteActivePattern()
    {
        using var session = new NaplpsStreamSession(W, H, prodigy: true);
        // DOMAIN with a 1-grid pel vertex, then a hash fill pattern.
        var (dop, dops) = NaplpsCommandBuilder.BuildDomain(1, 3, 2, new System.Numerics.Vector3(1f / 256, 1f / 256, 0));
        var (top, tops) = NaplpsCommandBuilder.BuildTexture(0, false, 1);
        session.Append([dop, .. dops, top, .. tops]);

        // Off-grid position: 3/40 is not representable; must round to the wire grid.
        var count = session.FillRect(3.0 / 40, 0.4, 1.0 / 40, 0.0390625, color: 6);
        session.ExecTo(count - 1);

        var cmds = session.Format!.Commands;
        Assert.IsInstanceOfType<RectangleSetFilledCommand>(cmds[^1].Command);

        var buf = new byte[W * H * 4];
        session.CopyFramebufferTo(buf);
        long green = 0;
        for (var i = 0; i < buf.Length; i += 4)
        {
            if (buf[i] < 60 && buf[i + 1] > 120 && buf[i + 2] < 60) { green++; }
        }

        // One 16x25px cell (h/0.78125*480 = 24 rows + the authentic-pel row), SOLID.
        // With the hash pattern left active this measures ~243; solid measures ~400.
        Assert.IsTrue(green > 300, $"expected a solid cell block, got {green} green pixels");

        var rect = (RectangleSetFilledCommand)cmds[^1].Command;
        Assert.AreEqual(Math.Round(3.0 / 40 * 256) / 256, rect.StartPoint.X, 0.0001, "x not grid-rounded");
        Assert.AreNotEqual(3.0 / 40, rect.StartPoint.X, "off-grid x should have been quantized");
    }

    /// <summary>Non-finite geometry must be rejected, not encoded as a degenerate rect.</summary>
    [TestMethod]
    public void FillRect_RejectsNonFiniteArguments()
    {
        using var session = new NaplpsStreamSession(W, H, prodigy: true);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            session.FillRect(0.1, 0.1, double.NaN, 0.05, 6));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            session.FillRect(double.PositiveInfinity, 0.1, 0.05, 0.05, 6));
    }

    /// <summary>An append rejected in argument validation must leave the session unchanged
    /// (bytes, counts, pixels) and the session usable afterwards.</summary>
    [TestMethod]
    public void Append_RejectedArguments_LeaveTheSessionUntouched()
    {
        using var session = new NaplpsStreamSession(W, H, prodigy: true);
        var good = System.IO.File.ReadAllBytes(Example("MM01.NAP"));
        session.Append(good);
        while (session.ExecNext() is not null) { }
        var countBefore = session.CommandCount;
        var before = new byte[W * H * 4];
        session.CopyFramebufferTo(before);

        try
        {
            session.Append([]);   // invalid: throws
            Assert.Fail("empty append should throw");
        }
        catch (ArgumentException)
        {
        }

        Assert.AreEqual(countBefore, session.CommandCount);
        var after = new byte[W * H * 4];
        session.CopyFramebufferTo(after);
        CollectionAssert.AreEqual(before, after, "failed append mutated the canvas");

        // And appending after the failure still works.
        var moreCount = session.DrawText(0.1, 0.1, 7, -1, -1, -1, "OK"u8.ToArray());
        Assert.IsTrue(moreCount > countBefore);
    }
}
