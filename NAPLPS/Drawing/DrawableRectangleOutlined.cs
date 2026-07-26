// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;
using PointF = SixLabors.ImageSharp.PointF;

namespace NAPLPS.Drawing;

public class DrawableRectangleOutlined : Drawable, IDrawable
{
    private readonly RectangleOutlinedCommand _command;

    public DrawableRectangleOutlined(RectangleOutlinedCommand command) : base(command)
    {
        _command = command;
    }

    public void Draw(Image<Rgba32> image, NaplpsState state, Size size)
    {
        var startPoint = _command.Points.FirstOrDefault();
        var dimensions = _command.Dimensions;

        var (x1, y1, x2, y2) = NaplpsUtils.ConvertRectToScreen(
            size,
            startPoint.X,
            startPoint.Y,
            dimensions.X,
            dimensions.Y
        );

        var (brush, pen) = GetBrushAndPenFromFillableCommand(size, state);

        float outlineWidth = GetPenWidth(size);
        // Inset by half pen width so the stroke falls entirely inside the boundary,
        // matching PP3's Bresenham line drawing which places pixels AT the boundary.
        float inset = outlineWidth / 2f;
        var rect = new RectangularPolygon(
            new PointF(x1 + inset, y1 + inset),
            new PointF(x2 - inset, y2 - inset));

        var outlinePen = GetTexturedPen(GetOutlineColor(), outlineWidth);

        image.Mutate(x => x.Draw(FillOptions(), outlinePen, rect));
    }
}
