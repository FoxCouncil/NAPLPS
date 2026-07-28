// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using System.ComponentModel;
using AC = NAPLPS.Commands.AsciiCharCommand;
using CC = NAPLPS.Commands.ControlCommand;
using MC = NAPLPS.Commands.MosaicElementCommand;
using NCR = NAPLPS.NaplpsCommandReference;

namespace NAPLPS;

/// <summary>This was normally stored in some memory block, 4Kb iirc</summary>
public class NaplpsState
{
    public static JsonSerializerOptions GlobalJsonSerializerOptions { get; } = new()
    {
        Converters = { new NCRArrayJsonConverter(), new Vector3JsonConverter(), new Vector2JsonConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /* Initialization */

    public static readonly int C0 = 0;
    public static readonly int GLeft = 32;
    public static readonly int C1 = 128;
    public static readonly int GRight = 160;

    /// <summary>
    /// Identifies one of the four G-set slots per ANSI X3.110 §4.3.2.
    /// G0-G3 designations are independent of GL/GR invocations.
    /// </summary>
    public enum GsetSlot
    {
        G0,
        G1,
        G2,
        G3,
    }

    // Current G-set designations (which character set is loaded into each slot).
    // Defaults per §4.3.3: G0 = Primary, G1 = General PDI (POI), G2 = Supplementary, G3 = Mosaic.
    private NCR[] _g0Designation = PrimaryCharacterSet;
    private NCR[] _g1Designation = GeneralPDISet;
    private NCR[] _g2Designation = SupplementaryCharacterSet;
    private NCR[] _g3Designation = MosaicSet;

    // Which G-set is currently invoked into each in-use area. Defaults: G0→GL, G1→GR.
    private GsetSlot _glInvocation = GsetSlot.G0;
    private GsetSlot _grInvocation = GsetSlot.G1;

    // Which G-set slots are designated as the MACRO SET (final byte 0x7A). The macro set has
    // no static table: a byte resolved through a macro-designated slot invokes that macro
    // (parse-time expansion). Tracked per-slot so SS3/LS3 into a macro-designated G3 works.
    private readonly bool[] _gsetIsMacro = new bool[4];

    /// <summary>
    /// True if <paramref name="opcode"/> would resolve through a G-set currently designated as
    /// the macro set (via the active single-shift, or the locked GL/GR invocation). The parser
    /// treats such a byte as a macro invocation rather than a character/command lookup.
    /// </summary>
    public bool IsMacroByte(byte opcode)
    {
        bool inGL = opcode >= 0x20 && opcode <= 0x7F;
        bool inGR = opcode >= 0xA0;
        if (!inGL && !inGR)
        {
            return false;
        }

        if (PendingSingleShift.HasValue)
        {
            return _gsetIsMacro[(int)PendingSingleShift.Value];
        }

        return _gsetIsMacro[(int)(inGL ? _glInvocation : _grInvocation)];
    }

    /// <summary>
    /// One-shot single-shift state per §6.1.3.3 / §6.1.3.4. When set, the very next
    /// resolved byte (in GL or GR) uses this slot's designation instead of the
    /// invoked one. Cleared after consumption.
    /// </summary>
    public GsetSlot? PendingSingleShift { get; set; }

    /// <summary>
    /// ANSI X3.110 §5.3.2.1: a non-spacing accent character (G2 supplementary set positions
    /// 0x40-0x4F) leaves no advance and is composed onto the NEXT spacing character.
    /// Set when a non-spacing accent is processed, consumed by the next AsciiCharCommand
    /// that overlays it onto its own glyph at the same pen position.
    /// </summary>
    [JsonIgnore]
    public char? PendingAccentChar { get; set; }

    /// <summary>
    /// G2 code (0x20..0x7F) of a pending non-spacing accent, alongside <see cref="PendingAccentChar"/>.
    /// Selects the MVDI supplementary glyph to overlay (see <see cref="Drawing.MvdiFont.ForSupplementary"/>) —
    /// the Unicode char alone is ambiguous for the ASCII-range accents (grave, tilde, slash, underscore).
    /// </summary>
    [JsonIgnore]
    public int? PendingAccentCode { get; set; }

    /// <summary>
    /// Set by <see cref="ResolveByte"/> when the byte just resolved came from the G2
    /// SupplementaryCharacterSet (via SS2 single-shift or an LS2/LS2R locking shift), with the
    /// G2 code (0x20..0x7F) in <see cref="ResolvedSupplementaryCode"/>. Consumed immediately by
    /// the AsciiCharCommand constructed for that byte to pick the supplementary glyph.
    /// </summary>
    [JsonIgnore]
    public bool ResolvedFromSupplementary { get; private set; }

    /// <summary>G2 code (0x20..0x7F) accompanying <see cref="ResolvedFromSupplementary"/>.</summary>
    [JsonIgnore]
    public int ResolvedSupplementaryCode { get; private set; }

    /// <summary>
    /// The NAPLPS system this state belongs to. Set at parse time; drives Prodigy-specific text
    /// metrics (MVDI vector-font advance widths) and defaults. Preserved across per-command clones.
    /// </summary>
    public NaplpsSystemType SystemType { get; set; } = NaplpsSystemType.NAPLPS;

    public NaplpsState()
    {
        Reset();
    }

    /// <summary>
    /// Construct a state for a system that supports more than the default 16 palette entries.
    /// Per ANSI X3.110 §5.2.1, systems capable of >16 colors should generate the extended
    /// palette algorithmically (greyscale ramp + hue-circle). 16 stays on <see cref="ColorMapDefaults"/>
    /// for back-compat with the rest of the codebase.
    /// </summary>
    public NaplpsState(int colorCapacity) : this()
    {
        ColorCapacity = colorCapacity;
        if (colorCapacity > 16)
        {
            ColorMap = GenerateDefaultPalette(colorCapacity);
        }
    }

    /// <summary>
    /// Number of palette entries this system can address. 16 is the NAPLPS default; spec
    /// allows 32, 64, 128, 256 for extended-color systems. Drives the initial <see cref="ColorMap"/>
    /// fill at construction time.
    /// </summary>
    public int ColorCapacity { get; set; } = 16;

    /* In-Use Table Manipulation */

    public void Reset()
    {
        _g0Designation = PrimaryCharacterSet;
        _g1Designation = GeneralPDISet;
        _g2Designation = SupplementaryCharacterSet;
        _g3Designation = MosaicSet;
        _glInvocation = GsetSlot.G0;
        _grInvocation = GsetSlot.G1;
        PendingSingleShift = null;

        // C0 and C1 are fixed (not dynamically designated in NAPLPS).
        C0Set.CopyTo(InUseTable, C0);
        C1Set.CopyTo(InUseTable, C1);

        RebuildInUseTable();
    }

    /// <summary>
    /// Copy the currently invoked G-sets into the GL and GR areas of the in-use table.
    /// Call after any locking shift or designation that affects an invoked slot.
    /// </summary>
    private void RebuildInUseTable()
    {
        GetGsetTable(_glInvocation).CopyTo(InUseTable, GLeft);
        GetGsetTable(_grInvocation).CopyTo(InUseTable, GRight);
    }

    private NCR[] GetGsetTable(GsetSlot slot)
    {
        return slot switch
        {
            GsetSlot.G0 => _g0Designation,
            GsetSlot.G1 => _g1Designation,
            GsetSlot.G2 => _g2Designation,
            GsetSlot.G3 => _g3Designation,
            _ => throw new ArgumentOutOfRangeException(nameof(slot)),
        };
    }

    /// <summary>SI (0x0F): invoke G0 into GL.</summary>
    public void DoShiftIn()
    {
        _glInvocation = GsetSlot.G0;
        InLockingManner = true;
        RebuildInUseTable();
    }

    /// <summary>SO (0x0E): invoke G1 into GL.</summary>
    public void DoShiftOut()
    {
        _glInvocation = GsetSlot.G1;
        InLockingManner = true;
        RebuildInUseTable();
    }

    /// <summary>LS2 (ESC 6/14): invoke G2 into GL.</summary>
    public void DoLockingShiftTwo()
    {
        _glInvocation = GsetSlot.G2;
        InLockingManner = true;
        RebuildInUseTable();
    }

    /// <summary>LS3 (ESC 6/15): invoke G3 into GL.</summary>
    public void DoLockingShiftThree()
    {
        _glInvocation = GsetSlot.G3;
        InLockingManner = true;
        RebuildInUseTable();
    }

    /// <summary>LS1R (ESC 7/14 or 6/11): invoke G1 into GR.</summary>
    public void DoLockingShiftOneRight()
    {
        _grInvocation = GsetSlot.G1;
        InLockingManner = true;
        RebuildInUseTable();
    }

    /// <summary>LS2R (ESC 7/13 or 6/12): invoke G2 into GR.</summary>
    public void DoLockingShiftTwoRight()
    {
        _grInvocation = GsetSlot.G2;
        InLockingManner = true;
        RebuildInUseTable();
    }

    /// <summary>LS3R (ESC 7/12 or 6/13): invoke G3 into GR.</summary>
    public void DoLockingShiftThreeRight()
    {
        _grInvocation = GsetSlot.G3;
        InLockingManner = true;
        RebuildInUseTable();
    }

    /// <summary>SS2 (0x19): invoke G2 for the next single byte only.</summary>
    public void DoSingleShiftTwo()
    {
        PendingSingleShift = GsetSlot.G2;
    }

    /// <summary>SS3 (0x1D): invoke G3 for the next single byte only.</summary>
    public void DoSingleShiftThree()
    {
        PendingSingleShift = GsetSlot.G3;
    }

    /// <summary>
    /// Designate (load) a G-set slot with a different character set. Per §4.3.2,
    /// "if any of the G-sets are redesignated via an escape sequence while in the
    /// in-use table, the new code interpretations are simultaneously invoked",
    /// so the in-use table is rebuilt automatically when the slot is currently invoked.
    /// </summary>
    public void DesignateGset(GsetSlot slot, NCR[] set)
    {
        switch (slot)
        {
            case GsetSlot.G0: _g0Designation = set; break;
            case GsetSlot.G1: _g1Designation = set; break;
            case GsetSlot.G2: _g2Designation = set; break;
            case GsetSlot.G3: _g3Designation = set; break;
        }

        if (_glInvocation == slot || _grInvocation == slot)
        {
            RebuildInUseTable();
        }
    }

    /// <summary>
    /// Resolve a single byte to its NCR, consuming any pending single-shift.
    /// Use this in the main parse loop for byte-to-command dispatch. Use
    /// <see cref="PeekByteWithoutConsumingShift"/> for lookahead that must not
    /// trigger the one-shot consumption.
    /// </summary>
    public NaplpsCommandReference? ResolveByte(byte opcode)
    {
        ResolvedFromSupplementary = false;
        ResolvedSupplementaryCode = 0;

        NaplpsCommandReference? chosen;

        // SS2/SS3 only affect the GL or GR areas, not C0/C1.
        if (PendingSingleShift.HasValue && (opcode is >= 0x20 and <= 0x7F || opcode >= 0xA0))
        {
            var alt = GetGsetTable(PendingSingleShift.Value);
            PendingSingleShift = null;
            int idx = opcode <= 0x7F ? opcode - 0x20 : opcode - 0xA0;
            chosen = idx >= 0 && idx < alt.Length ? alt[idx] : null;
        }
        else
        {
            chosen = InUseTable[opcode];
        }

        // Tag when the resolved character came from the G2 supplementary set (single-shifted or
        // locking-shifted into GL/GR). Keyed on set identity so a re-designated G2 doesn't match.
        if (chosen != null && (opcode is >= 0x20 and <= 0x7F || opcode >= 0xA0))
        {
            int g2idx = opcode <= 0x7F ? opcode - 0x20 : opcode - 0xA0;
            if (g2idx >= 0 && g2idx < SupplementaryCharacterSet.Length
                && ReferenceEquals(chosen, SupplementaryCharacterSet[g2idx]))
            {
                ResolvedFromSupplementary = true;
                ResolvedSupplementaryCode = 0x20 + g2idx;
            }
        }

        return chosen;
    }

    /// <summary>Peek-style byte resolution that does NOT consume PendingSingleShift.</summary>
    public NaplpsCommandReference? PeekByteWithoutConsumingShift(byte opcode)
    {
        return InUseTable[opcode];
    }

    public bool DoEscape(NaplpsOperands sequence)
    {
        if (sequence.Count == 0)
        {
            return false;
        }

        // Single-byte ESC sequences (no F-byte): C-set designation prefixes and locking shifts.
        if (sequence.Count == 1)
        {
            switch (sequence[0])
            {
                case CC.EscapeC0Set: // ESC 2/1 — not a complete designation on its own
                {
                    C0Set.CopyTo(InUseTable, C0);
                    return true;
                }

                case 0x6E: DoLockingShiftTwo(); return true;            // LS2
                case 0x6F: DoLockingShiftThree(); return true;          // LS3
                case 0x7E: DoLockingShiftOneRight(); return true;       // LS1R
                case 0x7D: DoLockingShiftTwoRight(); return true;       // LS2R
                case 0x7C: DoLockingShiftThreeRight(); return true;     // LS3R
                case 0x6B: DoLockingShiftOneRight(); return true;       // LS1R alt
                case 0x6C: DoLockingShiftTwoRight(); return true;       // LS2R alt
                case 0x6D: DoLockingShiftThreeRight(); return true;     // LS3R alt
            }

            return false;
        }

        // Two-byte ESC I F sequences: G-set designation per §4.3 / Table 1.
        if (sequence.Count >= 2)
        {
            // Legacy single-case handling for ESC 2/1 (C0 designation) when followed by another byte.
            if (sequence[0] == CC.EscapeC0Set)
            {
                C0Set.CopyTo(InUseTable, C0);
                return true;
            }

            GsetSlot? slot = sequence[0] switch
            {
                0x28 => GsetSlot.G0,        // 2/8 — 94-char into G0
                0x29 => GsetSlot.G1,        // 2/9 — into G1
                0x2A => GsetSlot.G2,        // 2/10 — into G2
                0x2B => GsetSlot.G3,        // 2/11 — into G3
                0x2D => GsetSlot.G1,        // 2/13 — 96-char alt into G1
                0x2E => GsetSlot.G2,        // 2/14 — 96-char alt into G2
                0x2F => GsetSlot.G3,        // 2/15 — 96-char alt into G3
                _ => null,
            };

            if (slot.HasValue)
            {
                // Final byte 0x7A designates the MACRO SET into this slot (it has no static
                // table). Bytes resolved through this slot then invoke macros (see IsMacroByte).
                if (sequence[1] == 0x7A)
                {
                    _gsetIsMacro[(int)slot.Value] = true;
                    return true;
                }

                var newSet = ResolveSetFromFinalByte(sequence[1]);
                if (newSet != null)
                {
                    _gsetIsMacro[(int)slot.Value] = false;
                    DesignateGset(slot.Value, newSet);
                }
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Map an ESC I F final byte to a G-set table per §4.3 Table 1. Returns null
    /// for sets we don't yet have static tables for (Macro Set, DRCS) — those
    /// are currently dynamic per-character lookups handled elsewhere.
    /// </summary>
    private static NCR[]? ResolveSetFromFinalByte(byte finalByte)
    {
        return finalByte switch
        {
            0x42 => PrimaryCharacterSet,            // 4/2 — Primary
            0x7C => SupplementaryCharacterSet,      // 7/12 — Supplementary
            0x57 => GeneralPDISet,                  // 5/7 — POI / PDI
            0x7D => MosaicSet,                      // 7/13 — Mosaic
            // 0x7A — Macro Set (dynamic per-character; not a static table)
            // 0x7B — DRCS Set (dynamic per-character; not a static table)
            _ => null,
        };
    }

    /* In-Use Tables */
    [Browsable(false)]
    // AOT: route this property through the NCR-array converter so the JSON source gen
    // doesn't attempt to generate reflection-based code for NaplpsCommandReference (which
    // has a DAM-annotated Type parameter the source gen can't satisfy statically).
    [JsonConverter(typeof(NAPLPS.Helpers.NCRArrayJsonConverter))]
    public NCR[] InUseTable { get; set; } = new NCR[256];

    [Category("In-Use Tables")]
    [ReadOnly(true)]
    public bool InLockingManner { get; set; } = false;

    /* Parsing States */

    /// <summary>Future Spec [2 = x,y,0 3 = x,y,z]</summary>
    /// <figure>11</figure>
    [Category("Parsing")]
    [ReadOnly(true)]
    public byte Dimensionality { get; set; } = 2;

    [Category("Parsing")]
    [ReadOnly(true)]
    public byte MultiByteValue { get; set; } = 3;

    [Category("Parsing")]
    [ReadOnly(true)]
    public byte SingleByteValue { get; set; } = 1;

    /* Drawing States */

    [Category("Drawing")]
    [ReadOnly(true)]
    public Vector2 LogicalPel { get; set; } = new(0f, 0f);

    [Category("Drawing")]
    [ReadOnly(true)]
    public Vector3 Pen { get; set; } = new();

    /// <summary>
    /// ANSI X3.110: The graphics drawing point, separate from the text cursor (Pen).
    /// In MoveTogether mode (default), DrawingPoint always equals Pen.
    /// In other modes, they can diverge.
    /// </summary>
    [Browsable(false)]
    [JsonIgnore]
    public Vector3 DrawingPoint { get; set; } = new();

    /// <summary>
    /// Call after a text operation (character display, cursor movement) updates Pen.
    /// Syncs DrawingPoint based on TextMoveAttributes.
    /// </summary>
    public void SyncAfterTextMove()
    {
        switch (TextMoveAttributes)
        {
            case TextMoveAttributes.MoveTogether:
            case TextMoveAttributes.CursorLeads:
            {
                DrawingPoint = Pen;
            }
            break;
        }
    }

    /// <summary>
    /// Call after a graphics operation updates DrawingPoint.
    /// Syncs Pen (text cursor) based on TextMoveAttributes.
    /// </summary>
    public void SyncAfterGraphicsMove()
    {
        switch (TextMoveAttributes)
        {
            case TextMoveAttributes.MoveTogether:
            case TextMoveAttributes.DrawingPointLeads:
            {
                Pen = DrawingPoint;
            }
            break;
        }
    }

    [Category("Drawing")]
    [ReadOnly(true)]
    public NaplpsField Field { get; set; } = new();

    [Category("Drawing")]
    [ReadOnly(true)]
    public NaplpsTexture Texture { get; set; } = new();

    /* Text States */

    /// <summary>
    /// Rotation causes the character field and the cursor to rotate counterclockwise
    /// about the character field origin.This rotation is measured relative to
    /// horizontal within the unit screen and is independent of the character path.
    /// The character field origin is the lower left corner of the character field at the
    /// default 0 degree rotation regardless of the sign of the character field
    /// dimensions dx and dy. All alphanumeric characters (including
    /// diacritical marks and underlines), DRCS, mosaics, and separated mosaic
    /// characters, and the underline produced when underline mode (see 6.2.7.15) is in
    /// effect, are affected by rotation so that the relative position of the images
    /// within the character field is unchanged.
    /// </summary>
    [Category("Text")]
    [ReadOnly(true)]
    public TextRotation TextRotation { get; set; }

    /// <summary>
    /// This determines the direction of the character path, that is, the direction in 
    /// which the cursor is advanced after a character is deposited. The character path is 
    /// defined relative to horizontal within the unit screen and is independent of the character rotation.
    /// The default character path is right.
    /// </summary>
    [Category("Text")]
    [ReadOnly(true)]
    public TextPath TextPath { get; set; }

    /// <summary>
    /// This determines the distance the cursor is moved after a character is displayed or
    /// after a SPACE or APB(backspace) or APF(horizontal tab) character is
    /// received. The distance the cursor is moved is in multiples of the character
    /// field width(dx) or height(dy), whichever lies parallel to the character path,
    /// depending on the character path and character rotation. This is known as the
    /// intercharacter spacing.
    /// </summary>
    [Category("Text")]
    [ReadOnly(true)]
    public TextSpacing TextSpacing { get; set; }

    [Category("Text")]
    [ReadOnly(true)]
    public TextInterrowSpacing TextInterrowSpacing { get; set; }

    [Category("Text")]
    [ReadOnly(true)]
    public TextMoveAttributes TextMoveAttributes { get; set; }

    [Category("Text")]
    [ReadOnly(true)]
    public TextCursorStyle TextCursorStyle { get; set; }

    /// <summary>
    /// The default dimensions of the character field are dx = 1/40 and dy = 5/128,
    /// consistent with the physical resolution.
    /// </summary>
    [Category("Text")]
    [ReadOnly(true)]
    public Vector2 CharSize { get; set; } = new Vector2(1.0f / 40.0f, 5.0f / 128.0f);

    /* C1 Control States */

    /// <summary>Reverse video mode swaps foreground/background for text</summary>
    [Category("C1 Controls")]
    [ReadOnly(true)]
    public bool IsReverseVideo { get; set; } = false;

    /// <summary>Underline mode draws underline under text characters</summary>
    [Category("C1 Controls")]
    [ReadOnly(true)]
    public bool IsUnderline { get; set; } = false;

    /// <summary>Text size mode: 0=normal, 1=small, 2=medium, 3=double height, 4=double size</summary>
    [Category("C1 Controls")]
    [ReadOnly(true)]
    public byte TextSizeMode { get; set; } = 0;

    /// <summary>Scroll mode enables scrolling when text reaches bottom of field</summary>
    [Category("C1 Controls")]
    [ReadOnly(true)]
    public bool IsScrollMode { get; set; } = false;

    /// <summary>Set to true when an APD in scroll mode would move pen below field origin</summary>
    [Browsable(false)]
    public bool ScrollEventOccurred { get; set; } = false;

    /// <summary>Word wrap mode wraps text at word boundaries</summary>
    /// <summary>
    /// ANSI X3.110 §5.3.2.3.6: "If an explicit APR APD (or APD APR) sequence is received
    /// after an automatic APR APD is executed but before the character field origin is moved,
    /// aligned, or set by any other received command or sequence, the explicit APR APD (or
    /// APD APR) sequence shall be executed as a null operation."
    /// Set after auto-wrap fires. Cleared by any command that moves/aligns/sets the cursor,
    /// or after the explicit APR+APD pair is consumed as a no-op.
    /// </summary>
    [Browsable(false)]
    public bool AutoWrapJustOccurred { get; set; } = false;

    [Category("C1 Controls")]
    [ReadOnly(true)]
    public bool IsWordWrapMode { get; set; } = false;

    /// <summary>Number of BEL (0x07) characters received. GUI/CLI can use this to trigger alerts.</summary>
    [Browsable(false)]
    [JsonIgnore]
    public int BellCount { get; set; } = 0;

    /// <summary>Set when CAN (0x18) is received to immediately terminate macro execution.</summary>
    [Browsable(false)]
    [JsonIgnore]
    public bool IsCancelRequested { get; set; } = false;

    /// <summary>
    /// Cursor position where the current run of characters began - the last place the cursor was
    /// put by something other than a character advance or an automatic CR-LF. Used to decide
    /// whether a run is flowing inside the active field and therefore subject to its auto-wrap.
    /// Maintained by <c>AsciiCharCommand</c> together with <see cref="LastCharPen"/>.
    /// </summary>
    [Browsable(false)]
    [JsonIgnore]
    public Vector3 TextRunOrigin { get; set; } = new();

    /// <summary>
    /// Cursor position left behind by the previous character. A mismatch against
    /// <see cref="Pen"/> on the next character means the cursor was moved externally, which
    /// starts a new run.
    /// </summary>
    [Browsable(false)]
    [JsonIgnore]
    public Vector3? LastCharPen { get; set; }

    /// <summary>Blink mode causes subsequent text to blink</summary>
    [Category("C1 Controls")]
    [ReadOnly(true)]
    public bool IsBlinkMode { get; set; } = false;

    /// <summary>Protect mode prevents field from being modified</summary>
    [Category("C1 Controls")]
    [ReadOnly(true)]
    public bool IsProtectMode { get; set; } = false;

    /* Macro States */

    /// <summary>Storage for defined macros (keyed by macro name character)</summary>
    [Browsable(false)]
    public Dictionary<char, byte[]> Macros { get; set; } = new();

    /// <summary>Currently defining a macro (null if not in macro definition mode)</summary>
    [Browsable(false)]
    public char? MacroBeingDefined { get; set; } = null;

    /// <summary>Type of macro being defined: 0=DEF MACRO, 1=DEFP MACRO, 2=DEFT MACRO</summary>
    [Browsable(false)]
    public byte MacroDefType { get; set; } = 0;

    /// <summary>Buffer for collecting macro bytes during definition</summary>
    [Browsable(false)]
    public List<byte> MacroBuffer { get; set; } = new();

    /* DRCS States */

    /// <summary>Storage for DRCS character bitmaps (keyed by character code)</summary>
    [Browsable(false)]
    public Dictionary<byte, bool[,]> DrcsCharacters { get; set; } = new();

    /// <summary>Currently defining DRCS characters (null if not in DRCS definition mode)</summary>
    [Browsable(false)]
    public byte? DrcsStartCode { get; set; } = null;

    /// <summary>Buffer for collecting DRCS bitmap data during definition</summary>
    [Browsable(false)]
    public List<byte> DrcsBuffer { get; set; } = new();

    /* Texture Pattern States */

    /// <summary>Programmable texture mask A (default: vertical hatching)</summary>
    [Browsable(false)]
    public bool[,]? TextureMaskA { get; set; } = null;

    /// <summary>Programmable texture mask B (default: horizontal hatching)</summary>
    [Browsable(false)]
    public bool[,]? TextureMaskB { get; set; } = null;

    /// <summary>Programmable texture mask C (default: cross hatching)</summary>
    [Browsable(false)]
    public bool[,]? TextureMaskC { get; set; } = null;

    /// <summary>Programmable texture mask D (default: solid)</summary>
    [Browsable(false)]
    public bool[,]? TextureMaskD { get; set; } = null;

    /// <summary>Currently defining texture pattern (null if not in texture definition mode)</summary>
    [Browsable(false)]
    public byte? TextureBeingDefined { get; set; } = null;

    /// <summary>Buffer for collecting texture pattern data during definition</summary>
    [Browsable(false)]
    public List<byte> TextureBuffer { get; set; } = new();

    /* Blink Animation States */

    /// <summary>List of active blink processes</summary>
    [Browsable(false)]
    public List<BlinkProcess> BlinkProcesses { get; set; } = new();

    /* Color States */

    [Category("Color")]
    [ReadOnly(true)]
    public byte ColorMode { get; set; }

    [Category("Color")]
    [ReadOnly(true)]
    public Dictionary<byte, NaplpsColor> ColorMap { get; set; } = new(ColorMapDefaults);

    [Category("Color")]
    [ReadOnly(true)]
    public byte ColorMapForeground { get; set; } = 0x07;

    [Category("Color")]
    [ReadOnly(true)]
    public byte ColorMapBackground { get; set; }

    [Category("Color")]
    [ReadOnly(true)]
    public bool IsTransparent { get; set; }

    /// <summary>Tracks which palette entries have been used by SET COLOR or SELECT COLOR since last reset (for mode 0 auto-allocation).</summary>
    [Browsable(false)]
    public HashSet<byte> UsedPaletteEntries { get; set; } = new();

    [Category("Color")]
    [ReadOnly(true)]
    public NaplpsColor Foreground { get; set; } = NaplpsColor.White;

    [Category("Color")]
    [ReadOnly(true)]
    public NaplpsColor Background { get; set; } = new();

    /* Error Tracking */

    [JsonIgnore]
    public List<NaplpsError> Errors { get; } = [];

    public void RecordError(NaplpsErrorSeverity severity, NaplpsErrorType type, string message, byte? opcode = null, long? streamPosition = null)
    {
        Errors.Add(new NaplpsError(severity, type, message, opcode, streamPosition));


#if DEBUG
        if (severity == NaplpsErrorSeverity.Error)
        {
            System.Diagnostics.Debugger.Break();
        }
#endif
    }

    /* Helpers */

    // AOT-safe JSON round-trip: use the source-generated JsonTypeInfo for NaplpsState.
    // We can't pass the whole GlobalJsonSerializerOptions (with its runtime Converters list)
    // directly to the typed overload, so we instead look up the type-info from a dedicated
    // context that inherits the naming policy + write-indented setting, and inject the
    // custom converters via the typeinfo's Options property.
    private static readonly JsonTypeInfo<NaplpsState> s_typeInfo = CreateTypeInfo();

    private static JsonTypeInfo<NaplpsState> CreateTypeInfo()
    {
        // Build fresh options that carry OUR custom converters (NCRArray/Vector3/Vector2)
        // alongside the source gen's naming policy + indentation, then hand them to a new
        // context instance. Can't mutate `NaplpsStateJsonContext.Default.Options` — that
        // instance is locked read-only once any type-info has been resolved from it.
        var opts = new JsonSerializerOptions(GlobalJsonSerializerOptions);
        var ctx = new NaplpsStateJsonContext(opts);
        return (JsonTypeInfo<NaplpsState>)ctx.GetTypeInfo(typeof(NaplpsState))!;
    }

    // The source gen still emits a metadata table for NaplpsCommandReference because NCR[]
    // appears as a property type even though InUseTable has an explicit converter. That
    // metadata is DEAD CODE — NCRArrayJsonConverter handles NCR[] end-to-end without
    // ever invoking the generated NCR ctor metadata. Suppressing IL2026/IL3050 at the
    // Serialize/Deserialize call sites since we've verified the actual runtime path is
    // converter-only.
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "NCR[] round-trips through NCRArrayJsonConverter; generated NCR metadata is never executed.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "NCR[] round-trips through NCRArrayJsonConverter; generated NCR metadata is never executed.")]
    public string ToJson() => JsonSerializer.Serialize(this, s_typeInfo);

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "NCR[] round-trips through NCRArrayJsonConverter; generated NCR metadata is never executed.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "NCR[] round-trips through NCRArrayJsonConverter; generated NCR metadata is never executed.")]
    public static NaplpsState FromJson(string json) => JsonSerializer.Deserialize(json, s_typeInfo) ?? new();

    public NaplpsState Clone()
    {
        var json = ToJson();

        return FromJson(json);
    }

    // This is the "viewer" property that will show in the PropertyGrid.
    [JsonIgnore]
    [Category("In-Use Tables")]
    [ReadOnly(true)]
    [DisplayName("In-Use Table")]
    [Description("Human-readable view of the 256-entry in-use table.")]
    public string InUseTableView => FormatInUseTable();

    private string FormatInUseTable()
    {
        static bool Matches(NCR[] table, int offset, NCR[] set)
        {
            if (offset + set.Length > table.Length)
            {
                return false;
            }

            for (int i = 0; i < set.Length; i++)
            {
                if (!ReferenceEquals(table[offset + i], set[i]))
                {
                    return false;
                }
            }

            return true;
        }

        string Resolve(int offset, params (string name, NCR[] set)[] candidates)
        {
            foreach (var (name, set) in candidates)
            {
                if (Matches(InUseTable, offset, set))
                {
                    return name;
                }
            }

            return "Unknown";
        }

        var c0 = Resolve(C0, ("C0Set", C0Set));
        var gLeft = Resolve(GLeft, ("PrimaryCharacterSet", PrimaryCharacterSet), ("SupplementaryCharacterSet", SupplementaryCharacterSet), ("GeneralPDISet", GeneralPDISet), ("MosaicSet", MosaicSet));
        var c1 = Resolve(C1, ("C1Set", C1Set));
        var gRight = Resolve(GRight, ("GeneralPDISet", GeneralPDISet), ("MosaicSet", MosaicSet));

        return $"C0={c0}\nGLeft={gLeft}\nC1={c1}\nGRight={gRight}";
    }

    public override string ToString()
    {
        return $"{MultiByteValue}/{SingleByteValue} <{ColorMode},<{ColorMapForeground:D2}, {ColorMapBackground:D2}> F:{Foreground:D2} B:{Background:D2} <{Pen.X},{Pen.Y}>({LogicalPel.X},{LogicalPel.Y})";
    }

    /* Defaults */

    /// <summary>
    /// Generates a default palette per ANSI X3.110 algorithm:
    /// First half = uniformly spaced greyscale (R=G=B).
    /// Second half = hues equally spaced around the hue circle
    /// (Blue at 0, Red at 120, Green at 240 degrees).
    /// </summary>
    public static Dictionary<byte, NaplpsColor> GenerateDefaultPalette(int entryCount)
    {
        var palette = new Dictionary<byte, NaplpsColor>();
        int half = entryCount / 2;

        // First half: greyscale
        for (int i = 0; i < half; i++)
        {
            byte val = (byte)(i * 255 / Math.Max(1, half - 1));
            palette[(byte)i] = new NaplpsColor(val, val, val);
        }

        // Second half: hues around the circle
        // Blue=0, Red=120, Green=240 degrees
        for (int i = 0; i < half; i++)
        {
            float angle = i * 360.0f / half;
            float blueAngle = 0f, redAngle = 120f, greenAngle = 240f;

            // Find P1 (closest primary), P2 (second closest), P3 (farthest)
            float distB = Math.Min(Math.Abs(angle - blueAngle), 360f - Math.Abs(angle - blueAngle));
            float distR = Math.Min(Math.Abs(angle - redAngle), 360f - Math.Abs(angle - redAngle));
            float distG = Math.Min(Math.Abs(angle - greenAngle), 360f - Math.Abs(angle - greenAngle));

            var primaries = new (float dist, float angleP, int idx)[] { (distB, blueAngle, 2), (distR, redAngle, 1), (distG, greenAngle, 0) };
            Array.Sort(primaries, (a, b) => a.dist.CompareTo(b.dist));

            float[] rgb = new float[3]; // [G, R, B]
            rgb[primaries[0].idx] = 1.0f;
            rgb[primaries[1].idx] = Math.Clamp(Math.Abs(angle - primaries[0].angleP) / 60f, 0f, 1f);

            // Handle wrap-around for angle distance
            float p2dist = Math.Min(Math.Abs(angle - primaries[0].angleP), 360f - Math.Abs(angle - primaries[0].angleP));
            rgb[primaries[1].idx] = Math.Clamp(p2dist / 60f, 0f, 1f);
            rgb[primaries[2].idx] = 0f;

            palette[(byte)(half + i)] = new NaplpsColor((byte)(rgb[0] * 255), (byte)(rgb[1] * 255), (byte)(rgb[2] * 255));
        }

        return palette;
    }

    public static readonly Dictionary<byte, NaplpsColor> ColorMapDefaults = new()
    {
        {0x0, NaplpsColor.From3BitGRB(0, 0, 0)},
        {0x1, NaplpsColor.From3BitGRB(1, 1, 1)},
        {0x2, NaplpsColor.From3BitGRB(2, 2, 2)},
        {0x3, NaplpsColor.From3BitGRB(3, 3, 3)},
        {0x4, NaplpsColor.From3BitGRB(4, 4, 4)},
        {0x5, NaplpsColor.From3BitGRB(5, 5, 5)},
        {0x6, NaplpsColor.From3BitGRB(6, 6, 6)},
        {0x7, NaplpsColor.From3BitGRB(7, 7, 7)},
        {0x8, NaplpsColor.From3BitGRB(0, 0, 7)},
        {0x9, NaplpsColor.From3BitGRB(0, 5, 7)},
        {0xA, NaplpsColor.From3BitGRB(0, 7, 4)},
        {0xB, NaplpsColor.From3BitGRB(2, 7, 0)},
        {0xC, NaplpsColor.From3BitGRB(7, 7, 0)},
        {0xD, NaplpsColor.From3BitGRB(7, 2, 0)},
        {0xE, NaplpsColor.From3BitGRB(7, 0, 4)},
        {0xF, NaplpsColor.From3BitGRB(5, 0, 7)},
    };

    // Prodigy color palette - NaplpsColor constructor is (green, red, blue)
    public static readonly Dictionary<byte, NaplpsColor> ColorMapProdigyDefaults = new()
    {
        {0x0, new NaplpsColor(0x00, 0x00, 0x00)},  // Black
        {0x1, new NaplpsColor(0x00, 0xAA, 0x00)},  // Red
        {0x2, new NaplpsColor(0x55, 0x55, 0x55)},  // Grey
        {0x3, new NaplpsColor(0x00, 0x00, 0xAA)},  // Blue
        {0x4, new NaplpsColor(0xAA, 0xAA, 0xAA)},  // Light Grey
        {0x5, new NaplpsColor(0x55, 0xAA, 0x00)},  // Brown
        {0x6, new NaplpsColor(0xAA, 0x00, 0x00)},  // Green
        {0x7, new NaplpsColor(0xFF, 0xFF, 0xFF)},  // White
        {0x8, new NaplpsColor(0x55, 0x55, 0xFF)},  // Light Blue
        {0x9, new NaplpsColor(0x00, 0xAA, 0xAA)},  // Magenta
        {0xA, new NaplpsColor(0x55, 0xFF, 0xFF)},  // Light Magenta
        {0xB, new NaplpsColor(0x55, 0xFF, 0x55)},  // Light Red
        {0xC, new NaplpsColor(0xFF, 0xFF, 0x55)},  // Yellow
        {0xD, new NaplpsColor(0xFF, 0x55, 0x55)},  // Light Green
        {0xE, new NaplpsColor(0xFF, 0x55, 0xFF)},  // Light Cyan
        {0xF, new NaplpsColor(0xAA, 0x00, 0xAA)},  // Cyan
    };

    public static readonly NCR[] C0Set =
    [
        new NCR(typeof(CC), Null),
        new NCR(typeof(CC), StartOfHeading),
        new NCR(typeof(CC), StartOfText),
        new NCR(typeof(CC), EndOfText),
        new NCR(typeof(CC), EndOfTransmission),
        new NCR(typeof(CC), Enquiry),
        new NCR(typeof(CC), Acknowledge),
        new NCR(typeof(CC), Bell),
        new NCR(typeof(CC), ActivePositionBackward),
        new NCR(typeof(CC), ActivePositionForward),
        new NCR(typeof(CC), ActivePositionDown),
        new NCR(typeof(CC), ActivePositionUp),
        new NCR(typeof(CC), ClearScreen),
        new NCR(typeof(CC), ActivePositionReturn),
        new NCR(typeof(CC), ShiftOut),
        new NCR(typeof(CC), ShiftIn),
        new NCR(typeof(CC), DataLinkEscape),
        new NCR(typeof(CC), DeviceControl1),
        new NCR(typeof(CC), DeviceControl2),
        new NCR(typeof(CC), DeviceControl3),
        new NCR(typeof(CC), DeviceControl4),
        new NCR(typeof(CC), NegativeAcknowledge),
        new NCR(typeof(CC), SynchronousIdle),
        new NCR(typeof(CC), EndOfBlock),
        new NCR(typeof(CC), Cancel),
        new NCR(typeof(CC), SingleShiftTwo),
        new NCR(typeof(CC), ServiceDelimiterCharacter),
        new NCR(typeof(CC), Escape),
        new NCR(typeof(CC), ActivePositionSet),
        new NCR(typeof(CC), SingleShiftThree),
        new NCR(typeof(CC), ActivePositionHome),
        new NCR(typeof(CC), NonSelectiveReset),
    ];

    public static readonly NCR[] C1Set =
    [
        new NCR(typeof(CC), DefMacro),
        new NCR(typeof(CC), DefPMacro),
        new NCR(typeof(CC), DefTMacro),
        new NCR(typeof(CC), DefDRCS),
        new NCR(typeof(CC), DefTexture),
        new NCR(typeof(CC), End),
        new NCR(typeof(CC), Repeat),
        new NCR(typeof(CC), RepeatToEOL),
        new NCR(typeof(CC), ReverseVideo),
        new NCR(typeof(CC), NormalVideo),
        new NCR(typeof(CC), SmallText),
        new NCR(typeof(CC), MedText),
        new NCR(typeof(CC), NormalText),
        new NCR(typeof(CC), DoubleHeight),
        new NCR(typeof(CC), BlinkStart),
        new NCR(typeof(CC), DoubleSize),
        new NCR(typeof(CC), Protect),
        new NCR(typeof(CC), EDC1),
        new NCR(typeof(CC), EDC2),
        new NCR(typeof(CC), EDC3),
        new NCR(typeof(CC), EDC4),
        new NCR(typeof(CC), WordWrapOn),
        new NCR(typeof(CC), WordWrapOff),
        new NCR(typeof(CC), ScrollOn),
        new NCR(typeof(CC), ScrollOff),
        new NCR(typeof(CC), UnderLineStart),
        new NCR(typeof(CC), UnderLineStop),
        new NCR(typeof(CC), FlashCursor),
        new NCR(typeof(CC), SteadyCursor),
        new NCR(typeof(CC), CursorOff),
        new NCR(typeof(CC), BlinkStop),
        new NCR(typeof(CC), Unprotect)
    ];

    public static readonly NCR[] PrimaryCharacterSet =
    [
        new NCR(typeof(AC), ' '),
        new NCR(typeof(AC), '!'),
        new NCR(typeof(AC), '"'),
        new NCR(typeof(AC), '#'),
        new NCR(typeof(AC), '$'),
        new NCR(typeof(AC), '%'),
        new NCR(typeof(AC), '&'),
        new NCR(typeof(AC), '\''),
        new NCR(typeof(AC), '('),
        new NCR(typeof(AC), ')'),
        new NCR(typeof(AC), '*'),
        new NCR(typeof(AC), '+'),
        new NCR(typeof(AC), ','),
        new NCR(typeof(AC), '-'),
        new NCR(typeof(AC), '.'),
        new NCR(typeof(AC), '/'),

        new NCR(typeof(AC), '0'),
        new NCR(typeof(AC), '1'),
        new NCR(typeof(AC), '2'),
        new NCR(typeof(AC), '3'),
        new NCR(typeof(AC), '4'),
        new NCR(typeof(AC), '5'),
        new NCR(typeof(AC), '6'),
        new NCR(typeof(AC), '7'),
        new NCR(typeof(AC), '8'),
        new NCR(typeof(AC), '9'),
        new NCR(typeof(AC), ':'),
        new NCR(typeof(AC), ';'),
        new NCR(typeof(AC), '<'),
        new NCR(typeof(AC), '='),
        new NCR(typeof(AC), '>'),
        new NCR(typeof(AC), '?'),

        new NCR(typeof(AC), '@'),
        new NCR(typeof(AC), 'A'),
        new NCR(typeof(AC), 'B'),
        new NCR(typeof(AC), 'C'),
        new NCR(typeof(AC), 'D'),
        new NCR(typeof(AC), 'E'),
        new NCR(typeof(AC), 'F'),
        new NCR(typeof(AC), 'G'),
        new NCR(typeof(AC), 'H'),
        new NCR(typeof(AC), 'I'),
        new NCR(typeof(AC), 'J'),
        new NCR(typeof(AC), 'K'),
        new NCR(typeof(AC), 'L'),
        new NCR(typeof(AC), 'M'),
        new NCR(typeof(AC), 'N'),
        new NCR(typeof(AC), 'O'),

        new NCR(typeof(AC), 'P'),
        new NCR(typeof(AC), 'Q'),
        new NCR(typeof(AC), 'R'),
        new NCR(typeof(AC), 'S'),
        new NCR(typeof(AC), 'T'),
        new NCR(typeof(AC), 'U'),
        new NCR(typeof(AC), 'V'),
        new NCR(typeof(AC), 'W'),
        new NCR(typeof(AC), 'X'),
        new NCR(typeof(AC), 'Y'),
        new NCR(typeof(AC), 'Z'),
        new NCR(typeof(AC), '['),
        new NCR(typeof(AC), '\\'),
        new NCR(typeof(AC), ']'),
        new NCR(typeof(AC), '^'),
        new NCR(typeof(AC), '_'),

        new NCR(typeof(AC), '`'),
        new NCR(typeof(AC), 'a'),
        new NCR(typeof(AC), 'b'),
        new NCR(typeof(AC), 'c'),
        new NCR(typeof(AC), 'd'),
        new NCR(typeof(AC), 'e'),
        new NCR(typeof(AC), 'f'),
        new NCR(typeof(AC), 'g'),
        new NCR(typeof(AC), 'h'),
        new NCR(typeof(AC), 'i'),
        new NCR(typeof(AC), 'j'),
        new NCR(typeof(AC), 'k'),
        new NCR(typeof(AC), 'l'),
        new NCR(typeof(AC), 'm'),
        new NCR(typeof(AC), 'n'),
        new NCR(typeof(AC), 'o'),

        new NCR(typeof(AC), 'p'),
        new NCR(typeof(AC), 'q'),
        new NCR(typeof(AC), 'r'),
        new NCR(typeof(AC), 's'),
        new NCR(typeof(AC), 't'),
        new NCR(typeof(AC), 'u'),
        new NCR(typeof(AC), 'v'),
        new NCR(typeof(AC), 'w'),
        new NCR(typeof(AC), 'x'),
        new NCR(typeof(AC), 'y'),
        new NCR(typeof(AC), 'z'),
        new NCR(typeof(AC), '{'),
        new NCR(typeof(AC), '|'),
        new NCR(typeof(AC), '}'),
        new NCR(typeof(AC), '~'),
        new NCR(typeof(DeleteCommand))
    ];

    public static readonly NCR[] SupplementaryCharacterSet =
    [
        new NCR(typeof(AC), ' '),
        new NCR(typeof(AC), '¡'),
        new NCR(typeof(AC), '¢'),
        new NCR(typeof(AC), '£'),
        new NCR(typeof(AC), '$'),
        new NCR(typeof(AC), '¥'),
        new NCR(typeof(AC), '#'),
        new NCR(typeof(AC), '§'),
        new NCR(typeof(AC), '¤'),
        new NCR(typeof(AC), '‘'),
        new NCR(typeof(AC), '“'),
        new NCR(typeof(AC), '«'),
        new NCR(typeof(AC), '←'),
        new NCR(typeof(AC), '↑'),
        new NCR(typeof(AC), '→'),
        new NCR(typeof(AC), '↓'),

        new NCR(typeof(AC), '°'),
        new NCR(typeof(AC), '±'),
        new NCR(typeof(AC), '²'),
        new NCR(typeof(AC), '³'),
        new NCR(typeof(AC), '×'),
        new NCR(typeof(AC), 'µ'),
        new NCR(typeof(AC), '¶'),
        new NCR(typeof(AC), '·'),
        new NCR(typeof(AC), '÷'),
        new NCR(typeof(AC), '’'),
        new NCR(typeof(AC), '”'),
        new NCR(typeof(AC), '»'),
        new NCR(typeof(AC), '¼'),
        new NCR(typeof(AC), '½'),
        new NCR(typeof(AC), '¾'),
        new NCR(typeof(AC), '¿'),

        // Non Spacing Diacritical Marks
        new NCR(typeof(AC), '→'),
        new NCR(typeof(AC), '`'),
        new NCR(typeof(AC), '´'),
        new NCR(typeof(AC), 'ˆ'),
        new NCR(typeof(AC), '~'),
        new NCR(typeof(AC), '¯'),
        new NCR(typeof(AC), '˘'),
        new NCR(typeof(AC), '˙'),
        new NCR(typeof(AC), '¨'),
        new NCR(typeof(AC), '/'),
        new NCR(typeof(AC), '˚'),
        new NCR(typeof(AC), '¸'),
        new NCR(typeof(AC), '_'),
        new NCR(typeof(AC), '˝'),
        new NCR(typeof(AC), '˛'),
        new NCR(typeof(AC), 'ˇ'),

        new NCR(typeof(AC), '―'),
        new NCR(typeof(AC), '¹'),
        new NCR(typeof(AC), '®'),
        new NCR(typeof(AC), '©'),
        new NCR(typeof(AC), '™'),
        new NCR(typeof(AC), '♪'),
        new NCR(typeof(AC), '─'),
        new NCR(typeof(AC), '│'),
        new NCR(typeof(AC), '╱'),
        new NCR(typeof(AC), '╲'),
        new NCR(typeof(AC), '◢'),
        new NCR(typeof(AC), '◣'),
        new NCR(typeof(AC), '⅛'),
        new NCR(typeof(AC), '⅜'),
        new NCR(typeof(AC), '⅝'),
        new NCR(typeof(AC), '⅞'),

        new NCR(typeof(AC), 'Ω'),
        new NCR(typeof(AC), 'Æ'),
        new NCR(typeof(AC), 'Ð'),
        new NCR(typeof(AC), 'ª'),
        new NCR(typeof(AC), 'Ħ'),
        new NCR(typeof(AC), '┼'),
        new NCR(typeof(AC), 'Ĳ'),
        new NCR(typeof(AC), 'Ŀ'),
        new NCR(typeof(AC), 'Ł'),
        new NCR(typeof(AC), 'Ø'),
        new NCR(typeof(AC), 'Œ'),
        new NCR(typeof(AC), 'º'),
        new NCR(typeof(AC), 'Þ'),
        new NCR(typeof(AC), 'Ŧ'),
        new NCR(typeof(AC), 'Ŋ'),
        new NCR(typeof(AC), 'ŉ'),

        new NCR(typeof(AC), 'ĸ'),
        new NCR(typeof(AC), 'æ'),
        new NCR(typeof(AC), 'đ'),
        new NCR(typeof(AC), 'ð'),
        new NCR(typeof(AC), 'ħ'),
        new NCR(typeof(AC), 'ı'),
        new NCR(typeof(AC), 'ĳ'),
        new NCR(typeof(AC), 'ŀ'),
        new NCR(typeof(AC), 'ł'),
        new NCR(typeof(AC), 'ø'),
        new NCR(typeof(AC), 'œ'),
        new NCR(typeof(AC), 'ß'),
        new NCR(typeof(AC), 'þ'),
        new NCR(typeof(AC), 'ŧ'),
        new NCR(typeof(AC), 'ŋ'),
        new NCR(typeof(DeleteCommand))
    ];

    public static readonly NCR[] GeneralPDISet =
    [
        new NCR(typeof(ResetCommand)),
        new NCR(typeof(DomainCommand)),
        new NCR(typeof(TextCommand)),
        new NCR(typeof(TextureCommand)),
        new NCR(typeof(PointSetAbsoluteCommand)),
        new NCR(typeof(PointSetRelativeCommand)),
        new NCR(typeof(PointAbsoluteCommand)),
        new NCR(typeof(PointRelativeCommand)),
        new NCR(typeof(LineAbsoluteCommand)),
        new NCR(typeof(LineRelativeCommand)),
        new NCR(typeof(LineSetAbsoluteCommand)),
        new NCR(typeof(LineSetRelativeCommand)),
        new NCR(typeof(ArcOutlinedCommand)),
        new NCR(typeof(ArcFilledCommand)),
        new NCR(typeof(ArcSetOutlinedCommand)),
        new NCR(typeof(ArcSetFilledCommand)),

        new NCR(typeof(RectangleOutlinedCommand)),
        new NCR(typeof(RectangleFilledCommand)),
        new NCR(typeof(RectangleSetOutlinedCommand)),
        new NCR(typeof(RectangleSetFilledCommand)),
        new NCR(typeof(PolygonOutlinedCommand)),
        new NCR(typeof(PolygonFilledCommand)),
        new NCR(typeof(PolygonSetOutlinedCommand)),
        new NCR(typeof(PolygonSetFilledCommand)),
        new NCR(typeof(IncrementalFieldCommand)),
        new NCR(typeof(IncrementalPointCommand)),
        new NCR(typeof(IncrementalLineCommand)),
        new NCR(typeof(IncrementalPolygonFilledCommand)),
        new NCR(typeof(SetColorCommand)),
        new NCR(typeof(WaitCommand)),
        new NCR(typeof(SelectColorCommand)),
        new NCR(typeof(BlinkCommand)),

        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),

        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),

        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),

        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
    ];

    /// <summary>
    /// Telidon v699 PDI set. Same as GeneralPDISet but with Incremental commands
    /// and Blink replaced with NaplpsCommand (no-op) — these didn't exist in the
    /// original 1978 Telidon specification.
    /// </summary>
    public static readonly NCR[] TelidonPDISet =
    [
        new NCR(typeof(ResetCommand)),          // 0x20/0xA0
        new NCR(typeof(DomainCommand)),         // 0x21/0xA1
        new NCR(typeof(TextCommand)),           // 0x22/0xA2
        new NCR(typeof(TextureCommand)),        // 0x23/0xA3
        new NCR(typeof(PointSetAbsoluteCommand)),
        new NCR(typeof(PointSetRelativeCommand)),
        new NCR(typeof(PointAbsoluteCommand)),
        new NCR(typeof(PointRelativeCommand)),
        new NCR(typeof(LineAbsoluteCommand)),
        new NCR(typeof(LineRelativeCommand)),
        new NCR(typeof(LineSetAbsoluteCommand)),
        new NCR(typeof(LineSetRelativeCommand)),
        new NCR(typeof(ArcOutlinedCommand)),
        new NCR(typeof(ArcFilledCommand)),
        new NCR(typeof(ArcSetOutlinedCommand)),
        new NCR(typeof(ArcSetFilledCommand)),

        new NCR(typeof(RectangleOutlinedCommand)),
        new NCR(typeof(RectangleFilledCommand)),
        new NCR(typeof(RectangleSetOutlinedCommand)),
        new NCR(typeof(RectangleSetFilledCommand)),
        new NCR(typeof(PolygonOutlinedCommand)),
        new NCR(typeof(PolygonFilledCommand)),
        new NCR(typeof(PolygonSetOutlinedCommand)),
        new NCR(typeof(PolygonSetFilledCommand)),
        new NCR(typeof(IncrementalFieldCommand)),
        new NCR(typeof(NaplpsCommand)),         // Telidon: no IncrementalPoint
        new NCR(typeof(NaplpsCommand)),         // Telidon: no IncrementalLine
        new NCR(typeof(NaplpsCommand)),         // Telidon: no IncrementalPolygonFilled
        new NCR(typeof(SetColorCommand)),
        new NCR(typeof(WaitCommand)),
        new NCR(typeof(SelectColorCommand)),
        new NCR(typeof(NaplpsCommand)),         // Telidon: no Blink

        // Remainder: NumericalDataCommand (same as GeneralPDISet)
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),

        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),

        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),

        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
        new NCR(typeof(NumericalDataCommand)),
    ];

    public static readonly NCR[] MosaicSet =
    [
        new NCR(typeof(MC), [false, false, false, false, false, false]),
        new NCR(typeof(MC), [ true, false, false, false, false, false]),
        new NCR(typeof(MC), [false,  true, false, false, false, false]),
        new NCR(typeof(MC), [ true,  true, false, false, false, false]),
        new NCR(typeof(MC), [false, false,  true, false, false, false]),
        new NCR(typeof(MC), [ true, false,  true, false, false, false]),
        new NCR(typeof(MC), [false,  true,  true, false, false, false]),
        new NCR(typeof(MC), [ true,  true,  true, false, false, false]),
        new NCR(typeof(MC), [false, false, false,  true, false, false]),
        new NCR(typeof(MC), [ true, false, false,  true, false, false]),
        new NCR(typeof(MC), [false,  true, false,  true, false, false]),
        new NCR(typeof(MC), [ true,  true, false,  true, false, false]),
        new NCR(typeof(MC), [false, false,  true,  true, false, false]),
        new NCR(typeof(MC), [ true, false,  true,  true, false, false]),
        new NCR(typeof(MC), [false,  true,  true,  true, false, false]),
        new NCR(typeof(MC), [ true,  true,  true,  true, false, false]),

        new NCR(typeof(MC), [false, false, false, false,  true, false]),
        new NCR(typeof(MC), [ true, false, false, false,  true, false]),
        new NCR(typeof(MC), [false,  true, false, false,  true, false]),
        new NCR(typeof(MC), [ true,  true, false, false,  true, false]),
        new NCR(typeof(MC), [false, false,  true, false,  true, false]),
        new NCR(typeof(MC), [ true, false,  true, false,  true, false]),
        new NCR(typeof(MC), [false,  true,  true, false,  true, false]),
        new NCR(typeof(MC), [ true,  true,  true, false,  true, false]),
        new NCR(typeof(MC), [false, false, false,  true,  true, false]),
        new NCR(typeof(MC), [ true, false, false,  true,  true, false]),
        new NCR(typeof(MC), [false,  true, false,  true,  true, false]),
        new NCR(typeof(MC), [ true,  true, false,  true,  true, false]),
        new NCR(typeof(MC), [false, false,  true,  true,  true, false]),
        new NCR(typeof(MC), [ true, false,  true,  true,  true, false]),
        new NCR(typeof(MC), [false,  true,  true,  true,  true, false]),
        new NCR(typeof(MC), [ true,  true,  true,  true,  true, false]),

        new NCR(typeof(MC), [false, false, false, false, false, false]),
        new NCR(typeof(MC), [false, false, false, false, false, false]),
        new NCR(typeof(MC), [false, false, false, false, false, false]),
        new NCR(typeof(MC), [false, false, false, false, false, false]),
        new NCR(typeof(MC), [false, false, false, false, false, false]),
        new NCR(typeof(MC), [false, false, false, false, false, false]),
        new NCR(typeof(MC), [false, false, false, false, false, false]),
        new NCR(typeof(MC), [false, false, false, false, false, false]),
        new NCR(typeof(MC), [false, false, false, false, false, false]),
        new NCR(typeof(MC), [false, false, false, false, false, false]),
        new NCR(typeof(MC), [false, false, false, false, false, false]),
        new NCR(typeof(MC), [false, false, false, false, false, false]),
        new NCR(typeof(MC), [false, false, false, false, false, false]),
        new NCR(typeof(MC), [false, false, false, false, false, false]),
        new NCR(typeof(MC), [false, false, false, false, false, false]),

        new NCR(typeof(MC), [false, false, false, false, false, false]),
        new NCR(typeof(MC), [false, false, false, false, false, false]),
        new NCR(typeof(MC), [false, false, false, false, false, false]),
        new NCR(typeof(MC), [false, false, false, false, false, false]),
        new NCR(typeof(MC), [false, false, false, false, false, false]),
        new NCR(typeof(MC), [false, false, false, false, false, false]),
        new NCR(typeof(MC), [false, false, false, false, false, false]),
        new NCR(typeof(MC), [false, false, false, false, false, false]),
        new NCR(typeof(MC), [false, false, false, false, false, false]),
        new NCR(typeof(MC), [false, false, false, false, false, false]),
        new NCR(typeof(MC), [false, false, false, false, false, false]),
        new NCR(typeof(MC), [false, false, false, false, false, false]),
        new NCR(typeof(MC), [false, false, false, false, false, false]),
        new NCR(typeof(MC), [false, false, false, false, false, false]),
        new NCR(typeof(MC), [ true,  true,  true,  true,  true,  true]),

        new NCR(typeof(MC), [false, false, false, false, false,  true]), // 0
        new NCR(typeof(MC), [ true, false, false, false, false,  true]), // 1
        new NCR(typeof(MC), [false,  true, false, false, false,  true]), // 2
        new NCR(typeof(MC), [ true,  true, false, false, false,  true]), // 3
        new NCR(typeof(MC), [false, false,  true, false, false,  true]), // 4
        new NCR(typeof(MC), [ true, false,  true, false, false,  true]), // 5
        new NCR(typeof(MC), [false,  true,  true, false, false,  true]), // 6
        new NCR(typeof(MC), [ true,  true,  true, false, false,  true]), // 7
        new NCR(typeof(MC), [false, false, false,  true, false,  true]), // 8
        new NCR(typeof(MC), [ true, false, false,  true, false,  true]), // 9
        new NCR(typeof(MC), [false,  true, false,  true, false,  true]), // 10
        new NCR(typeof(MC), [ true,  true, false,  true, false,  true]), // 11
        new NCR(typeof(MC), [false, false,  true,  true, false,  true]), // 12
        new NCR(typeof(MC), [ true, false,  true,  true, false,  true]), // 13
        new NCR(typeof(MC), [false,  true,  true,  true, false,  true]), // 14
        new NCR(typeof(MC), [ true,  true,  true,  true, false,  true]), // 15

        new NCR(typeof(MC), [false, false, false, false,  true,  true]), // 0
        new NCR(typeof(MC), [ true, false, false, false,  true,  true]), // 1
        new NCR(typeof(MC), [false,  true, false, false,  true,  true]), // 2
        new NCR(typeof(MC), [ true,  true, false, false,  true,  true]), // 3
        new NCR(typeof(MC), [false, false,  true, false,  true,  true]), // 4
        new NCR(typeof(MC), [ true, false,  true, false,  true,  true]), // 5
        new NCR(typeof(MC), [false,  true,  true, false,  true,  true]), // 6
        new NCR(typeof(MC), [ true,  true,  true, false,  true,  true]), // 7
        new NCR(typeof(MC), [false, false, false,  true,  true,  true]), // 8
        new NCR(typeof(MC), [ true, false, false,  true,  true,  true]), // 9
        new NCR(typeof(MC), [false,  true, false,  true,  true,  true]), // 10
        new NCR(typeof(MC), [ true,  true, false,  true,  true,  true]), // 11
        new NCR(typeof(MC), [false, false,  true,  true,  true,  true]), // 12
        new NCR(typeof(MC), [ true, false,  true,  true,  true,  true]), // 13
        new NCR(typeof(MC), [false,  true,  true,  true,  true,  true]), // 14
        new NCR(typeof(MC), [ true,  true,  true,  true,  true,  true]), // 15
    ];
}
