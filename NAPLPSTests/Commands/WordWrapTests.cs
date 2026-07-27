// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using System.Numerics;

namespace NAPLPSTests.Commands;

[TestClass]
public class WordWrapTests
{
    private const float CharW = 1.0f / 40.0f;      // 0.025
    private const float CharH = 5.0f / 128.0f;     // 0.0390625

    /// <summary>
    /// Defaults to Prodigy: the exact-edge check-before-draw and word retraction are
    /// device-verified MVDI behaviors and gated to Prodigy content. The legacy tests pass
    /// prodigy: false to exercise the unchanged generic-NAPLPS wrap.
    /// </summary>
    private static NaplpsState CreateFieldState(float fieldWidth = 0.5f, float fieldHeight = 0.5f, bool prodigy = true)
    {
        var state = new NaplpsState();
        state.SystemType = prodigy ? NaplpsSystemType.Prodigy : NaplpsSystemType.NAPLPS;
        state.Field = new NaplpsField(new Vector3(0, 0, 0), new Vector3(fieldWidth, fieldHeight, 0));
        state.Pen = new Vector3(0, fieldHeight, 0);
        state.CharSize = new Vector2(CharW, CharH);
        return state;
    }

    private static AsciiCharCommand Type(NaplpsState state, char c)
    {
        return new AsciiCharCommand(c, state, (byte)c, new NaplpsOperands([]));
    }

    /// <summary>
    /// Fills the current row: starts at the field's line start and places one character per
    /// column, so the NEXT character's cell pokes past the far edge and wraps. The check is
    /// made BEFORE drawing at the exact edge (device-verified): with fieldWidth 0.1 and charW
    /// 0.025 the columns are 0, 0.025, 0.05, 0.075 - four fillers fit exactly and the fifth
    /// character trips the wrap.
    /// </summary>
    private static void AdvanceToWrapThreshold(NaplpsState state, float y = 0.4f)
    {
        state.Pen = new Vector3(0f, y, 0);

        for (var i = 0; i < 4; i++)
        {
            Type(state, 'X');
            Assert.IsFalse(state.AutoWrapJustOccurred, $"filler {i} must not have wrapped");
        }
    }

    [TestMethod]
    public void WordBreakChars_AreRecognized()
    {
        Assert.IsTrue(AsciiCharCommand.IsWordBreakChar(' '));
        Assert.IsTrue(AsciiCharCommand.IsWordBreakChar('!'));
        Assert.IsTrue(AsciiCharCommand.IsWordBreakChar('-'));
        Assert.IsTrue(AsciiCharCommand.IsWordBreakChar(','));
        Assert.IsTrue(AsciiCharCommand.IsWordBreakChar('.'));
        Assert.IsTrue(AsciiCharCommand.IsWordBreakChar('/'));
        Assert.IsTrue(AsciiCharCommand.IsWordBreakChar('('));
        Assert.IsTrue(AsciiCharCommand.IsWordBreakChar(')'));
    }

    [TestMethod]
    public void WordBreakChars_LettersAreNot()
    {
        Assert.IsFalse(AsciiCharCommand.IsWordBreakChar('A'));
        Assert.IsFalse(AsciiCharCommand.IsWordBreakChar('z'));
        Assert.IsFalse(AsciiCharCommand.IsWordBreakChar('0'));
    }

    [TestMethod]
    public void AutoWrap_PenReturnsToFieldOriginX()
    {
        var state = CreateFieldState(0.1f, 0.5f);
        AdvanceToWrapThreshold(state);

        Type(state, 'X');

        // The wrapping character is placed at the field origin and the pen advances past it.
        Assert.IsTrue(state.AutoWrapJustOccurred);
        Assert.IsTrue(state.Pen.X < 0.05f, $"Expected pen X near the origin, got {state.Pen.X}");
    }

    /// <summary>
    /// On the Prodigy path every character - spaces included - breaks character-level at the
    /// exact edge and is placed at the new line start; nothing is consumed. Device-verified
    /// on the polaroid-ad capture: the wrap is a pure character-level break.
    /// </summary>
    [TestMethod]
    public void SpaceAtFarEdge_WrapsLikeAnyCharacter()
    {
        var state = CreateFieldState(0.1f, 0.5f);
        AdvanceToWrapThreshold(state);

        var cmd = Type(state, ' ');

        Assert.IsFalse(cmd.IsDiscarded, "nothing is consumed at the edge");
        Assert.IsTrue(state.AutoWrapJustOccurred, "the space must have tripped the wrap");
        Assert.AreEqual(0f, cmd.DrawPen.X, 1e-6f, "the space is placed at the new line start");
        Assert.AreEqual(0.4f - CharH, cmd.DrawPen.Y, 1e-6f, "the space sits on the next row");
        Assert.AreEqual(CharW, state.Pen.X, 1e-6f, "the pen advances past the wrapped space");
    }

