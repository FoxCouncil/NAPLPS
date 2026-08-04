// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSApp.Editor;

/// <summary>
/// Abstract base class for visual editor tools.
/// Each tool handles pointer events and commits NAPLPS commands.
/// </summary>
public abstract class EditorToolBase
{
    public abstract string Name { get; }

    /// <summary>Called when the pointer is pressed on the canvas.</summary>
    /// <param name="normX">NAPLPS normalized X [0,1]</param>
    /// <param name="normY">NAPLPS normalized Y [0,0.75]</param>
    /// <param name="isRightButton">True if right mouse button was pressed</param>
    public abstract void OnPointerPressed(float normX, float normY, bool isRightButton);

    /// <summary>Called when the pointer moves on the canvas.</summary>
    public abstract void OnPointerMoved(float normX, float normY);

    /// <summary>Called when the pointer is released on the canvas.</summary>
    /// <returns>List of (opcode, operands) tuples to commit as commands, or empty if nothing to commit.</returns>
    public abstract List<(byte opcode, NaplpsOperands operands)> OnPointerReleased(float normX, float normY);

    /// <summary>Returns a preview shape for rubber-band display, or null if nothing to preview.</summary>
    public virtual ToolPreview? GetPreview() => null;

    /// <summary>Optional one-line status-bar hint telling the user what the next click/drag does
    /// (buffered tools can include a live count). Null = no hint. Surfaced via
    /// <see cref="ViewModels.MainWindowViewModel.ToolHint"/>.</summary>
    public virtual string? ToolHint => null;

    /// <summary>
    /// Cancel any in-progress operation (e.g. mid-polygon click sequence) and reset state.
    /// Default implementation just clears the drag flag; tools with per-stroke buffers
    /// (PolygonTool, IncrementalLineTool) should override to drop accumulated points.
    /// </summary>
    public virtual void Reset()
    {
        IsDragging = false;
    }

    /// <summary>Whether the tool is currently in a drag operation.</summary>
    public bool IsDragging { get; protected set; }

    /// <summary>Start position of the current drag in NAPLPS coords.</summary>
    public float StartX { get; protected set; }
    public float StartY { get; protected set; }

    /// <summary>Current position during drag.</summary>
    public float CurrentX { get; protected set; }
    public float CurrentY { get; protected set; }

    /// <summary>
    /// Optional replacement action produced by the tool after a pointer release — used
    /// by SelectTool's drag-to-edit vertex handles, which MUTATE an existing command
    /// instead of APPENDING a new one. The ViewModel's commit path inspects this after
    /// <see cref="OnPointerReleased"/> returns and, if non-null, executes it through the
    /// undo manager before falling through to the add-commands path. Tools that don't
    /// edit-in-place leave this as null (the default).
    /// </summary>
    public virtual IEditorAction? PendingEditAction => null;

    /// <summary>True when this tool's next commit will produce filled geometry. The commit
    /// path uses this to prepend a TEXTURE command that carries the UI's current fill-pattern
    /// selection, so patterns actually take effect on blank files (which have no prior
    /// TEXTURE in the stream). Shape tools override this based on their IsFilled toggle.</summary>
    public virtual bool EmitsFilledGeometry => false;
}
