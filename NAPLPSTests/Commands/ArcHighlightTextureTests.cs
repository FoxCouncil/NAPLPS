// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using System.Numerics;
using NAPLPS.Drawing;

namespace NAPLPSTests.Commands;

/// <summary>
/// Highlight is a FILL attribute: it asks for a filled area's boundary to be drawn, and that
/// boundary is solid regardless of the current line texture. An UNFILLED arc has no area to
/// highlight, so the bit says nothing about how its stroke is drawn and the line texture
/// governs as it would for any other stroke.
///
/// Treating the bit as "solid stroke" unconditionally turns dotted decorative arcs into solid
/// rings - see the outermost ring of the china illustration in place-settings.nap.
/// </summary>
[TestClass]
public class ArcHighlightTextureTests
{
    private static readonly SixLabors.ImageSharp.Size Frame = new(640, 480);

    // Prodigy CLUT: 2 = grey (85,85,85), 7 = white.
    private const byte FillIndex = 2;
    private const byte StrokeIndex = 7;

    private static void Emit(List<byte> bytes, byte opcode, NaplpsOperands operands)
    {
        bytes.Add(opcode);

        for (var i = 0; i < operands.Count; i++)
        {
            bytes.Add(operands[i]);
        }
    }

    /// <summary>
    /// An arc whose first vertex is absolute and whose other two are displacements: a dome
    /// from (0.20,0.15) through (0.50,0.60) to (0.80,0.15).
    /// </summary>
    private static byte[] ArcStream(byte opcode, byte lineTexture, bool highlight, byte foreground, byte background)
    {
        var bytes = new List<byte>
        {
            // DOMAIN: single-byte value 1, multi-byte value 3, two dimensions.
            0xA1,
            NaplpsEncoder.EncodeDomainFixedByte(1, 3, 2),
            0xC0,
            0xC0,
            0xC9,
        };

        Emit(bytes, 0xBE, NaplpsEncoder.EncodeSelectColorForegroundBackground(foreground, background));
        Emit(bytes, 0xA3, new NaplpsOperands([NaplpsEncoder.EncodeTextureFixedByte(lineTexture, highlight, 0)]));
        Emit(bytes, opcode, NaplpsEncoder.EncodeVertices2D(
        [
            new Vector3(0.20f, 0.15f, 0),
            new Vector3(0.30f, 0.45f, 0),
            new Vector3(0.30f, -0.45f, 0),
        ]));

        return bytes.ToArray();
    }

    /// <summary>Counts pure white pixels, which only the stroke or the highlight outline can produce.</summary>
    private static int CountWhite(byte[] stream)
    {
        var fmt = NaplpsFormat.FromBytes(stream, NaplpsSystemType.Prodigy);
        using var ctx = new DrawContext(fmt, Frame) { AuthenticGeometry = true };
        ctx.Render();

        var white = 0;

        for (var y = 0; y < ctx.Image.Height; y++)
        {
            for (var x = 0; x < ctx.Image.Width; x++)
            {
                var p = ctx.Image[x, y];

                if (p.R == 255 && p.G == 255 && p.B == 255)
                {
                    white++;
                }
            }
        }

        return white;
    }

    // ARC SET OUTLINED: unfilled, so the highlight bit has no area to act on.
    private const byte ArcSetOutlined = 0xAE;

    // ARC SET FILLED: the highlight bit asks for the filled area to be outlined.
    private const byte ArcSetFilled = 0xAF;

    [TestMethod]
    public void UnfilledArc_WithHighlightBitSet_StillHonorsDottedTexture()
    {
        var solid = CountWhite(ArcStream(ArcSetOutlined, (byte)NaplpsTexture.LineTextures.Solid, true, StrokeIndex, 0));
        var dotted = CountWhite(ArcStream(ArcSetOutlined, (byte)NaplpsTexture.LineTextures.Dotted, true, StrokeIndex, 0));

        Assert.IsTrue(solid > 500, $"the solid arc should be a full stroke (white={solid})");

        // Forcing solid on the highlight bit makes these two identical.
        Assert.IsTrue(dotted < solid * 3 / 4, $"the dotted arc must have gaps (solid={solid} dotted={dotted})");
        Assert.IsTrue(dotted > solid / 4, $"the dotted arc must keep its dots (solid={solid} dotted={dotted})");
    }

    [TestMethod]
    public void FilledArc_HighlightOutline_StaysSolidRegardlessOfTexture()
    {
        // Color mode 2 so the highlight outline takes the background color (white) and is
        // separable from the grey fill.
        var solid = CountWhite(ArcStream(ArcSetFilled, (byte)NaplpsTexture.LineTextures.Solid, true, FillIndex, StrokeIndex));
        var dotted = CountWhite(ArcStream(ArcSetFilled, (byte)NaplpsTexture.LineTextures.Dotted, true, FillIndex, StrokeIndex));

        Assert.IsTrue(solid > 500, $"the highlight outline should be drawn (white={solid})");
        Assert.AreEqual(solid, dotted, "a filled area's highlight outline is solid whatever the line texture");
    }
}
