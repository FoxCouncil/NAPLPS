// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;
using PointF = SixLabors.ImageSharp.PointF;

namespace NAPLPS.Drawing;

/// <summary>
/// Renders INCREMENTAL LINE (scribble) commands.
/// Draws polylines with motion-based segments using rectangular pel sweep.
/// </summary>
public class DrawableIncrementalLine : Drawable, IDrawable
{
    private readonly IncrementalLineCommand _command;

    public DrawableIncrementalLine(IncrementalLineCommand command) : base(command)
    {
        _command = command;
    }

    public void Draw(Image<Rgba32> image, NaplpsState state, Size size)
    {
        if (!_command.IsValid || _command.Segments.Count == 0)
        {
            return;
        }

        // Get starting position from current pen + offset
        var startPos = state.Pen;
        startPos.X += _command.StartOffset.X;
        startPos.Y += _command.StartOffset.Y;

        var startPoint = ConvertNormalizedToPoint(size, startPos.X, startPos.Y);
        float currentX = startPoint.X;
        float currentY = startPoint.Y;

        // Get pen color
        var (fgColor, _) = GetISColorFromState(state);

        // Rectangular pel sweep with sign-aware offsets
        var (dxMin, dxMax, dyMin, dyMax) = GetPelOffsets(size);

        var hulls = new List<PointF[]>();

        image.Mutate(ctx =>
        {
            foreach (var segment in _command.Segments)
            {
                // Dx and Dy are in normalized coordinate space with correct sign
                // Convert to screen pixel displacement
                var (dx, dy) = ConvertNormalizedToScreenScale(size, segment.Dx, segment.Dy);

                // Note: Y is inverted in screen coordinates
                float nextX = currentX + dx;
                float nextY = currentY - dy;

                if (segment.Draw)
                {
                    var p1 = new PointF(currentX, currentY);
                    var p2 = new PointF(nextX, nextY);
                    var hull = DrawableLine.ConvexHullOfSweptPel(p1, p2, dxMin, dxMax, dyMin, dyMax);
                    hulls.Add(hull);
                }

                currentX = nextX;
                currentY = nextY;
            }
        });

        foreach (var h in hulls)
        {
            FillShape(image, h, fgColor);
        }

        // Update pen position to last point (convert back to normalized)
        var (normX, normY) = ConvertScreenToNormalizedF(size, currentX, currentY);
        state.Pen = new Vector3(normX, normY, 0);
    }
}
