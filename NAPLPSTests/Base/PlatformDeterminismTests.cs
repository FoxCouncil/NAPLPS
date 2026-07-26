// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using System.Security.Cryptography;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using PointF = SixLabors.ImageSharp.PointF;
using Color = SixLabors.ImageSharp.Color;

namespace NAPLPSTests.Base;

/// <summary>
/// Guards the primitives the renderer is built on against platform-dependent output, WITHOUT
/// rendering a single corpus file. The visual baseline suite can only tell you that a finished
/// frame differs somewhere, takes minutes, and needs 78 MB of committed PNGs; these run in
/// milliseconds and name the exact operation that broke.
///
/// The hashes below were produced on Windows x64 (AVX2, Vector&lt;float&gt;.Count=8) and confirmed
/// identical on macOS 26 arm64 (NEON, Count=4). A failure here means some drawing operation has
/// become architecture-sensitive - fix that rather than re-recording the hash, or the baselines
/// stop being portable again. See issue #45.
/// </summary>
[TestClass]
public class PlatformDeterminismTests
{
    private const int W = 400;
    private const int H = 300;

    private static readonly Color Fg = Color.ParseHex("FF3040FF");

    private static string Hash(Image<Rgba32> image)
    {
        var bytes = new byte[image.Width * image.Height * 4];

        image.CopyPixelDataTo(bytes);

        return Convert.ToHexString(SHA256.HashData(bytes))[..16];
    }

    private static string Draw(bool antialias, Action<IImageProcessingContext, DrawingOptions> draw)
    {
        using var image = new Image<Rgba32>(W, H, new Rgba32(0, 0, 0, 255));

        var options = new DrawingOptions
        {
            GraphicsOptions = new GraphicsOptions { Antialias = antialias },
        };

        image.Mutate(ctx => draw(ctx, options));

        return Hash(image);
    }

    /// <summary>Fractional coordinates throughout - integer ones would hide sub-pixel disagreement.</summary>
    private static readonly PointF[] Line = [new(12.3f, 17.9f), new(377.6f, 271.2f)];

    private static readonly PointF[] ConvexQuad = [new(40.4f, 30.6f), new(350.2f, 60.9f), new(300.7f, 250.1f), new(70.3f, 200.8f)];

    /// <summary>
    /// A concave arc-shaped polygon: the exact shape class that broke portability. Built with
    /// DetMath so the geometry itself cannot be the variable under test.
    /// </summary>
    private static PointF[] ConcaveArc()
    {
        var points = new List<PointF>();

        for (int i = 0; i <= 84; i++)
        {
            double a = -2.55 + 1.96 * ((double)i / 84);

            points.Add(new PointF((float)(268.0 + 43.35 * DetMath.Cos(a)), (float)(203.35 + 43.35 * DetMath.Sin(a))));
        }

        points.Add(points[0]);

        return points.ToArray();
    }

    [TestMethod]
    public void SolidStrokeIsPortable()
    {
        Assert.AreEqual("6EB417B95476EC26", Draw(true, (c, o) => c.DrawLine(o, Pens.Solid(Fg, 3.7f), Line)));
        Assert.AreEqual("78A838202B0A3403", Draw(false, (c, o) => c.DrawLine(o, Pens.Solid(Fg, 3.7f), Line)));
    }

    [TestMethod]
    public void PatternStrokeIsPortable()
    {
        Assert.AreEqual("38EAACB8752E8895", Draw(true, (c, o) => c.DrawLine(o, new PatternPen(Fg, 3.7f, [3f, 1f]), Line)));
        Assert.AreEqual("DD56AD041BA200B4", Draw(false, (c, o) => c.DrawLine(o, new PatternPen(Fg, 3.7f, [3f, 1f]), Line)));
        Assert.AreEqual("0ACE899F0247060A", Draw(true, (c, o) => c.DrawLine(o, new PatternPen(Fg, 3.7f, [1f, 1f]), Line)));
    }

