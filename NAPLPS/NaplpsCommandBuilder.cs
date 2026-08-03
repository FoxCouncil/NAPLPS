// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPS;

/// <summary>
/// High-level factory that combines NAPLPS opcodes with encoded operands
/// to produce command byte sequences suitable for AddCommand().
/// Opcodes are derived from the GeneralPDISet at offset 0xA0.
/// </summary>
public static class NaplpsCommandBuilder
{
    // PDI opcodes (GeneralPDISet base = 0xA0)
    public const byte OpReset = 0xA0;
    public const byte OpDomain = 0xA1;
    public const byte OpText = 0xA2;
    public const byte OpTexture = 0xA3;
    public const byte OpPointSetAbsolute = 0xA4;
    public const byte OpPointSetRelative = 0xA5;
    public const byte OpPointAbsolute = 0xA6;
    public const byte OpPointRelative = 0xA7;
    public const byte OpLineAbsolute = 0xA8;
    public const byte OpLineRelative = 0xA9;
    public const byte OpLineSetAbsolute = 0xAA;
    public const byte OpLineSetRelative = 0xAB;
    public const byte OpArcOutlined = 0xAC;
    public const byte OpArcFilled = 0xAD;
    public const byte OpArcSetOutlined = 0xAE;
    public const byte OpArcSetFilled = 0xAF;
    public const byte OpRectangleOutlined = 0xB0;
    public const byte OpRectangleFilled = 0xB1;
    public const byte OpRectangleSetOutlined = 0xB2;
    public const byte OpRectangleSetFilled = 0xB3;
    public const byte OpPolygonOutlined = 0xB4;
    public const byte OpPolygonFilled = 0xB5;
    public const byte OpPolygonSetOutlined = 0xB6;
    public const byte OpPolygonSetFilled = 0xB7;
    public const byte OpIncrementalField = 0xB8;
    public const byte OpIncrementalPoint = 0xB9;
    public const byte OpIncrementalLine = 0xBA;
    public const byte OpIncrementalPolygonFilled = 0xBB;
    public const byte OpSetColor = 0xBC;
    public const byte OpWait = 0xBD;
    public const byte OpSelectColor = 0xBE;
    public const byte OpBlink = 0xBF;

    // C0 control codes
    public const byte OpNonSelectiveReset = 0x1F;

    // C1 control codes (C1Set base = 0x80)
    public const byte OpDefMacro = 0x80;
    public const byte OpDefPMacro = 0x81;
    public const byte OpDefTMacro = 0x82;
    public const byte OpDefDrcs = 0x83;
    public const byte OpDefTexture = 0x84;
    public const byte OpEnd = 0x85;
    public const byte OpRepeat = 0x86;

    /// <summary>Move pen to absolute position (no draw). Creates PointSetAbsoluteCommand.</summary>
    public static (byte opcode, NaplpsOperands operands) BuildPointSetAbsolute(float x, float y, int multiByteValue = 3)
    {
        return (OpPointSetAbsolute, NaplpsEncoder.EncodeVertex2D(x, y, multiByteValue));
    }

    /// <summary>Draw line from current pen to absolute position. Creates LineAbsoluteCommand.</summary>
    public static (byte opcode, NaplpsOperands operands) BuildLineAbsolute(float x, float y, int multiByteValue = 3)
    {
        return (OpLineAbsolute, NaplpsEncoder.EncodeVertex2D(x, y, multiByteValue));
    }

    /// <summary>Draw a chained polyline through absolute positions in one LINE command (X3.110 5.3.3.2). Creates LineAbsoluteCommand.</summary>
    public static (byte opcode, NaplpsOperands operands) BuildLineAbsolute(Vector3[] points, int multiByteValue = 3)
    {
        return (OpLineAbsolute, NaplpsEncoder.EncodeVertices2D(points, multiByteValue));
    }

    /// <summary>Draw connected lines through absolute positions. Creates LineSetAbsoluteCommand.</summary>
    public static (byte opcode, NaplpsOperands operands) BuildLineSetAbsolute(Vector3[] points, int multiByteValue = 3)
    {
        return (OpLineSetAbsolute, NaplpsEncoder.EncodeVertices2D(points, multiByteValue));
    }

