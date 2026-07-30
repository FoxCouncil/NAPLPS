// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;
using PointF = SixLabors.ImageSharp.PointF;

namespace NAPLPS.Drawing;

public class DrawableRectangleSetFilled : Drawable, IDrawable
{
    private readonly RectangleSetFilledCommand _command;

    public DrawableRectangleSetFilled(RectangleSetFilledCommand command) : base(command)
    {
        _command = command;
    }

    public void Draw(Image<Rgba32> image, NaplpsState state, Size size)
    {
        if (!_command.IsValid || _command.Vertices.Count < 2)
        {
            return;
        }

        // ANSI X3.110 §5.3.2.5.1: a transparent drawing color produces no output.
        if (state.IsTransparent)
        {
            return;
        }

        var (brush, pen) = GetBrushAndPenFromFillableCommand(size, state);
        var vertices = _command.Vertices;
        var fillOptions = FillOptions();
        bool wantOutline = !_command.ShouldFill || _command.Texture.ShouldHighlight;

        // A highlighted outline is always solid; a plain outline follows the current line texture.
        bool solidOutline = _command.Texture.ShouldHighlight || _command.Texture.LineTexture == NaplpsTexture.LineTextures.Solid;

        // Authentic mode: the perimeter is plotted with the integer pel swept from the boundary
        // outward (like straight lines / outlined rects), not an anti-aliased half-pen inset. This
        // gives the correct stroke width and placement (e.g. the MadMaze magenta frame).
        bool authenticOutline = wantOutline && solidOutline && Options.AuthenticGeometry;

        // Authentic mode: the device's filled region includes the boundary pel swept outward
        // (right + up for a positive pel), which an exact-bounds fill omits and leaves ~1 pel
        // short on the top and right edges. Extend the fill by the round-half-up pel there.
        int ex0 = 0, ex1 = 0, ey0 = 0, ey1 = 0;
        if (Options.AuthenticGeometry)
        {
            var (ox0, ox1, oy0, oy1, _, _) = GetDashPel(size);
            ex0 = ox0; ex1 = ox1; ey0 = oy0; ey1 = oy1;
        }

        var fillRects = new List<RectangularPolygon>();

        image.Mutate(x =>
        {
            for (int i = 0; i < vertices.Count - 1; i += 2)
            {
                var (x1, y1, x2, y2) = NaplpsUtils.ConvertRectToScreen(size, vertices[i].X, vertices[i].Y, vertices[i + 1].X, vertices[i + 1].Y);

                if (_command.ShouldFill)
                {
                    fillRects.Add(new RectangularPolygon(new PointF(x1 + ex0, y1 + ey0), new PointF(x2 + ex1, y2 + ey1)));
                }

                if (wantOutline && !authenticOutline)
                {
                    float outlineWidth = GetPenWidth(size);
                    float inset = outlineWidth / 2f;
                    var insetRect = new RectangularPolygon(
                        new PointF(x1 + inset, y1 + inset),
                        new PointF(x2 - inset, y2 - inset));

                    // A hard-edged SOLID stroke rasterizes through our deterministic filler
                    // (issue #45: ImageSharp's differs between x64 and arm64). Textured pens keep
                    // their dash pattern on ImageSharp; anti-aliased previews stay there too.
                    bool solidPen = _command.Texture.ShouldHighlight || _command.Texture.LineTexture == NaplpsTexture.LineTextures.Solid;

                    if (solidPen && !Options.Antialias)
                    {
                        StrokePathHard(image, insetRect, outlineWidth, GetOutlineColor());
                    }
                    else
                    {
                        var outlinePen = _command.Texture.ShouldHighlight
                            ? Pens.Solid(GetOutlineColor(), outlineWidth)
                            : GetTexturedPen(GetOutlineColor(), outlineWidth);
                        x.Draw(fillOptions, outlinePen, insetRect);
                    }
                }
            }
        });

        if (fillRects.Count > 0)
        {
            var (sFg, sBg) = ((FillableGeometricDrawingCommandBase)_command).GetColors(state);
            var src = GetFillSource(size, sFg, sBg);

            foreach (var r in fillRects)
            {
                FillShape(image, r.Points.Span, brush, src);
            }
        }

        if (authenticOutline)
        {
            var color = GetOutlineColor();
            // Round-half-up pel (1/256 -> 3px) matching the reference render's frame strokes (e.g. the
            // MadMaze magenta highlight frame), not the truncated 2px GetPelOffsets.
            var (dxMin, dxMax, dyMin, dyMax, _, _) = GetDashPel(size);

            for (int i = 0; i < vertices.Count - 1; i += 2)
            {
                var (x1, y1, x2, y2) = NaplpsUtils.ConvertRectToScreen(size, vertices[i].X, vertices[i].Y, vertices[i + 1].X, vertices[i + 1].Y);
                var tl = new PointF(x1, y1);
                var tr = new PointF(x2, y1);
                var br = new PointF(x2, y2);
                var bl = new PointF(x1, y2);
                DrawableLine.PlotSweptPelLine(image, tl, tr, dxMin, dxMax, dyMin, dyMax, color);
                DrawableLine.PlotSweptPelLine(image, tr, br, dxMin, dxMax, dyMin, dyMax, color);
                DrawableLine.PlotSweptPelLine(image, br, bl, dxMin, dxMax, dyMin, dyMax, color);
                DrawableLine.PlotSweptPelLine(image, bl, tl, dxMin, dxMax, dyMin, dyMax, color);
            }
        }
    }
}
