// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;
using PointF = SixLabors.ImageSharp.PointF;

namespace NAPLPS.Drawing;

public class DrawablePolygon : Drawable, IDrawable
{
    // Authentic-mode geometry: the MVDI device samples the pel by its corner, not its centre, and
    // its vertical pel spans a full row while the horizontal spans a half (from the display-ratio Y
    // compression). ImageSharp's non-AA fill/stroke is centre-sampled, so polygons land ~1 pel thin
    // on their top/left edges. Shifting the vertices by this pel-corner offset recovers those edge
    // pels for both the fill and the outline stroke. Calibrated against the reference render: reduces the
    // Ads/main corpus mean 0.74 -> 0.67% and the Screens mean 2.74 -> 2.53% (both improve; applying
    // it to fill only was worse, since much of the corpus is outlined polygon line-art).
    private const float PelCornerOffsetX = 0.5f;
    private const float PelCornerOffsetY = -1.0f;

    private readonly PolygonCommand _command;

    public DrawablePolygon(PolygonCommand command) : base(command)
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

        float ox = Options.AuthenticGeometry ? PelCornerOffsetX : 0f;
        float oy = Options.AuthenticGeometry ? PelCornerOffsetY : 0f;

        var polygonPoints = new List<PointF>();   // pel-corner-shifted: fill + non-authentic outline
        var rawPoints = new List<PointF>();         // unshifted device points: authentic swept-pel outline

        foreach (var drawPoint in _command.Points)
        {
            var polyPoint = NaplpsUtils.ConvertNormalizedToPoint(size, drawPoint.X, drawPoint.Y);
            polygonPoints.Add(new PointF(polyPoint.X + ox, polyPoint.Y + oy));
            rawPoints.Add(new PointF(polyPoint.X, polyPoint.Y));
        }

        if (polygonPoints.Count <= 1)
        {
            return;
        }

        var polygon = new Polygon(polygonPoints.ToArray());
        var (brush, pen) = GetBrushAndPenFromFillableCommand(size, state);

        var fillOptions = FillOptions();

        bool wantOutline = !_command.ShouldFill || _command.Texture.ShouldHighlight;

        // A highlighted outline is always solid; a plain outline follows the current line texture.
        bool solidOutline = _command.Texture.ShouldHighlight || _command.Texture.LineTexture == NaplpsTexture.LineTextures.Solid;

        // Authentic mode: plot the perimeter with the integer pel swept from the boundary corner
        // outward (right + up for a positive pel) - the SAME shared pel plotter MVDI uses for straight
        // lines and outlined rects, not an anti-aliased centre-sampled pen. A centred pen sits half
        // its width down-left of the true boundary, shifting outlined glyphs (e.g. the Sheraton
        // wordmark "o", an octagonal outlined polygon) down-left of the reference render.
        // Any HARD-EDGED solid outline goes through the integer pel plotter, not just authentic
        // mode's. ImageSharp's non-antialiased stroke is not reproducible across CPU architectures
        // (issue #45), and the pel plotter is both deterministic and what the device did.
        bool authenticOutline = wantOutline && solidOutline && !Options.Antialias;

        if (_command.ShouldFill)
        {
            var (pFg, pBg) = ((FillableGeometricDrawingCommandBase)_command).GetColors(state);

            FillShape(image, polygonPoints.ToArray(), brush, GetFillSource(size, pFg, pBg));

            // X3.110 5.3.3.4: a filled polygon's boundary is drawn with the logical pel and
            // the interior filled - a centre-sampled fill alone leaves sliver shapes mostly
            // unpainted (TL80TB10's wordmark-G miter wedge). Patterned (hatched/masked)
            // fills keep their pattern to the edge - no solid boundary band (the MSZT
            // striped background) - so only solid fills draw the boundary. A highlight
            // outline, when requested, overpaints below.
            if (_command.Texture.TexturePattern == NaplpsTexture.TexturePatterns.Solid)
            {
                if (Options.AuthenticGeometry && !Options.Antialias)
                {
                    // Device rasterization: sweep the pel along each edge exactly as lines
                    // are drawn - anchored on the path, extending in the pel's sign
                    // direction (the G wedge's closure diagonal stays bare on its left
                    // while the up-right sweep bridges the cap to the serif stem).
                    var boundaryColor = pFg.ToISColor();
                    var (bdxMin, bdxMax, bdyMin, bdyMax) = GetPelOffsets(size);

                    for (int i = 0; i < rawPoints.Count - 1; i++)
                    {
                        DrawableLine.PlotSweptPelLine(image, rawPoints[i], rawPoints[i + 1], bdxMin, bdxMax, bdyMin, bdyMax, boundaryColor);
                    }

                    if (rawPoints[^1] != rawPoints[0])
                    {
                        DrawableLine.PlotSweptPelLine(image, rawPoints[^1], rawPoints[0], bdxMin, bdxMax, bdyMin, bdyMax, boundaryColor);
                    }
                }
                else if (!Options.Antialias)
                {
                    // Standard rendering: same boundary rule in this path's own style - a
                    // centre-sampled pel-width perimeter stroke in the drawing color. Rasterized
                    // deterministically; ImageSharp's own hard-edged stroke left 13 standard files
                    // one boundary pixel apart on arm64 (issue #45).
                    StrokePathHard(image, polygon, GetPenWidthF(size), pFg.ToISColor());
                }
                else
                {
                    // Anti-aliased preview only.
                    var boundaryPen = Pens.Solid(pFg.ToISColor(), GetPenWidthF(size));
                    image.Mutate(x => x.Draw(fillOptions, boundaryPen, polygon));
                }
            }
        }

        image.Mutate(x =>
        {
            if (wantOutline && !authenticOutline)
            {
                float outlineWidth = GetPenWidth(size);
                var outlinePen = _command.Texture.ShouldHighlight
                    ? Pens.Solid(GetOutlineColor(), outlineWidth)
                    : GetTexturedPen(GetOutlineColor(), outlineWidth);
                x.Draw(fillOptions, outlinePen, polygon);
            }
        });

        if (authenticOutline)
        {
            var color = GetOutlineColor();
            var (dxMin, dxMax, dyMin, dyMax) = GetPelOffsets(size);
            for (int i = 0; i < rawPoints.Count - 1; i++)
            {
                DrawableLine.PlotSweptPelLine(image, rawPoints[i], rawPoints[i + 1], dxMin, dxMax, dyMin, dyMax, color);
            }

            // Close the ring if the vertex list is not already closed (last == first).
            if (rawPoints[^1] != rawPoints[0])
            {
                DrawableLine.PlotSweptPelLine(image, rawPoints[^1], rawPoints[0], dxMin, dxMax, dyMin, dyMax, color);
            }
        }
    }
}
