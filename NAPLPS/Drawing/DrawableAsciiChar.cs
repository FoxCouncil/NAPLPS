// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using SixLabors.Fonts;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;
using FontFamily = SixLabors.Fonts.FontFamily;
using PointF = SixLabors.ImageSharp.PointF;
using TextRotation = NAPLPS.Commands.TextCommand.TextRotation;

namespace NAPLPS.Drawing;

public class DrawableAsciiChar : Drawable, IDrawable
{
    private readonly AsciiCharCommand _command;
    private static readonly FontCollection _fontCollection = new();
    private static readonly FontFamily _fontFamily;

    // Pre-computed reference measurements at size 100
    private static readonly float _refLineHeight;
    private static readonly float _refTopOffset;
    private static readonly float _refCharWidth;
    private static readonly float _refCapHeight;

    // NAPLPS Spec-defined width classes (0-9) for ASCII 0x20-0x7E
    // From docs/NAPLPS.ASC lines 2227-2245
    // Class 9 = widest (full character field width), Class 0 = narrowest
    // Goal: "make the interfont gap between all characters identical"
    private static readonly byte[] _asciiWidthClass = new byte[95]
    {
        // 0x20-0x2F: space ! " # $ % & ' ( ) * + , - . /
        9, 0, 4, 6, 9, 9, 9, 0, 1, 1, 9, 9, 3, 5, 0, 9,
        // 0x30-0x3F: 0 1 2 3 4 5 6 7 8 9 : ; < = > ?
        5, 1, 5, 5, 5, 5, 5, 5, 5, 5, 0, 3, 5, 8, 5, 8,
        // 0x40-0x4F: @ A B C D E F G H I J K L M N O
        9, 5, 5, 5, 5, 5, 5, 8, 5, 2, 5, 5, 5, 9, 5, 9,
        // 0x50-0x5F: P Q R S T U V W X Y Z [ \ ] ^ _
        5, 6, 5, 5, 9, 5, 9, 9, 9, 9, 9, 4, 9, 4, 2, 9,
        // 0x60-0x6F: ` a b c d e f g h i j k l m n o
        1, 5, 5, 5, 5, 5, 5, 5, 5, 0, 4, 5, 0, 9, 5, 5,
        // 0x70-0x7E: p q r s t u v w x y z { | } ~
        5, 5, 5, 5, 2, 5, 9, 9, 9, 5, 5, 5, 0, 5, 9
    };

    // ANSI X3.110 full displacement table for text sizes < 12/256
    // Rows indexed by (n-6) where n = charFieldWidth * 256 truncated, columns by width class 0-9
    private static readonly int[,] _displacementTable = new int[,]
    {
        //       0  1  2  3  4  5  6  7  8  9
        /* 6 */ { 2, 3, 4, 3, 4, 5, 6, 4, 5, 6 },
        /* 7 */ { 3, 4, 5, 4, 5, 6, 7, 5, 6, 7 },
        /* 8 */ { 2, 3, 4, 4, 5, 6, 7, 6, 7, 8 },
        /* 9 */ { 3, 4, 5, 5, 6, 7, 8, 7, 8, 9 },
        /*10 */ { 4, 5, 6, 6, 7, 8, 9, 8, 9, 10 },
        /*11 */ { 3, 4, 6, 6, 7, 8, 10, 8, 10, 11 },
    };

    // Row 12: unit spacing numbers used for the large text (>=12/256) algorithm
    private static readonly int[] _unitSpacingRow12 = { 6, 5, 4, 4, 3, 2, 1, 2, 1, 0 };

    // Supplementary character width classes (same layout as ASCII, positions 0x20-0x7E in supp set)
    private static readonly byte[] _suppWidthClass = new byte[95]
    {
        // 0x20-0x2F
        9, 0, 9, 5, 9, 9, 9, 6, 9, 1, 6, 8, 9, 9, 9, 9,
        // 0x30-0x3F
        9, 4, 4, 4, 9, 6, 9, 0, 9, 1, 1, 8, 9, 9, 9, 9,
        // 0x40-0x4F (non-spacing accents — use class 9 for max width)
        9, 7, 2, 2, 9, 5, 5, 0, 4, 9, 1, 7, 9, 9, 9, 6,
        // 0x50-0x5F
        5, 9, 5, 5, 6, 6, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9,
        // 0x60-0x6F
        6, 9, 6, 4, 9, 9, 9, 4, 9, 9, 9, 9, 5, 9, 6, 2,
        // 0x70-0x7E
        9, 9, 9, 9, 5, 0, 4, 4, 7, 9, 9, 6, 5, 4, 5
    };

    // Legacy row 8 lookup — used for glyph rendering width
    private static readonly int[] _displacementRow8 = { 2, 3, 4, 4, 5, 6, 7, 6, 7, 8 };

    // Actual VGA 8x16 font glyph pixel widths (rightmost set pixel column).
    // PP3's MVDI measures actual glyph widths from the font ROM for proportional advance.
    // All glyphs are 6-8px wide — proportional mode is nearly monospaced with this font.
    private static readonly byte[] _vgaGlyphWidths = new byte[]
    {
        8, 6, 8, 7, 8, 7, 8, 7, 8, 6, 8, 8, 8, 6, 8, 7, // SP ! " # $ % & ' ( ) * + , - . /
        8, 7, 7, 8, 7, 8, 7, 8, 7, 8, 7, 8, 7, 8, 7, 8, // 0-9 : ; < = > ?
        7, 8, 7, 7, 8, 7, 8, 7, 8, 7, 8, 7, 8, 7, 8, 7, // @ A-O
        8, 7, 8, 7, 7, 8, 7, 8, 7, 8, 7, 8, 7, 8, 7, 8, // P-_
        7, 8, 7, 8, 6, 7, 8, 7, 8, 7, 8, 7, 8, 7, 8, 7, // ` a-o
        8, 6, 8, 7, 8, 7, 7, 8, 7, 8, 6, 8, 7, 8, 7, 8, // p-~ DEL
    };

    /// <summary>
    /// Returns the actual VGA font glyph pixel width ratio for proportional advance.
    /// </summary>
    public static float GetGlyphAdvanceRatio(char c)
    {
        if (c >= 0x20 && c <= 0x7F)
        {
            return _vgaGlyphWidths[c - 0x20] / 8f;
        }

        return 0.875f; // default 7/8 for unknown chars
    }

