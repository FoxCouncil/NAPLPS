// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;
using PointF = SixLabors.ImageSharp.PointF;

namespace NAPLPS.Drawing;

public class DrawableLineSet : Drawable, IDrawable
{
    private readonly LineCommand _command;

    public DrawableLineSet(LineCommand command) : base(command)
    {
        _command = command;
    }

    public void Draw(Image<Rgba32> image, NaplpsState state, Size size)
    {
        var points = new List<PointF>();

        foreach (var verts in _command.Points)
        {
            var thePoint = NaplpsUtils.ConvertNormalizedToPoint(size, verts.X, verts.Y);
            points.Add(new PointF(thePoint.X, thePoint.Y));
        }

        if (points.Count < 2)
        {
            return;
        }

        var palette = (Drawable.UseLivePalette && Drawable.LivePalette != null) ? Drawable.LivePalette : state.ColorMap;
        var color = state.ColorMode == 0 ? state.Foreground.ToColor() : palette[state.ColorMapForeground].ToColor();
        var isColor = color.ToISColor();

        var (dxMin, dxMax, dyMin, dyMax) = GetPelOffsets(size);

        if (Options.AuthenticGeometry)
        {
            var pelPattern = DrawableLine.PelDashPattern(state.Texture.LineTexture);
            if (pelPattern != null)
            {
                var (ox0, ox1, oy0, oy1, pelMajor) = GetDashPel(size);
                DrawableLine.PlotDashedPolyline(image, points, asSet: true, ox0, ox1, oy0, oy1, pelMajor, pelPattern, isColor,
                    TextureGapColor(state));
                return;
            }

            // SET variants draw discrete line segments in pairs: (start, end), (start, end), ...
            for (var i = 0; i < points.Count - 1; i += 2)
            {
                DrawableLine.PlotSweptPelLine(image, points[i], points[i + 1], dxMin, dxMax, dyMin, dyMax, isColor);
            }

            return;
        }

        image.Mutate(ctx =>
        {
            // SET variants draw discrete line segments in pairs: (start, end), (start, end), ...
            for (var i = 0; i < points.Count - 1; i += 2)
            {
                var p1 = points[i];
                var p2 = points[i + 1];

                var hull = DrawableLine.ConvexHullOfSweptPel(p1, p2, dxMin, dxMax, dyMin, dyMax);
                ctx.FillPolygon(isColor, hull);
            }
        });
    }
}
