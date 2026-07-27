// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;

namespace NAPLPS.Drawing;

/// <summary>
/// Renders a DRCS (Dynamically Redefinable Character Set) character.
/// Takes the AsciiCharCommand pen position and the bool[,] bitmap from state.DrcsCharacters,
/// scales the bitmap to fit the character cell, and renders each pixel as a filled rectangle.
/// </summary>
public class DrawableDrcsChar : Drawable, IDrawable
{
    private readonly AsciiCharCommand _command;
    private readonly bool[,] _bitmap;
    private readonly Vector3? _penOverride;

    /// <summary>
    /// Draws at the command's <see cref="AsciiCharCommand.DrawPen"/> (see
    /// <see cref="DrawableAsciiChar"/>); REPEAT expansion passes each successive position as
    /// <paramref name="penOverride"/>.
    /// </summary>
    public DrawableDrcsChar(AsciiCharCommand command, bool[,] bitmap, Vector3? penOverride = null) : base(command)
    {
        _command = command;
        _bitmap = bitmap;
        _penOverride = penOverride;
    }

    public void Draw(Image<Rgba32> image, NaplpsState state, Size size)
    {
        var pen = _penOverride ?? _command.DrawPen;
        var penPoint = ConvertNormalizedToPoint(size, pen.X, pen.Y);
        var (charSizeX, charSizeY) = ConvertNormalizedToScreenScale(size, state.CharSize.X, state.CharSize.Y);

        float cellW = MathF.Max(1f, MathF.Abs(charSizeX));
        float cellH = MathF.Max(1f, MathF.Abs(charSizeY));

        // Cell top-left in screen coords (Y-down)
        float cellTopX = penPoint.X;
        float cellTopY = penPoint.Y - cellH;

        var (fgColor, bgColor) = GetISColorFromState(state);

        // Reverse video: swap foreground and background colors
        if (state.IsReverseVideo)
        {
            (fgColor, bgColor) = (bgColor, fgColor);
        }

        int bitmapRows = _bitmap.GetLength(0);
        int bitmapCols = _bitmap.GetLength(1);

        if (bitmapRows == 0 || bitmapCols == 0)
        {
            return;
        }

        float pixelW = cellW / bitmapCols;
        float pixelH = cellH / bitmapRows;

        image.Mutate(ctx =>
        {
            // Fill background in color mode 2
            if (state.ColorMode == 2)
            {
                var bgRect = new RectangleF(cellTopX, cellTopY, cellW + 1, cellH);
                ctx.Fill(FillOptions(), bgColor, bgRect);
            }

            // Render each set pixel as a filled rectangle
            for (int row = 0; row < bitmapRows; row++)
            {
                for (int col = 0; col < bitmapCols; col++)
                {
                    if (_bitmap[row, col])
                    {
                        float px = cellTopX + col * pixelW;
                        float py = cellTopY + row * pixelH;
                        var pixelRect = new RectangleF(px, py, pixelW + 0.5f, pixelH + 0.5f);
                        ctx.Fill(FillOptions(), fgColor, pixelRect);
                    }
                }
            }
        });
    }
}