    /// <summary>Draw filled rectangle with given dimensions relative to pen. Creates RectangleFilledCommand.</summary>
    public static (byte opcode, NaplpsOperands operands) BuildRectangleFilled(float width, float height, int multiByteValue = 3)
    {
        return (OpRectangleFilled, NaplpsEncoder.EncodeVertex2D(width, height, multiByteValue));
    }

    /// <summary>Draw outlined rectangle with given dimensions relative to pen. Creates RectangleOutlinedCommand.</summary>
    public static (byte opcode, NaplpsOperands operands) BuildRectangleOutlined(float width, float height, int multiByteValue = 3)
    {
        return (OpRectangleOutlined, NaplpsEncoder.EncodeVertex2D(width, height, multiByteValue));
    }

    /// <summary>Draw filled polygon with relative vertices from pen. Creates PolygonFilledCommand.</summary>
    public static (byte opcode, NaplpsOperands operands) BuildPolygonFilled(Vector3[] relativeVertices, int multiByteValue = 3)
    {
        return (OpPolygonFilled, NaplpsEncoder.EncodeVertices2D(relativeVertices, multiByteValue));
    }

    /// <summary>Draw outlined polygon with relative vertices from pen. Creates PolygonOutlinedCommand.</summary>
    public static (byte opcode, NaplpsOperands operands) BuildPolygonOutlined(Vector3[] relativeVertices, int multiByteValue = 3)
    {
        return (OpPolygonOutlined, NaplpsEncoder.EncodeVertices2D(relativeVertices, multiByteValue));
    }

    /// <summary>Draw outlined arc. Operands are mid-point and end-point relative to pen.</summary>
    public static (byte opcode, NaplpsOperands operands) BuildArcOutlined(float midRelX, float midRelY, float endRelX, float endRelY, int multiByteValue = 3)
    {
        var operands = NaplpsEncoder.EncodeVertex2D(midRelX, midRelY, multiByteValue);
        var endOperands = NaplpsEncoder.EncodeVertex2D(endRelX, endRelY, multiByteValue);
        operands.AddRange(endOperands);
        return (OpArcOutlined, operands);
    }

    /// <summary>Draw filled arc. Operands are mid-point and end-point relative to pen.</summary>
    public static (byte opcode, NaplpsOperands operands) BuildArcFilled(float midRelX, float midRelY, float endRelX, float endRelY, int multiByteValue = 3)
    {
        var operands = NaplpsEncoder.EncodeVertex2D(midRelX, midRelY, multiByteValue);
        var endOperands = NaplpsEncoder.EncodeVertex2D(endRelX, endRelY, multiByteValue);
        operands.AddRange(endOperands);
        return (OpArcFilled, operands);
    }

    /// <summary>Draw an outlined CIRCLE: a SINGLE relative vertex (the diameter / through point).
    /// A 1-vertex non-set arc has end coincident with start, which the parser/renderer treat as a
    /// circle (diameter = start→vertex). Emitting two vertices would instead be a segment arc.</summary>
    public static (byte opcode, NaplpsOperands operands) BuildArcOutlinedCircle(float midRelX, float midRelY, int multiByteValue = 3)
    {
        return (OpArcOutlined, NaplpsEncoder.EncodeVertex2D(midRelX, midRelY, multiByteValue));
    }

    /// <summary>Draw a filled CIRCLE: a SINGLE relative vertex (the diameter / through point).</summary>
    public static (byte opcode, NaplpsOperands operands) BuildArcFilledCircle(float midRelX, float midRelY, int multiByteValue = 3)
    {
        return (OpArcFilled, NaplpsEncoder.EncodeVertex2D(midRelX, midRelY, multiByteValue));
    }

    /// <summary>Set foreground color by palette index (color mode 1).</summary>
    public static (byte opcode, NaplpsOperands operands) BuildSelectColor(byte fgIndex)
    {
        return (OpSelectColor, NaplpsEncoder.EncodeSelectColorForeground(fgIndex));
    }

    /// <summary>Set foreground and background colors by palette index (color mode 2).</summary>
    public static (byte opcode, NaplpsOperands operands) BuildSelectColor(byte fgIndex, byte bgIndex)
    {
        return (OpSelectColor, NaplpsEncoder.EncodeSelectColorForegroundBackground(fgIndex, bgIndex));
    }

