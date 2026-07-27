// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPS.Drawing;

/// <summary>
/// Handles rendering of the Repeat (0x86) and RepeatToEOL (0x87) control commands.
/// These commands repeat the last displayed character N times.
/// </summary>
public class DrawableRepeat : Drawable, IDrawable
{
    private readonly ControlCommand _command;
    private readonly bool _isRepeatToEOL;

    public DrawableRepeat(ControlCommand command) : base(command)
    {
        _command = command;
        _isRepeatToEOL = command.Command == NaplpsControlCommands.RepeatToEOL;
    }

    public void Draw(Image<Rgba32> image, NaplpsState state, Size size)
    {
        // This is handled specially by DrawContext which tracks the last character
        // and calls RenderRepeat directly
    }

    /// <summary>
    /// Gets the repeat count from the command operands.
    /// For Repeat: count is in operands[0], values 0xC0-0xFF have 0x40 subtracted.
    /// For RepeatToEOL: count is calculated based on remaining line width.
    /// </summary>
    public int GetRepeatCount(NaplpsState state)
    {
        if (_isRepeatToEOL)
        {
            // X3.110 6.2.7.3: the repeat is bounded by the active field only when the cursor's
            // full character field lies entirely within it; otherwise the line ends at the
            // unit screen. COLORBAR's field extends left of the visible screen, so its bars
            // repeat to the screen edge on the device.
            // Membership is the COLUMN axis only: the repeat runs along the character path, so
            // the field bounds it iff the cursor sits within the field's columns - the row it
            // is on does not matter (device-verified: the GO00A000/XEB striping rows repeat to
            // the field's right edge even though they sit outside its rows, while COLORBAR's
            // bars, right of its off-screen field's columns, run to the screen edge).
            var f = state.Field;
            bool inField = f.IsSet
                && state.Pen.X >= f.Left && state.Pen.X < f.Right;
            var fieldEndX = inField ? f.Right : 1.0f;
            return Math.Max(0, (int)((fieldEndX - state.Pen.X) / state.CharSize.X));
        }

        if (_command.Operands.Count > 0)
        {
            var countByte = _command.Operands[0];

            // NAPLPS: the repeat count is bits 6 through 1 of the byte following REPEAT; the byte
            // is discarded unless bits 7..1 fall in 0x40..0x7F (7-bit) or 0xC0..0xFF (8-bit high
            // bit set). So gate on the low 7 bits and take the low 6 bits as the count.
            if ((countByte & 0x7F) < 0x40)
            {
                return 0;
            }

            return countByte & 0x3F;
        }

        return 0;
    }

    public bool IsRepeatToEOL => _isRepeatToEOL;
}
