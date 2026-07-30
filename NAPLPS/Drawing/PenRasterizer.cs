// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS
//
// Faithful scalar transcription of ImageSharp.Drawing v2.1.7's polygon scan rasterizer
// (PolygonScanner / ScanEdgeCollection / ActiveEdgeList / RasterizerExtensions, (c) Six Labors,
// Six Labors Split License). Kept behaviourally identical - including its deliberate quirks -
// so hard-edged strokes look exactly as they always have.

using PointF = SixLabors.ImageSharp.PointF;

namespace NAPLPS.Drawing;

/// <summary>
/// Renders a filled path region the way ImageSharp's non-antialiased Fill does, but with one
/// tie-break rule on every CPU.
///
/// Why this exists: ImageSharp snaps vertex Y to a 1/8-subpixel grid before scanning, and its
/// SIMD paths ROUND TIES DIFFERENTLY PER ARCHITECTURE - AVX uses ceil(y*8 - 0.5) (ties down),
/// NEON uses round-away-from-zero (ties up), and the scalar tail uses MidpointRounding.AwayFromZero.
/// A vertex sitting exactly on the 1/16 grid therefore lands on different subrows on x64 and
/// arm64, which flips boundary pixels: that is the residual cross-platform divergence from
/// issue #45, still reachable through pen strokes. This port computes everything scalar with the
/// AVX tie rule, so x64 output is preserved and arm64 now matches it.
/// </summary>
internal static class PenRasterizer
{
    private const int Subsampling = 8;                       // FillPathProcessor.MinimumSubpixelCount
    private const float SubpixelDistance = 1f / Subsampling;
    private const float SubpixelArea = SubpixelDistance / Subsampling;