    /// <summary>
    /// Build a TEXT command. Fixed byte 1: rotation+path+spacing. Fixed byte 2:
    /// interrow+moveAttrs+cursor. Followed by an optional multi-value vertex for
    /// character size (omit by passing charWidth or charHeight &lt; 0).
    /// </summary>
    public static (byte opcode, NaplpsOperands operands) BuildText(
        float charWidth, float charHeight,
        TextCommand.TextSpacing spacing = TextCommand.TextSpacing.One,
        TextCommand.TextPath path = TextCommand.TextPath.Right,
        TextCommand.TextRotation rotation = TextCommand.TextRotation.Zero,
        TextCommand.TextInterrowSpacing interrow = TextCommand.TextInterrowSpacing.One,
        TextCommand.TextMoveAttributes moveAttributes = TextCommand.TextMoveAttributes.MoveTogether,
        TextCommand.TextCursorStyle cursorStyle = TextCommand.TextCursorStyle.Underscore,
        int multiByteValue = 3)
    {
        var operands = new NaplpsOperands
        {
            NaplpsEncoder.EncodeTextFixedByte1((byte)rotation, (byte)path, (byte)spacing),
            NaplpsEncoder.EncodeTextFixedByte2((byte)interrow, (byte)moveAttributes, (byte)cursorStyle),
        };

        if (charWidth >= 0 && charHeight >= 0)
        {
            operands.AddRange(NaplpsEncoder.EncodeVertex2D(charWidth, charHeight, multiByteValue));
        }

        return (OpText, operands);
    }

    // ---- Point variants ----------------------------------------------------

    /// <summary>Move pen by relative offset (no draw). Creates PointSetRelativeCommand.</summary>
    public static (byte opcode, NaplpsOperands operands) BuildPointSetRelative(float dx, float dy, int multiByteValue = 3)
    {
        return (OpPointSetRelative, NaplpsEncoder.EncodeVertex2D(dx, dy, multiByteValue));
    }

    /// <summary>Plot a point at an absolute position. Creates PointAbsoluteCommand.</summary>
    public static (byte opcode, NaplpsOperands operands) BuildPointAbsolute(float x, float y, int multiByteValue = 3)
    {
        return (OpPointAbsolute, NaplpsEncoder.EncodeVertex2D(x, y, multiByteValue));
    }

    /// <summary>Plot a point at a relative offset from the pen. Creates PointRelativeCommand.</summary>
    public static (byte opcode, NaplpsOperands operands) BuildPointRelative(float dx, float dy, int multiByteValue = 3)
    {
        return (OpPointRelative, NaplpsEncoder.EncodeVertex2D(dx, dy, multiByteValue));
    }

    // ---- Line variants -----------------------------------------------------

    /// <summary>Draw a line from pen by relative offset. Creates LineRelativeCommand.</summary>
    public static (byte opcode, NaplpsOperands operands) BuildLineRelative(float dx, float dy, int multiByteValue = 3)
    {
        return (OpLineRelative, NaplpsEncoder.EncodeVertex2D(dx, dy, multiByteValue));
    }

    /// <summary>Draw a chained polyline through relative offsets in one LINE command (X3.110 5.3.3.2). Creates LineRelativeCommand.</summary>
    public static (byte opcode, NaplpsOperands operands) BuildLineRelative(Vector3[] deltas, int multiByteValue = 3)
    {
        return (OpLineRelative, NaplpsEncoder.EncodeVertices2D(deltas, multiByteValue));
    }

    /// <summary>Draw a connected polyline through relative offsets. Creates LineSetRelativeCommand.</summary>
    public static (byte opcode, NaplpsOperands operands) BuildLineSetRelative(Vector3[] relativePoints, int multiByteValue = 3)
    {
        return (OpLineSetRelative, NaplpsEncoder.EncodeVertices2D(relativePoints, multiByteValue));
    }

    // ---- Rectangle variants ------------------------------------------------

    /// <summary>Draw an outlined rectangle from an absolute origin with relative dimensions. Creates RectangleSetOutlinedCommand.</summary>
    public static (byte opcode, NaplpsOperands operands) BuildRectangleSetOutlined(float x, float y, float width, float height, int multiByteValue = 3)
    {
        var operands = NaplpsEncoder.EncodeVertex2D(x, y, multiByteValue);
        operands.AddRange(NaplpsEncoder.EncodeVertex2D(width, height, multiByteValue));
        return (OpRectangleSetOutlined, operands);
    }