    /// <summary>
    /// Generic NAPLPS keeps the legacy wrap unchanged: the boundary is tested AFTER the pen
    /// advance, with a three-character tolerance band on the Right path. With fieldWidth 0.1
    /// the threshold is pen > 0.175, so a run from the origin wraps on the eighth character.
    /// </summary>
    [TestMethod]
    public void Legacy_WrapsAtToleranceThreshold_NotExactEdge()
    {
        var state = CreateFieldState(0.1f, 0.5f, prodigy: false);
        state.Pen = new Vector3(0f, 0.4f, 0);

        for (var i = 0; i < 7; i++)
        {
            Type(state, 'X');
            Assert.IsFalse(state.AutoWrapJustOccurred, $"char {i} is inside the legacy tolerance band");
        }

        Type(state, 'X');
        Assert.IsTrue(state.AutoWrapJustOccurred, "the eighth character crosses the legacy threshold");
    }

    /// <summary>
    /// Legacy generic behavior: a space that trips the boundary is discarded only in word
    /// wrap mode.
    /// </summary>
    [TestMethod]
    public void Legacy_SpaceDiscardFollowsWordWrapMode()
    {
        foreach (var mode in new[] { false, true })
        {
            var state = CreateFieldState(0.1f, 0.5f, prodigy: false);
            state.IsWordWrapMode = mode;
            state.Pen = new Vector3(0f, 0.4f, 0);

            for (var i = 0; i < 7; i++)
            {
                Type(state, 'X');
            }

            var cmd = Type(state, ' ');

            Assert.IsTrue(state.AutoWrapJustOccurred, $"mode={mode}: the space must have tripped the wrap");
            Assert.AreEqual(mode, cmd.IsDiscarded, $"mode={mode}: discard only in word wrap mode");
        }
    }

    /// <summary>
    /// MVDI's wrap is the same REGARDLESS of the WORD WRAP mode: device probe WRAPE
    /// (explicit WORD WRAP OFF) renders identically to WRAPB (no mode control at all).
    /// </summary>
    [TestMethod]
    public void Prodigy_WrapIgnoresWordWrapMode()
    {
        foreach (var mode in new[] { false, true })
        {
            var state = CreateFieldState(0.1f, 0.5f);
            state.IsWordWrapMode = mode;
            AdvanceToWrapThreshold(state);

            var cmd = Type(state, 'X');

            Assert.IsTrue(state.AutoWrapJustOccurred, $"mode={mode}: exact-edge wrap must fire");
            Assert.AreEqual(0f, cmd.DrawPen.X, 1e-6f, $"mode={mode}: same character-level break");
        }
    }

    [TestMethod]
    public void NormalChar_NotDiscarded()
    {
        var state = CreateFieldState();
        state.IsWordWrapMode = true;

        var cmd = Type(state, 'A');

        Assert.IsFalse(cmd.IsDiscarded);
    }

    [TestMethod]
    public void CharInMiddleOfField_NoWrap()
    {
        var state = CreateFieldState();
        state.Pen = new Vector3(0.1f, 0.4f, 0); // Well within field

        float penXBefore = state.Pen.X;

        Type(state, 'A');

        // Pen should have advanced, not wrapped
        Assert.IsTrue(state.Pen.X > penXBefore);
    }

    [TestMethod]
    public void AutoWrap_PenMovesDownByInterrowSpacing()
    {
        var state = CreateFieldState(0.1f, 0.5f);
        AdvanceToWrapThreshold(state);
        float penYBefore = state.Pen.Y;

        Type(state, 'X');

        // Y should have decreased by approximately CharSize.Y * interrow multiplier
        Assert.IsTrue(state.Pen.Y < penYBefore, $"Expected pen Y to decrease, was {penYBefore}, now {state.Pen.Y}");
    }

