// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;
using PointF = SixLabors.ImageSharp.PointF;

namespace NAPLPS.Drawing;

public class DrawableArc : Drawable, IDrawable
{
    private readonly ArcCommand _command;

    public DrawableArc(ArcCommand command) : base(command)
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

        var (brush, pen) = GetBrushAndPenFromFillableCommand(size, state);

        // Hard-edged fills bypass ImageSharp entirely; see ScanlineFiller for why.
        var (srcFg, srcBg) = ((FillableGeometricDrawingCommandBase)_command).GetColors(state);
        var fillSource = GetFillSource(size, srcFg, srcBg);

        // Check for spline: more vertices than a standard arc requires
        bool isSet = _command is ArcSetFilledCommand or ArcSetOutlinedCommand;
        int normalArcVertexCount = isSet ? 3 : 2;

        if (_command.Vertices.Count > normalArcVertexCount)
        {
            DrawSpline(image, state, size, brush, pen, isSet, fillSource);
            return;
        }

        // Use floating point for precision - don't convert to integer Point yet
        var (startX, startY) = ConvertNormalizedToScreenF(size, _command.StartPoint.X, _command.StartPoint.Y);
        var (midX, midY) = ConvertNormalizedToScreenF(size, _command.IntermediatePointDisplacement.X, _command.IntermediatePointDisplacement.Y);
        var (endX, endY) = ConvertNormalizedToScreenF(size, _command.EndPointDisplacement.X, _command.EndPointDisplacement.Y);

        // Check if start and end are the same (circle) using original normalized coords
        // to avoid precision loss from screen conversion
        bool isCircle = Math.Abs(_command.StartPoint.X - _command.EndPointDisplacement.X) < 0.0001f && Math.Abs(_command.StartPoint.Y - _command.EndPointDisplacement.Y) < 0.0001f;

