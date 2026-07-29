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
