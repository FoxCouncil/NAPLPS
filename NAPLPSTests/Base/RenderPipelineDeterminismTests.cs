// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using System.Security.Cryptography;
using NAPLPS.Drawing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NAPLPSTests.Base;

/// <summary>
/// End-to-end determinism cover for the render pipeline using streams built in-process, so it needs
/// no corpus file, no committed PNG and no 33-minute run. Each case is a handful of bytes through
/// the real parser, state machine and drawables, hashed to a single value.
///
/// The visual baseline suite still exists to catch VISUAL regressions across the whole corpus. This
/// one exists to catch PORTABILITY regressions immediately and to say which primitive broke. The
/// hashes were recorded on Windows x64 and verified identical on macOS arm64; see issue #45 and
/// <see cref="PlatformDeterminismTests"/> for the primitive-level counterpart.
/// </summary>
[TestClass]
public class RenderPipelineDeterminismTests
{
    private const int W = 640;
    private const int H = 480;

    private const byte Solid = 0;
    private const byte Dotted = 1;
    private const byte VerticalHatch = 1;

    /// <summary>
    /// POINT SET ABS then TEXTURE - the same minimal prologue ArcRenderTests uses, which is known
    /// to produce visible output. Every case below asserts an ink floor so a stream that silently
    /// stops drawing fails loudly instead of pinning the hash of a blank canvas.
    /// </summary>
    private static List<byte> Prologue(float x, float y, byte lineTexture, byte texturePattern, bool highlight)
    {
        var bytes = new List<byte> { NaplpsCommandBuilder.OpPointSetAbsolute };

        bytes.AddRange(NaplpsEncoder.EncodeVertex2D(x, y));
        bytes.Add(NaplpsCommandBuilder.OpTexture);
        bytes.Add(NaplpsEncoder.EncodeTextureFixedByte(lineTexture, highlight, texturePattern));

        return bytes;
    }

