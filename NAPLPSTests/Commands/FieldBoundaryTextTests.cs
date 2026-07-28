// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSTests.Commands;

/// <summary>
/// Tests for the interaction between FIELD commands and text cursor advancement.
///
/// BACKGROUND — THE BUG THIS PREVENTS:
///
/// NAPLPS uses "sign-and-fraction" encoding for coordinates. The first bit is a sign bit,
/// and the remaining bits represent a binary fraction. Two's complement means:
///   - sign=0, fraction=0.75 → +0.75
///   - sign=1, fraction=0.0  → -1.0  (computed as -1 + 0)
///
/// The IncrementalFieldCommand defines a text field with an origin and dimensions.
/// In COLORBAR.NAP (a Prodigy 8-bit file), the field's X dimension operand bytes decode
/// to sign=1, fraction=0 → -1.0. This is the encoding's way of saying "full unit screen
/// extent" because +1.0 is not representable (max positive ≈ 0.999).
///
/// With Origin=(0,0) and Dimensions.X=-1.0, the old code computed:
///   fieldRight = Origin.X + Dimensions.X = 0 + (-1) = -1
///
/// After each character advanced the pen by ~0.023 (CharSize.X), the boundary check:
///   pen.X > fieldRight → 0.023 > -1 → true → WRAP!
///
/// Every single character triggered an auto-wrap (carriage return + line feed),
/// causing text to render VERTICALLY (one character per line) instead of horizontally.
///
/// FIX: IncrementalFieldCommand now takes Math.Abs() of dimensions, since field
/// dimensions are sizes, not direction vectors. -1.0 becomes 1.0 (full screen width).
/// Additionally, CheckFieldBoundary guards against zero-width fields.
/// </summary>
[TestClass]
public class FieldBoundaryTextTests
{
    /// <summary>
    /// COLORBAR.NAP is a Prodigy 8-bit file whose IncrementalFieldCommand produces
    /// Dimensions.X = -1.0. With the bug, ALL text rendered one character per line.
    /// This test loads the file and verifies text advances horizontally.
    /// </summary>
    [TestMethod]
    public void ColorbarTextRendersHorizontally()
    {
        var nap = NaplpsFormat.FromFile("examples/COLORBAR.NAP");

        // Find consecutive ASCII character commands (the "CYAN" label)
        var textChars = new List<(char Ch, float PenX, float PenY)>();

        foreach (var seq in nap.Commands)
        {
            if (seq.Command is AsciiCharCommand ascii && !ascii.IsDiscarded)
            {
                textChars.Add((ascii.AsciiCharacter, seq.State.Pen.X, seq.State.Pen.Y));

                // We only need the first word to prove the point
                if (textChars.Count >= 4)
                {
                    break;
                }
            }
        }

        // "CYAN" should be the first 4 text characters
        Assert.AreEqual(4, textChars.Count);
        Assert.AreEqual('C', textChars[0].Ch);
        Assert.AreEqual('Y', textChars[1].Ch);
        Assert.AreEqual('A', textChars[2].Ch);
        Assert.AreEqual('N', textChars[3].Ch);

        // THE KEY ASSERTION: pen.X must INCREASE between characters (horizontal text).
        // With the bug, pen.X stayed at 0.0 for every character while pen.Y decreased.
        Assert.IsTrue(textChars[1].PenX > textChars[0].PenX, $"'Y' pen.X ({textChars[1].PenX}) should be right of 'C' pen.X ({textChars[0].PenX}) — text should advance horizontally");
        Assert.IsTrue(textChars[2].PenX > textChars[1].PenX, $"'A' pen.X ({textChars[2].PenX}) should be right of 'Y' pen.X ({textChars[1].PenX})");
        Assert.IsTrue(textChars[3].PenX > textChars[2].PenX, $"'N' pen.X ({textChars[3].PenX}) should be right of 'A' pen.X ({textChars[2].PenX})");

        // All characters in "CYAN" should be on the same line (same Y position)
        Assert.AreEqual(textChars[0].PenY, textChars[1].PenY, 0.001f, "All chars in 'CYAN' should share the same Y position");
        Assert.AreEqual(textChars[0].PenY, textChars[3].PenY, 0.001f, "First and last char should share the same Y position");
    }

