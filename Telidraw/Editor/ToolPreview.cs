// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSApp.Editor;

public enum PreviewShape
{
    None,
    Line,
    Rectangle,
    Polygon
}

/// <summary>
/// Data class for rubber-band preview of the current tool operation.
/// Coordinates are in NAPLPS normalized space.
/// </summary>
public class ToolPreview
{
    public PreviewShape Shape { get; set; } = PreviewShape.None;

    public float X1 { get; set; }
    public float Y1 { get; set; }
    public float X2 { get; set; }
    public float Y2 { get; set; }

    /// <summary>For polygon previews — list of (x,y) vertices.</summary>
    public List<(float X, float Y)> Points { get; set; } = [];

    public bool IsFilled { get; set; }

    /// <summary>Whether this is a selection highlight rather than a tool preview.</summary>
    public bool IsSelection { get; set; }

    /// <summary>Vertex handles painted as small squares on top of the selection outline.
    /// Used by SelectTool to show draggable anchors on the selected command's vertices.</summary>
    public List<(float X, float Y)> Handles { get; set; } = [];

    /// <summary>Optional faint full-circle ghost the arc lies on (normalized centre + normalized
    /// radius). Null centre = no ghost. Drawn under the dashed preview + handles so the user can
    /// see which circle a click-on-start / Shift-snap will produce.</summary>
    public (float X, float Y)? GhostCenter { get; set; }
    public float GhostRadius { get; set; }
}
