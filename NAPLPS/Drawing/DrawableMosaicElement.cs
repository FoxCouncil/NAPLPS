// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;
using PointF = SixLabors.ImageSharp.PointF;

namespace NAPLPS.Drawing;

/// <summary>
/// Renders a NAPLPS mosaic character. Mosaic characters are 2x3 block grids
/// where each of 6 cells can be independently on or off.
///
/// Grid layout:
/// -----
/// |1|2|
/// -----
/// |3|4|
/// -----
/// |5|6|
/// -----
///
/// When underline mode is ON, mosaics display in "separated" mode: the character
/// field is reduced by one logical pel width and height before subdividing.
/// When underline mode is OFF, mosaics display in "contiguous" mode (full field).
/// </summary>
public class DrawableMosaicElement : Drawable, IDrawable
{
    private readonly MosaicElementCommand _command;

    public DrawableMosaicElement(MosaicElementCommand command) : base(command)
    {
        _command = command;
    }

    public void Draw(Image<Rgba32> image, NaplpsState state, Size size)
    {
        // Convert pen position (normalized NAPLPS coords) to screen pixel coordinates
        var penPoint = ConvertNormalizedToPoint(size, state.Pen.X, state.Pen.Y);

        // Convert character cell size (normalized) to screen pixels
        var (charSizeX, charSizeY) = ConvertNormalizedToScreenScale(size, state.CharSize.X, state.CharSize.Y);

        // Mosaics are NEVER proportionally spaced - use full cell width
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

        // Determine mosaic drawing area based on separated vs contiguous mode.
        // Underline ON = separated mode: reduce field by logical pel size, left/bottom justified.
        // Underline OFF = contiguous mode: use full character field.
        float mosaicX = cellTopX;
        float mosaicY = cellTopY;
        float mosaicW = cellW;
        float mosaicH = cellH;

        if (state.IsUnderline)
        {
            var (pelSizeX, pelSizeY) = ConvertNormalizedToScreenScale(size, state.LogicalPel.X, state.LogicalPel.Y);
            float pelW = MathF.Max(1f, MathF.Abs(pelSizeX));
            float pelH = MathF.Max(1f, MathF.Abs(pelSizeY));

            mosaicW = MathF.Max(1f, cellW - pelW);
            mosaicH = MathF.Max(1f, cellH - pelH);

            // Left/bottom justified: mosaicX stays the same, mosaicY shifts down
            // so the reduced area sits at the bottom of the cell
            mosaicY = cellTopY + (cellH - mosaicH);
        }

        // Subdivide mosaic area into 2 columns x 3 rows
        float subW = mosaicW / 2f;
        float subH = mosaicH / 3f;

        // Map bits to grid positions (col, row)
        // Bit1=top-left(0,0), Bit2=top-right(1,0), Bit3=mid-left(0,1),
        // Bit4=mid-right(1,1), Bit5=bot-left(0,2), Bit6=bot-right(1,2)
        bool[] bits = { _command.Bit1, _command.Bit2, _command.Bit3, _command.Bit4, _command.Bit5, _command.Bit6 };
        int[] cols = { 0, 1, 0, 1, 0, 1 };
        int[] rows = { 0, 0, 1, 1, 2, 2 };

        image.Mutate(ctx =>
        {
            // In color mode 2, fill the entire character field with background color
            if (state.ColorMode == 2)
            {
                var bgRect = new RectangleF(cellTopX, cellTopY, cellW + 1, cellH);
                ctx.Fill(FillOptions(), bgColor, bgRect);
            }

            // Draw each mosaic sub-cell
            for (int i = 0; i < 6; i++)
            {
                if (bits[i])
                {
                    float rx = mosaicX + cols[i] * subW;
                    float ry = mosaicY + rows[i] * subH;

                    // Add 0.5f overlap to avoid sub-pixel gaps between adjacent cells
                    var cellRect = new RectangleF(rx, ry, subW + 0.5f, subH + 0.5f);
                    ctx.Fill(FillOptions(), fgColor, cellRect);
                }
                else if (state.ColorMode == 2)
                {
                    // In color mode 2, invisible cells are drawn in background color
                    // (already handled by the full background fill above, but if separated
                    // mode reduced the area, we need to explicitly fill the mosaic sub-cells)
                    if (state.IsUnderline)
                    {
                        float rx = mosaicX + cols[i] * subW;
                        float ry = mosaicY + rows[i] * subH;

                        var cellRect = new RectangleF(rx, ry, subW + 0.5f, subH + 0.5f);
                        ctx.Fill(FillOptions(), bgColor, cellRect);
                    }
                }
            }
        });
    }
}
