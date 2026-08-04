// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSApp.Editor.Tools;

/// <summary>
/// Filled-scribble variant of <see cref="IncrementalLineTool"/> — same motion-code
/// construction but emits IncrementalPolygonFilled so the traced area is filled on render.
/// Useful for free-form filled shapes (blobs, organic fills).
/// </summary>
public class IncrementalPolygonTool : EditorToolBase
{
    public override string Name => "IncrementalPolygon";

    private readonly List<(float X, float Y)> _samples = [];

    public override string? ToolHint => IsDragging
        ? $"Scribble Filled: drag a path; filled on release. {_samples.Count} sample(s)."
        : "Scribble Filled: press and drag to trace a filled blob (IncrementalPolygonFilled).";

    public override void OnPointerPressed(float normX, float normY, bool isRightButton)
    {
        _samples.Clear();
        _samples.Add((normX, normY));
        StartX = normX;
        StartY = normY;
        CurrentX = normX;
        CurrentY = normY;
        IsDragging = true;
    }

    public override void OnPointerMoved(float normX, float normY)
    {
        if (!IsDragging) { return; }

        var last = _samples[^1];
        float dx = normX - last.X;
        float dy = normY - last.Y;
        if (dx * dx + dy * dy < 0.0001f) { return; }

        _samples.Add((normX, normY));
        CurrentX = normX;
        CurrentY = normY;
    }

    public override List<(byte opcode, NaplpsOperands operands)> OnPointerReleased(float normX, float normY)
    {
        IsDragging = false;
        if (_samples.Count < 3) { return []; }

        var result = new List<(byte, NaplpsOperands)>
        {
            NaplpsCommandBuilder.BuildPointSetAbsolute(_samples[0].X, _samples[0].Y),
        };

        var codes = IncrementalLineTool.BuildMotionCodesFromSamples(_samples, out float stepDx, out float stepDy);
        result.Add(NaplpsCommandBuilder.BuildIncrementalPolygonFilled(stepDx, stepDy, codes));

        return result;
    }

    public override ToolPreview? GetPreview()
    {
        if (!IsDragging || _samples.Count < 2) { return null; }

        var preview = new ToolPreview { Shape = PreviewShape.Polygon, IsFilled = true };
        foreach (var s in _samples)
        {
            preview.Points.Add((s.X, s.Y));
        }
        return preview;
    }

    public override void Reset()
    {
        _samples.Clear();
        base.Reset();
    }
}
