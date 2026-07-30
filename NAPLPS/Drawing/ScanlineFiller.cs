// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using PointF = SixLabors.ImageSharp.PointF;

namespace NAPLPS.Drawing;

/// <summary>
/// What a hard-edged fill paints with: a flat colour, or a tiled on/off pattern with a foreground
/// and an optional background (null background = the gaps are left alone, which is how colour
/// modes 0 and 1 let the canvas show through).
/// </summary>
internal readonly struct FillSource
{
    public FillSource(Rgba32 foreground)
    {
        Foreground = foreground;
        Background = null;
        Pattern = null;
    }

    public FillSource(Rgba32 foreground, Rgba32? background, bool[,] pattern)
    {
        Foreground = foreground;
        Background = background;
        Pattern = pattern;
    }

    public Rgba32 Foreground { get; }

    public Rgba32? Background { get; }

    public bool[,]? Pattern { get; }
}

/// <summary>
/// A polygon scanline filler that is bit-identical on every CPU architecture.
///
/// It exists because ImageSharp's is not. With anti-aliasing off ImageSharp decides a pixel by
/// thresholding its coverage at 0.5, and a pixel whose coverage is EXACTLY 0.5 - which happens
/// constantly on axis-aligned edges at half-pixel coordinates, and on curves at particular radii -
/// is resolved differently on x64 and arm64. That was the last cause of cross-platform baseline
/// mismatches in issue #45, and it cannot be fixed from outside the library: nudging geometry only
/// moves which shapes land on the boundary.
///
/// So the tie-breaking is ours now. Every value here is computed with the IEEE-exact operations
/// (+ - * /) and every ordering is total, so there is no tie left for an architecture to break
/// its own way:
///
///   - a pixel is inside when its CENTRE is inside, the classic convention, which gives the hard
///     edges the device had anyway;
///   - crossings are sorted by (x, edgeIndex), so equal x values still have one defined order;
///   - the even-odd rule matches what ImageSharp applied, so shapes keep their existing topology.
/// </summary>
internal static class ScanlineFiller
{
    /// <summary>Fills the closed polygon described by <paramref name="points"/>.</summary>
    public static void Fill(Image<Rgba32> image, ReadOnlySpan<PointF> points, in FillSource source)
    {
        Fill(image, [points.ToArray()], source);
    }

    /// <summary>
    /// Fills a shape made of several closed contours under one even-odd pass, so a contour inside
    /// another (a stroke outline's inner ring) reads as a hole rather than getting filled over.
    /// </summary>
    public static void Fill(Image<Rgba32> image, IReadOnlyList<PointF[]> contours, in FillSource source)
    {
        FillContours(image, contours, source);
    }

    private static void FillContours(Image<Rgba32> image, IReadOnlyList<PointF[]> contours, in FillSource source)
    {
        int width = image.Width;
        int height = image.Height;

        bool any = false;
        float minYf = 0f;
        float maxYf = 0f;

        foreach (var contour in contours)
        {
            if (contour.Length < 3)
            {
                continue;
            }

            foreach (var p in contour)
            {
                if (!any) { minYf = maxYf = p.Y; any = true; continue; }
                if (p.Y < minYf) { minYf = p.Y; }
                if (p.Y > maxYf) { maxYf = p.Y; }
            }
        }

        if (!any)
        {
            return;
        }

        int yStart = Math.Max(0, (int)Math.Floor(minYf - 0.5));
        int yEnd = Math.Min(height - 1, (int)Math.Ceiling(maxYf));

        if (yStart > yEnd)
        {
            return;
        }

        // (x, edgeIndex) pairs. edgeIndex is a GLOBAL index across contours, carried purely to
        // make the sort a total order.
        var crossings = new List<(double X, int Edge)>(16);

        for (int py = yStart; py <= yEnd; py++)
        {
            double sampleY = py + 0.5;

            crossings.Clear();

            int edgeBase = 0;

            foreach (var points in contours)
            {
                if (points.Length < 3)
                {
                    continue;
                }

                for (int e = 0; e < points.Length; e++)
                {
                    var a = points[e];
                    var b = points[(e + 1) % points.Length];

                    double ay = a.Y;
                    double by = b.Y;

                    // Half-open in Y so a vertex shared by two edges is counted exactly once.
                    bool spans = (ay <= sampleY && by > sampleY) || (by <= sampleY && ay > sampleY);

                    if (!spans)
                    {
                        continue;
                    }

                    double t = (sampleY - ay) / (by - ay);

                    crossings.Add((a.X + t * (b.X - a.X), edgeBase + e));
                }

                edgeBase += points.Length;
            }

            if (crossings.Count < 2)
            {
                continue;
            }

            crossings.Sort(static (l, r) => l.X != r.X ? l.X.CompareTo(r.X) : l.Edge.CompareTo(r.Edge));

            for (int i = 0; i + 1 < crossings.Count; i += 2)
            {
                double xa = crossings[i].X;
                double xb = crossings[i + 1].X;

                // Pixel px is inside when px + 0.5 lies in [xa, xb).
                int pxStart = (int)Math.Ceiling(xa - 0.5);
                int pxEnd = (int)Math.Ceiling(xb - 0.5) - 1;

                if (pxStart < 0) { pxStart = 0; }
                if (pxEnd > width - 1) { pxEnd = width - 1; }

                for (int px = pxStart; px <= pxEnd; px++)
                {
                    if (source.Pattern is bool[,] pattern)
                    {
                        int ph = pattern.GetLength(0);
                        int pw = pattern.GetLength(1);

                        if (pattern[((py % ph) + ph) % ph, ((px % pw) + pw) % pw])
                        {
                            image[px, py] = source.Foreground;
                        }
                        else if (source.Background is Rgba32 bg)
                        {
                            image[px, py] = bg;
                        }
                    }
                    else
                    {
                        image[px, py] = source.Foreground;
                    }
                }
            }
        }
    }
}