    /// <summary>
    /// MVDI's default wrap is whole-word retraction (device probes WRAPB/WRAPC): when a
    /// character-level break at the exact edge: the character that no longer fits opens the
    /// new row, and the characters already placed stay exactly where they were drawn.
    /// Device-verified on the polaroid-ad capture ("LO" stays on the button, "OK" wraps) and
    /// probe WRAPA (an unbroken 26-character run fills each row to the exact edge).
    /// </summary>
    [TestMethod]
    public void CharacterLevelBreak_PriorCharsStayPut()
    {
        var state = CreateFieldState(0.1f, 0.5f);
        state.Pen = new Vector3(0f, 0.4f, 0);

        Type(state, 'A');                 // column 0
        Type(state, 'B');                 // column 1
        Type(state, ' ');                 // column 2
        var cmdC = Type(state, 'C');      // column 3 - the last cell that fits
        Assert.IsFalse(state.AutoWrapJustOccurred);
        Assert.AreEqual(0.075f, cmdC.DrawPen.X, 1e-6f);

        var cmdD = Type(state, 'D');      // cell would poke past the edge -> wraps alone

        Assert.IsTrue(state.AutoWrapJustOccurred, "D must have tripped the wrap");
        Assert.AreEqual(0.075f, cmdC.DrawPen.X, 1e-6f, "C stays where it was drawn");
        Assert.AreEqual(0.4f, cmdC.DrawPen.Y, 1e-6f);
        Assert.AreEqual(0f, cmdD.DrawPen.X, 1e-6f, "D opens the new row");
        Assert.AreEqual(0.4f - CharH, cmdD.DrawPen.Y, 1e-6f);
        Assert.AreEqual(CharW, state.Pen.X, 1e-6f, "the pen sits past the wrapped character");
    }

    /// <summary>
    /// Generic NAPLPS never retracts: the wrap only moves the pen, and every character stays
    /// where it was drawn.
    /// </summary>
    [TestMethod]
    public void Legacy_WordDoesNotRetract()
    {
        var state = CreateFieldState(0.1f, 0.5f, prodigy: false);
        state.Pen = new Vector3(0f, 0.4f, 0);

        Type(state, 'A');
        Type(state, 'B');
        Type(state, ' ');
        var cmdC = Type(state, 'C');
        var cmdD = Type(state, 'D');
        var cmdE = Type(state, 'E');
        var cmdF = Type(state, 'F');
        Assert.IsFalse(state.AutoWrapJustOccurred, "still inside the legacy tolerance band");

        var cmdG = Type(state, 'G');
        Assert.IsTrue(state.AutoWrapJustOccurred, "G's advance crosses the legacy threshold");

        Assert.AreEqual(0.075f, cmdC.DrawPen.X, 1e-6f, "C stays where it was drawn");
        Assert.AreEqual(0.4f, cmdC.DrawPen.Y, 1e-6f);
        Assert.AreEqual(0.125f, cmdE.DrawPen.X, 1e-6f, "E stays past the field edge, inside the tolerance");
        Assert.AreEqual(0.175f, cmdG.DrawPen.X, 1e-6f, "G draws before the wrap moves the pen");
        Assert.AreEqual(0f, state.Pen.X, 1e-6f, "the pen has wrapped to the line start");
    }

    /// <summary>
    /// A hyphen is content: at the far edge it wraps like any character that no longer fits
    /// and is never consumed.
    /// </summary>
    [TestMethod]
    public void HyphenAtLineEnd_NotDiscarded()
    {
        var state = CreateFieldState(0.1f, 0.5f);
        AdvanceToWrapThreshold(state);

        var cmd = Type(state, '-');

        Assert.IsTrue(state.AutoWrapJustOccurred, "the hyphen must still have tripped the wrap");
        Assert.IsFalse(cmd.IsDiscarded, "hyphen must never be discarded by word wrap (spec: only spaces discard)");
        Assert.AreEqual(0f, cmd.DrawPen.X, 1e-6f, "the hyphen opens the new row");
    }

    /// <summary>
    /// A run positioned past the field's far edge along the text path is not flowing in the
    /// field, so the field's auto-wrap must not break it. Regression guard for the Eaasy Sabre
    /// "Return to EAASY SABRE Main Menu" label, which MVDI draws on one line: it starts right of
    /// its field, and a run-origin-blind threshold would wrap it after two glyphs.
    /// </summary>
    [TestMethod]
    public void RunStartingRightOfField_DoesNotWrap()
    {
        var state = CreateFieldState(0.1f, 0.5f);
        state.Pen = new Vector3(0.16f, 0.4f, 0);

        for (var i = 0; i < 10; i++)
        {
            Type(state, 'X');
            Assert.IsFalse(state.AutoWrapJustOccurred, $"char {i} of an out-of-field run must not wrap");
        }

        Assert.AreEqual(0.4f, state.Pen.Y, 1e-6f, "the cursor must not have moved to another row");
    }

