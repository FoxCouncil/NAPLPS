// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using System.Numerics;
using NAPLPS.Drawing;

namespace NAPLPSTests.Commands;

/// <summary>
/// A highlight-filled area is outlined with the background color when color mode 2 is in
/// force, and with black otherwise. The mode that governs is the one selected when the
/// command was decoded - not the one the stream happens to end in. Every command carries its
/// own <see cref="GeometricDrawingCommandBase.ColorMode"/> snapshot for exactly this reason;
/// reading the shared live parse state instead makes the whole file take its outline color
/// from the last SELECT COLOR in it.
/// </summary>
[TestClass]
public class ArcHighlightOutlineTests
{
    private static readonly SixLabors.ImageSharp.Size Frame = new(640, 480);

    // Prodigy CLUT: 2 = grey (0x555555), 4 = light grey (0xAAAAAA), 7 = white.
    private const byte FillIndex = 2;
    private const byte BackdropIndex = 4;
    private const byte HighlightIndex = 7;

    private static void Emit(List<byte> bytes, byte opcode, NaplpsOperands operands)
    {
        bytes.Add(opcode);

        for (var i = 0; i < operands.Count; i++)
        {
            bytes.Add(operands[i]);
        }
    }

    private static void EmitTexture(List<byte> bytes, bool highlight)
    {
        Emit(bytes, 0xA3, new NaplpsOperands([NaplpsEncoder.EncodeTextureFixedByte(0, highlight, 0)]));
    }

    /// <summary>
    /// ARC SET FILLED at the given horizontal offset. The first vertex is absolute, the other
    /// two are displacements, so the arc is a dome whose chord runs along the bottom.
    /// </summary>
    private static void EmitArc(List<byte> bytes, float x)
    {
        Emit(bytes, 0xAF, NaplpsEncoder.EncodeVertices2D(
        [
            new Vector3(x, 0.18f, 0),
            new Vector3(0.17f, 0.30f, 0),
            new Vector3(0.17f, -0.30f, 0),
        ]));
    }

    /// <summary>
    /// Two identical highlight-filled arcs on a light-grey backdrop. The left one is drawn
    /// under color mode 2 (foreground grey, background white); the right one under color
    /// mode 1, which selects a foreground only. The stream therefore ENDS in mode 1 while the
    /// left arc's own mode is 2, so the two candidate readings disagree on the left arc.
    /// </summary>
    private static byte[] TwoArcStream()
    {
        var bytes = new List<byte>();

        // DOMAIN: single-byte value 1, multi-byte value 3, two dimensions.
        bytes.Add(0xA1);
        bytes.Add(NaplpsEncoder.EncodeDomainFixedByte(1, 3, 2));
        bytes.Add(0xC0);
        bytes.Add(0xC0);
        bytes.Add(0xC9);

        // Backdrop, so that white, black and the grey fill are all distinguishable.
        Emit(bytes, 0xBE, NaplpsEncoder.EncodeSelectColorForegroundBackground(BackdropIndex, 0));
        EmitTexture(bytes, highlight: false);
        Emit(bytes, 0xB3, NaplpsEncoder.EncodeVertices2D([new Vector3(0.02f, 0.02f, 0), new Vector3(0.96f, 0.74f, 0)]));

        Emit(bytes, 0xBE, NaplpsEncoder.EncodeSelectColorForegroundBackground(FillIndex, HighlightIndex));
        EmitTexture(bytes, highlight: true);
        EmitArc(bytes, 0.08f);

        Emit(bytes, 0xBE, NaplpsEncoder.EncodeSelectColorForeground(FillIndex));
        EmitTexture(bytes, highlight: true);
        EmitArc(bytes, 0.55f);

        return bytes.ToArray();
    }

    private static (int left, int right) CountColorByHalf(byte[] stream, System.Drawing.Color color)
    {
        var fmt = NaplpsFormat.FromBytes(stream, NaplpsSystemType.Prodigy);
        using var ctx = new DrawContext(fmt, Frame);
        ctx.Render();

        int left = 0, right = 0;

        for (var y = 0; y < ctx.Image.Height; y++)
        {
            for (var x = 0; x < ctx.Image.Width; x++)
            {
                var p = ctx.Image[x, y];

                if (p.R != color.R || p.G != color.G || p.B != color.B)
                {
                    continue;
                }

                if (x < ctx.Image.Width / 2)
                {
                    left++;
                }
                else
                {
                    right++;
                }
            }
        }

        return (left, right);
    }

    [TestMethod]
    public void HighlightFilledArc_InColorMode2_OutlinesWithItsOwnBackgroundColor()
    {
        var (left, right) = CountColorByHalf(TwoArcStream(), Color.White);

        // Reading the shared live parse state yields the mode the stream ended in - 1 - so the
        // left arc would be outlined black and there would be no white anywhere.
        Assert.IsTrue(left > 500, $"the mode-2 arc must be outlined in its background color (white px left={left})");
        Assert.AreEqual(0, right, "the mode-1 arc must not take a background-colored outline");
    }

    [TestMethod]
    public void HighlightFilledArc_InColorMode1_OutlinesBlack()
    {
        var stream = TwoArcStream();
        var (_, blackRight) = CountColorByHalf(stream, Color.Black);

        // The backdrop leaves a black margin down both edges of the frame; the mode-1 arc adds
        // its own black outline on the right-hand side.
        var blackOnBackdropOnly = CountColorByHalf(BackdropOnlyStream(), Color.Black).right;

        Assert.IsTrue(
            blackRight > blackOnBackdropOnly + 500,
            $"the mode-1 arc must be outlined black (right black px {blackRight} vs backdrop-only {blackOnBackdropOnly})");
    }

    [TestMethod]
    public void FilledArcWithoutHighlight_TakesNoBackgroundColoredOutline()
    {
        var bytes = new List<byte>();

        bytes.Add(0xA1);
        bytes.Add(NaplpsEncoder.EncodeDomainFixedByte(1, 3, 2));
        bytes.Add(0xC0);
        bytes.Add(0xC0);
        bytes.Add(0xC9);

        Emit(bytes, 0xBE, NaplpsEncoder.EncodeSelectColorForegroundBackground(BackdropIndex, 0));
        EmitTexture(bytes, highlight: false);
        Emit(bytes, 0xB3, NaplpsEncoder.EncodeVertices2D([new Vector3(0.02f, 0.02f, 0), new Vector3(0.96f, 0.74f, 0)]));

        Emit(bytes, 0xBE, NaplpsEncoder.EncodeSelectColorForegroundBackground(FillIndex, HighlightIndex));
        EmitTexture(bytes, highlight: false);
        EmitArc(bytes, 0.08f);

        var (left, right) = CountColorByHalf(bytes.ToArray(), Color.White);

        Assert.AreEqual(0, left + right, "a plain filled arc has no highlight outline to color");
    }

    private static byte[] BackdropOnlyStream()
    {
        var bytes = new List<byte>();

        bytes.Add(0xA1);
        bytes.Add(NaplpsEncoder.EncodeDomainFixedByte(1, 3, 2));
        bytes.Add(0xC0);
        bytes.Add(0xC0);
        bytes.Add(0xC9);

        Emit(bytes, 0xBE, NaplpsEncoder.EncodeSelectColorForegroundBackground(BackdropIndex, 0));
        EmitTexture(bytes, highlight: false);
        Emit(bytes, 0xB3, NaplpsEncoder.EncodeVertices2D([new Vector3(0.02f, 0.02f, 0), new Vector3(0.96f, 0.74f, 0)]));

        return bytes.ToArray();
    }
}
