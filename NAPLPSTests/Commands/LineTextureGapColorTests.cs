// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using System.Numerics;
using NAPLPS.Drawing;

namespace NAPLPSTests.Commands;

/// <summary>
/// A dotted or dashed stroke's GAPS are painted with the background color under color mode 2, and
/// are left transparent under modes 0/1 - the same rule the fill path already applies to texture
/// patterns. On the device the dots and the gaps tile the stroke exactly: the two counts sum to
/// what the same geometry draws with a solid texture, with nothing left over.
///
/// Leaving the gaps transparent in mode 2 loses the second color entirely - see the arc in the
/// square above the word "Gift" in place-settings.nap, which reads as a solid cyan stroke on the
/// device and as a bare dotted blue one without this.
/// </summary>
[TestClass]
public class LineTextureGapColorTests
{
    private static readonly SixLabors.ImageSharp.Size Frame = new(640, 480);

    // Prodigy CLUT: 7 = white, 2 = grey (85,85,85). Distinct from the black canvas and each other.
    private const byte StrokeIndex = 7;
    private const byte GapIndex = 2;

    // ARC SET OUTLINED and LINE SET ABSOLUTE: two unrelated primitives, one shared stroke path.
    private const byte ArcSetOutlined = 0xAE;
    private const byte LineSetAbsolute = 0xAA;

    private static void Emit(List<byte> bytes, byte opcode, NaplpsOperands operands)
    {
        bytes.Add(opcode);

        for (var i = 0; i < operands.Count; i++)
        {
            bytes.Add(operands[i]);
        }
    }

    /// <summary>
    /// A stroke of the given primitive under the given texture. <paramref name="mode2"/> selects a
    /// foreground AND a background; otherwise only a foreground is selected, leaving color mode 1.
    /// </summary>
    private static byte[] Stream(byte opcode, byte lineTexture, bool mode2)
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

        Emit(bytes, 0xBE, mode2
            ? NaplpsEncoder.EncodeSelectColorForegroundBackground(StrokeIndex, GapIndex)
            : NaplpsEncoder.EncodeSelectColorForeground(StrokeIndex));

        Emit(bytes, 0xA3, new NaplpsOperands([NaplpsEncoder.EncodeTextureFixedByte(lineTexture, false, 0)]));

        // The arc is a dome from (0.20,0.15) through (0.50,0.60) to (0.80,0.15), given as an
        // absolute start plus two displacements. The line is a long diagonal of absolute points.
        Emit(bytes, opcode, opcode == ArcSetOutlined
            ? NaplpsEncoder.EncodeVertices2D(
            [
                new Vector3(0.20f, 0.15f, 0),
                new Vector3(0.30f, 0.45f, 0),
                new Vector3(0.30f, -0.45f, 0),
            ])
            : NaplpsEncoder.EncodeVertices2D(
            [
                new Vector3(0.10f, 0.10f, 0),
                new Vector3(0.80f, 0.65f, 0),
            ]));

        return bytes.ToArray();
    }

    /// <summary>Counts stroke-colored and gap-colored pixels in one render.</summary>
    private static (int stroke, int gap) Count(byte[] stream)
    {
        var fmt = NaplpsFormat.FromBytes(stream, NaplpsSystemType.Prodigy);
        using var ctx = new DrawContext(fmt, Frame) { AuthenticGeometry = true };
        ctx.Render();

        int stroke = 0, gap = 0;

        for (var y = 0; y < ctx.Image.Height; y++)
        {
            for (var x = 0; x < ctx.Image.Width; x++)
            {
                var p = ctx.Image[x, y];

                if (p.R == 255 && p.G == 255 && p.B == 255)
                {
                    stroke++;
                }
                else if (p.R == 85 && p.G == 85 && p.B == 85)
                {
                    gap++;
                }
            }
        }

        return (stroke, gap);
    }

    /// <summary>
    /// The dots and the gaps together have to cover exactly what the solid texture covers - no
    /// more (the gap must not spill past the stroke) and no less (it must not stop at the pel
    /// boundaries and leave the stride between them bare).
    /// </summary>
    private static void AssertGapsTileTheStroke(byte opcode, byte lineTexture)
    {
        var (solid, _) = Count(Stream(opcode, (byte)NaplpsTexture.LineTextures.Solid, mode2: true));
        var (dots, gaps) = Count(Stream(opcode, lineTexture, mode2: true));

        Assert.IsTrue(solid > 500, $"the solid stroke should cover the path (solid={solid})");
        Assert.IsTrue(dots > 0 && dots < solid, $"the textured stroke must have gaps (solid={solid} dots={dots})");

        // Without the fix this is dots + 0.
        Assert.AreEqual(solid, dots + gaps,
            $"dots and gaps must tile the stroke (solid={solid} dots={dots} gaps={gaps})");
    }

    [TestMethod]
    public void DottedArc_InColorMode2_PaintsItsGapsWithTheBackgroundColor()
    {
        AssertGapsTileTheStroke(ArcSetOutlined, (byte)NaplpsTexture.LineTextures.Dotted);
    }

    [TestMethod]
    public void DashedArc_InColorMode2_PaintsItsGapsWithTheBackgroundColor()
    {
        AssertGapsTileTheStroke(ArcSetOutlined, (byte)NaplpsTexture.LineTextures.Dashed);
    }

    /// <summary>The rule belongs to the stroke engine, not to the arc: a polyline behaves the same.</summary>
    [TestMethod]
    public void DottedPolyline_InColorMode2_PaintsItsGapsWithTheBackgroundColor()
    {
        AssertGapsTileTheStroke(LineSetAbsolute, (byte)NaplpsTexture.LineTextures.Dotted);
    }

    [TestMethod]
    public void DottedStrokes_InColorMode1_LeaveTheirGapsTransparent()
    {
        var (arcDots, arcGaps) = Count(Stream(ArcSetOutlined, (byte)NaplpsTexture.LineTextures.Dotted, mode2: false));
        var (lineDots, lineGaps) = Count(Stream(LineSetAbsolute, (byte)NaplpsTexture.LineTextures.Dotted, mode2: false));

        Assert.IsTrue(arcDots > 0, "the arc should still be stroked");
        Assert.IsTrue(lineDots > 0, "the line should still be stroked");

        // Modes 0/1 select no background, so the canvas shows through the gaps.
        Assert.AreEqual(0, arcGaps, "a mode 1 arc must not paint its gaps");
        Assert.AreEqual(0, lineGaps, "a mode 1 line must not paint its gaps");
    }
}
