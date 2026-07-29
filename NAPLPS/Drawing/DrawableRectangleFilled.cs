// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;
using PointF = SixLabors.ImageSharp.PointF;

namespace NAPLPS.Drawing;

public class DrawableRectangleFilled : Drawable, IDrawable
{
    private readonly RectangleFilledCommand _command;

    public DrawableRectangleFilled(RectangleFilledCommand command) : base(command)
    {
        _command = command;
    }

    public void Draw(Image<Rgba32> image, NaplpsState state, Size size)
    {
        // ANSI X3.110 §5.3.2.5.1: a transparent drawing color produces no output.
        if (state.IsTransparent)
        {
            return;
        }

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
        var fillOptions = FillOptions();

        // Authentic mode: the device's filled region includes the boundary pel swept outward
        // (right + up for a positive pel), which an exact-bounds fill omits and leaves ~1 pel
        // short on the top and right edges. Extend the fill by the round-half-up pel on the
        // pel-anchored edges; the outline still tracks the true bounds.
        int fx1 = x1, fy1 = y1, fx2 = x2, fy2 = y2;
        if (Options.AuthenticGeometry)
        {
            var (ox0, ox1, oy0, oy1, _, _) = GetDashPel(size);
            fx1 += ox0; fy1 += oy0; fx2 += ox1; fy2 += oy1;
        }
        var rect = new RectangularPolygon(new PointF(fx1, fy1), new PointF(fx2, fy2));

        if (_command.ShouldFill)
        {
            var (rFg, rBg) = ((FillableGeometricDrawingCommandBase)_command).GetColors(state);

            FillShape(image, rect.Points.Span, brush, GetFillSource(size, rFg, rBg));
        }

        image.Mutate(x =>
        {
            if (!_command.ShouldFill || _command.Texture.ShouldHighlight)
            {
                float outlineWidth = GetPenWidth(size);
                float inset = outlineWidth / 2f;
                var insetRect = new RectangularPolygon(
                    new PointF(x1 + inset, y1 + inset),
                    new PointF(x2 - inset, y2 - inset));
                var outlinePen = _command.Texture.ShouldHighlight
                    ? Pens.Solid(GetOutlineColor(), outlineWidth)
                    : GetTexturedPen(GetOutlineColor(), outlineWidth);
                x.Draw(fillOptions, outlinePen, insetRect);
            }
        });
    }
}