    static DrawableAsciiChar()
    {
        var assembly = typeof(DrawableAsciiChar).Assembly;

        // Use PRM5X10 as the base font — it's the "standard" NAPLPS font
        using var stream = assembly.GetManifestResourceStream("NAPLPS.Fonts.PRM5X10.TTF");

        if (stream == null)
        {
            throw new InvalidOperationException("Could not load embedded font resource.");
        }

        _fontFamily = _fontCollection.Add(stream);

        // Measure reference bounds at size 100
        var refFont = _fontFamily.CreateFont(100f, FontStyle.Regular);
        var refBounds = TextMeasurer.MeasureBounds("Mg", new TextOptions(refFont));
        var refWidthBounds = TextMeasurer.MeasureBounds("M", new TextOptions(refFont));

        _refLineHeight = refBounds.Height;
        _refTopOffset = refBounds.Top;
        _refCharWidth = refWidthBounds.Width;

        var refCapBounds = TextMeasurer.MeasureBounds("H", new TextOptions(refFont));
        _refCapHeight = refCapBounds.Height;
    }

    /// <summary>
    /// Gets the width ratio of a character relative to full cell width.
    /// Simplified version using row 8 lookup. Used for rendering cell sizing.
    /// </summary>
    public static float GetCharWidthRatio(char c)
    {
        if (c >= 0x20 && c <= 0x7E)
        {
            int widthClass = _asciiWidthClass[c - 0x20];
            return _displacementRow8[widthClass] / 8f;
        }

        // Supplementary/extended characters: use width estimates based on Unicode category.
        // The NAPLPS spec doesn't define proportional widths for the supplementary set,
        // but full-width (1.0) is wrong for narrow symbols like ®, ©, °, etc.
        return c switch
        {
            '®' or '©' or '™' => 0.625f,  // Narrow symbols (width class ~5)
            '°' or '·' or '¨' or '¸' or '˚' or '˙' => 0.375f,  // Tiny symbols (class ~3)
            '±' or '×' or '÷' or '¬' => 0.75f,   // Math operators (class ~6)
            '²' or '³' or '¹' => 0.375f,  // Superscripts (class ~3)
            '¼' or '½' or '¾' => 0.875f,  // Fractions (class ~7)
            '←' or '→' or '↑' or '↓' => 0.875f,  // Arrows (class ~7)
            '─' or '―' => 1.0f,  // Full-width dashes/lines
            _ => 0.75f  // Default: slightly narrower than full width
        };
    }

    /// <summary>
    /// Returns the raw Row 8 displacement value (0-8) for a character's width class.
    /// Used by PP3's fixed-point integer arithmetic: advance = (n * disp) / 8.
    /// </summary>
    public static int GetWidthClassDisplacement(char c)
    {
        int widthClass;
        if (c >= 0x20 && c <= 0x7E)
        {
            widthClass = _asciiWidthClass[c - 0x20];
        }
        else
        {
            widthClass = 9;
        }

        return _displacementRow8[widthClass];
    }

    /// <summary>
    /// Proportional spacing: advance = charW * displacement[row][class] / n.
    /// Pre-selects a displacement row based on text size, then adds displacement
    /// directly to the pen position. The ratio disp/n is constant per class
    /// regardless of text size.
    /// </summary>
    public static float GetProportionalDisplacement(float charFieldWidth, char c)
    {
        int widthClass;
        if (c >= 0x20 && c <= 0x7E)
        {
            widthClass = _asciiWidthClass[c - 0x20];
        }
        else
        {
            widthClass = 9;
        }

        int n = (int)(charFieldWidth * 256f);
        int clampedN = Math.Clamp(n, 6, 11);
        int row = clampedN - 6;

        int disp = _displacementTable[row, widthClass];

        // PP3-matching formula: displacement is the table value scaled to the actual
        // char field width. Preserves visual parity with reference PP3 renders even when
        // the char field doesn't fall on an exact 1/256th boundary.
        return charFieldWidth * disp / clampedN;
    }

    /// <summary>
    /// Gets a horizontal stretch boost factor for thin characters.
    /// These characters have lots of built-in whitespace in the font and need
    /// extra stretching to fill their cells properly.
    /// </summary>
    private static float GetThinCharacterBoost(char c)
    {
        return c switch
        {
            'i' or 'l' or ':' or '.' or '\'' => 4.0f,   // Very thin - need 2x stretch
            '1' => 2.0f,  // Moderately thin
            't' or 'I' or '!' or '.' or ',' or ';' => 1.8f,  // Thin punctuation
            '(' or ')' or '[' or ']' or '|' => 1.6f,  // Brackets/pipe
            'j' => 1.4f,
            'f' => 1.2f,
            'w' or 'W' or 'X' or 'Y' or 'G' or 'm' or 'x' or 'M' or 'T' or '@' or '/' or '&' => 0.8f, // Too big
            'v' or 'V' => 0.6f, // Really big!
            _ => 1.0f,  // Normal characters - no boost
        };
    }

    private static void GetCharXBackoff(ref float offsetX, char character)
    {
        switch (character)
        {
            case 't':
            case '1':
            case ',':
            {
                offsetX -= 10;
            }
            break;

            case 'I':
            {
                offsetX -= 8;
            }
            break;

            case 'R':
            {
                offsetX -= 4;
            }
            break;
        }
    }

