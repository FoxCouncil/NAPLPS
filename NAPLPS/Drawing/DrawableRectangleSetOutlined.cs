// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;
using PointF = SixLabors.ImageSharp.PointF;

namespace NAPLPS.Drawing;

public class DrawableRectangleSetOutlined : Drawable, IDrawable
{
    private readonly RectangleSetOutlinedCommand _command;

    public DrawableRectangleSetOutlined(RectangleSetOutlinedCommand command) : base(command)
    {
        _command = command;
    }

    public void Draw(Image<Rgba32> image, NaplpsState state, Size size)
    {
        if (!_command.IsValid || _command.Vertices.Count < 2)
        {
            return;
        }

        var (brush, pen) = GetBrushAndPenFromFillableCommand(size, state);
        var vertices = _command.Vertices;
        var outlineColor = GetOutlineColor();

        // Authentic mode: plot the four edges with the integer pel swept from the boundary (like
        // the device rasterizer / straight lines), instead of an anti-aliased half-pen inset stroke.
        if (Options.AuthenticGeometry && _command.Texture.LineTexture == NaplpsTexture.LineTextures.Solid)
        {
            // Round-half-up pel (e.g. 1/256 -> 3px, matching the reference render's frame strokes), not the
            // truncated GetPelOffsets which yields 2px.
            var (dxMin, dxMax, dyMin, dyMax, _) = GetDashPel(size);

            for (int i = 0; i < vertices.Count - 1; i += 2)
            {
                var (x1, y1, x2, y2) = NaplpsUtils.ConvertRectToScreen(size, vertices[i].X, vertices[i].Y, vertices[i + 1].X, vertices[i + 1].Y);
                var tl = new PointF(x1, y1);
                var tr = new PointF(x2, y1);
                var br = new PointF(x2, y2);
                var bl = new PointF(x1, y2);
                DrawableLine.PlotSweptPelLine(image, tl, tr, dxMin, dxMax, dyMin, dyMax, outlineColor);
                DrawableLine.PlotSweptPelLine(image, tr, br, dxMin, dxMax, dyMin, dyMax, outlineColor);
                DrawableLine.PlotSweptPelLine(image, br, bl, dxMin, dxMax, dyMin, dyMax, outlineColor);
                DrawableLine.PlotSweptPelLine(image, bl, tl, dxMin, dxMax, dyMin, dyMax, outlineColor);
            }

            return;
        }

        image.Mutate(x =>
        {
            for (int i = 0; i < vertices.Count - 1; i += 2)
            {
                var v1 = vertices[i];
                var v2 = vertices[i + 1];

                var (x1, y1, x2, y2) = NaplpsUtils.ConvertRectToScreen(size, v1.X, v1.Y, v2.X, v2.Y);

                float outlineWidth = GetPenWidth(size);
                // Inset by half pen width so the stroke falls entirely inside the boundary,
                // matching PP3's Bresenham line drawing which places pixels AT the boundary.
                float inset = outlineWidth / 2f;
                var rect = new RectangularPolygon(
                    new PointF(x1 + inset, y1 + inset),
                    new PointF(x2 - inset, y2 - inset));

                x.Draw(FillOptions(), GetTexturedPen(outlineColor, outlineWidth), rect);
            }
        });
    }
}