    /// <summary>
    /// Field dimensions keep their sign - X3.110 5.3.3.6.2 places the origin in any of the
    /// four corners via the dimension signs, so normalizing them flips the field onto the
    /// wrong side of the origin (device-verified: MVDI keeps a negative-dy field BELOW its
    /// origin). COLORBAR.NAP's field decodes with Dimensions.X = -1.0; the field extends left
    /// of the origin, and its text renders horizontally because the runs begin at or beyond
    /// the field's far edge, where the wrap does not arm (see WordWrapTests).
    /// </summary>
    [TestMethod]
    public void NegativeFieldDimensionsKeepTheirDirection()
    {
        var nap = NaplpsFormat.FromFile("examples/COLORBAR.NAP");
        var field = nap.State.Field;

        Assert.IsTrue(field.Dimensions.X < 0, $"COLORBAR's raw X dimension is negative, got {field.Dimensions.X}");
        Assert.AreEqual(field.Origin.X + field.Dimensions.X, field.Left, 1e-6f, "field must extend LEFT of its origin");
        Assert.AreEqual(field.Origin.X, field.Right, 1e-6f, "the origin is the field's right edge");
        Assert.IsTrue(field.Width > 0 && field.IsSet, "normalized accessors expose a positive extent");
    }

    /// <summary>
    /// A field with zero dimensions (no FIELD command issued) should not trigger
    /// any boundary wrapping. This is the default state.
    /// </summary>
    [TestMethod]
    public void DefaultFieldDoesNotWrap()
    {
        // maple.nap is a simple NAPLPS file that doesn't set a field.
        // If it has text, it should render without wrapping issues.
        var nap = NaplpsFormat.FromFile("examples/maple.nap");

        Assert.AreEqual(0f, nap.State.Field.Dimensions.X, "Default field should have zero X dimension");
        Assert.AreEqual(0f, nap.State.Field.Dimensions.Y, "Default field should have zero Y dimension");
    }

    /// <summary>
    /// On Prodigy, FIELD leaves the cursor on the baseline of the field's first text row,
    /// which is the field ORIGIN - not the field's top edge (X3.110 5.3.3.6.2). Measured
    /// against MVDI on the Eaasy Sabre button labels (TQ000009): with the cursor at the top
    /// edge our glyph ink landed exactly one field height above the device's, and CharSize.Y
    /// as the offset does not fit that displacement. Generic NAPLPS keeps the historical
    /// top-edge placement (icosamp is authored against it).
    /// </summary>
    [TestMethod]
    public void FieldLeavesCursorAtItsOrigin()
    {
        // Two 3-byte 2D vertices: the field origin then its size.
        var state = new NaplpsState { MultiByteValue = 3, SystemType = NaplpsSystemType.Prodigy };
        var field = new IncrementalFieldCommand(state, 0xB8, new NaplpsOperands(
        [
            0xCA, 0xD4, 0xC0,
            0xD2, 0xC6, 0xC0
        ]));

        // The two candidate cursor placements - origin, and origin plus the field height - only
        // differ if the field has a height, so the assertion below needs one.
        Assert.IsTrue(field.Dimensions.Y > 0.01f, $"test field needs a height, got {field.Dimensions.Y}");
        Assert.IsTrue(field.Origin.Y > 0.01f, $"test field needs a non-zero origin, got {field.Origin.Y}");

        Assert.AreEqual(field.Origin.X, state.Pen.X, 1e-6f, "cursor X must be the field origin");
        Assert.AreEqual(field.Origin.Y, state.Pen.Y, 1e-6f, "cursor Y must be the field origin, not its top edge");
    }

    /// <summary>
    /// The generic path keeps the historical placement: cursor at the field's top edge,
    /// computed with absolute dimensions.
    /// </summary>
    [TestMethod]
    public void FieldLeavesGenericCursorAtTopEdge()
    {
        var state = new NaplpsState { MultiByteValue = 3 };
        var field = new IncrementalFieldCommand(state, 0xB8, new NaplpsOperands(
        [
            0xCA, 0xD4, 0xC0,
            0xD2, 0xC6, 0xC0
        ]));

        Assert.AreEqual(field.Origin.X, state.Pen.X, 1e-6f, "cursor X must be the field origin");
        Assert.AreEqual(field.Origin.Y + Math.Abs(field.Dimensions.Y), state.Pen.Y, 1e-6f, "cursor Y must be the field's top edge");
    }
}