    // VGA 8x16 ROM font - standard IBM VGA BIOS bitmap font (public domain)
    // 96 ASCII characters (0x20-0x7F), 16 bytes per character, MSB = leftmost pixel
    private const int VGA_CHAR_WIDTH = 8;
    private const int VGA_CHAR_HEIGHT = 16;
    private static readonly byte[] _vgaFontData = new byte[]
    {
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // 0x20
        0x00, 0x00, 0x18, 0x3C, 0x3C, 0x3C, 0x18, 0x18, 0x18, 0x00, 0x18, 0x18, 0x00, 0x00, 0x00, 0x00, // 0x21 !
        0x00, 0x66, 0x66, 0x66, 0x24, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // 0x22 "
        0x00, 0x00, 0x00, 0x6C, 0x6C, 0xFE, 0x6C, 0x6C, 0x6C, 0xFE, 0x6C, 0x6C, 0x00, 0x00, 0x00, 0x00, // 0x23 #
        0x18, 0x18, 0x7C, 0xC6, 0xC2, 0xC0, 0x7C, 0x06, 0x06, 0x86, 0xC6, 0x7C, 0x18, 0x18, 0x00, 0x00, // 0x24 $
        0x00, 0x00, 0x00, 0x00, 0xC2, 0xC6, 0x0C, 0x18, 0x30, 0x60, 0xC6, 0x86, 0x00, 0x00, 0x00, 0x00, // 0x25 %
        0x00, 0x00, 0x38, 0x6C, 0x6C, 0x38, 0x76, 0xDC, 0xCC, 0xCC, 0xCC, 0x76, 0x00, 0x00, 0x00, 0x00, // 0x26 &
        0x00, 0x30, 0x30, 0x30, 0x60, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // 0x27 '
        0x00, 0x00, 0x0C, 0x18, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x18, 0x0C, 0x00, 0x00, 0x00, 0x00, // 0x28 (
        0x00, 0x00, 0x30, 0x18, 0x0C, 0x0C, 0x0C, 0x0C, 0x0C, 0x0C, 0x18, 0x30, 0x00, 0x00, 0x00, 0x00, // 0x29 )
        0x00, 0x00, 0x00, 0x00, 0x00, 0x66, 0x3C, 0xFF, 0x3C, 0x66, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // 0x2A *
        0x00, 0x00, 0x00, 0x00, 0x00, 0x18, 0x18, 0x7E, 0x18, 0x18, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // 0x2B +
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x18, 0x18, 0x18, 0x30, 0x00, 0x00, 0x00, // 0x2C ,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFE, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // 0x2D -
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x18, 0x18, 0x00, 0x00, 0x00, 0x00, // 0x2E .
        0x00, 0x00, 0x00, 0x00, 0x02, 0x06, 0x0C, 0x18, 0x30, 0x60, 0xC0, 0x80, 0x00, 0x00, 0x00, 0x00, // 0x2F /
        0x00, 0x00, 0x38, 0x6C, 0xC6, 0xC6, 0xD6, 0xD6, 0xC6, 0xC6, 0x6C, 0x38, 0x00, 0x00, 0x00, 0x00, // 0x30 0
        0x00, 0x00, 0x18, 0x38, 0x78, 0x18, 0x18, 0x18, 0x18, 0x18, 0x18, 0x7E, 0x00, 0x00, 0x00, 0x00, // 0x31 1
        0x00, 0x00, 0x7C, 0xC6, 0x06, 0x0C, 0x18, 0x30, 0x60, 0xC0, 0xC6, 0xFE, 0x00, 0x00, 0x00, 0x00, // 0x32 2
        0x00, 0x00, 0x7C, 0xC6, 0x06, 0x06, 0x3C, 0x06, 0x06, 0x06, 0xC6, 0x7C, 0x00, 0x00, 0x00, 0x00, // 0x33 3
        0x00, 0x00, 0x0C, 0x1C, 0x3C, 0x6C, 0xCC, 0xFE, 0x0C, 0x0C, 0x0C, 0x1E, 0x00, 0x00, 0x00, 0x00, // 0x34 4
        0x00, 0x00, 0xFE, 0xC0, 0xC0, 0xC0, 0xFC, 0x06, 0x06, 0x06, 0xC6, 0x7C, 0x00, 0x00, 0x00, 0x00, // 0x35 5
        0x00, 0x00, 0x38, 0x60, 0xC0, 0xC0, 0xFC, 0xC6, 0xC6, 0xC6, 0xC6, 0x7C, 0x00, 0x00, 0x00, 0x00, // 0x36 6
        0x00, 0x00, 0xFE, 0xC6, 0x06, 0x06, 0x0C, 0x18, 0x30, 0x30, 0x30, 0x30, 0x00, 0x00, 0x00, 0x00, // 0x37 7
        0x00, 0x00, 0x7C, 0xC6, 0xC6, 0xC6, 0x7C, 0xC6, 0xC6, 0xC6, 0xC6, 0x7C, 0x00, 0x00, 0x00, 0x00, // 0x38 8
        0x00, 0x00, 0x7C, 0xC6, 0xC6, 0xC6, 0x7E, 0x06, 0x06, 0x06, 0x0C, 0x78, 0x00, 0x00, 0x00, 0x00, // 0x39 9
        0x00, 0x00, 0x00, 0x00, 0x18, 0x18, 0x00, 0x00, 0x00, 0x18, 0x18, 0x00, 0x00, 0x00, 0x00, 0x00, // 0x3A :
        0x00, 0x00, 0x00, 0x00, 0x18, 0x18, 0x00, 0x00, 0x00, 0x18, 0x18, 0x30, 0x00, 0x00, 0x00, 0x00, // 0x3B ;
        0x00, 0x00, 0x00, 0x06, 0x0C, 0x18, 0x30, 0x60, 0x30, 0x18, 0x0C, 0x06, 0x00, 0x00, 0x00, 0x00, // 0x3C <
        0x00, 0x00, 0x00, 0x00, 0x00, 0x7E, 0x00, 0x00, 0x7E, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // 0x3D =
        0x00, 0x00, 0x00, 0x60, 0x30, 0x18, 0x0C, 0x06, 0x0C, 0x18, 0x30, 0x60, 0x00, 0x00, 0x00, 0x00, // 0x3E >
        0x00, 0x00, 0x7C, 0xC6, 0xC6, 0x0C, 0x18, 0x18, 0x18, 0x00, 0x18, 0x18, 0x00, 0x00, 0x00, 0x00, // 0x3F ?
        0x00, 0x00, 0x00, 0x7C, 0xC6, 0xC6, 0xDE, 0xDE, 0xDE, 0xDC, 0xC0, 0x7C, 0x00, 0x00, 0x00, 0x00, // 0x40 @
        0x00, 0x00, 0x10, 0x38, 0x6C, 0xC6, 0xC6, 0xFE, 0xC6, 0xC6, 0xC6, 0xC6, 0x00, 0x00, 0x00, 0x00, // 0x41 A
        0x00, 0x00, 0xFC, 0x66, 0x66, 0x66, 0x7C, 0x66, 0x66, 0x66, 0x66, 0xFC, 0x00, 0x00, 0x00, 0x00, // 0x42 B
        0x00, 0x00, 0x3C, 0x66, 0xC2, 0xC0, 0xC0, 0xC0, 0xC0, 0xC2, 0x66, 0x3C, 0x00, 0x00, 0x00, 0x00, // 0x43 C
        0x00, 0x00, 0xF8, 0x6C, 0x66, 0x66, 0x66, 0x66, 0x66, 0x66, 0x6C, 0xF8, 0x00, 0x00, 0x00, 0x00, // 0x44 D
        0x00, 0x00, 0xFE, 0x66, 0x62, 0x68, 0x78, 0x68, 0x60, 0x62, 0x66, 0xFE, 0x00, 0x00, 0x00, 0x00, // 0x45 E
        0x00, 0x00, 0xFE, 0x66, 0x62, 0x68, 0x78, 0x68, 0x60, 0x60, 0x60, 0xF0, 0x00, 0x00, 0x00, 0x00, // 0x46 F
        0x00, 0x00, 0x3C, 0x66, 0xC2, 0xC0, 0xC0, 0xDE, 0xC6, 0xC6, 0x66, 0x3A, 0x00, 0x00, 0x00, 0x00, // 0x47 G
        0x00, 0x00, 0xC6, 0xC6, 0xC6, 0xC6, 0xFE, 0xC6, 0xC6, 0xC6, 0xC6, 0xC6, 0x00, 0x00, 0x00, 0x00, // 0x48 H
        0x00, 0x00, 0x3C, 0x18, 0x18, 0x18, 0x18, 0x18, 0x18, 0x18, 0x18, 0x3C, 0x00, 0x00, 0x00, 0x00, // 0x49 I
        0x00, 0x00, 0x1E, 0x0C, 0x0C, 0x0C, 0x0C, 0x0C, 0xCC, 0xCC, 0xCC, 0x78, 0x00, 0x00, 0x00, 0x00, // 0x4A J
        0x00, 0x00, 0xE6, 0x66, 0x66, 0x6C, 0x78, 0x78, 0x6C, 0x66, 0x66, 0xE6, 0x00, 0x00, 0x00, 0x00, // 0x4B K
        0x00, 0x00, 0xF0, 0x60, 0x60, 0x60, 0x60, 0x60, 0x60, 0x62, 0x66, 0xFE, 0x00, 0x00, 0x00, 0x00, // 0x4C L
        0x00, 0x00, 0xC6, 0xEE, 0xFE, 0xFE, 0xD6, 0xC6, 0xC6, 0xC6, 0xC6, 0xC6, 0x00, 0x00, 0x00, 0x00, // 0x4D M
        0x00, 0x00, 0xC6, 0xE6, 0xF6, 0xFE, 0xDE, 0xCE, 0xC6, 0xC6, 0xC6, 0xC6, 0x00, 0x00, 0x00, 0x00, // 0x4E N
        0x00, 0x00, 0x7C, 0xC6, 0xC6, 0xC6, 0xC6, 0xC6, 0xC6, 0xC6, 0xC6, 0x7C, 0x00, 0x00, 0x00, 0x00, // 0x4F O
        0x00, 0x00, 0xFC, 0x66, 0x66, 0x66, 0x7C, 0x60, 0x60, 0x60, 0x60, 0xF0, 0x00, 0x00, 0x00, 0x00, // 0x50 P
        0x00, 0x00, 0x7C, 0xC6, 0xC6, 0xC6, 0xC6, 0xC6, 0xC6, 0xD6, 0xDE, 0x7C, 0x0C, 0x0E, 0x00, 0x00, // 0x51 Q
        0x00, 0x00, 0xFC, 0x66, 0x66, 0x66, 0x7C, 0x6C, 0x66, 0x66, 0x66, 0xE6, 0x00, 0x00, 0x00, 0x00, // 0x52 R
        0x00, 0x00, 0x7C, 0xC6, 0xC6, 0x60, 0x38, 0x0C, 0x06, 0xC6, 0xC6, 0x7C, 0x00, 0x00, 0x00, 0x00, // 0x53 S
        0x00, 0x00, 0x7E, 0x7E, 0x5A, 0x18, 0x18, 0x18, 0x18, 0x18, 0x18, 0x3C, 0x00, 0x00, 0x00, 0x00, // 0x54 T
        0x00, 0x00, 0xC6, 0xC6, 0xC6, 0xC6, 0xC6, 0xC6, 0xC6, 0xC6, 0xC6, 0x7C, 0x00, 0x00, 0x00, 0x00, // 0x55 U
        0x00, 0x00, 0xC6, 0xC6, 0xC6, 0xC6, 0xC6, 0xC6, 0xC6, 0x6C, 0x38, 0x10, 0x00, 0x00, 0x00, 0x00, // 0x56 V
        0x00, 0x00, 0xC6, 0xC6, 0xC6, 0xC6, 0xD6, 0xD6, 0xD6, 0xFE, 0xEE, 0x6C, 0x00, 0x00, 0x00, 0x00, // 0x57 W
        0x00, 0x00, 0xC6, 0xC6, 0x6C, 0x7C, 0x38, 0x38, 0x7C, 0x6C, 0xC6, 0xC6, 0x00, 0x00, 0x00, 0x00, // 0x58 X
        0x00, 0x00, 0x66, 0x66, 0x66, 0x66, 0x3C, 0x18, 0x18, 0x18, 0x18, 0x3C, 0x00, 0x00, 0x00, 0x00, // 0x59 Y
        0x00, 0x00, 0xFE, 0xC6, 0x86, 0x0C, 0x18, 0x30, 0x60, 0xC2, 0xC6, 0xFE, 0x00, 0x00, 0x00, 0x00, // 0x5A Z
        0x00, 0x00, 0x3C, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x3C, 0x00, 0x00, 0x00, 0x00, // 0x5B [
        0x00, 0x00, 0x00, 0x80, 0xC0, 0xE0, 0x70, 0x38, 0x1C, 0x0E, 0x06, 0x02, 0x00, 0x00, 0x00, 0x00, // 0x5C backslash
        0x00, 0x00, 0x3C, 0x0C, 0x0C, 0x0C, 0x0C, 0x0C, 0x0C, 0x0C, 0x0C, 0x3C, 0x00, 0x00, 0x00, 0x00, // 0x5D ]
        0x10, 0x38, 0x6C, 0xC6, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // 0x5E ^
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00, // 0x5F _
        0x00, 0x30, 0x18, 0x0C, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // 0x60 `
        0x00, 0x00, 0x00, 0x00, 0x00, 0x78, 0x0C, 0x7C, 0xCC, 0xCC, 0xCC, 0x76, 0x00, 0x00, 0x00, 0x00, // 0x61 a
        0x00, 0x00, 0xE0, 0x60, 0x60, 0x78, 0x6C, 0x66, 0x66, 0x66, 0x66, 0x7C, 0x00, 0x00, 0x00, 0x00, // 0x62 b
        0x00, 0x00, 0x00, 0x00, 0x00, 0x7C, 0xC6, 0xC0, 0xC0, 0xC0, 0xC6, 0x7C, 0x00, 0x00, 0x00, 0x00, // 0x63 c
        0x00, 0x00, 0x1C, 0x0C, 0x0C, 0x3C, 0x6C, 0xCC, 0xCC, 0xCC, 0xCC, 0x76, 0x00, 0x00, 0x00, 0x00, // 0x64 d
        0x00, 0x00, 0x00, 0x00, 0x00, 0x7C, 0xC6, 0xFE, 0xC0, 0xC0, 0xC6, 0x7C, 0x00, 0x00, 0x00, 0x00, // 0x65 e
        0x00, 0x00, 0x1C, 0x36, 0x32, 0x30, 0x78, 0x30, 0x30, 0x30, 0x30, 0x78, 0x00, 0x00, 0x00, 0x00, // 0x66 f
        0x00, 0x00, 0x00, 0x00, 0x00, 0x76, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0x7C, 0x0C, 0xCC, 0x78, 0x00, // 0x67 g
        0x00, 0x00, 0xE0, 0x60, 0x60, 0x6C, 0x76, 0x66, 0x66, 0x66, 0x66, 0xE6, 0x00, 0x00, 0x00, 0x00, // 0x68 h
        0x00, 0x00, 0x18, 0x18, 0x00, 0x38, 0x18, 0x18, 0x18, 0x18, 0x18, 0x3C, 0x00, 0x00, 0x00, 0x00, // 0x69 i
        0x00, 0x00, 0x06, 0x06, 0x00, 0x0E, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x66, 0x66, 0x3C, 0x00, // 0x6A j
        0x00, 0x00, 0xE0, 0x60, 0x60, 0x66, 0x6C, 0x78, 0x78, 0x6C, 0x66, 0xE6, 0x00, 0x00, 0x00, 0x00, // 0x6B k
        0x00, 0x00, 0x38, 0x18, 0x18, 0x18, 0x18, 0x18, 0x18, 0x18, 0x18, 0x3C, 0x00, 0x00, 0x00, 0x00, // 0x6C l
        0x00, 0x00, 0x00, 0x00, 0x00, 0xEC, 0xFE, 0xD6, 0xD6, 0xD6, 0xD6, 0xC6, 0x00, 0x00, 0x00, 0x00, // 0x6D m
        0x00, 0x00, 0x00, 0x00, 0x00, 0xDC, 0x66, 0x66, 0x66, 0x66, 0x66, 0x66, 0x00, 0x00, 0x00, 0x00, // 0x6E n
        0x00, 0x00, 0x00, 0x00, 0x00, 0x7C, 0xC6, 0xC6, 0xC6, 0xC6, 0xC6, 0x7C, 0x00, 0x00, 0x00, 0x00, // 0x6F o
        0x00, 0x00, 0x00, 0x00, 0x00, 0xDC, 0x66, 0x66, 0x66, 0x66, 0x66, 0x7C, 0x60, 0x60, 0xF0, 0x00, // 0x70 p
        0x00, 0x00, 0x00, 0x00, 0x00, 0x76, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0x7C, 0x0C, 0x0C, 0x1E, 0x00, // 0x71 q
        0x00, 0x00, 0x00, 0x00, 0x00, 0xDC, 0x76, 0x66, 0x60, 0x60, 0x60, 0xF0, 0x00, 0x00, 0x00, 0x00, // 0x72 r
        0x00, 0x00, 0x00, 0x00, 0x00, 0x7C, 0xC6, 0x60, 0x38, 0x0C, 0xC6, 0x7C, 0x00, 0x00, 0x00, 0x00, // 0x73 s
        0x00, 0x00, 0x10, 0x30, 0x30, 0xFC, 0x30, 0x30, 0x30, 0x30, 0x36, 0x1C, 0x00, 0x00, 0x00, 0x00, // 0x74 t
        0x00, 0x00, 0x00, 0x00, 0x00, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0x76, 0x00, 0x00, 0x00, 0x00, // 0x75 u
        0x00, 0x00, 0x00, 0x00, 0x00, 0xC6, 0xC6, 0xC6, 0xC6, 0xC6, 0x6C, 0x38, 0x00, 0x00, 0x00, 0x00, // 0x76 v
        0x00, 0x00, 0x00, 0x00, 0x00, 0xC6, 0xC6, 0xD6, 0xD6, 0xD6, 0xFE, 0x6C, 0x00, 0x00, 0x00, 0x00, // 0x77 w
        0x00, 0x00, 0x00, 0x00, 0x00, 0xC6, 0x6C, 0x38, 0x38, 0x38, 0x6C, 0xC6, 0x00, 0x00, 0x00, 0x00, // 0x78 x
        0x00, 0x00, 0x00, 0x00, 0x00, 0xC6, 0xC6, 0xC6, 0xC6, 0xC6, 0xC6, 0x7E, 0x06, 0x0C, 0xF8, 0x00, // 0x79 y
        0x00, 0x00, 0x00, 0x00, 0x00, 0xFE, 0xCC, 0x18, 0x30, 0x60, 0xC6, 0xFE, 0x00, 0x00, 0x00, 0x00, // 0x7A z
        0x00, 0x00, 0x0E, 0x18, 0x18, 0x18, 0x70, 0x18, 0x18, 0x18, 0x18, 0x0E, 0x00, 0x00, 0x00, 0x00, // 0x7B {
        0x00, 0x00, 0x18, 0x18, 0x18, 0x18, 0x18, 0x18, 0x18, 0x18, 0x18, 0x18, 0x00, 0x00, 0x00, 0x00, // 0x7C |
        0x00, 0x00, 0x70, 0x18, 0x18, 0x18, 0x0E, 0x18, 0x18, 0x18, 0x18, 0x70, 0x00, 0x00, 0x00, 0x00, // 0x7D }
        0x00, 0x76, 0xDC, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // 0x7E ~
        0x00, 0x00, 0x00, 0x00, 0x10, 0x38, 0x6C, 0xC6, 0xC6, 0xC6, 0xFE, 0x00, 0x00, 0x00, 0x00, 0x00, // 0x7F
    };