    /// <summary>Fills <paramref name="contours"/> (even-odd, the ImageSharp default) with <paramref name="color"/>.</summary>
    public static void FillEvenOdd(Image<Rgba32> image, IReadOnlyList<PointF[]> contours, Rgba32 color)
    {
        // Bounds exactly as FillPathProcessor<TPixel> computes them: floor/ceil of the path
        // bounds, intersected with the image.
        float bLeft = float.MaxValue, bTop = float.MaxValue, bRight = float.MinValue, bBottom = float.MinValue;

        foreach (var ring in contours)
        {
            foreach (var p in ring)
            {
                if (p.X < bLeft) { bLeft = p.X; }
                if (p.X > bRight) { bRight = p.X; }
                if (p.Y < bTop) { bTop = p.Y; }
                if (p.Y > bBottom) { bBottom = p.Y; }
            }
        }

        if (bLeft > bRight)
        {
            return;
        }

        int minX = Math.Max(0, (int)MathF.Floor(bLeft));
        int minY = Math.Max(0, (int)MathF.Floor(bTop));
        int maxX = Math.Min(image.Width, (int)MathF.Ceiling(bRight));
        int maxY = Math.Min(image.Height, (int)MathF.Ceiling(bBottom));

        if (minX >= maxX || minY >= maxY)
        {
            return;
        }

        var edges = BuildEdges(contours);

        if (edges.Count == 0)
        {
            return;
        }

        // sorted0: edge indices by Y0; sorted1: by Y1. Same introsort the original uses.
        var sorted0 = new int[edges.Count];
        var sorted1 = new int[edges.Count];
        var keys0 = new float[edges.Count];
        var keys1 = new float[edges.Count];

        for (int i = 0; i < edges.Count; i++)
        {
            keys0[i] = edges[i].Y0;
            keys1[i] = edges[i].Y1;
            sorted0[i] = i;
            sorted1[i] = i;
        }

        Array.Sort(keys0, sorted0);
        Array.Sort(keys1, sorted1);

        var active = new ActiveList(edges.Count);
        int idx0 = 0, idx1 = 0;
        float subPixelY;

        // SkipEdgesBeforeMinY: fake-scan at edge start/end Y positions below the top of interest.
        // NOTE the two cursor pairs, exactly as the original: i0/i1 walk the breakpoint values to
        // pick the next Y to visit, while idx0/idx1 are advanced ONLY by Enter/LeaveEdges as edges
        // are actually consumed. Sharing one pair drops an edge per step, which ate stroke
        // segments on any shape extending above the canvas.
        {
            subPixelY = edges[sorted0[0]].Y0;

            int i0 = 1;
            int i1 = 0;

            while (subPixelY < minY)
            {
                EnterEdges(edges, sorted0, ref idx0, subPixelY, ref active);
                LeaveEdges(edges, sorted1, ref idx1, subPixelY, ref active);
                active.RemoveLeavingEdges();

                bool hasMore0 = i0 < sorted0.Length;
                bool hasMore1 = i1 < sorted1.Length;

                if (!hasMore0 && !hasMore1)
                {
                    break;
                }

                float y0 = hasMore0 ? edges[sorted0[i0]].Y0 : float.MaxValue;
                float y1 = hasMore1 ? edges[sorted1[i1]].Y1 : float.MaxValue;

                if (y0 < y1) { subPixelY = y0; i0++; }
                else { subPixelY = y1; i1++; }
            }
        }

        int scanlineWidth = maxX - minX;
        var scanline = new float[scanlineWidth];
        var intersections = new float[edges.Count * 2 + 4];
        bool scanlineDirty = true;

        for (int pixelLineY = minY; pixelLineY < maxY; pixelLineY++)
        {
            if (scanlineDirty)
            {
                Array.Clear(scanline);
                scanlineDirty = false;
            }

            float yPlusOne = pixelLineY + 1;
            subPixelY = pixelLineY - SubpixelDistance;

            while (true)
            {
                subPixelY += SubpixelDistance;
                EnterEdges(edges, sorted0, ref idx0, subPixelY, ref active);
                LeaveEdges(edges, sorted1, ref idx1, subPixelY, ref active);

                if (subPixelY >= yPlusOne)
                {
                    break;
                }

                int count = active.ScanOddEven(subPixelY, edges, intersections);

                // RasterizerExtensions.ScanCurrentSubpixelLineInto, xOffset = 0. The single-pixel
                // span "overcount" (both partial terms applied to the same pixel) is theirs, kept.
                for (int point = 0; point + 1 < count; point += 2)
                {
                    float scanStart = intersections[point] - minX;
                    float scanEnd = intersections[point + 1] - minX;
                    int startX = (int)MathF.Floor(scanStart);
                    int endX = (int)MathF.Floor(scanEnd);

                    if (startX >= 0 && startX < scanline.Length)
                    {
                        float subpixelWidth = (startX + 1 - scanStart) / SubpixelDistance;
                        scanline[startX] += subpixelWidth * SubpixelArea;
                        scanlineDirty |= subpixelWidth > 0;
                    }

                    if (endX >= 0 && endX < scanline.Length)
                    {
                        float subpixelWidth = (scanEnd - endX) / SubpixelDistance;
                        scanline[endX] += subpixelWidth * SubpixelArea;
                        scanlineDirty |= subpixelWidth > 0;
                    }

                    int nextX = startX + 1;
                    endX = Math.Min(endX, scanline.Length);
                    nextX = Math.Max(nextX, 0);

                    if (endX > nextX)
                    {
                        for (int x = nextX; x < endX; x++)
                        {
                            scanline[x] += SubpixelDistance;
                        }

                        scanlineDirty = true;
                    }
                }
            }

            if (!scanlineDirty)
            {
                continue;
            }

            for (int x = 0; x < scanlineWidth; x++)
            {
                if (scanline[x] >= 0.5f)
                {
                    image[minX + x, pixelLineY] = color;
                }
            }
        }
    }

    // ------------------------------------------------------------------ edges

    /// <summary>x = p*y + q, exactly as ScanEdge including the centering-for-accuracy trick.</summary>
    private readonly struct Edge
    {
        public readonly float Y0;
        public readonly float Y1;
        private readonly float p;
        private readonly float q;
        public readonly int EmitV0;
        public readonly int EmitV1;

        public Edge(PointF p0, PointF p1, int emit0, int emit1)
        {
            Y0 = p0.Y;
            Y1 = p1.Y;
            EmitV0 = emit0;
            EmitV1 = emit1;

            float dy = p1.Y - p0.Y;
            float cx = (p0.X + p1.X) * 0.5f;
            float cy = (p0.Y + p1.Y) * 0.5f;

            float ax = p0.X - cx, ay = p0.Y - cy;
            float bx = p1.X - cx, by = p1.Y - cy;

            p = (bx - ax) / dy;
            q = ((ax * by) - (bx * ay)) / dy + (cx - (p * cy));
        }

        public float GetX(float y) => (p * y) + q;
    }

