// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using System.Numerics;

namespace NAPLPSTests.Commands;

[TestClass]
public class IncrementalPointTests
{
    /// <summary>
    /// A 4-pel-wide unit field with a quarter-screen pel: each row consumes 4 two-bit color
    /// specifications (8 bits), so the end-of-row flush discards the remainder of the byte
    /// in flight (X3.110 5.3.3.6.3: interpretation resumes at b6 of the next byte).
    /// </summary>
    private static NaplpsState CreateState()
    {
        var state = new NaplpsState();
        state.Field = new NaplpsField(new Vector3(0, 0, 0), new Vector3(1f, 1f, 0));
        state.LogicalPel = new Vector2(0.25f, 0.25f);
        state.DrawingPoint = new Vector3(0, 0, 0);
        state.Pen = new Vector3(0, 0, 0);
        return state;
    }

    /// <summary>
    /// Mode-0 direct color specifications interleave their bits G,R,B most-significant-
    /// first, the same convention as SET COLOR operand bits - NOT three contiguous fields.
    /// A 6-bit spec of 110100 is therefore G=11, R=10, B=00: full green, 2/3 red, no blue.
    /// Verified through the drawable itself with a render probe.
    /// </summary>
    [TestMethod]
    public void DirectColor_BitsInterleaveGrb()
    {
        var state = CreateState();
        state.ColorMode = 0;

        // Packing counter 6; spec bits 110100 packed into one string byte (b6..b1).
        var cmd = new IncrementalPointCommand(state, 0x39, new NaplpsOperands([0x40 | 6, 0x40 | 0b110100]));
        Assert.AreEqual(1, cmd.Deposits.Count);

        using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(64, 64);
        new NAPLPS.Drawing.DrawableIncrementalPoint(cmd).Draw(image, state, new SixLabors.ImageSharp.Size(64, 64));

        // The quarter-screen pel deposits at the drawing point (0,0) = bottom-left corner.
        var px = image[2, 62];
        Assert.AreEqual((170, 255, 0), (px.R, px.G, px.B), "g,r,b,g,r,b of 110100: R=10 -> 170, G=11 -> 255, B=00 -> 0");
    }

    [TestMethod]
    public void PackingCounterZero_IsNullOperation()
    {
        var state = CreateState();
        var cmd = new IncrementalPointCommand(state, 0x39, new NaplpsOperands([0x40]));

        Assert.IsFalse(cmd.IsValid);
        Assert.AreEqual(0, cmd.Deposits.Count);
    }

    [TestMethod]
    public void PackingCounterOver48_IsNullOperation()
    {
        var state = CreateState();
        var cmd = new IncrementalPointCommand(state, 0x39, new NaplpsOperands([0x40 | 49]));

        Assert.IsFalse(cmd.IsValid);
        Assert.AreEqual(0, cmd.Deposits.Count);
    }

    [TestMethod]
    public void DecodesPackedColorsWithEndOfRowByteFlush()
    {
        var state = CreateState();

        // Packing counter 2. Row 1: b1 = 000110 carries colors 00,01,10; b2 = 110000 opens
        // with color 11 which fills the row, so its remaining four bits are FLUSHED.
        // Row 2: b3 = 010101 carries 01,01,01; b4 = 001111 opens with 00 which fills the
        // row; its remainder is flushed and the data ends.
        var cmd = new IncrementalPointCommand(state, 0x39, new NaplpsOperands(
        [
            2, 0b000110, 0b110000, 0b010101, 0b001111
        ]));

        Assert.IsTrue(cmd.IsValid);
        Assert.AreEqual(8, cmd.Deposits.Count, "four pels per row, two rows");

        int[] expectedColors = [0, 1, 2, 3, 1, 1, 1, 0];
        for (var i = 0; i < 8; i++)
        {
            Assert.AreEqual(expectedColors[i], cmd.Deposits[i].ColorValue, $"deposit {i} color");
        }

        // Row 1 walks the drawing point right one pel per deposit from the field origin.
        for (var i = 0; i < 4; i++)
        {
            Assert.AreEqual(i * 0.25f, cmd.Deposits[i].X, 1e-6f, $"deposit {i} X");
            Assert.AreEqual(0f, cmd.Deposits[i].Y, 1e-6f, $"deposit {i} Y");
        }

        // Row 2 returns to the opposite X boundary and steps one signed pel height (up).
        for (var i = 4; i < 8; i++)
        {
            Assert.AreEqual((i - 4) * 0.25f, cmd.Deposits[i].X, 1e-6f, $"deposit {i} X");
            Assert.AreEqual(0.25f, cmd.Deposits[i].Y, 1e-6f, $"deposit {i} Y");
        }
    }