    public DrawableAsciiChar(AsciiCharCommand command) : base(command)
    {
        _command = command;
    }

    public void Draw(Image<Rgba32> image, NaplpsState state, Size size)
    {
        // Vector-stroke glyphs, drawn with the integer pel plotter to match the device character
        // generator. This is the Prodigy default (see DrawMvdiFont).
        if (Options.UseMvdiFont)
        {
            // ANSI X3.110 5.3.2.1: a non-spacing accent leaves no advance and is composed onto the
            // FOLLOWING spacing char; it draws nothing on its own (the base glyph overlays it via
            // OverlayAccentCode). See DrawMvdiFont.
            if (_command.IsNonSpacing)
            {
                return;
            }

            DrawMvdiFont(image, state, size, _command.AsciiCharacter);
            return;
        }

        if (Options.UseBitmapFont)
        {
            DrawBitmapFont(image, state, size, _command.AsciiCharacter);
            return;
        }

        DrawTrueTypeFont(image, state, size, _command.AsciiCharacter);
    }


    /// <summary>
    /// Renders a character as MVDI's authentic vector strokes (see <see cref="MvdiFont"/>),
    /// drawn with the integer pel plotter to match the reference render's hard-edged character generator.
    /// </summary>
    private void DrawMvdiFont(Image<Rgba32> image, NaplpsState state, Size size, char glyphChar)
    {
        // MVDI's device pen: round-half-up (not floor) of the normalized pen. Verified from
        // reference captures — golden stroke offsets are pen-independent only under rounding.
        double dr = NaplpsUtils.DisplayRatio;
        int penXdev = (int)Math.Round((double)state.Pen.X * size.Width, MidpointRounding.AwayFromZero);
        int penYdev = size.Height - (int)Math.Round((double)state.Pen.Y / dr * size.Height, MidpointRounding.AwayFromZero);

        var (charSizeX, charSizeY) = ConvertNormalizedToScreenScale(size, state.CharSize.X, state.CharSize.Y);
        float cellW = MathF.Max(1f, MathF.Abs(charSizeX));
        float cellH = MathF.Max(1f, MathF.Abs(charSizeY));

        var (fgColor, bgColor) = GetISColorFromState(state);
        if (state.IsReverseVideo)
        {
            (fgColor, bgColor) = (bgColor, fgColor);
        }

        // Color mode 2 fills the character field with the background color first.
        if (state.ColorMode == 2)
        {
            var bgRect = new RectangleF(penXdev, penYdev - cellH, cellW + 1, cellH);
            image.Mutate(ctx => ctx.Fill(FillOptions(), bgColor, bgRect));
        }

        // Grid -> device mapping (calibrated pixel-for-pixel on the dominant corpus sizes):
        //   device_x = penXdev + round(unitX * gridX)                       unitX = CharSize.X*W/7.5
        //   device_y = penYdev - round(CharSize.Y*W*(36*gridY + 18)/(5*75))
        // Left bearing is spacing-conditional: under FIXED spacing the generator keeps the bearing
        // (ink at penX + unitX*gridX, so glyphs sit at their natural grid position); under
        // PROPORTIONAL spacing it left-justifies (subtract the bearing). The vertical row map is
        // linear in gridY, anchored at the baseline (gridY=2 -> yScale*18, matches the reference at
        // every size) with slope 7.2 px/unit (cap gridY=8 -> yScale*61.2). The earlier 7*gridY+4 was
        // calibrated only at ky=10 and let tall glyphs fall ~1px short per ~22 ky (up to +4px at
        // ky=90, e.g. SNOOP1's ky=66 wordmark); measured by the vscale sweep across ky 8..90.
        var glyph = _command.IsSupplementary
            ? MvdiFont.ForSupplementary(_command.SupplementaryCode)
            : MvdiFont.ForChar(glyphChar);
        double csx = Math.Abs(state.CharSize.X);
        double csy = Math.Abs(state.CharSize.Y);
        int kx = (int)Math.Round(csx * 256.0);
        int ky = (int)Math.Round(csy * 256.0);

        // The glyph metrics (HorizStep, HorizRowTopOffset, VertStrokePel, HorizStrokePel) are
        // absolute device-pixel tables calibrated at 640x480. NAPLPS coordinates - including CharSize
        // - are normalized (a fraction of the screen), so a glyph must occupy the same screen FRACTION
        // at any resolution: scale the metrics by size/reference. At 640x480 the factor is exactly 1
        // (the calibrated, pixel-perfect result is untouched); higher
        // resolutions render the same CharSize proportionally larger (same apparent size, more pels).
        // Smaller/denser text comes from a smaller CharSize, which a higher-res raster renders crisply.
        double sx = size.Width / 640.0;
        double sy = size.Height / 480.0;

        // Horizontal grid->device (calibrated pixel-exactly, see MvdiFont.HStep):
        //   deviceX(gx) = round_half_up(640*penX + u(kx)*gx)
        // The pen MUST stay fractional inside the round - its sub-pel value sets the DDA phase.
        // u(kx) is a nonlinear staircase, NOT a clean em (the old CharSize*W/7.5 was wrong at kx>=6
        // and dropping the fractional pen shifted small glyphs). Under proportional spacing MVDI
        // left-justifies each glyph by its bearing (gx-lb); fixed spacing keeps the bearing.
        double penXf = (double)state.Pen.X * size.Width;
        double u = MvdiFont.HorizStep(kx) * sx;

        bool fixedSpacing = state.TextSpacing != TextSpacing.Proportional;
        int DevX(int gridX, int lb) => (int)Math.Round(penXf + u * (fixedSpacing ? gridX : gridX - lb), MidpointRounding.AwayFromZero);

        // UNIFIED grid->device Y: every stroke endpoint (horizontal, vertical, diagonal) uses the
        // SAME per-row device offset (MvdiFont.HorizRowTopOffset, the Off2/Off8 staircase). The
        // previous code split this - horizontals used the staircase but vertical/diagonal used a
        // linear DevY a pixel off - which mis-registered every stroke junction on curved glyphs
        // (a/e/s/o/n...). The generator builds one per-row device table and indexes it for both
        // endpoints of every stroke, so horizontal and vertical strokes sharing a grid row hit the
        // identical row.
        int DevYrow(int gridY) => penYdev - (int)Math.Round(MvdiFont.HorizRowTopOffset(gridY, ky) * sy, MidpointRounding.AwayFromZero);

        // The generator stamps a UNIFORM rectangular pel (pelX wide x pelY tall) at every point
        // along each stroke, extending down-right from the path point.
        // pelX = vertical-stroke width (VertStrokePel, kx-keyed); pelY = horizontal-stroke height
        // (HorizStrokePel, ky-keyed). There is NO direction-dependent thickness - one pel serves
        // horizontals, verticals and diagonals alike (the old per-direction heuristics were the bug).
        int pelX = Math.Max(1, (int)Math.Round(MvdiFont.VertStrokePel(kx) * sx, MidpointRounding.AwayFromZero));
        int pelY = Math.Max(1, (int)Math.Round(MvdiFont.HorizStrokePel(ky) * sy, MidpointRounding.AwayFromZero));

        // Glyph rotation: MVDI rotates the whole character cell about the pen origin by TextRotation.
        // Rotate each stroke endpoint's offset from the pen (screen Y is down, so Ninety reads as
        // 90 deg CCW). For quarter turns the non-square pel turns with the strokes, so swap X/Y.
        var rot = state.TextRotation;
        if (rot == TextRotation.Ninety || rot == TextRotation.TwoSeventy)
        {
            (pelX, pelY) = (pelY, pelX);
        }

        (int X, int Y) RotPoint(int devX, int devY)
        {
            int dx = devX - penXdev, dy = devY - penYdev;
            (int rx, int ry) = rot switch
            {
                TextRotation.Ninety => (dy, -dx),
                TextRotation.OneEighty => (-dx, -dy),
                TextRotation.TwoSeventy => (-dy, dx),
                _ => (dx, dy),
            };
            return (penXdev + rx, penYdev + ry);
        }

        void DrawGlyph(MvdiFont.Glyph gl)
        {
            var segs = gl.Segments;
            for (int i = 0; i + 3 < segs.Length; i += 4)
            {
                var a = RotPoint(DevX(segs[i], gl.LeftBearing), DevYrow(segs[i + 1]));
                var b = RotPoint(DevX(segs[i + 2], gl.LeftBearing), DevYrow(segs[i + 3]));
                DrawableLine.PlotMvdiStroke(image, a.X, a.Y, b.X, b.Y, pelX, pelY, fgColor);
            }
        }

        DrawGlyph(glyph);

        // ANSI X3.110 5.3.2.1: a pending non-spacing accent composes onto this base glyph at the
        // same pen. The MVDI supplementary mark is pre-positioned in the cell grid (above-letter
        // marks at y=8..9, below-letter at y=0..2), so it overlays with the identical grid->device
        // mapping. The accent left-justifies by its own bearing under proportional spacing.
        if (_command.OverlayAccentCode is int accentCode)
        {
            DrawGlyph(MvdiFont.ForSupplementary(accentCode));
        }

        if (state.IsUnderline)
        {
            image.Mutate(ctx =>
            {
                float fullFieldW = MathF.Max(1f, MathF.Abs(charSizeX));
                var (_, pelY) = ConvertNormalizedToScreenScale(size, state.LogicalPel.X, state.LogicalPel.Y);
                float underlineThickness = MathF.Max(1f, MathF.Abs(pelY));
                var underlinePen = Pens.Solid(fgColor, underlineThickness);
                ctx.DrawLine(FillOptions(), underlinePen, new PointF(penXdev, penYdev), new PointF(penXdev + fullFieldW, penYdev));
            });
        }
    }

