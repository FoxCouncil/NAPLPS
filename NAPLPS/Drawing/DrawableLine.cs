// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;
using PointF = SixLabors.ImageSharp.PointF;

namespace NAPLPS.Drawing;

public class DrawableLine : Drawable, IDrawable
{
    private readonly LineCommand _command;

    public DrawableLine(LineCommand command) : base(command)
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

        // NAPLPS spec: lines are drawn by sweeping the rectangular logical pel along the path.
        // The pel is NOT centered — its origin corner is determined by the sign of pel dimensions.
        var (dxMin, dxMax, dyMin, dyMax) = GetPelOffsets(size);

        if (Options.AuthenticGeometry)
        {
            var pelPattern = PelDashPattern(state.Texture.LineTexture);
            if (pelPattern != null)
            {
                var (ox0, ox1, oy0, oy1, pelMajor) = GetDashPel(size);
                PlotDashedPolyline(image, points, asSet: false, ox0, ox1, oy0, oy1, pelMajor, pelPattern, isColor);
                return;
            }

            for (int i = 0; i < points.Count - 1; i++)
            {
                PlotSweptPelLine(image, points[i], points[i + 1], dxMin, dxMax, dyMin, dyMax, isColor);
            }

            return;
        }

        for (int i = 0; i < points.Count - 1; i++)
        {
            FillShape(image, ConvexHullOfSweptPel(points[i], points[i + 1], dxMin, dxMax, dyMin, dyMax), isColor);
        }
    }

    /// <summary>
    /// The repeating on/off pattern (in PELS) for a NAPLPS line texture; null for solid. A 1-pel ON
    /// element renders as a single isolated dot, a multi-pel ON element as a contiguous dash. Per the
    /// spec: Dotted = 1 on/1 off, Dashed = 3 on/1 off, Dotted-dashed = 1 on/1 off/3 on/1 off.
    /// </summary>
    internal static int[]? PelDashPattern(NaplpsTexture.LineTextures texture) => texture switch
    {
        NaplpsTexture.LineTextures.Dotted => new[] { 1, 1 },
        NaplpsTexture.LineTextures.Dashed => new[] { 3, 1 },
        NaplpsTexture.LineTextures.DottedDashed => new[] { 1, 1, 3, 1 },
        _ => null,
    };

    /// <summary>
    /// Plots a dotted/dashed polyline exactly as the MVDI device rasterizer does (verified against
    /// the reference render). Walks the integer Bresenham path across every segment with ONE dash counter that
    /// is continuous over the whole stroke (phase carries across vertices, shared vertices are not
    /// double-counted), and stamps the P x P pel only at pel boundaries (every P major-axis steps)
    /// whose pel index is "on". Because a dot is a single pel stamp, every dot is a clean axis-aligned
    /// block and the Bresenham minor-axis jog always lands in a gap - matching the reference render's dots.
    /// </summary>
    internal static void PlotDashedPolyline(Image<Rgba32> image, IReadOnlyList<PointF> points, bool asSet,
        int ox0, int ox1, int oy0, int oy1, int pelMajor, int[] pelPattern, ISColor color)
    {
        var rgba = color.ToPixel<Rgba32>();
        int w = image.Width, h = image.Height;
        int P = Math.Max(1, pelMajor);
        int patTotal = 0;
        foreach (var v in pelPattern) patTotal += v;
        if (patTotal <= 0)
        {
            return;
        }

        bool OnPel(int pelIx)
        {
            int p = ((pelIx % patTotal) + patTotal) % patTotal;
            int acc = 0;
            for (int k = 0; k < pelPattern.Length; k++)
            {
                acc += pelPattern[k];
                if (p < acc) return (k & 1) == 0; // even element index = ON
            }
            return false;
        }

        void Stamp(int cx, int cy)
        {
            for (int yy = cy + oy0; yy < cy + oy1; yy++)
            {
                if ((uint)yy >= (uint)h) continue;
                for (int xx = cx + ox0; xx < cx + ox1; xx++)
                {
                    if ((uint)xx >= (uint)w) continue;
                    image[xx, yy] = rgba;
                }
            }
        }

        // Collect the continuous device path (dedup shared vertices for connected polylines).
        var path = new List<(int x, int y)>();
        int step = asSet ? 2 : 1;
        for (int s = 0; s + 1 < points.Count; s += step)
        {
            int x0 = (int)MathF.Round(points[s].X), y0 = (int)MathF.Round(points[s].Y);
            int x1 = (int)MathF.Round(points[s + 1].X), y1 = (int)MathF.Round(points[s + 1].Y);
            int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1, err = dx + dy;
            bool first = true;
            while (true)
            {
                // For a connected polyline, skip the shared start vertex (already added by the prior segment).
                if (!(first && !asSet && s > 0 && path.Count > 0 && path[^1] == (x0, y0)))
                {
                    path.Add((x0, y0));
                }
                first = false;
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }

        for (int i = 0; i < path.Count; i++)
        {
            if (i % P == 0 && OnPel(i / P))
            {
                Stamp(path[i].x, path[i].y);
            }
        }
    }

    /// <summary>
    /// Draws a straight segment by stepping integer device pixels (Bresenham) and stamping the
    /// integer pel rectangle at each step, writing pixels directly with no anti-aliasing. This
    /// reproduces the hard staircase of the original device line rasterizer. For a 1x1 pel this
    /// is a classic single-pixel Bresenham line.
    /// </summary>
    internal static void PlotSweptPelLine(Image<Rgba32> image, PointF p1, PointF p2, float dxMin, float dxMax, float dyMin, float dyMax, ISColor color)
    {
        var px = image;
        var rgba = color.ToPixel<Rgba32>();

        int ox0 = (int)MathF.Round(dxMin);
        int ox1 = (int)MathF.Round(dxMax);
        int oy0 = (int)MathF.Round(dyMin);
        int oy1 = (int)MathF.Round(dyMax);

        // Ensure at least a 1-pixel pel footprint.
        if (ox1 <= ox0) ox1 = ox0 + 1;
        if (oy1 <= oy0) oy1 = oy0 + 1;

        int x0 = (int)MathF.Round(p1.X);
        int y0 = (int)MathF.Round(p1.Y);
        int x1 = (int)MathF.Round(p2.X);
        int y1 = (int)MathF.Round(p2.Y);

        int w = image.Width, h = image.Height;

        void StampPel(int cx, int cy)
        {
            for (int yy = cy + oy0; yy < cy + oy1; yy++)
            {
                if ((uint)yy >= (uint)h) continue;

                for (int xx = cx + ox0; xx < cx + ox1; xx++)
                {
                    if ((uint)xx >= (uint)w) continue;
                    px[xx, yy] = rgba;
                }
            }
        }

        int dx = Math.Abs(x1 - x0);
        int dy = -Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;

        while (true)
        {
            StampPel(x0, y0);

            if (x0 == x1 && y0 == y1)
            {
                break;
            }

            int e2 = 2 * err;

            if (e2 >= dy)
            {
                err += dy;
                x0 += sx;
            }

            if (e2 <= dx)
            {
                err += dx;
                y0 += sy;
            }
        }
    }

    /// <summary>
    /// Draws one glyph stroke as the device character generator does: a run-length Bresenham with a
    /// uniform pel. The DDA always starts at the lower-Y endpoint; it is X-major iff |dx|&gt;|dy|;
    /// the Bresenham error is seeded at -floor(major/2) and steps the minor axis only when it
    /// crosses &gt;0. At every path point a uniform pelX-wide x pelY-tall rectangle is stamped,
    /// extending DOWN-RIGHT from the point. This replaces the direction-dependent swept-pel
    /// heuristics for glyph strokes.
    /// </summary>
    internal static void PlotMvdiStroke(Image<Rgba32> image, int x0, int y0, int x1, int y1, int pelX, int pelY, ISColor color)
    {
        var rgba = color.ToPixel<Rgba32>();
        int w = image.Width, h = image.Height;
        if (pelX < 1) pelX = 1;
        if (pelY < 1) pelY = 1;

        void Stamp(int cx, int cy)
        {
            for (int yy = cy; yy < cy + pelY; yy++)
            {
                if ((uint)yy >= (uint)h) continue;
                for (int xx = cx; xx < cx + pelX; xx++)
                {
                    if ((uint)xx >= (uint)w) continue;
                    image[xx, yy] = rgba;
                }
            }
        }

        // Orient so the DDA runs in +Y from the lower endpoint.
        if (y0 > y1)
        {
            (x0, y0, x1, y1) = (x1, y1, x0, y0);
        }
        int dx = x1 - x0, dy = y1 - y0;   // dy >= 0
        int sx = dx >= 0 ? 1 : -1;
        int adx = Math.Abs(dx), ady = dy;

        Stamp(x0, y0);
        if (adx == 0 && ady == 0)
        {
            return;
        }

        if (adx > ady)   // X-major
        {
            int err = -(adx / 2);
            int x = x0, y = y0;
            for (int i = 0; i < adx; i++)
            {
                x += sx;
                err += ady;
                if (err > 0) { y += 1; err -= adx; }
                Stamp(x, y);
            }
        }
        else             // Y-major (includes exact 45)
        {
            int err = -(ady / 2);
            int x = x0, y = y0;
            for (int i = 0; i < ady; i++)
            {
                y += 1;
                err += adx;
                if (err > 0) { x += sx; err -= ady; }
                Stamp(x, y);
            }
        }
    }

    /// <summary>
    /// Computes the convex hull of the logical pel rectangle positioned at two points.
    /// Default: pel extends RIGHT by pelW and UP by pelH (positive NAPLPS dimensions).
    /// </summary>
    internal static PointF[] ConvexHullOfSweptPel(PointF p1, PointF p2, float pelW, float pelH)
    {
        return ConvexHullOfSweptPel(p1, p2, 0, pelW, -pelH, 0);
    }

    /// <summary>
    /// Computes the convex hull of the logical pel swept from P1 to P2.
    /// ANSI X3.110: the rectangular pel is placed at every point along the path.
    /// The hull of (4 pel corners at P1) + (4 pel corners at P2) gives the correct
    /// filled region for a straight segment.
    /// (dxMin, dxMax, dyMin, dyMax) define the pel rectangle offset from the drawing point.
    /// </summary>
    internal static PointF[] ConvexHullOfSweptPel(PointF p1, PointF p2, float dxMin, float dxMax, float dyMin, float dyMax)
    {
        // 8 corners: full pel rectangle at both endpoints
        var allPoints = new PointF[8];
        allPoints[0] = new PointF(p1.X + dxMin, p1.Y + dyMin);
        allPoints[1] = new PointF(p1.X + dxMax, p1.Y + dyMin);
        allPoints[2] = new PointF(p1.X + dxMax, p1.Y + dyMax);
        allPoints[3] = new PointF(p1.X + dxMin, p1.Y + dyMax);
        allPoints[4] = new PointF(p2.X + dxMin, p2.Y + dyMin);
        allPoints[5] = new PointF(p2.X + dxMax, p2.Y + dyMin);
        allPoints[6] = new PointF(p2.X + dxMax, p2.Y + dyMax);
        allPoints[7] = new PointF(p2.X + dxMin, p2.Y + dyMax);

        return ComputeConvexHull(allPoints);
    }

    /// <summary>
    /// Perpendicular-only hull for shape outlines (rectangles, polygons, arcs).
    /// Projects the pel onto the perpendicular axis only, so the stroke doesn't
    /// overshoot past shared vertices at corners. This avoids ear/bump artifacts
    /// where consecutive outline edges meet.
    /// </summary>
    internal static PointF[] PerpendicularHullOfSweptPel(PointF p1, PointF p2, float dxMin, float dxMax, float dyMin, float dyMax)
    {
        float lineX = p2.X - p1.X;
        float lineY = p2.Y - p1.Y;
        float lineLen = MathF.Sqrt(lineX * lineX + lineY * lineY);

        if (lineLen < 0.001f)
        {
            return
            [
                new PointF(p1.X + dxMin, p1.Y + dyMin),
                new PointF(p1.X + dxMax, p1.Y + dyMin),
                new PointF(p1.X + dxMax, p1.Y + dyMax),
                new PointF(p1.X + dxMin, p1.Y + dyMax)
            ];
        }

        float perpX = -lineY / lineLen;
        float perpY = lineX / lineLen;

        float[] perpProj =
        [
            dxMin * perpX + dyMin * perpY,
            dxMax * perpX + dyMin * perpY,
            dxMax * perpX + dyMax * perpY,
            dxMin * perpX + dyMax * perpY
        ];

        float perpMin = perpProj.Min();
        float perpMax = perpProj.Max();

        var allPoints = new PointF[4];
        allPoints[0] = new PointF(p1.X + perpMin * perpX, p1.Y + perpMin * perpY);
        allPoints[1] = new PointF(p1.X + perpMax * perpX, p1.Y + perpMax * perpY);
        allPoints[2] = new PointF(p2.X + perpMin * perpX, p2.Y + perpMin * perpY);
        allPoints[3] = new PointF(p2.X + perpMax * perpX, p2.Y + perpMax * perpY);

        return ComputeConvexHull(allPoints);
    }

    /// <summary>
    /// Andrew's monotone chain convex hull algorithm. O(n log n).
    /// Returns vertices in counter-clockwise order.
    /// </summary>
    private static PointF[] ComputeConvexHull(PointF[] points)
    {
        int n = points.Length;
        if (n < 3)
        {
            return points;
        }

        // Sort by X, then by Y
        Array.Sort(points, (a, b) => a.X == b.X ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));

        var hull = new PointF[2 * n];
        int k = 0;

        // Build lower hull
        for (int i = 0; i < n; i++)
        {
            while (k >= 2 && Cross(hull[k - 2], hull[k - 1], points[i]) <= 0)
            {
                k--;
            }

            hull[k++] = points[i];
        }

        // Build upper hull
        int lower = k + 1;

        for (int i = n - 2; i >= 0; i--)
        {
            while (k >= lower && Cross(hull[k - 2], hull[k - 1], points[i]) <= 0)
            {
                k--;
            }

            hull[k++] = points[i];
        }

        // Remove the last point (duplicate of the first)
        var result = new PointF[k - 1];
        Array.Copy(hull, result, k - 1);
        return result;
    }

    private static float Cross(PointF o, PointF a, PointF b)
    {
        return (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
    }
}