    /// <summary>Draw a filled rectangle from an absolute origin with relative dimensions. Creates RectangleSetFilledCommand.</summary>
    public static (byte opcode, NaplpsOperands operands) BuildRectangleSetFilled(float x, float y, float width, float height, int multiByteValue = 3)
    {
        var operands = NaplpsEncoder.EncodeVertex2D(x, y, multiByteValue);
        operands.AddRange(NaplpsEncoder.EncodeVertex2D(width, height, multiByteValue));
        return (OpRectangleSetFilled, operands);
    }

    // ---- Polygon Set variants ---------------------------------------------

    /// <summary>Draw an outlined polygon with absolute start and relative vertices. Creates PolygonSetOutlinedCommand.</summary>
    public static (byte opcode, NaplpsOperands operands) BuildPolygonSetOutlined(Vector3 absoluteStart, Vector3[] relativeVertices, int multiByteValue = 3)
    {
        var operands = NaplpsEncoder.EncodeVertex2D(absoluteStart.X, absoluteStart.Y, multiByteValue);
        operands.AddRange(NaplpsEncoder.EncodeVertices2D(relativeVertices, multiByteValue));
        return (OpPolygonSetOutlined, operands);
    }

    /// <summary>Draw a filled polygon with absolute start and relative vertices. Creates PolygonSetFilledCommand.</summary>
    public static (byte opcode, NaplpsOperands operands) BuildPolygonSetFilled(Vector3 absoluteStart, Vector3[] relativeVertices, int multiByteValue = 3)
    {
        var operands = NaplpsEncoder.EncodeVertex2D(absoluteStart.X, absoluteStart.Y, multiByteValue);
        operands.AddRange(NaplpsEncoder.EncodeVertices2D(relativeVertices, multiByteValue));
        return (OpPolygonSetFilled, operands);
    }

    // ---- Arc Set variants --------------------------------------------------

    /// <summary>Draw an outlined arc with absolute start, relative mid and end. Creates ArcSetOutlinedCommand.</summary>
    public static (byte opcode, NaplpsOperands operands) BuildArcSetOutlined(float startX, float startY, float midRelX, float midRelY, float endRelX, float endRelY, int multiByteValue = 3)
    {
        var operands = NaplpsEncoder.EncodeVertex2D(startX, startY, multiByteValue);
        operands.AddRange(NaplpsEncoder.EncodeVertex2D(midRelX, midRelY, multiByteValue));
        operands.AddRange(NaplpsEncoder.EncodeVertex2D(endRelX, endRelY, multiByteValue));
        return (OpArcSetOutlined, operands);
    }

    /// <summary>Draw a filled arc with absolute start, relative mid and end. Creates ArcSetFilledCommand.</summary>
    public static (byte opcode, NaplpsOperands operands) BuildArcSetFilled(float startX, float startY, float midRelX, float midRelY, float endRelX, float endRelY, int multiByteValue = 3)
    {
        var operands = NaplpsEncoder.EncodeVertex2D(startX, startY, multiByteValue);
        operands.AddRange(NaplpsEncoder.EncodeVertex2D(midRelX, midRelY, multiByteValue));
        operands.AddRange(NaplpsEncoder.EncodeVertex2D(endRelX, endRelY, multiByteValue));
        return (OpArcSetFilled, operands);
    }

    // ---- Attribute / state -------------------------------------------------

    /// <summary>
    /// Build a DOMAIN command. singleByteValue 1-4, multiByteValue 1-8, dimensionality 2 or 3.
    /// Optionally followed by a logical pel size vertex.
    /// </summary>
    public static (byte opcode, NaplpsOperands operands) BuildDomain(byte singleByteValue, byte multiByteValue, byte dimensionality, Vector3? logicalPel = null)
    {
        var operands = new NaplpsOperands
        {
            NaplpsEncoder.EncodeDomainFixedByte(singleByteValue, multiByteValue, dimensionality),
        };

        if (logicalPel.HasValue)
        {
            operands.AddRange(NaplpsEncoder.EncodeVertex2D(logicalPel.Value.X, logicalPel.Value.Y, multiByteValue));
        }

        return (OpDomain, operands);
    }