    /// <summary>
    /// Renders using VGA 8x16 bitmap font with nearest-neighbor scaling.
    /// Pixel-accurate PP3-matching output.
    /// </summary>
    private void DrawBitmapFont(Image<Rgba32> image, NaplpsState state, Size size, char glyphChar)
    {
        var penPoint = ConvertNormalizedToPoint(size, state.Pen.X, state.Pen.Y);
        var (charSizeX, charSizeY) = ConvertNormalizedToScreenScale(size, state.CharSize.X, state.CharSize.Y);

        float fullCellW = MathF.Max(1f, MathF.Abs(charSizeX));
        float cellH = MathF.Max(1f, MathF.Abs(charSizeY));

        // Glyph at proportional width, centered in advance cell.
        // Prevents narrow chars (i, l, t) from stretching to full cell width.
        float widthRatio = GetCharWidthRatio(glyphChar);
        float glyphW = MathF.Max(1f, MathF.Abs(charSizeX) * widthRatio);

        // Advance cell depends on spacing mode
        float advanceCellW;
        if (state.TextSpacing == TextSpacing.Proportional)
        {
            float propDisp = GetProportionalDisplacement(state.CharSize.X, glyphChar);
            var (propW, _) = ConvertNormalizedToScreenScale(size, propDisp, 0);
            advanceCellW = MathF.Max(1f, MathF.Abs(propW));
        }
        else
        {
            float spacingMul = state.TextSpacing switch
            {
                TextSpacing.FiveQuarters => 1.25f,
                TextSpacing.ThreeHalves => 1.5f,
                _ => 1.0f
            };
            advanceCellW = fullCellW * spacingMul;
        }

        float cellW = glyphW;
        float centerOffset = MathF.Max(0, (advanceCellW - glyphW) / 2f);
        float cellTopX = penPoint.X + centerOffset;

        float cellTopY = penPoint.Y - cellH;

        var (fgColor, bgColor) = GetISColorFromState(state);

        if (state.IsReverseVideo)
        {
            (fgColor, bgColor) = (bgColor, fgColor);
        }

        // Look up glyph in VGA font
        char ch = glyphChar;
        int glyphIndex = (ch >= 0x20 && ch <= 0x7F) ? ch - 0x20 : 0;
        int glyphOffset = glyphIndex * VGA_CHAR_HEIGHT;

        image.Mutate(ctx =>
        {
            // Color mode 2: fill background across full cell width
            if (state.ColorMode == 2)
            {
                float bgX = state.TextSpacing == TextSpacing.Proportional ? penPoint.X : penPoint.X;
                float bgW = state.TextSpacing == TextSpacing.Proportional ? cellW + 1 : fullCellW + 1;
                var bgRect = new RectangleF(bgX, cellTopY, bgW, cellH);
                ctx.Fill(FillOptions(), bgColor, bgRect);
            }

            // Nearest-neighbor scale: map each destination pixel to source bitmap
            for (int dy = 0; dy < (int)cellH; dy++)
            {
                int srcRow = dy * VGA_CHAR_HEIGHT / (int)cellH;
                if (srcRow >= VGA_CHAR_HEIGHT || glyphOffset + srcRow >= _vgaFontData.Length)
                {
                    continue;
                }

                byte rowData = _vgaFontData[glyphOffset + srcRow];
                if (rowData == 0)
                {
                    continue;
                }

                for (int dx = 0; dx < (int)cellW; dx++)
                {
                    int srcCol = dx * VGA_CHAR_WIDTH / (int)cellW;
                    if (srcCol >= VGA_CHAR_WIDTH)
                    {
                        continue;
                    }

                    if ((rowData & (1 << (7 - srcCol))) != 0)
                    {
                        int px = (int)(cellTopX + dx);
                        int py = (int)(cellTopY + dy);

                        if (px >= 0 && px < image.Width && py >= 0 && py < image.Height)
                        {
                            image[px, py] = fgColor.ToPixel<Rgba32>();
                        }
                    }
                }
            }

            // ANSI X3.110 §6.2.7.15: underline starts at character field origin,
            // extends across full field width (dx), thickness = logical pel dy.
            if (state.IsUnderline)
            {
                float underlineY = penPoint.Y;
                var (pelX, pelY) = ConvertNormalizedToScreenScale(size, state.LogicalPel.X, state.LogicalPel.Y);
                float underlineThickness = MathF.Max(1f, MathF.Abs(pelY));
                var underlinePen = Pens.Solid(fgColor, underlineThickness);
                ctx.DrawLine(FillOptions(), underlinePen, new PointF(cellTopX, underlineY), new PointF(cellTopX + cellW, underlineY));
            }
        });
    }

