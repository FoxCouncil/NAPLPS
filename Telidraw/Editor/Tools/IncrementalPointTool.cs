// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSApp.Editor.Tools;

/// <summary>
/// Raster paint tool — on press+drag, captures a grid of "painted" pixels and emits a
/// single IncrementalPoint command covering the bounding box. 1-bit-per-pixel resolution.
/// Click once (no drag) paints a single pixel at that location.
/// </summary>
public class IncrementalPointTool : EditorToolBase
{
    public override string Name => "IncrementalPoint";

    /// <summary>Pixel size in normalized coords. Default = 1/80 of unit screen.</summary>
    public float PixelSize { get; set; } = 1.0f / 80.0f;

    private readonly HashSet<(int col, int row)> _paintedCells = [];

    public override string? ToolHint => IsDragging
        ? $"Raster Paint: drag to paint 1-bit pels on a {PixelSize:0.###}-unit grid. {_paintedCells.Count} cell(s)."
        : "Raster Paint: press and drag to paint a pixel grid (IncrementalPoint raster brush).";

    public override void OnPointerPressed(float normX, float normY, bool isRightButton)
    {
        _paintedCells.Clear();
        StartX = normX;
        StartY = normY;
        CurrentX = normX;
        CurrentY = normY;
        IsDragging = true;
        PaintCell(normX, normY);
    }

    public override void OnPointerMoved(float normX, float normY)
    {
        if (!IsDragging) { return; }
        CurrentX = normX;
        CurrentY = normY;
        PaintCell(normX, normY);
    }

    private void PaintCell(float x, float y)
    {
        int col = (int)System.MathF.Floor(x / PixelSize);
        int row = (int)System.MathF.Floor(y / PixelSize);
        _paintedCells.Add((col, row));
    }

    public override List<(byte opcode, NaplpsOperands operands)> OnPointerReleased(float normX, float normY)
    {
        IsDragging = false;
        if (_paintedCells.Count == 0) { return []; }

        // Compute bounding box of painted cells. Emit IncrementalPoint covering the bbox,
        // with 1bpp pixel values (on=1, off=0) for each cell in row-major order.
        int minCol = int.MaxValue, maxCol = int.MinValue, minRow = int.MaxValue, maxRow = int.MinValue;
        foreach (var (c, r) in _paintedCells)
        {
            if (c < minCol) { minCol = c; }
            if (c > maxCol) { maxCol = c; }
            if (r < minRow) { minRow = r; }
            if (r > maxRow) { maxRow = r; }
        }

        int width = maxCol - minCol + 1;
        int height = maxRow - minRow + 1;
        var pixels = new int[width * height];
        foreach (var (c, r) in _paintedCells)
        {
            int idx = (r - minRow) * width + (c - minCol);
            pixels[idx] = 1;
        }

        return
        [
            // Move pen to bbox origin so the renderer's cursor starts at the raster anchor.
            NaplpsCommandBuilder.BuildPointSetAbsolute(minCol * PixelSize, minRow * PixelSize),
            NaplpsCommandBuilder.BuildIncrementalPoint(1, pixels),
        ];
    }

    public override void Reset()
    {
        _paintedCells.Clear();
        base.Reset();
    }
}