    /// <summary>
    /// Build a TEXTURE command. linePattern 0-3 (solid, dashed, etc.), highlight outlines filled
    /// objects, fillPattern 0-7 selects pattern A-H. Optionally followed by mask size vertex.
    /// </summary>
    public static (byte opcode, NaplpsOperands operands) BuildTexture(byte linePattern, bool highlight, byte fillPattern, Vector3? maskSize = null, int multiByteValue = 3)
    {
        var operands = new NaplpsOperands
        {
            NaplpsEncoder.EncodeTextureFixedByte(linePattern, highlight, fillPattern),
        };

        if (maskSize.HasValue)
        {
            operands.AddRange(NaplpsEncoder.EncodeVertex2D(maskSize.Value.X, maskSize.Value.Y, multiByteValue));
        }

        return (OpTexture, operands);
    }

    /// <summary>
    /// Build a SET COLOR command for color mode 0 (RGB direct).
    /// Color is encoded as N triplets of (G,R,B) bits packed across operand bytes.
    /// One byte → 6 bits per component max; two bytes → 12 bits, etc.
    /// </summary>
    public static (byte opcode, NaplpsOperands operands) BuildSetColorRgb(byte g, byte r, byte b, int byteCount = 1)
    {
        if (byteCount < 1 || byteCount > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(byteCount), "Must be 1-4.");
        }

        // Per SetColorCommand.ParseColorComponents: each operand byte contributes
        // 2 triplets of (G,R,B) — bits (5,4,3) and (2,1,0) in 0-indexed positions.
        // Total bits per component = byteCount * 2.
        int bitsPerComponent = byteCount * 2;
        int maxValue = (1 << bitsPerComponent) - 1;

        // Scale 8-bit input down to bitsPerComponent
        int gv = g * maxValue / 255;
        int rv = r * maxValue / 255;
        int bv = b * maxValue / 255;

        var operands = new NaplpsOperands();

        for (int byteIndex = 0; byteIndex < byteCount; byteIndex++)
        {
            // Pull two triplets out of each component, MSB first, two at a time.
            int shiftHi = bitsPerComponent - 1 - (byteIndex * 2);
            int shiftLo = bitsPerComponent - 2 - (byteIndex * 2);

            int gHi = (gv >> shiftHi) & 1;
            int gLo = (gv >> shiftLo) & 1;
            int rHi = (rv >> shiftHi) & 1;
            int rLo = (rv >> shiftLo) & 1;
            int bHi = (bv >> shiftHi) & 1;
            int bLo = (bv >> shiftLo) & 1;

            byte data = 0;
            data |= (byte)(gHi << 5);
            data |= (byte)(rHi << 4);
            data |= (byte)(bHi << 3);
            data |= (byte)(gLo << 2);
            data |= (byte)(rLo << 1);
            data |= (byte)(bLo << 0);

            operands.Add((byte)(0xC0 | data));
        }

