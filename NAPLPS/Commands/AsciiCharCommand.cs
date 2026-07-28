// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using NAPLPS.Drawing;

namespace NAPLPS.Commands;

[AddCommand(120, "ASCII Character", "A single alphanumeric glyph from the active Primary or Supplementary set.", Category = CommandCategory.Character, DslKeyword = "char")]
public class AsciiCharCommand : NaplpsCommand
{
    /// <summary>
    /// ANSI X3.110: Special characters that allow mid-word breaking when embedded
    /// within a word (not at beginning or end) during word wrap.
    /// </summary>
    private static readonly HashSet<char> WordBreakChars = new()
    {
        '!', '"', '$', '%', '(', ')', '[', ']', '<', '>', '{', '}',
        '^', '*', '+', '-', '/', ',', '.', ':', ';', '=', '?', '_', '~'
    };

    public char AsciiCharacter { get; }

    /// <summary>
    /// True if this character was resolved from the G2 Supplementary set (via SS2 or a locking
    /// shift). Selects the MVDI supplementary glyph table for rendering. See
    /// <see cref="SupplementaryCode"/>.
    /// </summary>
    public bool IsSupplementary { get; }

    /// <summary>G2 code (0x20..0x7F) when <see cref="IsSupplementary"/>; 0 otherwise.</summary>
    public int SupplementaryCode { get; }

    /// <summary>
    /// True if this character is a non-spacing accent from the supplementary set.
    /// Non-spacing accents don't advance the cursor.
    /// </summary>
    public bool IsNonSpacing { get; }

    /// <summary>
    /// True if this character was discarded (a space that hit the field's far edge is consumed
    /// by the automatic CR-LF and never drawn).
    /// </summary>
    public bool IsDiscarded { get; }

    /// <summary>
    /// Pen position this glyph is drawn at, captured after any automatic CR-LF and before the
    /// character advance. The renderer uses this instead of the sequence's state snapshot:
    /// the snapshot is cloned before the command executes, so on the Prodigy
    /// check-before-draw path it cannot see this command's own wrap.
    /// </summary>
    public Vector3 DrawPen { get; private set; }

    /// <summary>
    /// ANSI X3.110 §5.3.2.1: when a non-spacing accent precedes this character, it is
    /// composed (overlaid) onto this glyph at the same pen position. Captured from
    /// <see cref="NaplpsState.PendingAccentChar"/> at construction; rendered by
    /// <c>DrawableAsciiChar</c> after the base glyph.
    /// </summary>
    public char? OverlayAccent { get; }

    /// <summary>
    /// G2 code (0x20..0x7F) of the accent to overlay, alongside <see cref="OverlayAccent"/>.
    /// Selects the MVDI supplementary glyph the renderer composes onto this base glyph.
    /// </summary>
    public int? OverlayAccentCode { get; }