    /// <summary>
    /// 5.3.3.6.3 step 3: a row step that would exceed the active field in Y holds the Y
    /// value constant and scrolls the field content by -dy instead of stepping. The walk
    /// therefore keeps depositing at the held row, with one scroll event recorded per
    /// overflowing row for the renderer to apply.
    /// </summary>
    [TestMethod]
    public void YOverflow_HoldsRowAndRecordsScrollEvents()
    {
        var state = CreateState();

        // Packing counter 2, four pels per row: rows land at y = 0, .25, .5, .75; the
        // fifth and sixth rows overflow the unit field and scroll instead. Each row is
        // 8 payload bits = two string bytes (the second byte's remainder flushes).
        var rowBytes = new byte[] { 0b010101, 0b010000 };
        var operands = new List<byte> { 2 };
        for (var row = 0; row < 6; row++)
        {
            operands.AddRange(rowBytes);
        }

        var cmd = new IncrementalPointCommand(state, 0x39, new NaplpsOperands([.. operands]));

        Assert.IsTrue(cmd.IsValid);
        Assert.AreEqual(24, cmd.Deposits.Count, "four pels per row, six rows");
        CollectionAssert.AreEqual(new[] { 16, 20 }, cmd.ScrollBreaks, "one scroll before each overflowing row's first deposit");

        for (var i = 16; i < 24; i++)
        {
            Assert.AreEqual(0.75f, cmd.Deposits[i].Y, 1e-6f, $"deposit {i} must hold the last fitting row");
        }
    }

    /// <summary>
    /// The renderer applies each recorded scroll to the display image lying within the
    /// field: content drawn before the overflow (including this command's own earlier
    /// rows) shifts by -dy, and the held row deposits into the vacated strip.
    /// </summary>
    [TestMethod]
    public void YOverflow_ScrollsFieldContentInTheRender()
    {
        var state = CreateState();
        state.ColorMode = 0;

        // One pel per row (field clipped to a quarter-screen column), packing 6: each
        // string byte is one deposit and one row. Five rows against a four-row field:
        // the fifth scrolls. Row colors (g,r,b,g,r,b interleave): 111111 white,
        // 110000 (170,170,0), 000000 black.
        state.Field = new NaplpsField(new Vector3(0, 0, 0), new Vector3(0.25f, 1f, 0));

        var cmd = new IncrementalPointCommand(state, 0x39, new NaplpsOperands(
        [
            6, 0b111111, 0b110000, 0b000000, 0b000000, 0b111111
        ]));

        Assert.AreEqual(5, cmd.Deposits.Count);
        CollectionAssert.AreEqual(new[] { 4 }, cmd.ScrollBreaks);

        var size = new SixLabors.ImageSharp.Size(64, 64);
        using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(64, 64);
        new NAPLPS.Drawing.DrawableIncrementalPoint(cmd).Draw(image, state, size);

        int pelHeight = (int)MathF.Max(1f, MathF.Ceiling(0.25f / (float)NaplpsUtils.DisplayRatio * 64));

        // The held row 5 (white) deposits at the field top, over the strip the scroll vacated.
        var heldRowProbe = image[2, 1];
        Assert.AreEqual((255, 255, 255), (heldRowProbe.R, heldRowProbe.G, heldRowProbe.B), "held row must deposit at the field top after the scroll");

        // Row 4 (black) shifted down one pel from the field top strip.
        var shiftedRow4Probe = image[2, 1 + pelHeight];
        Assert.AreEqual((0, 0, 0), (shiftedRow4Probe.R, shiftedRow4Probe.G, shiftedRow4Probe.B), "row 4 must shift down one pel");

        // The scroll pushed row 1 (white, deposited at the field bottom) out of the field
        // and moved row 2 (170,170,0) into its old home. Without the scroll this probe
        // would still read row 1's white.
        var bottomRowProbe = image[2, 50];
        Assert.AreEqual((170, 170, 0), (bottomRowProbe.R, bottomRowProbe.G, bottomRowProbe.B), "row 2 must occupy row 1's old home; row 1 scrolls out of the field");
    }

    [TestMethod]
    public void TerminationSetsDrawingPointToFieldOrigin()
    {
        var state = CreateState();
        state.DrawingPoint = new Vector3(0.5f, 0.5f, 0);

        new IncrementalPointCommand(state, 0x39, new NaplpsOperands([2, 0b000110]));

        Assert.AreEqual(0f, state.DrawingPoint.X, 1e-6f);
        Assert.AreEqual(0f, state.DrawingPoint.Y, 1e-6f);
    }

    [TestMethod]
    public void FieldSyncsTheDrawingPoint()
    {
        // 5.3.3.6.2 sets the drawing point itself; INCREMENTAL POINT rasters from it, so a
        // stale drawing point would skew the whole bitmap.
        var state = new NaplpsState { MultiByteValue = 3 };
        state.DrawingPoint = new Vector3(0.9f, 0.9f, 0);

        new IncrementalFieldCommand(state, 0xB8, new NaplpsOperands(
        [
            0xCA, 0xD4, 0xC0,
            0xD2, 0xC6, 0xC0
        ]));

        Assert.AreEqual(state.Pen.X, state.DrawingPoint.X, 1e-6f, "drawing point must follow the FIELD cursor");
        Assert.AreEqual(state.Pen.Y, state.DrawingPoint.Y, 1e-6f, "drawing point must follow the FIELD cursor");
    }
}