        if (isCircle)
        {
            // Circle - the diameter is the distance from start to mid point
            var circleDiameter = CalculateDistanceF(startX, startY, midX, midY);
            if (circleDiameter < 1)
            {
                circleDiameter = Math.Max(1, GetPenWidth(size));
            }

            float circleRadius = circleDiameter * 0.5f;
            float centerX = (startX + midX) * 0.5f;
            float centerY = (startY + midY) * 0.5f;

            var circle = new EllipsePolygon(new PointF(centerX, centerY), circleRadius);

            var circleCmd = (FillableGeometricDrawingCommandBase)_command;
            var (circFg, circBg) = circleCmd.GetColors(_command.State ?? new NaplpsState());
            var circleColor = (circleCmd.ShouldFill ? circBg : circFg).ToISColor();
            bool wantCircleOutline = !_command.ShouldFill || _command.Texture.ShouldHighlight;
            // Any hard-edged outline uses the integer pel plotter, not only authentic mode's:
            // ImageSharp's non-antialiased stroke is not portable across architectures (issue #45).
            bool authenticCircle = !Options.Antialias && wantCircleOutline;

            if (_command.ShouldFill)
            {
                FillShape(image, circle.Points.Span, brush, fillSource);
            }

            image.Mutate(x =>
            {
                if (wantCircleOutline && !authenticCircle)
                {
                    // Use centered round pen for circle outlines (symmetric)
                    float outlineWidth = GetPenWidth(size);
                    x.Draw(FillOptions(), Pens.Solid(circleColor, outlineWidth), circle);
                }
            });

            if (authenticCircle)
            {
                // Plot the circle with the hard integer pel, matching the device's stair-stepped ring.
                var (dxMin, dxMax, dyMin, dyMax) = GetPelOffsets(size);
                int csteps = Math.Max(48, (int)(2f * MathF.PI * circleRadius));
                var circlePts = new PointF[csteps + 1];
                for (int i = 0; i <= csteps; i++)
                {
                    float a = 2f * MathF.PI * i / csteps;
                    circlePts[i] = new PointF(centerX + circleRadius * DetMath.Cos(a), centerY + circleRadius * DetMath.Sin(a));
                }

                // Highlight outlines are solid per spec; otherwise honor the current line texture
                // with the same dashed-pel plot the arc path uses. The whole ring goes through one
                // PlotDashedPolyline call so the dash phase stays continuous around the circle.
                bool solidCircleOutline = _command.Texture.ShouldHighlight ||
                    _command.Texture.LineTexture == NaplpsTexture.LineTextures.Solid;
                var circlePelPattern = solidCircleOutline ? null : DrawableLine.PelDashPattern(_command.Texture.LineTexture);

                if (circlePelPattern != null)
                {
                    var (ox0, ox1, oy0, oy1, pelMajor) = GetDashPel(size);
                    DrawableLine.PlotDashedPolyline(image, circlePts, asSet: false, ox0, ox1, oy0, oy1, pelMajor, circlePelPattern, circleColor);
                }
                else
                {
                    for (int j = 0; j < circlePts.Length - 1; j++)
                    {
                        DrawableLine.PlotSweptPelLine(image, circlePts[j], circlePts[j + 1], dxMin, dxMax, dyMin, dyMax, circleColor);
                    }
                }
            }
        }
        else
        {
            // Arc - calculate circle center from 3 points
            var center = CalculateCircleCenterF(startX, startY, midX, midY, endX, endY);

            if (center == PointF.Empty)
            {
                // Collinear points - draw a line instead
                image.Mutate(x => x.DrawLine(FillOptions(), pen, new PointF(startX, startY), new PointF(endX, endY)));
                return;
            }

            // Calculate radius from center to any point (use start)
            float radius = CalculateDistanceF(center.X, center.Y, startX, startY);

            if (radius < 1)
            {
                radius = 1;
            }

            // Direct arc point generation using Atan2 — avoids fragile ArcLineSegment flags.
            // DetMath rather than MathF: `steps` below is an INTEGER derived from these angles, so a
            // last-ULP disagreement between two platforms' libm retessellates the whole curve rather
            // than nudging one pixel. See issue #45.
            float startAngle = DetMath.Atan2(startY - center.Y, startX - center.X);
            float midAngle = DetMath.Atan2(midY - center.Y, midX - center.X);
            float endAngle = DetMath.Atan2(endY - center.Y, endX - center.X);

            // Determine sweep direction: normalize angle diffs to [0, 2π)
            float startToMid = NormRadians(midAngle - startAngle);
            float startToEnd = NormRadians(endAngle - startAngle);

            // If mid point falls within [start, end) going CCW, sweep CCW; otherwise sweep CW
            float sweepAngle = (startToMid <= startToEnd) ? startToEnd : startToEnd - 2f * MathF.PI;

            // Generate arc points
            int steps = Math.Max(32, (int)(MathF.Abs(sweepAngle) * radius));
            var arcPoints = new PointF[steps + 1];

            for (int i = 0; i <= steps; i++)
            {
                float t = (float)i / steps;
                float a = startAngle + sweepAngle * t;
                arcPoints[i] = new PointF(center.X + radius * DetMath.Cos(a), center.Y + radius * DetMath.Sin(a));
            }

            if (arcPoints.Length >= 2)
            {
                var (dxMin, dxMax, dyMin, dyMax) = GetPelOffsets(size);

                // Determine the outline color from the command's own color state (not GetColorFromState
                // which can resolve the wrong palette entry). Use the same logic as GetBrushAndPenFromFillableCommand.
                var fillableCmd = (FillableGeometricDrawingCommandBase)_command;
                var (cmdFg, cmdBg) = fillableCmd.GetColors(_command.State ?? new NaplpsState());
                ISColor outlineColor;

                if (fillableCmd.ShouldFill && fillableCmd.Texture.ShouldHighlight)
                {
                    // The command's OWN color-mode snapshot, taken when it was constructed.
                    // _command.State is the shared live parse state, so by paint time it holds
                    // the mode the decoder finished in, not the one this arc was drawn under.
                    outlineColor = (fillableCmd.ColorMode == 2 ? cmdBg : Color.Black).ToISColor();
                }
                else
                {
                    outlineColor = (fillableCmd.ShouldFill ? cmdBg : cmdFg).ToISColor();
                }

                bool wantOutline = !_command.ShouldFill || _command.Texture.ShouldHighlight;
                bool solidOutline = _command.Texture.ShouldHighlight || _command.Texture.LineTexture == NaplpsTexture.LineTextures.Solid;
                // Authentic mode plots the arc with the hard integer pel (like straight lines),
                // reproducing the device rasterizer's stair-stepped curve instead of an AA pen.
                bool authenticOutline = !Options.Antialias && wantOutline;

                image.Mutate(x =>
                {
                    if (_command.ShouldFill)
                    {
                        // Every fill here goes through FillOptions() like the rest of the drawables.
                        // This path used to take ImageSharp's default options, so it anti-aliased even
                        // in authentic mode - wrong for a device that never anti-aliased, and the source
                        // of issue #45's residual platform divergence: ImageSharp's ANTI-ALIASED fill of
                        // a CONCAVE polygon is not bit-reproducible across x64 and arm64. The interior
                        // fill below is exactly that shape. With anti-aliasing off it is identical on
                        // both. Convex fills, strokes and pattern brushes were all verified portable.
                        //
                        // ANSI X3.110: filled arc area includes "the region of the outline
                        // and the chord traced by the logical pel." The fill extends by pel size.
                        // Sweep pel along arc curve first (extends fill outward)
                        for (int j = 0; j < arcPoints.Length - 1; j++)
                        {
                            var hull = DrawableLine.PerpendicularHullOfSweptPel(arcPoints[j], arcPoints[j + 1], dxMin, dxMax, dyMin, dyMax);
                            FillShape(image, hull, brush, fillSource);
                        }

                        // Sweep pel along the chord (start to end)
                        var chordHull = DrawableLine.PerpendicularHullOfSweptPel(arcPoints[0], arcPoints[^1], dxMin, dxMax, dyMin, dyMax);
                        FillShape(image, chordHull, brush, fillSource);

                        // Fill the arc-chord interior LAST to cover any sub-pixel gaps
                        // between adjacent pel sweep hulls
                        var fillPoints = new List<PointF>(arcPoints);
                        fillPoints.Add(new PointF(startX, startY));
                        FillShape(image, fillPoints.ToArray(), brush, fillSource);
                    }

                    // Draw outline for non-filled arcs, or highlight outline for filled arcs.
                    // Use centered round pen for outlines (symmetric, matches PP3 behavior)
                    // rather than asymmetric pel sweep which can leave visible edges
                    // that subsequent fills don't fully cover.
                    if (wantOutline && !authenticOutline)
                    {
                        float outlineWidth = GetPenWidthF(size);
                        // Highlight uses solid per spec; outlined arcs use current line texture
                        var outlinePen = _command.Texture.ShouldHighlight
                            ? Pens.Solid(outlineColor, outlineWidth)
                            : GetTexturedPen(outlineColor, outlineWidth);
                        x.DrawLine(FillOptions(), outlinePen, arcPoints);
                    }
                });

                if (authenticOutline)
                {
                    var pelPattern = solidOutline ? null : DrawableLine.PelDashPattern(_command.Texture.LineTexture);
                    if (pelPattern != null)
                    {
                        var (ox0, ox1, oy0, oy1, pelMajor) = GetDashPel(size);
                        DrawableLine.PlotDashedPolyline(image, arcPoints, asSet: false, ox0, ox1, oy0, oy1, pelMajor, pelPattern, outlineColor);
                    }
                    else
                    {
                        for (int j = 0; j < arcPoints.Length - 1; j++)
                        {
                            DrawableLine.PlotSweptPelLine(image, arcPoints[j], arcPoints[j + 1], dxMin, dxMax, dyMin, dyMax, outlineColor);
                        }
                    }
                }
            }
        }
    }

    // Uses centralized NaplpsUtils.ConvertNormalizedToScreenF

    private static float CalculateDistanceF(float x1, float y1, float x2, float y2)
    {
        float dx = x2 - x1;
        float dy = y2 - y1;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Normalize an angle in radians to [0, 2π).</summary>
    private static float NormRadians(float angle)
    {
        const float twoPi = 2f * MathF.PI;
        angle %= twoPi;

        if (angle < 0)
        {
            angle += twoPi;
        }

        return angle;
    }

    private void DrawSpline(Image<Rgba32> image, NaplpsState state, Size size, Brush brush, Pen pen, bool isSet, in FillSource fillSource)
    {
        // Build absolute control points from the vertex chain
        var controlPoints = new List<PointF>();

        if (isSet)
        {
            // Set variant: Vertices[0] is absolute start, rest are relative displacements
            var current = _command.Vertices[0];
            var (sx, sy) = ConvertNormalizedToScreenF(size, current.X, current.Y);
            controlPoints.Add(new PointF(sx, sy));

            for (int i = 1; i < _command.Vertices.Count; i++)
            {
                current += _command.Vertices[i];
                var (px, py) = ConvertNormalizedToScreenF(size, current.X, current.Y);
                controlPoints.Add(new PointF(px, py));
            }
        }
        else
        {
            // Non-set variant: start is the pen position, all vertices are relative displacements
            var current = _command.State.Pen;
            var (sx, sy) = ConvertNormalizedToScreenF(size, current.X, current.Y);
            controlPoints.Add(new PointF(sx, sy));

            for (int i = 0; i < _command.Vertices.Count; i++)
            {
                current += _command.Vertices[i];
                var (px, py) = ConvertNormalizedToScreenF(size, current.X, current.Y);
                controlPoints.Add(new PointF(px, py));
            }
        }

        if (controlPoints.Count < 2)
        {
            return;
        }

        // Generate Catmull-Rom spline points through all control points
        const int segmentSteps = 16;
        var splinePoints = new List<PointF>();

        for (int i = 0; i < controlPoints.Count - 1; i++)
        {
            // Catmull-Rom requires 4 points: p0, p1, p2, p3
            // For boundary segments, clamp to the nearest available point
            var p0 = controlPoints[Math.Max(i - 1, 0)];
            var p1 = controlPoints[i];
            var p2 = controlPoints[i + 1];
            var p3 = controlPoints[Math.Min(i + 2, controlPoints.Count - 1)];

            for (int step = 0; step < segmentSteps; step++)
            {
                float t = (float)step / segmentSteps;
                splinePoints.Add(CatmullRom(p0, p1, p2, p3, t));
            }
        }

        // Add the final point
        splinePoints.Add(controlPoints[controlPoints.Count - 1]);

        if (splinePoints.Count >= 2)
        {
            var splineArray = splinePoints.ToArray();

            if (_command.ShouldFill)
            {
                var fillPoints = new List<PointF>(splineArray) { splineArray[0] };

                FillShape(image, fillPoints.ToArray(), brush, fillSource);
            }

            if (!_command.ShouldFill || _command.Texture.ShouldHighlight)
            {
                image.Mutate(x => x.DrawLine(FillOptions(), pen, splineArray));
            }
        }
    }

    private static PointF CatmullRom(PointF p0, PointF p1, PointF p2, PointF p3, float t)
    {
        float t2 = t * t;
        float t3 = t2 * t;
        return new PointF(
            0.5f * ((2f * p1.X) + (-p0.X + p2.X) * t + (2f * p0.X - 5f * p1.X + 4f * p2.X - p3.X) * t2 + (-p0.X + 3f * p1.X - 3f * p2.X + p3.X) * t3),
            0.5f * ((2f * p1.Y) + (-p0.Y + p2.Y) * t + (2f * p0.Y - 5f * p1.Y + 4f * p2.Y - p3.Y) * t2 + (-p0.Y + 3f * p1.Y - 3f * p2.Y + p3.Y) * t3)
        );
    }

    private static PointF CalculateCircleCenterF(float ax, float ay, float bx, float by, float cx, float cy)
    {
        float d = 2 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));

        if (Math.Abs(d) < 0.0001f)
        {
            return PointF.Empty; // Collinear points
        }

        float ux = ((ax * ax + ay * ay) * (by - cy) + (bx * bx + by * by) * (cy - ay) + (cx * cx + cy * cy) * (ay - by)) / d;
        float uy = ((ax * ax + ay * ay) * (cx - bx) + (bx * bx + by * by) * (ax - cx) + (cx * cx + cy * cy) * (bx - ax)) / d;

        return new PointF(ux, uy);
    }
}
