// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using NAPLPS.Drawing;
using SixLabors.ImageSharp;

namespace NAPLPSTests.File;

/// <summary>
/// NaplpsStreamSession (the managed core behind the naplps_ctx_* C ABI): stepped
/// execution must be pixel-identical to a one-shot render, decoder state (DRCS,
/// character sets, position) must carry across appends including mid-command chunk
/// splits, a command split across a chunk boundary must be withheld rather than
/// half-painted, and draw_text must emit well-formed commands.
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

        // End of stream: releases a final command whose operands ran to the last byte.
        session.Flush();
        while (session.ExecNext() is not null) { }

        var buf = new byte[W * H * 4];
        session.CopyFramebufferTo(buf);
        return buf;
    }

    /// <summary>The naplps_ctx_flush contract: a command whose operand list runs to the last
    /// received byte is withheld, because at that point it is byte-identical to a truncated
    /// one and only the caller knows the stream has ended. Flush is that assertion, and it is
    /// idempotent.</summary>
    [TestMethod]
    public void Flush_ReleasesTheCommandEndingAtTheLastByte()
    {
        using var session = new NaplpsStreamSession(W, H, prodigy: false);

        // POINT SET ABSOLUTE (0xA4) followed by operand bytes and nothing to terminate them.
        Assert.AreEqual(0, session.Append([0xA4, 0xC0, 0xC0, 0xC0]), "operands at the frontier must be withheld");
        Assert.IsNull(session.ExecNext(), "nothing complete, nothing to paint");

        Assert.AreEqual(1, session.Flush(), "flush must release the trailing command");
        Assert.AreEqual(0, session.ExecNext(), "the released command must be paintable");

        Assert.AreEqual(1, session.Flush(), "flush must be idempotent");
        Assert.IsNull(session.ExecNext());
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

    /// <summary>Chunked appends that split mid-command must land on the same pixels as the
    /// one-shot render: a command split across a boundary is withheld until its terminating
    /// byte arrives, so no chunking can paint a partial one.</summary>
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

    /// <summary>draw_text emits Point Set Absolute + SELECT COLOR + TEXT + SI + chars that
    /// parse back with the requested attributes. The SI is what makes the payload draw as
    /// text rather than execute as PDI commands whatever the prior stream invoked into GL -
    /// see StreamSessionShiftStateTests.</summary>
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
        Assert.IsInstanceOfType<ControlCommand>(cmds[3].Command);
        Assert.IsInstanceOfType<AsciiCharCommand>(cmds[4].Command);
        Assert.IsInstanceOfType<AsciiCharCommand>(cmds[5].Command);

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
        session.Flush();   // the page stream is over; release its operand-terminated tail

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

    /// <summary>Transparent-background sessions are the window-overlay model: unpainted
    /// pixels stay (0,0,0,0), painted pixels are opaque, and the property survives later
    /// appends onto the same long-lived canvas.</summary>
    [TestMethod]
    public void TransparentBackground_OnlyPaintedPixelsCarryAlpha()
    {
        using var win = new NaplpsStreamSession(W, H, prodigy: true, transparentBackground: true);

        // Before any append: fully transparent.
        var buf = new byte[W * H * 4];
        win.CopyFramebufferTo(buf);
        Assert.AreEqual(0, buf[3], "pre-append framebuffer must be transparent");

        // Window-style content: a small filled box and a text run; most of the canvas untouched.
        var n1 = win.FillRect(0.1, 0.1, 0.2, 0.05, color: 4);
        win.ExecTo(n1 - 1);

        // Append MORE content afterward - the untouched pixels must stay transparent.
        var n2 = win.DrawText(0.12, 0.115, fg: 0, bg: -1, 0.025, 0.0390625, "OK"u8.ToArray());
        win.ExecTo(n2 - 1);

        win.CopyFramebufferTo(buf);
        long opaque = 0, transparent = 0, other = 0;
        for (var i = 0; i < buf.Length; i += 4)
        {
            switch (buf[i + 3])
            {
                case 255: opaque++; break;
                case 0: transparent++; break;
                default: other++; break;
            }
        }

        Assert.AreEqual(0, other, "alpha must be binary in Prodigy (hard-edge) mode");
        Assert.IsTrue(opaque > 2000, $"painted region missing ({opaque} opaque px)");
        Assert.IsTrue(transparent > W * H * 9 / 10, "untouched canvas must stay transparent");

        // Composite over a 'page' by alpha, the way a host would: the page must show
        // through every pixel the window did not paint, and the window must win where
        // it did.
        var page = new byte[W * H * 4];
        for (var i = 0; i < page.Length; i += 4) { page[i] = 1; page[i + 1] = 2; page[i + 2] = 3; page[i + 3] = 255; }

        long pageThrough = 0, windowWins = 0;
        for (var i = 0; i < buf.Length; i += 4)
        {
            if (buf[i + 3] == 0)
            {
                pageThrough++;
                Assert.AreEqual(1, page[i], "alpha-0 window pixel must leave the page pixel");
            }
            else
            {
                windowWins++;
            }
        }

        Assert.IsTrue(pageThrough > W * H * 9 / 10, "the page must show through the unpainted window");
        Assert.IsTrue(windowWins > 2000, "the window must win where it painted");

        var corner = ((H - 5) * W + 5) * 4;   // far from the box: must remain a page pixel
        Assert.AreEqual(0, buf[corner + 3], "corner should be a transparent window pixel");
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

    /// <summary>
    /// A synthesized run must never land on a paused stream: a bare ESC would consume the
    /// run's first opcode as its final byte, a DEF would take it as the macro name, an open
    /// operand list would truncate the caller's own command, and a deferred macro expansion
    /// would eat the run's leading bytes on resume. All of them reject with the same error.
    /// </summary>
    [TestMethod]
    public void SynthesizedRun_RejectsAnyDeferredTail()
    {
        // A bare ESC deferred at the frontier.
        using var esc = new NaplpsStreamSession(W, H, prodigy: true);
        esc.Append([0x0F, 0x1B]);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            esc.DrawText(0.1, 0.1, 7, -1, -1, -1, "X"u8.ToArray()));

        // An open operand list: LINE SET ABS with one of its coordinate bytes in flight.
        using var line = new NaplpsStreamSession(W, H, prodigy: true);
        line.Append([0x20]);
        line.Append([0xA8, 0xC1]);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            line.FillRect(0.1, 0.1, 0.05, 0.05, 6));

        // A deferred macro expansion: the tail lives in the injection queue while zero
        // real bytes are pending - the gate must see it there too. Designate the macro
        // set into G3 and lock-shift it, DEF MACRO '!' whose body is a bare REPEAT
        // opcode, END, then invoke '!' as the last byte of the append: the spliced
        // REPEAT defers awaiting its count while the pending buffer is empty.
        using var spliced = new NaplpsStreamSession(W, H, prodigy: false);
        spliced.Append([0x1B, 0x2F, 0x7A, 0x1B, 0x6F, 0x80, 0x21, 0x86, 0x85, 0x21]);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            spliced.DrawText(0.1, 0.1, 7, -1, -1, -1, "X"u8.ToArray()));
    }

    /// <summary>
    /// Before the system type is established, a synthesized run's bytes would join the
    /// held header and pollute detection (an A1 mid-marker plus the run's bytes locks the
    /// wrong system type forever). It must reject, not participate.
    /// </summary>
    [TestMethod]
    public void SynthesizedRun_RejectsAnUnestablishedSession()
    {
        using var session = new NaplpsStreamSession(W, H, prodigy: false);
        session.Append([0xA1]);   // half a Prodigy marker: detection undecided

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            session.FillRect(0.1, 0.1, 0.05, 0.05, 6));

        // The stream then resolves normally: the run did not poison detection.
        session.Append([0xC8, 0xC0, 0xC0, 0xC9, 0x20]);
        Assert.AreEqual(NaplpsSystemType.Prodigy, session.Format!.SystemType);
    }

    /// <summary>Flushing an intentionally-empty auto-detect session establishes it
    /// (generic NAPLPS), so synthesized runs work on a session with no wire bytes.</summary>
    [TestMethod]
    public void Flush_EstablishesAnEmptySession_ForSynthesizedRuns()
    {
        using var session = new NaplpsStreamSession(W, H, prodigy: false);
        session.Flush();

        Assert.IsNotNull(session.Format);
        Assert.AreEqual(NaplpsSystemType.NAPLPS, session.Format!.SystemType);

        var count = session.DrawText(0.1, 0.1, 7, -1, -1, -1, "X"u8.ToArray());
        Assert.IsTrue(count > 0, "a run on the established empty session must append");
    }

    /// <summary>A caller-pending SS2/SS3 is parked across a synthesized run and restored
    /// for the caller's next byte, instead of resolving the run's opcode through G2/G3.</summary>
    [TestMethod]
    public void SynthesizedRun_ParksACallerPendingSingleShift()
    {
        using var session = new NaplpsStreamSession(W, H, prodigy: true);
        session.Append([0x20, 0x19]);   // a complete command, then SS2 pending for the NEXT byte
        session.Flush();

        var shiftBefore = session.Format!.State.PendingSingleShift;
        Assert.IsNotNull(shiftBefore, "SS2 should leave a pending single shift");

        var count = session.DrawText(0.1, 0.1, 7, -1, -1, -1, "X"u8.ToArray());
        session.ExecTo(count - 1);

        Assert.AreEqual(shiftBefore, session.Format!.State.PendingSingleShift,
            "the caller's pending single shift must survive the synthesized run");
    }

    /// <summary>exec_to before anything is paintable is a status, not an error: the session
    /// reports -1 (nothing painted), which the ABI shim maps to the -4 status.</summary>
    [TestMethod]
    public void ExecTo_NothingPaintedYet_IsAStatusNotAnError()
    {
        using var session = new NaplpsStreamSession(W, H, prodigy: true);
        session.Append([0xA4, 0xC0]);   // a deferred partial command: zero complete commands

        Assert.AreEqual(-1, session.ExecTo(5), "nothing painted yet must report the status, not throw");
    }

    /// <summary>NaN sizes are neither a valid size nor the negative keep-current sentinel;
    /// they must be rejected, not silently treated as "keep".</summary>
    [TestMethod]
    public void DrawText_RejectsNaNSizes()
    {
        using var session = new NaplpsStreamSession(W, H, prodigy: true);
        session.Append([0x20]);
        session.Flush();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            session.DrawText(0.1, 0.1, 7, -1, double.NaN, 0.0390625, "X"u8.ToArray()));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            session.DrawText(0.1, 0.1, 7, -1, 0.025, double.NaN, "X"u8.ToArray()));
    }

    /// <summary>An append rejected in argument validation must leave the session unchanged
    /// (bytes, counts, pixels) and the session usable afterwards.</summary>
    [TestMethod]
    public void Append_RejectedArguments_LeaveTheSessionUntouched()
    {
        using var session = new NaplpsStreamSession(W, H, prodigy: true);
        var good = System.IO.File.ReadAllBytes(Example("MM01.NAP"));
        session.Append(good);
        session.Flush();
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

    /// <summary>stroke_rect is the hairline sibling of fill_rect: a one-pel RECTANGLE SET
    /// OUTLINED whose perimeter paints and whose interior does not - even when the page
    /// stream left a fill pattern active, since the run emits its own solid TEXTURE.</summary>
    [TestMethod]
    public void StrokeRect_PaintsHairlineOutline_InteriorUntouched()
    {
        using var session = new NaplpsStreamSession(W, H, prodigy: true);
        var (top, tops) = NaplpsCommandBuilder.BuildTexture(0, false, 1);
        session.Append([top, .. tops]);
        session.Flush();   // the page stream is over; release its operand-terminated tail

        var count = session.StrokeRect(0.25, 0.25, 0.5, 0.375, color: 6);
        session.ExecTo(count - 1);

        var cmds = session.Format!.Commands;
        Assert.IsInstanceOfType<RectangleSetOutlinedCommand>(cmds[^1].Command);

        var buf = new byte[W * H * 4];
        session.CopyFramebufferTo(buf);

        static bool IsGreen(byte[] b, int x, int y) =>
            b[((H - 1 - y) * W + x) * 4 + 1] > 100 && b[((H - 1 - y) * W + x) * 4] < 100;

        // A point on the left edge paints; the rectangle's center does not.
        var x0 = (int)(0.25 * W);
        var midY = (int)((0.25 + 0.375 / 2) * H);
        Assert.IsTrue(
            IsGreen(buf, x0, midY) || IsGreen(buf, x0 + 1, midY) || IsGreen(buf, x0 - 1, midY),
            "the outline's left edge should paint");
        Assert.IsFalse(IsGreen(buf, W / 2, midY), "the interior must stay unpainted");

        // The hairline is thin: the painted area is a small fraction of the filled area.
        long green = 0;
        for (var i = 0; i < buf.Length; i += 4)
        {
            if (buf[i + 1] > 100 && buf[i] < 100) { green++; }
        }

        var filledArea = (long)(0.5 * W) * (long)(0.375 * H);
        Assert.IsTrue(green > 100, $"outline missing ({green} green px)");
        Assert.IsTrue(green < filledArea / 4, $"outline too thick to be a hairline ({green} green px)");
    }

    /// <summary>The session must detect the system type with the file path's rules,
    /// incrementally: the A1 C8 Prodigy marker counts even behind leading CAN/NSR
    /// sentinels, and a lone A1 stays undecided until its partner byte arrives.</summary>
    [TestMethod]
    public void SystemType_DetectedIncrementally_ThroughSentinels()
    {
        // Sentinel-prefixed Prodigy marker fed one byte at a time: CAN, NSR, then A1 C8.
        using var prodigy = new NaplpsStreamSession(W, H, prodigy: false);
        foreach (var b in new byte[] { 0x18, 0x1F, 0xA1 })
        {
            prodigy.Append([b]);
        }

        Assert.IsNull(prodigy.Format, "a lone A1 must stay undecided");

        prodigy.Append([0xC8, 0xC0, 0xC0, 0xC9, 0x20]);
        Assert.IsNotNull(prodigy.Format);
        Assert.AreEqual(NaplpsSystemType.Prodigy, prodigy.Format!.SystemType,
            "the sentinel-prefixed A1 C8 marker must establish Prodigy, as the file path does");

        // A stream whose second byte rules the marker out locks generic NAPLPS.
        using var generic = new NaplpsStreamSession(W, H, prodigy: false);
        generic.Append([0xA1, 0xA3]);
        Assert.IsNotNull(generic.Format);
        Assert.AreEqual(NaplpsSystemType.NAPLPS, generic.Format!.SystemType);
    }
}