    public AsciiCharCommand(char asciiCharacter, NaplpsState state, byte opcode, NaplpsOperands operands) : base(state, opcode, operands)
    {
        AsciiCharacter = asciiCharacter;

        // Supplementary-set origin is tagged by NaplpsState.ResolveByte for the byte just
        // resolved into this command (SS2 single-shift or LS2/LS2R locking shift).
        IsSupplementary = state.ResolvedFromSupplementary;
        SupplementaryCode = state.ResolvedSupplementaryCode;

        // Non-spacing accents are the supplementary column 0x40-0x4F (diacritical marks). They
        // leave no advance and compose onto the following spacing char. Keying on the G2 code
        // (not the Unicode char) correctly catches the ASCII-range marks grave/tilde/slash/_.
        IsNonSpacing = IsSupplementary && SupplementaryCode is >= 0x40 and <= 0x4F;

        state.AutoWrapJustOccurred = false;

        if (IsNonSpacing)
        {
            // §5.3.2.1: accent waits for the next spacing char to compose onto.
            // Pen is intentionally NOT advanced.
            state.PendingAccentChar = asciiCharacter;
            state.PendingAccentCode = SupplementaryCode;
            DrawPen = state.Pen;
        }

        if (!IsNonSpacing)
        {
            // Consume any pending accent set by the previous non-spacing char so this
            // glyph's renderer can overlay it at the same field position.
            if (state.PendingAccentChar.HasValue)
            {
                OverlayAccent = state.PendingAccentChar;
                OverlayAccentCode = state.PendingAccentCode;
                state.PendingAccentChar = null;
                state.PendingAccentCode = null;
            }

            // A cursor position this class did not leave behind means something else moved the
            // cursor (POINT SET ABSOLUTE, FIELD, an explicit CR/LF/APH), which starts a new run.
            if (!state.LastCharPen.HasValue || state.LastCharPen.Value != state.Pen)
            {
                state.TextRunOrigin = state.Pen;
            }

            // The MVDI flow is device-verified against Prodigy hardware captures only, so it
            // is gated to Prodigy content. Generic NAPLPS keeps the legacy wrap (check AFTER
            // drawing, with a tolerance band) - the corpus this library grew up on was
            // authored against renderers that demonstrably do NOT wrap at the exact edge,
            // and there is no generic-NAPLPS oracle to calibrate a change against.
            if (state.SystemType == NaplpsSystemType.Prodigy)
            {
                // MVDI tests the pending character field against the field's far edge BEFORE
                // drawing, at the exact edge, character by character. Device-verified on the
                // polaroid-ad capture: its LOOK label's 'O' ends exactly ON the edge and
                // fits, the second 'O' pokes past and wraps, and "OK" lands at the line
                // start of the circularly-repositioned row while "LO" stays behind - a pure
                // character-level break, no word retraction, regardless of the WORD WRAP
                // mode. X3.110: "if the subsequent cursor movement would cause part of the
                // character field to be ... outside the active field, then an automatic
                // <carriage return> <linefeed> is executed."
                if (FieldWrapArmed(state) && CellExceedsFarEdge(state))
                {
                    PerformAutoWrap(state);
                    state.AutoWrapJustOccurred = true;
                }

                DrawPen = state.Pen;
                MovePen(state);
            }
            else
            {
                // Legacy generic-NAPLPS wrap: the glyph draws at the current pen, the pen
                // advances, and only then is the field boundary tested - with a tolerance
                // band on the Right path to match the historical renderers' integer
                // arithmetic. In word wrap mode a space that trips the boundary is discarded.
                DrawPen = state.Pen;
                MovePen(state);

                if (LegacyCheckFieldBoundary(state))
                {
                    if (state.IsWordWrapMode && asciiCharacter == ' ')
                    {
                        IsDiscarded = true;
                    }

                    LegacyPerformAutoWrap(state);
                    state.AutoWrapJustOccurred = true;
                }
            }

            state.SyncAfterTextMove();
            state.LastCharPen = state.Pen;
        }
    }