    [TestMethod]
    public void ConvexFillIsPortable()
    {
        Assert.AreEqual("5542DD5348D491C1", Draw(true, (c, o) => c.FillPolygon(o, Fg, ConvexQuad)));
        Assert.AreEqual("C2CBFE6B2D6B8B85", Draw(false, (c, o) => c.FillPolygon(o, Fg, ConvexQuad)));
    }

    [TestMethod]
    public void EllipseIsPortable()
    {
        Assert.AreEqual("B111EE78D2414C18", Draw(true, (c, o) => c.Draw(o, Pens.Solid(Fg, 2.9f), new EllipsePolygon(new PointF(200.4f, 150.6f), 120.3f))));
        Assert.AreEqual("951FEF3268EF0A29", Draw(true, (c, o) => c.Fill(o, Fg, new EllipsePolygon(new PointF(200.4f, 150.6f), 120.3f))));
        Assert.AreEqual("B8EC9C7BD39BAD51", Draw(false, (c, o) => c.Fill(o, Fg, new EllipsePolygon(new PointF(200.4f, 150.6f), 120.3f))));
    }

    [TestMethod]
    public void PatternBrushFillIsPortable()
    {
        var hatch = new bool[1, 6];

        for (int i = 0; i < 6; i++)
        {
            hatch[0, i] = i < 3;
        }

        Assert.AreEqual("50B527763F54B72F", Draw(true, (c, o) => c.FillPolygon(o, new PatternBrush(Fg, Color.ParseHex("204080FF"), hatch), ConvexQuad)));
        Assert.AreEqual("1EA092AEF0D688F9", Draw(true, (c, o) => c.FillPolygon(o, new PatternBrush(Fg, Color.Transparent, hatch), ConvexQuad)));
    }

    /// <summary>
    /// The regression that issue #45 turned on. ImageSharp's ANTI-ALIASED fill of a concave polygon
    /// is NOT portable - this test pins the observed fact so nobody has to rediscover it - while the
    /// same fill with anti-aliasing off IS portable, which is what the renderer now relies on.
    /// </summary>
    [TestMethod]
    public void ConcaveFillIsPortableOnlyWithoutAntialiasing()
    {
        var concave = ConcaveArc();

        // The portable one. DrawableArc fills through Drawable.FillOptions(), which disables
        // anti-aliasing in authentic mode, so this is the path real Prodigy rendering takes.
        Assert.AreEqual("9D9B4269EC5022AD", Draw(false, (c, o) => c.FillPolygon(o, Fg, concave)));

        // Deliberately NOT asserted against a golden value: this is the architecture-sensitive
        // path, and pinning it would just fail on whichever machine did not record it. Asserting
        // it differs from the non-antialiased result keeps the test meaningful.
        var antialiased = Draw(true, (c, o) => c.FillPolygon(o, Fg, concave));

        Assert.AreNotEqual("9D9B4269EC5022AD", antialiased);
    }

    /// <summary>
    /// Every angle the arc tessellator can ask for, hashed. Catches a DetMath regression instantly
    /// and without touching the renderer.
    /// </summary>
    [TestMethod]
    public void DetMathIsPortable()
    {
        var bytes = new List<byte>();

        for (int i = -2000; i <= 2000; i++)
        {
            double a = i * (Math.PI / 500.0);

            bytes.AddRange(BitConverter.GetBytes(DetMath.Sin(a)));
            bytes.AddRange(BitConverter.GetBytes(DetMath.Cos(a)));
            bytes.AddRange(BitConverter.GetBytes(DetMath.Atan2(a, 1.5)));
            bytes.AddRange(BitConverter.GetBytes(DetMath.Atan2(1.5, a)));
        }

        Assert.AreEqual("E9DC1C7FB8DFCCE4", Convert.ToHexString(SHA256.HashData(bytes.ToArray()))[..16]);
    }
}
