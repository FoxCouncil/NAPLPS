// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;
using PointF = SixLabors.ImageSharp.PointF;

namespace NAPLPS.Drawing;

public class DrawablePoint : Drawable, IDrawable
{
    private readonly PointCommand _command;

    public DrawablePoint(PointCommand command) : base(command)
    {
        _command = command;
    }

    public void Draw(Image<Rgba32> image, NaplpsState state, Size size)
    {
        if (!_command.IsVisible)
        {
            return;
        }

        var points = new List<PointF>();

        foreach (var verts in _command.Points)
        {
            var thePoint = NaplpsUtils.ConvertNormalizedToPoint(size, verts.X, verts.Y);

            points.Add(new PointF(thePoint.X, thePoint.Y));
        }

        if (points.Count < 1)
        {
            return;
        }


        // MVDI stamps a POINT as its logical pel RECTANGLE, anchored at the path point and
        // extending per the pel sign (right/left, up/down) - identical to the swept pel a LINE
        // lays down at each endpoint. The old centered EllipsePolygon collapsed anisotropic pels
        // (e.g. a 20x2 horizontal-bar pel or a 5x19 vertical-bar pel, as used by the calligraphic
        // title artwork in bantam-doubleday-dell) into a thin lens, thinning/breaking the strokes.
        var (dxMin, dxMax, dyMin, dyMax) = GetPelOffsets(size);
        float pelW = MathF.Max(1f, dxMax - dxMin);
        float pelH = MathF.Max(1f, dyMax - dyMin);

        var (brush, pen) = GetBrushAndPenFromFillableCommand(size, state);

        image.Mutate(x =>
        {
            foreach (var center in points)
            {
                var rect = new RectangleF(center.X + dxMin, center.Y + dyMin, pelW, pelH);
                x.Fill(FillOptions(), brush, rect);
            }
        });
    }
}