    private enum Cat { Up = 0, Down, Left, Right }

    private struct EdgeData
    {
        public Cat Category;
        public PointF Start;
        public PointF End;
        public int EmitStart;
        public int EmitEnd;

        public EdgeData(float startX, float endX, float y0, float y1)
        {
            Start = new PointF(startX, y0);
            End = new PointF(endX, y1);
            Category = y0 == y1
                ? (startX < endX ? Cat.Right : Cat.Left)
                : (y0 < y1 ? Cat.Down : Cat.Up);
            EmitStart = 0;
            EmitEnd = 0;
        }

        public readonly void EmitScanEdge(List<Edge> output)
        {
            if (Category is Cat.Left or Cat.Right)
            {
                return;
            }

            // Non-horizontal edges are stored top-down (Y0 < Y1), swapping emits with endpoints.
            if (Category == Cat.Up)
            {
                output.Add(new Edge(End, Start, EmitEnd, EmitStart));
            }
            else
            {
                output.Add(new Edge(Start, End, EmitStart, EmitEnd));
            }
        }
    }

    /// <summary>The emit-count table from ScanEdgeCollection.ApplyVertexCategory, verbatim.</summary>
    private static void ApplyVertexCategory(Cat from, Cat to, ref EdgeData fromEdge, ref EdgeData toEdge)
    {
        switch ((from, to))
        {
            case (Cat.Up, Cat.Up): toEdge.EmitStart = 1; break;
            case (Cat.Up, Cat.Down): toEdge.EmitStart = 1; fromEdge.EmitEnd = 1; break;
            case (Cat.Up, Cat.Left): fromEdge.EmitEnd = 2; break;
            case (Cat.Up, Cat.Right): fromEdge.EmitEnd = 1; break;
            case (Cat.Down, Cat.Up): toEdge.EmitStart = 1; fromEdge.EmitEnd = 1; break;
            case (Cat.Down, Cat.Down): toEdge.EmitStart = 1; break;
            case (Cat.Down, Cat.Left): fromEdge.EmitEnd = 1; break;
            case (Cat.Down, Cat.Right): fromEdge.EmitEnd = 2; break;
            case (Cat.Left, Cat.Up): toEdge.EmitStart = 1; break;
            case (Cat.Left, Cat.Down): toEdge.EmitStart = 2; break;
            case (Cat.Right, Cat.Up): toEdge.EmitStart = 2; break;
            case (Cat.Right, Cat.Down): toEdge.EmitStart = 1; break;
            default: break; // horizontal-horizontal pairs: collinear, no emit
        }
    }

    private static List<Edge> BuildEdges(IReadOnlyList<PointF[]> contours)
    {
        var output = new List<Edge>();

        foreach (var contour in contours)
        {
            int n = contour.Length;

            // Drop a repeated closing vertex; we re-close explicitly below.
            if (n > 1 && contour[0] == contour[n - 1])
            {
                n--;
            }

            if (n < 3)
            {
                continue;
            }

            // Vertex Y snapped to the subpixel grid, reproducing the original's x64 behaviour
            // EXACTLY - which is a hybrid: the AVX loop rounds ties with ceil(y*8 - 0.5)/8 for as
            // many leading vertices as fill whole 16-float chunks, and the scalar remainder loop
            // rounds the rest with MidpointRounding.AwayFromZero. The two differ only at exact
            // ties, but the committed baselines were rendered by that hybrid, so we keep it -
            // computed scalar, so arm64 gets the identical answer.
            int total = n + 1;                       // ring stores the first vertex repeated
            int vectorised = total - (total % Subsampling);
            var rounded = new float[total];

            for (int i = 0; i < n; i++)
            {
                rounded[i] = i < vectorised
                    ? MathF.Ceiling((contour[i].Y * Subsampling) - 0.5f) * SubpixelDistance
                    : MathF.Round(contour[i].Y * Subsampling, MidpointRounding.AwayFromZero) / Subsampling;
            }

            rounded[n] = n < vectorised
                ? MathF.Ceiling((contour[0].Y * Subsampling) - 0.5f) * SubpixelDistance
                : MathF.Round(contour[0].Y * Subsampling, MidpointRounding.AwayFromZero) / Subsampling;

            EdgeData EdgeAt(int i)
            {
                int j = i + 1 == n ? 0 : i + 1;

                return new EdgeData(contour[i].X, contour[j].X, rounded[i], rounded[i + 1 <= n ? i + 1 : 0]);
            }

            // RingWalker, verbatim: three-edge window, each Move classifies the two vertices
            // around the current edge and emits the previous one.
            var prev = EdgeAt(n - 1);
            var cur = EdgeAt(0);
            var next = EdgeAt(1);

            void Move(bool emitPrevious, ref EdgeData p, ref EdgeData c, ref EdgeData nx)
            {
                ApplyVertexCategory(p.Category, c.Category, ref p, ref c);
                ApplyVertexCategory(c.Category, nx.Category, ref c, ref nx);

                if (emitPrevious)
                {
                    p.EmitScanEdge(output);
                }

                p = c;
                c = nx;
            }

            Move(false, ref prev, ref cur, ref next);

            for (int i = 1; i < n - 1; i++)
            {
                next = EdgeAt(i + 1 == n ? 0 : i + 1);
                Move(true, ref prev, ref cur, ref next);
            }

            next = EdgeAt(0);
            Move(true, ref prev, ref cur, ref next);
            next = EdgeAt(1);
            Move(true, ref prev, ref cur, ref next);
        }

        return output;
    }