    /// <summary>
    /// The out-of-field exemption keys on where the RUN began, not on the cursor position of
    /// the preceding character. An explicit reposition back inside the field starts a new run
    /// and must re-arm the wrap.
    /// </summary>
    [TestMethod]
    public void RepositionBackIntoField_ReArmsWrap()
    {
        var state = CreateFieldState(0.1f, 0.5f);
        state.Pen = new Vector3(0.16f, 0.4f, 0);
        Type(state, 'X');
        Assert.IsFalse(state.AutoWrapJustOccurred);

        AdvanceToWrapThreshold(state, 0.3f);
        Type(state, 'X');

        Assert.IsTrue(state.AutoWrapJustOccurred, "a run restarted inside the field must wrap again");
    }

    /// <summary>
    /// A downward field (negative dy: origin at the TOP corner) does not arm the wrap for a
    /// run whose baseline sits at that origin - the cell pokes out above the field. Probe
    /// FLDNEG, device-verified: MVDI draws one unwrapped line.
    /// </summary>
    [TestMethod]
    public void BaselineAtTopOfDownwardField_DoesNotWrap()
    {
        var state = new NaplpsState();
        state.SystemType = NaplpsSystemType.Prodigy;
        state.Field = new NaplpsField(new Vector3(0.15f, 0.65f, 0), new Vector3(0.4f, -0.35f, 0));
        state.Pen = new Vector3(0.15f, 0.65f, 0);
        state.CharSize = new Vector2(CharW, CharH);

        for (var i = 0; i < 30; i++)
        {
            Type(state, 'X');
            Assert.IsFalse(state.AutoWrapJustOccurred, $"char {i}: baseline at the top edge must not field-wrap");
        }

        Assert.AreEqual(0.65f, state.Pen.Y, 1e-6f, "the cursor must stay on the origin row");
    }

    /// <summary>
    /// With scroll off the field is a circular window (X3.110 6.2.7.14): a wrap that would
    /// leave the field's bottom repositions the row so its cell top abuts the field top.
    /// Probe FLDPOS, device-verified: MVDI's wrapped line lands at the top of the field.
    /// </summary>
    [TestMethod]
    public void WrapFromBottomRow_ReentersAtFieldTop()
    {
        var state = CreateFieldState(0.1f, 0.3f);
        AdvanceToWrapThreshold(state, y: 0.02f);   // bottom row: the next row down would exit the field
        Type(state, 'X');

        Assert.IsTrue(state.AutoWrapJustOccurred, "the run must have wrapped");
        Assert.AreEqual(0.3f - CharH, state.Pen.Y, 1e-6f, "wrapped row's cell top must abut the field top");
        Assert.AreEqual(CharW, state.Pen.X, 1e-6f, "the wrapping character opens the new row and the pen advances past it");
    }

    /// <summary>
    /// A one-row Prodigy-style field (height == one cell) circularly wraps onto ITSELF -
    /// the overprint behavior recovered from the reception-system field RE.
    /// </summary>
    [TestMethod]
    public void OneRowField_WrapsOntoItself()
    {
        var state = CreateFieldState(0.1f, CharH);
        AdvanceToWrapThreshold(state, y: 0f);
        Type(state, 'X');

        Assert.IsTrue(state.AutoWrapJustOccurred);
        Assert.AreEqual(0f, state.Pen.Y, 1e-6f, "a one-row field wraps onto its own row (overprint)");
    }

    /// <summary>
    /// A field SHORTER than the character cell cannot wrap at all: X3.110 6.2.7.14
    /// repositions the wrapped row so the character field "lies entirely within the ...
    /// field", which is impossible there. Device-verified on the Eaasy Sabre button labels
    /// (TQ000009, fields 9/256 tall against a 10/256 cell): MVDI draws the labels straight
    /// through the far edge on one row.
    /// </summary>
    [TestMethod]
    public void FieldShorterThanCell_NeverWraps()
    {
        var state = new NaplpsState();
        state.SystemType = NaplpsSystemType.Prodigy;
        state.Field = new NaplpsField(new Vector3(0.019531f, 0.046875f, 0), new Vector3(0.183594f, 0.035156f, 0));
        state.Pen = new Vector3(0.019531f, 0.046875f, 0);
        state.CharSize = new Vector2(CharW, CharH);

        for (var i = 0; i < 10; i++)
        {
            Type(state, 'X');
            Assert.IsFalse(state.AutoWrapJustOccurred, $"char {i} must not wrap in a field shorter than the cell");
        }

        Assert.AreEqual(0.046875f, state.Pen.Y, 1e-6f, "the run must stay on its row");
    }
}