    /// <summary>
    /// True when the active field's automatic wrap governs the character about to be placed:
    /// a field is set, the pen's row lies in the field's perpendicular band, and the current
    /// run began inside the field along the text path. Whether the character actually trips
    /// the wrap is <see cref="CellExceedsFarEdge"/>. Prodigy-only; the generic path uses
    /// <see cref="LegacyCheckFieldBoundary"/>.
    /// </summary>
    private static bool FieldWrapArmed(NaplpsState state)
    {
        // Don't check if field hasn't been explicitly set (default struct has zero dimensions)
        if (!state.Field.IsSet)
        {
            return false;
        }

        var pen = state.Pen;
        float fieldRight = state.Field.Right;
        float fieldLeft = state.Field.Left;
        float fieldBottom = state.Field.Bottom;
        float fieldTop = state.Field.Top;

        // The wrap only operates when the character cell can lie within the field on the
        // perpendicular axis: X3.110 6.2.7.14 repositions the wrapped row "such that the
        // character field so defined lies entirely within the area or field", which is
        // impossible in a field shorter than the cell. Device-verified on the Eaasy Sabre
        // button labels (TQ000009): their fields are 9/256 tall against a 10/256 cell, and
        // MVDI draws the labels straight through the far edge without wrapping - while a
        // field exactly one cell tall wraps onto its own row (overprint).
        bool cellFits = state.TextPath switch
        {
            TextPath.Right or TextPath.Left => state.Field.Height + EdgeEpsilon >= state.CharSize.Y,
            TextPath.Up or TextPath.Down => state.Field.Width + EdgeEpsilon >= state.CharSize.X,
            _ => true
        };

        if (!cellFits)
        {
            return false;
        }

        // The field's line-wrap governs text whose baseline row lies within the field's rows,
        // strict at BOTH edges. Device-verified twice: FLDNEG (a baseline AT the top of a
        // downward field, cell poking out above, does not arm the wrap) and the
        // air-france-ad capture (its LOOK label sits a third of a cell BELOW its text
        // field's bottom row and MVDI draws it straight through the far edge, while
        // polaroid-ad's label at exactly the bottom row IS governed and wraps).
        bool inPerpBand;
        switch (state.TextPath)
        {
            case TextPath.Right:
            case TextPath.Left:
                inPerpBand = pen.Y >= fieldBottom && pen.Y < fieldTop;
                break;
            case TextPath.Up:
            case TextPath.Down:
                inPerpBand = pen.X >= fieldLeft && pen.X < fieldRight;
                break;
            default:
                inPerpBand = true;
                break;
        }

        if (!inPerpBand)
        {
            return false;
        }

        // The field's wrap also only governs a run that BEGAN inside the field along the text
        // path. A run positioned past the field's far edge - the "Return to EAASY SABRE Main
        // Menu" label on TQ00030D starts right of its field and MVDI draws it on one line - is
        // not flowing in the field and must not be broken by it. This cannot be folded into the
        // post-advance test above: by the time a legitimate in-field run trips the threshold its
        // own previous cursor position is already past the far edge too, so the decision has to
        // be made from the run's origin.
        var runOrigin = state.TextRunOrigin;

        // Strict inequality: a run beginning exactly AT the far edge is outside the field's
        // columns (its first cell already pokes past the edge). Device-verified on COLORBAR,
        // whose labels start at the right edge of its leftward field and never field-wrap.
        bool startedInField = state.TextPath switch
        {
            TextPath.Right => runOrigin.X < fieldRight,
            TextPath.Left => runOrigin.X > fieldLeft,
            TextPath.Down => runOrigin.Y > fieldBottom,
            TextPath.Up => runOrigin.Y < fieldTop,
            _ => true
        };

        return startedInField;
    }

    /// <summary>
    /// Float-accumulation guard on the exact-edge test, NOT a behavioral tolerance: the device
    /// makes this comparison in integer device coordinates, where a cell ending exactly on the
    /// edge fits exactly. Our pen accumulates float advances, so "exactly on the edge" arrives
    /// with noise around 1e-7 and would flip the comparison arbitrarily. The guard sits far
    /// below one device pixel (1/640) and one encodable coordinate (2^-16), so it can never
    /// absorb an authored overshoot.
    /// </summary>
    private const float EdgeEpsilon = 1e-4f;

    /// <summary>
    /// True when the character field about to be placed at the pen pokes past the active
    /// field's far edge along the text path. Exact-edge test: device-verified on MVDI that a
    /// cell ending exactly on the edge fits and the next one wraps. The cell extends one
    /// CharSize.X right of and one CharSize.Y above the pen, so the far edge needs the cell
    /// extent on the Right and Up paths and the pen alone on Left and Down.
    /// </summary>
    private static bool CellExceedsFarEdge(NaplpsState state)
    {
        var pen = state.Pen;
        var field = state.Field;

        return state.TextPath switch
        {
            TextPath.Right => field.Width > 0 && pen.X + state.CharSize.X > field.Right + EdgeEpsilon,
            TextPath.Left => field.Width > 0 && pen.X < field.Left - EdgeEpsilon,
            TextPath.Down => field.Height > 0 && pen.Y < field.Bottom - EdgeEpsilon,
            TextPath.Up => field.Height > 0 && pen.Y + state.CharSize.Y > field.Top + EdgeEpsilon,
            _ => false
        };
    }

