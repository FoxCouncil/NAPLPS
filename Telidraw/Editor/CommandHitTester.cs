// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSApp.Editor;

/// <summary>
/// Hit-tests NAPLPS commands against a normalized coordinate point.
/// Returns the index of the command under the cursor, or -1 if nothing hit.
/// Iterates in reverse so topmost (last drawn) commands are found first.
/// </summary>
public static class CommandHitTester
{
    private const float DefaultTolerance = 0.015f;

    /// <summary>
    /// Returns the command index at the given normalized position, or -1.
    /// </summary>
    public static int HitTest(NaplpsFormat format, float normX, float normY, float tolerance = DefaultTolerance)
    {
        for (int i = format.Commands.Count - 1; i >= 0; i--)
        {
            var (command, state) = format.Commands[i];

            if (command is GeometricDrawingCommandBase geo && geo.Points.Count > 0)
            {
                // Check line/polyline segments
                if (command is LineCommand or LineAbsoluteCommand or LineRelativeCommand
                    or LineSetAbsoluteCommand or LineSetRelativeCommand)
                {
                    for (int j = 0; j < geo.Points.Count - 1; j++)
                    {
                        var p1 = geo.Points[j];
                        var p2 = geo.Points[j + 1];

                        if (PointToSegmentDistance(normX, normY, p1.X, p1.Y, p2.X, p2.Y) < tolerance)
                        {
                            return i;
                        }
                    }

                    // Single-point line: check distance to point
                    if (geo.Points.Count == 1)
                    {
                        var p = geo.Points[0];

                        if (Distance(normX, normY, p.X, p.Y) < tolerance)
                        {
                            return i;
                        }
                    }
                }
                // Rectangle commands — check point-in-rect
                else if (command is RectangleFilledCommand or RectangleOutlinedCommand
                    or RectangleSetFilledCommand or RectangleSetOutlinedCommand)
                {
                    if (geo.Vertices.Count > 0)
                    {
                        var origin = geo.Points[0];
                        var dims = geo.Vertices[0];
                        float x1 = origin.X;
                        float y1 = origin.Y;
                        float x2 = origin.X + dims.X;
                        float y2 = origin.Y + dims.Y;

                        float minX = MathF.Min(x1, x2) - tolerance;
                        float maxX = MathF.Max(x1, x2) + tolerance;
                        float minY = MathF.Min(y1, y2) - tolerance;
                        float maxY = MathF.Max(y1, y2) + tolerance;

                        if (normX >= minX && normX <= maxX && normY >= minY && normY <= maxY)
                        {
                            return i;
                        }
                    }
                }
                // Arc, polygon, etc — bounding box check
                else
                {
                    if (IsNearAnyPoint(geo.Points, normX, normY, tolerance))
                    {
                        return i;
                    }
                }
            }
            // Point commands
            else if (command is PointCommand pointCmd)
            {
                var pen = state.Pen;

                if (Distance(normX, normY, pen.X, pen.Y) < tolerance)
                {
                    return i;
                }
            }
            // Ascii char — check character cell
            else if (command is AsciiCharCommand)
            {
                var pen = state.Pen;
                float cellW = state.CharSize.X;
                float cellH = state.CharSize.Y;

                if (normX >= pen.X - tolerance && normX <= pen.X + cellW + tolerance && normY >= pen.Y - tolerance && normY <= pen.Y + cellH + tolerance)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// Hit-test every command and return all matching indices. Topmost (highest index) first
    /// when sorted descending. Empty list if nothing hit. Used by Ctrl+click additive selection.
    /// </summary>
    public static List<int> HitTestAll(NaplpsFormat format, float normX, float normY, float tolerance = DefaultTolerance)
    {
        var hits = new List<int>();

        for (int i = format.Commands.Count - 1; i >= 0; i--)
        {
            if (HitsCommand(format, i, normX, normY, tolerance))
            {
                hits.Add(i);
            }
        }

        return hits;
    }

    /// <summary>
    /// Rubber-band rectangle select: returns indices of every command whose bounding box
    /// intersects the given rectangle (in normalized coords).
    /// </summary>
    public static List<int> HitTestRect(NaplpsFormat format, float x1, float y1, float x2, float y2)
    {
        float rectMinX = MathF.Min(x1, x2);
        float rectMaxX = MathF.Max(x1, x2);
        float rectMinY = MathF.Min(y1, y2);
        float rectMaxY = MathF.Max(y1, y2);

        var hits = new List<int>();

        for (int i = 0; i < format.Commands.Count; i++)
        {
            var bbox = GetBoundingBox(format, i);

            if (!bbox.HasValue)
            {
                continue;
            }

            var (bx, by, bw, bh) = bbox.Value;
            float bMaxX = bx + bw;
            float bMaxY = by + bh;

            // AABB intersection.
            if (bx <= rectMaxX && bMaxX >= rectMinX && by <= rectMaxY && bMaxY >= rectMinY)
            {
                hits.Add(i);
            }
        }

        return hits;
    }

    /// <summary>True if the command at <paramref name="index"/> hits the given point within tolerance.</summary>
    private static bool HitsCommand(NaplpsFormat format, int index, float normX, float normY, float tolerance)
    {
        var (command, state) = format.Commands[index];

        if (command is GeometricDrawingCommandBase geo && geo.Points.Count > 0)
        {
            if (command is LineCommand or LineAbsoluteCommand or LineRelativeCommand
                or LineSetAbsoluteCommand or LineSetRelativeCommand)
            {
                for (int j = 0; j < geo.Points.Count - 1; j++)
                {
                    var p1 = geo.Points[j];
                    var p2 = geo.Points[j + 1];

                    if (PointToSegmentDistance(normX, normY, p1.X, p1.Y, p2.X, p2.Y) < tolerance)
                    {
                        return true;
                    }
                }

                if (geo.Points.Count == 1)
                {
                    var p = geo.Points[0];

                    if (Distance(normX, normY, p.X, p.Y) < tolerance)
                    {
                        return true;
                    }
                }
            }
            else if (command is RectangleFilledCommand or RectangleOutlinedCommand
                or RectangleSetFilledCommand or RectangleSetOutlinedCommand)
            {
                if (geo.Vertices.Count > 0)
                {
                    var origin = geo.Points[0];
                    var dims = geo.Vertices[0];

                    float minX = MathF.Min(origin.X, origin.X + dims.X) - tolerance;
                    float maxX = MathF.Max(origin.X, origin.X + dims.X) + tolerance;
                    float minY = MathF.Min(origin.Y, origin.Y + dims.Y) - tolerance;
                    float maxY = MathF.Max(origin.Y, origin.Y + dims.Y) + tolerance;

                    if (normX >= minX && normX <= maxX && normY >= minY && normY <= maxY)
                    {
                        return true;
                    }
                }
            }
            else if (IsNearAnyPoint(geo.Points, normX, normY, tolerance))
            {
                return true;
            }
        }
        else if (command is PointCommand)
        {
            var pen = state.Pen;

            if (Distance(normX, normY, pen.X, pen.Y) < tolerance)
            {
                return true;
            }
        }
        else if (command is AsciiCharCommand)
        {
            var pen = state.Pen;
            float cellW = state.CharSize.X;
            float cellH = state.CharSize.Y;

            if (normX >= pen.X - tolerance && normX <= pen.X + cellW + tolerance && normY >= pen.Y - tolerance && normY <= pen.Y + cellH + tolerance)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the bounding box (in NAPLPS coords) of the command at the given index,
    /// or null if not calculable.
    /// </summary>
    public static (float X, float Y, float W, float H)? GetBoundingBox(NaplpsFormat format, int index)
    {
        if (index < 0 || index >= format.Commands.Count)
        {
            return null;
        }

        var (command, state) = format.Commands[index];

        if (command is GeometricDrawingCommandBase geo && geo.Points.Count > 0)
        {
            if (command is RectangleFilledCommand or RectangleOutlinedCommand
                or RectangleSetFilledCommand or RectangleSetOutlinedCommand)
            {
                if (geo.Vertices.Count > 0)
                {
                    var origin = geo.Points[0];
                    var dims = geo.Vertices[0];
                    float x = MathF.Min(origin.X, origin.X + dims.X);
                    float y = MathF.Min(origin.Y, origin.Y + dims.Y);
                    return (x, y, MathF.Abs(dims.X), MathF.Abs(dims.Y));
                }
            }

            // Generic bounding box from all points
            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;

            foreach (var p in geo.Points)
            {
                minX = MathF.Min(minX, p.X);
                minY = MathF.Min(minY, p.Y);
                maxX = MathF.Max(maxX, p.X);
                maxY = MathF.Max(maxY, p.Y);
            }

            return (minX, minY, maxX - minX, maxY - minY);
        }

        if (command is AsciiCharCommand)
        {
            return (state.Pen.X, state.Pen.Y, state.CharSize.X, state.CharSize.Y);
        }

        return null;
    }

    private static float Distance(float x1, float y1, float x2, float y2)
    {
        float dx = x2 - x1;
        float dy = y2 - y1;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static float PointToSegmentDistance(float px, float py, float ax, float ay, float bx, float by)
    {
        float dx = bx - ax;
        float dy = by - ay;
        float lenSq = dx * dx + dy * dy;

        if (lenSq < 1e-10f)
        {
            return Distance(px, py, ax, ay);
        }

        float t = ((px - ax) * dx + (py - ay) * dy) / lenSq;
        t = Math.Clamp(t, 0f, 1f);

        float closestX = ax + t * dx;
        float closestY = ay + t * dy;
        return Distance(px, py, closestX, closestY);
    }

    private static bool IsNearAnyPoint(List<System.Numerics.Vector3> points, float normX, float normY, float tolerance)
    {
        foreach (var p in points)
        {
            if (Distance(normX, normY, p.X, p.Y) < tolerance)
            {
                return true;
            }
        }

        return false;
    }
}