    /// <summary>Renders and returns (hash, ink) - ink guards against a case silently drawing nothing.</summary>
    private static (string Hash, int Ink) Render(byte[] stream, NaplpsSystemType systemType)
    {
        var format = NaplpsFormat.FromBytes(stream, systemType);

        using var ctx = new DrawContext(format, new SixLabors.ImageSharp.Size(W, H));

        ctx.Render();

        var bytes = new byte[W * H * 4];

        ctx.Image.CopyPixelDataTo(bytes);

        int ink = 0;

        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                var p = ctx.Image[x, y];

                if (p.R > 32 || p.G > 32 || p.B > 32)
                {
                    ink++;
                }
            }
        }

        return (Convert.ToHexString(SHA256.HashData(bytes))[..16], ink);
    }

    private static void Check(string expected, int minInk, byte[] stream, NaplpsSystemType systemType, string what)
    {
        var (hash, ink) = Render(stream, systemType);

        Assert.IsGreaterThan(minInk, ink, $"{what} drew almost nothing (ink={ink}) - the case is not exercising the renderer");

        Assert.AreEqual(expected, hash, $"{what} is no longer bit-reproducible");
    }

    /// <summary>A circle: start == end, so the displacements must cancel exactly.</summary>
    private static byte[] Circle(byte op, byte lineTexture, byte texturePattern, bool highlight = false)
    {
        var bytes = Prologue(0.3f, 0.5f, lineTexture, texturePattern, highlight);

        bytes.Add(op);
        bytes.AddRange(NaplpsEncoder.EncodeVertex2D(0.25f, 0f));
        bytes.AddRange(NaplpsEncoder.EncodeVertex2D(-0.25f, 0f));

        return bytes.ToArray();
    }

    /// <summary>A genuine arc - start, mid and end all distinct, so it takes the concave-fill path.</summary>
    private static byte[] Arc(byte op, byte lineTexture, byte texturePattern, bool highlight = false)
    {
        var bytes = Prologue(0.3f, 0.5f, lineTexture, texturePattern, highlight);

        bytes.Add(op);
        bytes.AddRange(NaplpsEncoder.EncodeVertex2D(0.25f, 0.25f));
        bytes.AddRange(NaplpsEncoder.EncodeVertex2D(0.25f, -0.125f));

        return bytes.ToArray();
    }

    [TestMethod]
    public void ProdigyArcsArePortable()
    {
        Check("6625A2422BD43FEF", 200, Arc(NaplpsCommandBuilder.OpArcOutlined, Solid, 0), NaplpsSystemType.Prodigy, "outlined solid arc");
        Check("4DC129C21C93D377", 100, Arc(NaplpsCommandBuilder.OpArcOutlined, Dotted, 0), NaplpsSystemType.Prodigy, "outlined dotted arc");
        Check("05F8EA791086A7DA", 200, Arc(NaplpsCommandBuilder.OpArcFilled, Solid, 0), NaplpsSystemType.Prodigy, "filled arc");
    }

    /// <summary>
    /// The exact shape class from issue #45: a FILLED arc, whose interior is a concave polygon.
    /// ImageSharp cannot fill that reproducibly with anti-aliasing on, so this is the case that
    /// regresses first if the arc path ever stops going through Drawable.FillOptions().
    ///
    /// Note the first hash equals the plain filled arc's: at a (0,0) logical pel GetFillBrush
    /// returns a SOLID brush whatever the texture says, so these vary the highlight bit rather
    /// than the hatch. The concave interior fill - the part that actually broke - is covered either
    /// way, and PatternBrush portability is pinned in PlatformDeterminismTests.
    /// </summary>
    [TestMethod]
    public void ProdigyFilledArcInteriorIsPortable()
    {
        Check("05F8EA791086A7DA", 200, Arc(NaplpsCommandBuilder.OpArcFilled, Solid, VerticalHatch), NaplpsSystemType.Prodigy, "filled arc, texture set");
        Check("A9ED645F049D5E08", 200, Arc(NaplpsCommandBuilder.OpArcFilled, Solid, VerticalHatch, highlight: true), NaplpsSystemType.Prodigy, "highlighted filled arc");
    }

    [TestMethod]
    public void ProdigyCirclesArePortable()
    {
        Check("CE297BF60AB258D7", 200, Circle(NaplpsCommandBuilder.OpArcOutlined, Solid, 0), NaplpsSystemType.Prodigy, "outlined circle");
        Check("729827933948C5D5", 100, Circle(NaplpsCommandBuilder.OpArcOutlined, Dotted, 0), NaplpsSystemType.Prodigy, "dotted circle");
        Check("0B01D70FA22CC969", 200, Circle(NaplpsCommandBuilder.OpArcFilled, Solid, VerticalHatch), NaplpsSystemType.Prodigy, "filled circle, texture set");
    }

    // Deliberately no Telidon cases here: a bare Telidon stream renders nothing without colour
    // setup (verified - ink=1), and a test that hashes a blank canvas pins nothing. Telidon's
    // exposure is the anti-aliased rasterizer, which PlatformDeterminismTests covers directly.

    /// <summary>
    /// Anti-aliasing must stay OFF by default. It is the one switch that can make output
    /// architecture-dependent (issue #45), so if a future change flips the default the baselines
    /// silently stop being shareable. This pins both halves: off by default, and honoured when set.
    /// </summary>
    [TestMethod]
    public void AntialiasIsOffByDefaultAndOptIn()
    {
        var stream = Arc(NaplpsCommandBuilder.OpArcFilled, Solid, 0);
        var format = NaplpsFormat.FromBytes(stream, NaplpsSystemType.Prodigy);

        using var ctx = new DrawContext(format, new SixLabors.ImageSharp.Size(W, H));

        Assert.IsFalse(ctx.Antialias, "anti-aliasing must default to off");

        var (hardHash, _) = Render(stream, NaplpsSystemType.Prodigy);

        Assert.AreEqual("05F8EA791086A7DA", hardHash, "the default render is no longer the hard-edged one");

        // Same stream with the option on must actually differ, or the switch is not wired up.
        var smoothFormat = NaplpsFormat.FromBytes(stream, NaplpsSystemType.Prodigy);

        using var smoothCtx = new DrawContext(smoothFormat, new SixLabors.ImageSharp.Size(W, H))
        {
            Antialias = true,
        };

        smoothCtx.Render();

        var smoothBytes = new byte[W * H * 4];

        smoothCtx.Image.CopyPixelDataTo(smoothBytes);

        var smoothHash = Convert.ToHexString(SHA256.HashData(smoothBytes))[..16];

        Assert.AreNotEqual(hardHash, smoothHash, "Antialias = true changed nothing - the option is not reaching the drawables");
    }
}