    /// <summary>
    /// Legacy generic-NAPLPS boundary test, evaluated AFTER the pen advance - the historical
    /// behavior, unchanged: a symmetric one-cell perpendicular band and a tolerance of three
    /// character widths on the Right path matching the historical renderers'
    /// integer-arithmetic boundary behavior, whose fixed-point pen accumulation differs from
    /// our float math. Prodigy content uses the device-verified exact-edge check-before-draw
    /// instead; its run-origin and strict-band gates are deliberately NOT applied here (they
    /// break generic content such as icosamp, whose rows are continued by explicit APDs from
    /// positions past the field's far edge and still wrap on the historical renderers).
    /// </summary>
    private static bool LegacyCheckFieldBoundary(NaplpsState state)
    {
        if (!state.Field.IsSet)
        {
            return false;
        }

        var pen = state.Pen;
        var field = state.Field;

        bool inPerpBand = state.TextPath switch
        {
            TextPath.Right or TextPath.Left =>
                pen.Y >= field.Bottom - state.CharSize.Y && pen.Y <= field.Top + state.CharSize.Y,
            TextPath.Up or TextPath.Down =>
                pen.X >= field.Left - state.CharSize.X && pen.X <= field.Right + state.CharSize.X,
            _ => true
        };

        if (!inPerpBand)
        {
            return false;
        }

        return state.TextPath switch
        {
            TextPath.Right => field.Width > 0 && pen.X > field.Right + state.CharSize.X * 3f,
            TextPath.Left => field.Width > 0 && pen.X < field.Left,
            TextPath.Down => field.Height > 0 && pen.Y < field.Bottom,
            TextPath.Up => field.Height > 0 && pen.Y > field.Top,
            _ => false
        };
    }

    /// <summary>
    /// Performs automatic carriage return + line feed. Moves the pen to the near edge of the
    /// field along the character path and advances one interrow perpendicular to it. With
    /// scroll off (the default), X3.110 6.2.7.14 makes the field a circular window: a row
    /// that would leave the field's far perpendicular edge is instead repositioned "to the
    /// opposite edge ... such that the character field so defined lies entirely within the
    /// area or field". Device-verified on MVDI: the wrapped row lands with its cell top at
    /// the field top, which for a one-row Prodigy field reduces to overprinting the same row.
    /// </summary>
    private static void PerformAutoWrap(NaplpsState state)
    {
        var pen = state.Pen;

        float interrowMultiplier = state.TextInterrowSpacing switch
        {
            TextInterrowSpacing.One => 1.0f,
            TextInterrowSpacing.FiveQuarters => 1.25f,
            TextInterrowSpacing.ThreeHalves => 1.5f,
            TextInterrowSpacing.Two => 2.0f,
            _ => 1.0f
        };

        switch (state.TextPath)
        {
            case TextPath.Right:
            {
                pen.X = state.Field.Origin.X;
                pen.Y -= state.CharSize.Y * interrowMultiplier;

                if (state.Field.Height > 0 && pen.Y < state.Field.Bottom)
                {
                    pen.Y = state.Field.Top - state.CharSize.Y;
                }
            }
            break;

            case TextPath.Left:
            {
                pen.X = state.Field.Origin.X + state.Field.Dimensions.X;
                pen.Y -= state.CharSize.Y * interrowMultiplier;

                if (state.Field.Height > 0 && pen.Y < state.Field.Bottom)
                {
                    pen.Y = state.Field.Top - state.CharSize.Y;
                }
            }
            break;

            case TextPath.Down:
            {
                pen.Y = state.Field.Top;
                pen.X += state.CharSize.X * interrowMultiplier;

                if (state.Field.Width > 0 && pen.X > state.Field.Right)
                {
                    pen.X = state.Field.Left + state.CharSize.X;
                }
            }
            break;

            case TextPath.Up:
            {
                pen.Y = state.Field.Bottom;
                pen.X += state.CharSize.X * interrowMultiplier;

                if (state.Field.Width > 0 && pen.X > state.Field.Right)
                {
                    pen.X = state.Field.Left + state.CharSize.X;
                }
            }
            break;
        }

        state.Pen = pen;
    }

