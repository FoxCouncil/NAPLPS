// Copyright (c) 2024 FoxCouncil - https://github.com/FoxCouncil/NAPLPS

using System.Net;
using NAPLPS;
using NAPLPS.Commands;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Color = SixLabors.ImageSharp.Color;
using Pens = SixLabors.ImageSharp.Drawing.Processing.Pens;
using PointF = SixLabors.ImageSharp.PointF;

namespace NAPLPSApp.Drawing;

public class DrawablePoint : Drawable, IDrawable
{
    private readonly PointCommand _command;

    public DrawablePoint(PointCommand command): base(command)
    {
        _command = command;
    }

    public void Draw(Image<Rgba32> image, NaplpsState state, System.Drawing.Size size)
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

        var logicalPel = _command.LogicalPel;
        var (width, height) = NaplpsUtils.ConvertNormalizedToScreenScale(size, logicalPel.X, logicalPel.Y);
        var (brush, pen) = GetBrushAndPenFromFillableCommand(size);

        image.Mutate(x =>
        {
            foreach (var center in points)
            {
                var ellipse = new EllipsePolygon(center.X, center.Y, width > 0 ? width : 1f, height > 0 ? height : 1f);
                x.Fill(brush, ellipse);
            }
        });
    }
}