    // ------------------------------------------------------------------ active list

    private static void EnterEdges(List<Edge> edges, int[] sorted0, ref int idx0, float subPixelY, ref ActiveList active)
    {
        while (idx0 < sorted0.Length && edges[sorted0[idx0]].Y0 <= subPixelY)
        {
            active.EnterEdge(sorted0[idx0]);
            idx0++;
        }
    }

    private static void LeaveEdges(List<Edge> edges, int[] sorted1, ref int idx1, float subPixelY, ref ActiveList active)
    {
        while (idx1 < sorted1.Length && edges[sorted1[idx1]].Y1 <= subPixelY)
        {
            active.LeaveEdge(sorted1[idx1]);
            idx1++;
        }
    }

    /// <summary>ActiveEdgeList, verbatim: flag bits, in-place compaction and per-state emits.</summary>
    private struct ActiveList(int capacity)
    {
        private const int EnteringFlag = 1 << 30;
        private const int LeavingFlag = 1 << 31;
        private const int StripMask = ~(EnteringFlag | LeavingFlag);

        private readonly int[] _buffer = new int[capacity];
        private int _count = 0;

        public void EnterEdge(int edgeIdx) => _buffer[_count++] = edgeIdx | EnteringFlag;

        public readonly void LeaveEdge(int edgeIdx)
        {
            for (int i = 0; i < _count; i++)
            {
                if (_buffer[i] == edgeIdx)
                {
                    _buffer[i] |= LeavingFlag;

                    return;
                }
            }
        }

        public void RemoveLeavingEdges()
        {
            int offset = 0;

            for (int i = 0; i < _count; i++)
            {
                int flagged = _buffer[i];

                if ((flagged & LeavingFlag) == LeavingFlag)
                {
                    offset++;
                }
                else
                {
                    _buffer[i - offset] = flagged & StripMask;
                }
            }

            _count -= offset;
        }

        public int ScanOddEven(float y, List<Edge> edges, float[] intersections)
        {
            int counter = 0;
            int offset = 0;

            for (int i = 0; i < _count; i++)
            {
                int flagged = _buffer[i];
                int edgeIdx = flagged & StripMask;
                var edge = edges[edgeIdx];
                float x = edge.GetX(y);

                if ((flagged & EnteringFlag) == EnteringFlag)
                {
                    Emit(x, edge.EmitV0, intersections, ref counter);
                }
                else if ((flagged & LeavingFlag) == LeavingFlag)
                {
                    Emit(x, edge.EmitV1, intersections, ref counter);
                    offset++;

                    continue;
                }
                else
                {
                    intersections[counter++] = x;
                }

                _buffer[i - offset] = edgeIdx;
            }

            _count -= offset;

            Array.Sort(intersections, 0, counter);

            return counter;
        }

        private static void Emit(float x, int times, float[] span, ref int counter)
        {
            if (times > 1)
            {
                span[counter++] = x;
            }

            if (times > 0)
            {
                span[counter++] = x;
            }
        }
    }
}