    /// <summary>
    /// Legacy generic-NAPLPS automatic CR-LF, unchanged from the historical behavior: the pen
    /// returns to the field origin along the character path and advances one interrow, with
    /// no circular reposition (the circular window is device-verified on MVDI only).
    /// </summary>
    private static void LegacyPerformAutoWrap(NaplpsState state)
    {
        var pen = state.Pen;

        float interrowMultiplier = state.TextInterrowSpacing switch
        {
            TextInterrowSpacing.One => 1.0f,
            TextInterrowSpacing.FiveQuarters => 1.25f,
            TextInterrowSpacing.ThreeHalves => 1.5f,
            TextInterrowSpacing.Two => 2.0f,
            _ => 1.0f
        };

        switch (state.TextPath)
        {
            case TextPath.Right:
            {
                pen.X = state.Field.Origin.X;
                pen.Y -= state.CharSize.Y * interrowMultiplier;
            }
            break;

            case TextPath.Left:
            {
                pen.X = state.Field.Origin.X + state.Field.Dimensions.X;
                pen.Y -= state.CharSize.Y * interrowMultiplier;
            }
            break;

            case TextPath.Down:
            {
                pen.Y = state.Field.Origin.Y + state.Field.Dimensions.Y;
                pen.X += state.CharSize.X * interrowMultiplier;
            }
            break;

            case TextPath.Up:
            {
                pen.Y = state.Field.Origin.Y;
                pen.X += state.CharSize.X * interrowMultiplier;
            }
            break;
        }

        state.Pen = pen;
    }

    private void MovePen(NaplpsState state) => AdvancePen(state, AsciiCharacter);

    /// <summary>
    /// Advances the state's pen by one character cell for <paramref name="character"/>, per the
    /// active spacing mode and text path. Shared by normal character advance, the REPEAT parser
    /// handler, and render-time repeat expansion so all three stay consistent.
    /// </summary>
    public static void AdvancePen(NaplpsState state, char character)
    {
        var pen = state.Pen;

        // PP3 confirmed: Spacing=One advances by full charW (pixel-counted at 640px).
        // Spacing=Proportional uses charW * row8[class]/8 ratio.
        float advance;

        if (state.SystemType == NaplpsSystemType.Prodigy)
        {
            // MVDI supports proportional + 3 fixed horizontal spacing modes (confirmed via the
            // Prodigy authoring tool). Fixed spacing advances by the full character cell (device
            // pitch = round(CharSize.X*640): 15px@0.0234, 16px@0.0250) times the fixed
            // multiplier; proportional advances per glyph. Verified against the reference render golden
            // run pitch.
            if (state.TextSpacing == TextSpacing.Proportional)
            {
                // Exact Prodigy proportional advance (per-glyph integer metric keyed on AdvanceWidth
                // + char-size register). Replaces the _displacementTable approximation that clamped
                // size to k=6..11 and drifted.
                advance = (float)MvdiFont.ProdigyProportionalAdvanceNorm(character, state.CharSize.X);
            }
            else
            {
                float mult = state.TextSpacing switch
                {
                    TextSpacing.FiveQuarters => 1.25f,
                    TextSpacing.ThreeHalves => 1.5f,
                    _ => 1.0f
                };

                advance = state.CharSize.X * mult;
            }
        }
        else if (state.TextSpacing == TextSpacing.Proportional)
        {
            // advance = charW * displacement[row][class] / n
            // where n = floor(charW * 256), row = clamp(n, 6, 11) - 6
            advance = DrawableAsciiChar.GetProportionalDisplacement(state.CharSize.X, character);
        }
        else
        {
            float spacingMultiplier = state.TextSpacing switch
            {
                TextSpacing.FiveQuarters => 1.25f,
                TextSpacing.ThreeHalves => 1.5f,
                _ => 1.0f
            };

            advance = state.CharSize.X * spacingMultiplier;
        }

        // Vertical paths: proportional spacing advances by the same normalized per-glyph metric as
        // horizontal text, applied along Y (confirmed against the reference render - rotated
        // proportional runs pitch at the glyph's proportional advance, not the char-cell height).
        // Fixed spacing keeps the char-cell height.
        float verticalAdvance = state.TextSpacing == TextSpacing.Proportional ? advance : state.CharSize.Y;

        switch (state.TextPath)
        {
            case TextPath.Right: pen.X += advance; break;
            case TextPath.Left: pen.X -= advance; break;
            case TextPath.Up: pen.Y += verticalAdvance; break;
            case TextPath.Down: pen.Y -= verticalAdvance; break;
        }

        state.Pen = pen;
    }

    /// <summary>
    /// Returns true if this character is a valid word break point for word wrap.
    /// </summary>
    public static bool IsWordBreakChar(char c) => c == ' ' || WordBreakChars.Contains(c);

    public override string ToString()
    {
        return $"ASCII({AsciiCharacter})";
    }
}
