// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using System.Numerics;

namespace NAPLPSApp.Editor.Tools;

/// <summary>
/// Multi-click polygon tool. Click adds vertices, double-click finalizes.
/// Creates PointSetAbsolute + PolygonFilled/Outlined commands.
/// Once the pen has 3+ vertices and the cursor enters <see cref="SnapRadius"/> of
/// the first vertex, the preview snaps closed and clicking commits the polygon.
/// Ctrl-click overrides the snap to add another real vertex on top of the origin.
/// </summary>
public class PolygonTool : EditorToolBase
{
    public override string Name => "Polygon";

    public bool IsFilled { get; set; } = true;

    public override bool EmitsFilledGeometry => IsFilled;

    private readonly List<(float X, float Y)> _vertices = [];

    /// <summary>Set by code-behind from PointerPressedEventArgs.ClickCount.</summary>
    public int ClickCount { get; set; } = 1;

    /// <summary>Set by the VM before a press when Ctrl is held; bypasses the close-on-snap
    /// behavior so the user can place a vertex on top of the origin.</summary>
    public bool ForceAddVertex { get; set; }

    /// <summary>Normalized-coord radius around the first vertex that snaps the free end
    /// closed. 0.02 ≈ 12 screen pixels on a 640-wide canvas — easy to hit without being
    /// twitchy about accidentally closing.</summary>
    private const float SnapRadius = 0.02f;

    /// <summary>True once the current press has landed inside the snap radius and the
    /// upcoming release should finalize instead of adding another vertex.</summary>
    private bool _shouldFinalize;

    private bool NearFirstVertex(float x, float y)
    {
        if (_vertices.Count < 3) { return false; }
        var first = _vertices[0];
        return MathF.Abs(first.X - x) <= SnapRadius && MathF.Abs(first.Y - y) <= SnapRadius;
    }

    public override void OnPointerPressed(float normX, float normY, bool isRightButton)
    {
        if (isRightButton)
        {
            // Right click cancels polygon
            _vertices.Clear();
            _shouldFinalize = false;
            IsDragging = false;
            return;
        }

        if (ClickCount >= 2 && _vertices.Count >= 2)
        {
            // Double-click finalizes
            _shouldFinalize = true;
            IsDragging = false;
            return;
        }

        // Close-on-snap: clicking inside the snap radius closes the polygon instead of
        // dropping another vertex. Ctrl forces a real add for the rare case the user
        // actually wants a vertex on top of the origin.
        if (!ForceAddVertex && NearFirstVertex(normX, normY))
        {
            _shouldFinalize = true;
            IsDragging = false;
            return;
        }

        _vertices.Add((normX, normY));
        CurrentX = normX;
        CurrentY = normY;
        IsDragging = true;
    }

    public override void OnPointerMoved(float normX, float normY)
    {
        // Visual snap: once closing is possible, lock the preview's free end onto the
        // origin when the pointer is close. Gives live feedback that "a click here closes."
        if (!ForceAddVertex && NearFirstVertex(normX, normY))
        {
            CurrentX = _vertices[0].X;
            CurrentY = _vertices[0].Y;
        }
        else
        {
            CurrentX = normX;
            CurrentY = normY;
        }
    }

    public override void Reset()
    {
        _vertices.Clear();
        _shouldFinalize = false;
        ClickCount = 1;
        base.Reset();
    }

    public override List<(byte opcode, NaplpsOperands operands)> OnPointerReleased(float normX, float normY)
    {
        if (_shouldFinalize)
        {
            _shouldFinalize = false;
            return Finalize();
        }

        if (ClickCount < 2 || _vertices.Count < 3)
        {
            // Not finalizing yet
            return [];
        }

        return Finalize();
    }

    private List<(byte opcode, NaplpsOperands operands)> Finalize()
    {
        if (_vertices.Count < 3)
        {
            _vertices.Clear();
            return [];
        }

        var commands = new List<(byte opcode, NaplpsOperands operands)>();

        // Move pen to first vertex
        commands.Add(NaplpsCommandBuilder.BuildPointSetAbsolute(_vertices[0].X, _vertices[0].Y));

        // Build polygon with relative vertices from first point
        var relativeVerts = new Vector3[_vertices.Count - 1];

        for (int i = 1; i < _vertices.Count; i++)
        {
            relativeVerts[i - 1] = new Vector3(
                _vertices[i].X - _vertices[i - 1].X,
                _vertices[i].Y - _vertices[i - 1].Y,
                0);
        }

        if (IsFilled)
        {
            commands.Add(NaplpsCommandBuilder.BuildPolygonFilled(relativeVerts));
        }
        else
        {
            commands.Add(NaplpsCommandBuilder.BuildPolygonOutlined(relativeVerts));
        }

        _vertices.Clear();
        IsDragging = false;
        return commands;
    }

    public override ToolPreview? GetPreview()
    {
        if (_vertices.Count == 0)
        {
            return null;
        }

        var preview = new ToolPreview
        {
            Shape = PreviewShape.Polygon,
            IsFilled = IsFilled
        };

        foreach (var v in _vertices)
        {
            preview.Points.Add(v);
        }

        // Add current mouse position as a live preview vertex
        preview.Points.Add((CurrentX, CurrentY));

        // Once closing is possible, draw the origin as a handle so the user can see the
        // snap target. If the cursor is currently snapped, CurrentX/Y already equals
        // vertex[0], so the preview polyline visibly closes the shape.
        if (_vertices.Count >= 3)
        {
            preview.Handles.Add(_vertices[0]);
        }

        return preview;
    }
}