    /// <summary>
    /// Renders using TrueType font (PRM5X10 or system fallback) with anti-aliased scaling.
    /// High-resolution output.
    /// </summary>
    private void DrawTrueTypeFont(Image<Rgba32> image, NaplpsState state, Size size, char glyphChar)
    {
        // Convert the pen (normalized NAPLPS coords) to screen pixel coordinates.
        var penPoint = ConvertNormalizedToPoint(size, state.Pen.X, state.Pen.Y);

        // Convert character cell size (normalized) to screen pixels
        var (charSizeX, charSizeY) = ConvertNormalizedToScreenScale(size, state.CharSize.X, state.CharSize.Y);

        float fullCellW = MathF.Max(1f, MathF.Abs(charSizeX));
        float cellH = MathF.Max(1f, MathF.Abs(charSizeY));

        // Glyph at proportional width, centered in advance cell.
        // Prevents narrow chars (i, l, t) from stretching to full cell width.
        float widthRatio = GetCharWidthRatio(glyphChar);
        float glyphW = MathF.Max(1f, MathF.Abs(charSizeX) * widthRatio);

        // Advance cell depends on spacing mode
        float advanceCellW;
        if (state.TextSpacing == TextSpacing.Proportional)
        {
            float propDisp = GetProportionalDisplacement(state.CharSize.X, glyphChar);
            var (propW, _) = ConvertNormalizedToScreenScale(size, propDisp, 0);
            advanceCellW = MathF.Max(1f, MathF.Abs(propW));
        }
        else
        {
            float spacingMul = state.TextSpacing switch
            {
                TextSpacing.FiveQuarters => 1.25f,
                TextSpacing.ThreeHalves => 1.5f,
                _ => 1.0f
            };
            advanceCellW = fullCellW * spacingMul;
        }

        float cellW = glyphW;
        float centerOffset = MathF.Max(0, (advanceCellW - glyphW) / 2f);
        float cellTopX = penPoint.X + centerOffset;
        float cellTopY = penPoint.Y - cellH;

        var rect = new RectangleF(cellTopX, cellTopY, cellW, cellH);

        var (fgColor, bgColor) = GetISColorFromState(state);

        // Reverse video: swap foreground and background colors
        if (state.IsReverseVideo)
        {
            (fgColor, bgColor) = (bgColor, fgColor);
        }

        // Use a fixed font size, then STRETCH it to fit the cell.
        // This mimics how old NAPLPS renderers worked — they'd blit a bitmap
        // font and stretch it to fit the character field dimensions.
        float fontSize = 100f;
        // Always the bundled PRM5X10. There used to be a SystemFonts lookup here for characters
        // outside 0x20-0x7E, which made output depend on which typefaces the host machine happened
        // to have installed - Segoe UI on Windows, Arial on macOS, DejaVu on Linux. It also never
        // rendered the supplementary set correctly: this path has no G2 accent composition, so each
        // non-spacing mark came out as a standalone symbol. See issue #45.
        var font = _fontFamily.CreateFont(fontSize, FontStyle.Regular);

        // Calculate scale factors to stretch the glyph to fit the cell
        // Full horizontal fill, slight vertical margin for authentic spacing
        float targetW = cellW;
        float targetH = cellH * 0.90f;

        float baseScaleX = targetW / _refCharWidth;
        float scaleY = targetH / _refLineHeight;

        // For thin characters, apply a horizontal stretch boost to fill the cell better
        // These characters have lots of built-in whitespace in the font design
        float boostFactor = GetThinCharacterBoost(glyphChar);
        float scaleX = baseScaleX * boostFactor;

        // Adjust X position to center the boosted glyph in its cell
        float baseGlyphWidth = _refCharWidth * baseScaleX;
        float boostedGlyphWidth = _refCharWidth * scaleX;
        float extraWidth = boostedGlyphWidth - baseGlyphWidth;
        float adjustedCellTopX = cellTopX - extraWidth / 2f;

        // Scaled dimensions
        float scaledLineHeight = _refLineHeight * scaleY;
        float scaledTopOffset = _refTopOffset * scaleY;

        // Center on cap height so text aligns visually with adjacent elements.
        // Full _refLineHeight includes descender space which shifts caps upward.
        float scaledCapHeight = _refCapHeight * scaleY;
        float vertPad = MathF.Min((cellH - scaledCapHeight) / 2f, cellH - scaledLineHeight);

        // Position for the glyph's top to land at cellTopY + vertPad
        // But we need to account for the transform scaling around origin
        float textOriginY = (cellTopY + vertPad - scaledTopOffset) / scaleY;
        float textOriginX = adjustedCellTopX / scaleX;

        GetCharXBackoff(ref textOriginX, glyphChar);

        // Create transform that scales from origin (0,0)
        var transform = Matrix3x2.CreateScale(scaleX, scaleY);

        // After creating the scale transform, compose with rotation if needed
        if (state.TextRotation != TextRotation.Zero)
        {
            float rotationRadians = state.TextRotation switch
            {
                TextRotation.Ninety => -MathF.PI / 2f,
                TextRotation.OneEighty => MathF.PI,
                TextRotation.TwoSeventy => MathF.PI / 2f,
                _ => 0f
            };

            // Rotate around the pen position (character field origin)
            var rotationTransform = Matrix3x2.CreateRotation(rotationRadians, new System.Numerics.Vector2(penPoint.X, penPoint.Y));
            transform = transform * rotationTransform;
        }

        var drawingOptions = new DrawingOptions
        {
            Transform = transform,
            GraphicsOptions = new GraphicsOptions { Antialias = Options.Antialias && !Options.HardText }
        };

        var charText = glyphChar.ToString();

        image.Mutate(ctx =>
        {
            // In color mode 2, fill character field with background color
            // (modes 0 and 1 only draw foreground pixels, no background fill)
            if (state.ColorMode == 2)
            {
                // Extend width by 1px to avoid gaps between adjacent characters
                var bgRect = new RectangleF(rect.X, rect.Y, rect.Width + 1, rect.Height);

                ctx.Fill(FillOptions(), bgColor, bgRect);
            }

            if (Options.DebugTextDrawing)
            {
                if (state.ColorMode != 2)
                {
                    ctx.Fill(FillOptions(), bgColor, rect);
                }

                var debugStrokePen = Pens.Solid(fgColor, 1f);
                var debugDashedPen = Pens.Dash(fgColor, 1f);

                // Crosshair at pen origin (bottom-left of the box)
                ctx.DrawLine(debugStrokePen, new PointF(cellTopX - 4, penPoint.Y), new PointF(cellTopX + 4, penPoint.Y));
                ctx.DrawLine(debugStrokePen, new PointF(cellTopX, penPoint.Y - 4), new PointF(cellTopX, penPoint.Y + 4));
                ctx.Draw(debugStrokePen, rect);

                // ASCII code label
                var labelFontSize = MathF.Max(8f, MathF.Min(14f, cellW));
                var labelFont = _fontFamily.CreateFont(labelFontSize, FontStyle.Regular);
                var labelText = $"{(int)glyphChar}";
                ctx.DrawText(labelText, labelFont, fgColor, new PointF(cellTopX + 2, cellTopY + 2));

                // Baseline reference line
                float debugBaseline = cellTopY + vertPad + scaledLineHeight;
                ctx.DrawLine(debugDashedPen, new PointF(cellTopX, debugBaseline), new PointF(cellTopX + cellW, debugBaseline));
            }

            // Draw with stretch transform
            ctx.DrawText(drawingOptions, charText, font, fgColor, new PointF(textOriginX, textOriginY));

            // ANSI X3.110 §6.2.7.15: underline starts at character field origin,
            // extends across full field width (dx, not proportional), thickness = logical pel dy.
            if (state.IsUnderline)
            {
                float fullFieldW = MathF.Max(1f, MathF.Abs(charSizeX));
                float underlineY = penPoint.Y;
                var (pelX, pelY) = ConvertNormalizedToScreenScale(size, state.LogicalPel.X, state.LogicalPel.Y);
                float underlineThickness = MathF.Max(1f, MathF.Abs(pelY));
                var underlinePen = Pens.Solid(fgColor, underlineThickness);
                ctx.DrawLine(FillOptions(), underlinePen, new PointF(cellTopX, underlineY), new PointF(cellTopX + fullFieldW, underlineY));
            }
        });
    }
}