        return (OpSetColor, operands);
    }

    /// <summary>Build SET COLOR with no operands — sets transparency in color mode 0.</summary>
    public static (byte opcode, NaplpsOperands operands) BuildSetColorTransparent()
    {
        return (OpSetColor, new NaplpsOperands());
    }

    /// <summary>
    /// Build a WAIT command. The spec mandates the first operand byte be exactly 0x5C.
    /// Subsequent bytes carry the wait interval(s) in tenths of a second (0-63 per byte).
    /// </summary>
    public static (byte opcode, NaplpsOperands operands) BuildWait(params byte[] tenthsOfSeconds)
    {
        if (tenthsOfSeconds.Length == 0)
        {
            throw new ArgumentException("Wait requires at least one interval byte.", nameof(tenthsOfSeconds));
        }

        // Spec: first operand is the literal 0x5C fixed-format byte (NOT 0xC0|x).
        var operands = new NaplpsOperands { 0x5C };

        foreach (var t in tenthsOfSeconds)
        {
            operands.Add(NaplpsEncoder.EncodeIntervalByte(t));
        }

        return (OpWait, operands);
    }

    /// <summary>
    /// Build a RESET command with explicit byte 1 / byte 2 flags. See ResetCommand for semantics.
    /// </summary>
    public static (byte opcode, NaplpsOperands operands) BuildReset(
        bool domainReset = false,
        ResetCommand.ColorModeReset colorMode = ResetCommand.ColorModeReset.NoAction,
        ResetCommand.ScreenBorderReset screenBorder = ResetCommand.ScreenBorderReset.NoAction,
        bool textReset = false,
        bool blinkReset = false,
        bool protectFields = false,
        bool textureReset = false,
        bool macrosReset = false,
        bool drcsReset = false)
    {
        var operands = new NaplpsOperands
        {
            NaplpsEncoder.EncodeResetByte1(domainReset, (byte)colorMode, (byte)screenBorder),
            NaplpsEncoder.EncodeResetByte2(textReset, blinkReset, protectFields, textureReset, macrosReset, drcsReset),
        };

        return (OpReset, operands);
    }

    /// <summary>Build a Non-Selective Reset (C0 control 0x1F) with no operands.</summary>
    public static (byte opcode, NaplpsOperands operands) BuildNonSelectiveReset()
    {
        return (OpNonSelectiveReset, new NaplpsOperands());
    }

    /// <summary>
    /// Build a BLINK command. The blink-from palette entry is taken from the current foreground;
    /// blinkToPaletteIndex is a 4-bit palette entry the foreground will swap to. Each (on, off, startDelay)
    /// triple defines one blink process; pass multiple triples to define multiple processes (each
    /// implicitly increments the from-entry per the SetColor incrementing algorithm).
    /// Intervals are in 1/10 second units, 0-63 per byte.
    /// </summary>
    public static (byte opcode, NaplpsOperands operands) BuildBlink(byte blinkToPaletteIndex, params (byte on, byte off, byte startDelay)[] processes)
    {
        var operands = new NaplpsOperands
        {
            NaplpsEncoder.EncodePaletteIndexByte(blinkToPaletteIndex),
        };

        foreach (var (on, off, startDelay) in processes)
        {
            operands.Add(NaplpsEncoder.EncodeIntervalByte(on));
            operands.Add(NaplpsEncoder.EncodeIntervalByte(off));
            operands.Add(NaplpsEncoder.EncodeIntervalByte(startDelay));
        }

        return (OpBlink, operands);
    }

    /// <summary>Build BLINK with no operands — terminates all blink processes.</summary>
    public static (byte opcode, NaplpsOperands operands) BuildBlinkStop()
    {
        return (OpBlink, new NaplpsOperands());
    }

    // ---- Field / region ----------------------------------------------------

    /// <summary>
    /// Build an INCREMENTAL FIELD command. With both vertices, sets the active field to
    /// the rectangle (origin, dimensions). With dimensions only, the origin is the current pen.
    /// With no operands, the field becomes the full unit screen.
    /// </summary>
    public static (byte opcode, NaplpsOperands operands) BuildField(Vector3? origin = null, Vector3? dimensions = null, int multiByteValue = 3)
    {
        var operands = new NaplpsOperands();

        if (origin.HasValue)
        {
            operands.AddRange(NaplpsEncoder.EncodeVertex2D(origin.Value.X, origin.Value.Y, multiByteValue));
        }

        if (dimensions.HasValue)
        {
            operands.AddRange(NaplpsEncoder.EncodeVertex2D(dimensions.Value.X, dimensions.Value.Y, multiByteValue));
        }

        return (OpIncrementalField, operands);
    }

    // ---- Bitstring-encoded ops (deferred to Phase 4-5 asset editors) ------

    /// <summary>
    /// Stub. The bitstring encoding is implemented when the Phase 4 IncrementalPoint editor
    /// tool lands — see plan.
    /// </summary>
    /// <summary>
    /// Encode a pixel bit-stream for IncrementalPoint. <paramref name="pixelValues"/> is
    /// an array of per-pixel intensities (0..2^bitsPerPixel-1). Pixels are packed MSB-first
    /// into the 6-bit data area of each operand byte (numerical-data range). First byte
    /// sits in operands[0] as the "bits-per-pixel" header (1,2,4,8) per spec §5.3.1.
    /// </summary>
    public static (byte opcode, NaplpsOperands operands) BuildIncrementalPoint(int bitsPerPixel, int[] pixelValues)
    {
        var operands = new NaplpsOperands();

        // Header byte: encode bitsPerPixel in bits 1-3 (1=1bpp, 2=2bpp, 4=4bpp, 8=8bpp).
        // Bit 7/8 forms the numerical-data base so this byte is a valid operand byte.
        byte header = (byte)(0xC0 | (bitsPerPixel & 0x3F));
        operands.Add(header);

        // Pack pixel values into a bit buffer, MSB-first per pixel.
        var bits = new List<bool>(pixelValues.Length * bitsPerPixel);
        foreach (var px in pixelValues)
        {
            for (int i = bitsPerPixel - 1; i >= 0; i--)
            {
                bits.Add(((px >> i) & 1) != 0);
            }
        }

        // Emit 6 bits per operand byte (data bits 1-6), MSB first in bit 5 down to bit 0.
        for (int i = 0; i < bits.Count; i += 6)
        {
            byte b = 0xC0;
            for (int bit = 0; bit < 6 && i + bit < bits.Count; bit++)
            {
                if (bits[i + bit])
                {
                    b |= (byte)(1 << (5 - bit));
                }
            }
            operands.Add(b);
        }

        return (OpIncrementalPoint, operands);
    }

    /// <summary>
    /// Encode an IncrementalLine motion-code stream. <paramref name="stepDx"/>/<paramref name="stepDy"/>
    /// is the base step size (signed deltas); <paramref name="motionCodes"/> is an array of
    /// 2-bit codes (0-3): 00=meta, 01/10/11=step/draw variants per §5.3.2.4.4 "rolling pen"
    /// encoding. Each operand byte packs 3 codes MSB-first in bits 5-0.
    /// </summary>
    public static (byte opcode, NaplpsOperands operands) BuildIncrementalLine(float stepDx, float stepDy, byte[] motionCodes)
    {
        var operands = NaplpsEncoder.EncodeVertex2D(stepDx, stepDy);
        PackMotionCodes(operands, motionCodes);
        return (OpIncrementalLine, operands);
    }

    /// <summary>
    /// Filled-polygon variant of IncrementalLine — same motion-code stream, draw-flag
    /// forced on so every step fills the area traced. See <see cref="BuildIncrementalLine"/>.
    /// </summary>
    public static (byte opcode, NaplpsOperands operands) BuildIncrementalPolygonFilled(float stepDx, float stepDy, byte[] motionCodes)
    {
        var operands = NaplpsEncoder.EncodeVertex2D(stepDx, stepDy);
        PackMotionCodes(operands, motionCodes);
        return (OpIncrementalPolygonFilled, operands);
    }

    /// <summary>
    /// Pack a sequence of 2-bit motion codes into operand bytes, 3 codes per byte.
    /// Bit layout mirrors DrawableIncrementalLine.ParseMotionCodes — bits 5-4 hold code0,
    /// bits 3-2 hold code1, bits 1-0 hold code2. Bit 7 (0x80) is the numerical-data flag.
    /// </summary>
    private static void PackMotionCodes(NaplpsOperands operands, byte[] motionCodes)
    {
        byte baseByte = NaplpsEncoder.Use7BitMode ? (byte)0x40 : (byte)0xC0;
        for (int i = 0; i < motionCodes.Length; i += 3)
        {
            byte b = baseByte;
            if (i < motionCodes.Length) { b |= (byte)((motionCodes[i] & 0x3) << 4); }
            if (i + 1 < motionCodes.Length) { b |= (byte)((motionCodes[i + 1] & 0x3) << 2); }
            if (i + 2 < motionCodes.Length) { b |= (byte)(motionCodes[i + 2] & 0x3); }
            operands.Add(b);
        }
    }

    /// <summary>
    /// Stub. DRCS bitmap encoder lands with the Phase 5 DRCS designer.
    /// </summary>
    public static (byte opcode, NaplpsOperands operands) BuildDefDrcs(byte charSlot, bool[,] bitmap)
    {
        throw new NotImplementedException("DefDRCS bitmap encoder lands with the Phase 5 DRCS designer.");
    }

    /// <summary>
    /// Stub. Texture mask encoder lands with the Phase 5 texture designer.
    /// MaskId 0-3 selects mask A-D.
    /// </summary>
    public static (byte opcode, NaplpsOperands operands) BuildDefTexture(byte maskId, bool[,] mask)
    {
        throw new NotImplementedException("DefTexture mask encoder lands with the Phase 5 texture designer.");
    }

    /// <summary>
    /// Stub. Macro body encoder lands with the Phase 5 macro recorder. Body is wrapped
    /// with End sentinel.
    /// </summary>
    public static (byte opcode, NaplpsOperands operands) BuildDefMacro(byte macroId, byte[] body)
    {
        throw new NotImplementedException("DefMacro body encoder lands with the Phase 5 macro recorder.");
    }
}
