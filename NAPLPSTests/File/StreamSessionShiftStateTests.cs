// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSTests.File;

/// <summary>
/// A run synthesized by DrawText / FillRect must decode the same way whatever decoder state it
/// lands in, and must hand that state back unchanged.
///
/// Two ways it used to fail. Operands are recognized by a lookup in the in-use table, not by a
/// range test, so operand bytes coded 7-bit (0x40 base) while GL held a character set decoded as
/// GLYPHS - every command executed with no operands at all, which drew a run of garble and left
/// the pen at home because Point Set Absolute never received a position. And the text payload
/// carried no shift control of its own, so with GL invoked with the PDI set the text executed as
/// drawing commands and blanked the canvas.
/// </summary>
[TestClass]
public class StreamSessionShiftStateTests
{
    private const int W = 640;
    private const int H = 480;

    private const byte ShiftIn = 0x0F;
    private const byte ShiftOut = 0x0E;
    private const byte Escape = 0x1B;

    /// <summary>
    /// The run must decode as the three drawing commands WITH their operands, followed by the
    /// payload as characters. Both defects show up here: as zero-operand commands, and as
    /// character commands appearing before the payload.
    /// </summary>
    private static void AssertRunDecodedAsCommands(NaplpsStreamSession session, int firstIndex, string expected)
    {
        var run = session.Format!.Commands.Skip(firstIndex).Select(s => s.Command).ToList();
        var shape = string.Join(" ", run.Select(c => $"{c.GetType().Name}({c.Operands.Count})"));

        var point = run.OfType<PointSetAbsoluteCommand>().Single();
        var color = run.OfType<SelectColorCommand>().Single();
        var text = run.OfType<TextCommand>().Single();

        Assert.AreEqual(3, point.Operands.Count, $"Point Set Absolute lost its operands :: {shape}");
        Assert.AreNotEqual(0, color.Operands.Count, $"SELECT COLOR lost its operands :: {shape}");
        Assert.AreEqual(5, text.Operands.Count, $"TEXT lost its operands :: {shape}");

        // Every character in the run has to be payload: nothing before it, nothing extra after.
        var chars = run.OfType<AsciiCharCommand>().ToList();
        Assert.AreEqual(expected, new string([.. chars.Select(c => c.AsciiCharacter)]),
            $"the characters drawn are not the payload :: {shape}");

        var firstChar = run.FindIndex(c => c is AsciiCharCommand);
        Assert.IsTrue(firstChar > run.IndexOf(text),
            $"a character was drawn before the payload began :: {shape}");
    }

    /// <summary>
    /// The client repro. NSR is three 7-bit bytes, so the stream still looks 7-bit when the run
    /// is built, and it leaves GL invoked with the character set - the state in which 7-bit
    /// operands cannot be told from text.
    /// </summary>
    [TestMethod]
    public void DrawText_AfterA7BitStreamEndingInTextMode_DecodesAsCommandsNotGlyphs()
    {
        using var session = new NaplpsStreamSession(W, H, prodigy: true, transparentBackground: true);
        session.Append([0x1F, 0x40, 0x40]);   // NSR
        session.Flush();

        var first = session.CommandCount;
        session.DrawText(0.45, 0.35, 7, -1, 6.0 / 256.0, 0.0390625, "Retype ID and"u8.ToArray());

        AssertRunDecodedAsCommands(session, first, "Retype ID and");

        // The position command actually ran, so the pen is where it was asked for and not home.
        var point = session.Format!.Commands.Skip(first).Select(s => s.Command)
            .OfType<PointSetAbsoluteCommand>().Single();
        Assert.AreEqual(0.45, point.Points[0].X, 1.0 / 256, "text did not land at the requested x");
        Assert.AreEqual(0.35, point.Points[0].Y, 1.0 / 256, "text did not land at the requested y");
    }

    /// <summary>
    /// With GL invoked with the PDI set - what a raw SO leaves behind - the payload would execute
    /// as drawing commands. The run has to shift into text itself.
    /// </summary>
    [TestMethod]
    public void DrawText_WithGraphicLeftInvokedWithPdi_DrawsItsTextInsteadOfExecutingIt()
    {
        using var session = new NaplpsStreamSession(W, H, prodigy: true, transparentBackground: true);
        session.Append([ShiftOut]);
        session.Flush();

        var first = session.CommandCount;
        session.DrawText(0.45, 0.35, 7, -1, 6.0 / 256.0, 0.0390625, "Retype ID and"u8.ToArray());

        AssertRunDecodedAsCommands(session, first, "Retype ID and");
    }

    /// <summary>
    /// The run forces GL to draw its text, so it has to put back what it found - a caller that
    /// paints a field between two chunks of one presentation keeps its shift state.
    /// </summary>
    [TestMethod]
    public void DrawText_RestoresTheIncomingGraphicLeftInvocation()
    {
        (byte[] bytes, NaplpsState.GsetSlot slot)[] cases =
        [
            ([ShiftIn], NaplpsState.GsetSlot.G0),
            ([ShiftOut], NaplpsState.GsetSlot.G1),
            ([Escape, 0x6E], NaplpsState.GsetSlot.G2),   // LS2
            ([Escape, 0x6F], NaplpsState.GsetSlot.G3),   // LS3
        ];

        foreach (var (bytes, slot) in cases)
        {
            using var session = new NaplpsStreamSession(W, H, prodigy: true, transparentBackground: true);
            session.Append(bytes);
            session.Flush();
            Assert.AreEqual(slot, session.Format!.State.GraphicLeftInvocation, "test setup did not take");

            session.DrawText(0.45, 0.35, 7, -1, 6.0 / 256.0, 0.0390625, "OK"u8.ToArray());

            Assert.AreEqual(slot, session.Format.State.GraphicLeftInvocation,
                $"draw_text did not restore GL invoked with {slot}");
        }
    }

    /// <summary>
    /// fill_rect emits no byte in GL and reads none, so it is immune once coded 8-bit. Same
    /// 7-bit-looking prior stream that broke draw_text.
    /// </summary>
    [TestMethod]
    public void FillRect_AfterA7BitStream_KeepsItsGeometry()
    {
        using var session = new NaplpsStreamSession(W, H, prodigy: true, transparentBackground: true);
        session.Append([0x1F, 0x40, 0x40]);   // NSR
        session.Flush();

        var glBefore = session.Format!.State.GraphicLeftInvocation;
        session.FillRect(0.1, 0.1, 0.2, 0.05, color: 6);

        var rect = (RectangleSetFilledCommand)session.Format.Commands[^1].Command;
        Assert.AreEqual(6, rect.Operands.Count, "RECTANGLE SET FILLED lost its operands");
        Assert.AreEqual(0.1, rect.StartPoint.X, 1.0 / 256, "rect x");
        Assert.AreEqual(0.1, rect.StartPoint.Y, 1.0 / 256, "rect y");
        Assert.AreEqual(glBefore, session.Format.State.GraphicLeftInvocation, "fill_rect moved GL");
    }
}
