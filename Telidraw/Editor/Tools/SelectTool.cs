// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSApp.Editor.Tools;

/// <summary>
/// Selection tool. Click hit-tests; Shift/Ctrl modifiers add to the selection.
/// Click-drag rubber-bands a rectangle and selects every command intersecting it.
/// Does not commit any commands on release.
/// </summary>
public class SelectTool : EditorToolBase
{
    public override string Name => "Select";

    /// <summary>The NaplpsFormat to hit-test against. Set by the ViewModel when this tool activates.</summary>
    public NaplpsFormat? Format { get; set; }

    /// <summary>Index of the primary selected command, or -1 for no selection. Backwards-compat with the single-select path.</summary>
    public int SelectedIndex
    {
        get => SelectedIndices.Count > 0 ? SelectedIndices[^1] : -1;
        set
        {
            SelectedIndices.Clear();

            if (value >= 0)
            {
                SelectedIndices.Add(value);
            }
        }
    }

    /// <summary>All currently selected command indices in selection order. Last entry is the primary selection.</summary>
    public List<int> SelectedIndices { get; } = [];

    /// <summary>Whether to add to the existing selection on the next pointer press (Shift / Ctrl).</summary>
    public bool AdditiveModifier { get; set; }

    /// <summary>True if a rubber-band rectangle drag is in progress.</summary>
    private bool _isRubberBanding;

    /// <summary>Index of the handle currently being dragged (0 = first vertex). -1 when not
    /// in a handle-drag gesture. When set, pointer-move updates the preview; pointer-up
    /// commits a ReplaceCommandAction via <see cref="PendingEditAction"/>.</summary>
    private int _handleDragIndex = -1;

    /// <summary>Hit-test radius for vertex handles, in NAPLPS-normalized coords. 0.015 ≈ 10 screen
    /// pixels on a default 640×480 canvas — large enough to grab without being fussy.</summary>
    private const float HandleHitRadius = 0.015f;

    private IEditorAction? _pendingEditAction;
    public override IEditorAction? PendingEditAction => _pendingEditAction;

    /// <summary>Decode the current primary-selection command's editable vertex handles.
    /// Returns an empty list for non-editable or multi-vertex commands (those defer to the
    /// Properties panel's numeric editors and Shift+Arrow nudge).</summary>
    public List<(float X, float Y)> GetSelectedHandles()
    {
        var result = new List<(float X, float Y)>();
        if (Format == null || SelectedIndex < 0 || SelectedIndex >= Format.Commands.Count) { return result; }
        var seq = Format.Commands[SelectedIndex];
        int mv = Math.Max(1, (int)seq.State.MultiByteValue);
        if (seq.Command.Operands.Count < mv) { return result; }
        if (seq.Command is NAPLPS.Commands.PointSetAbsoluteCommand
            or NAPLPS.Commands.PointAbsoluteCommand
            or NAPLPS.Commands.LineAbsoluteCommand)
        {
            var (x, y) = NAPLPS.NaplpsEncoder.DecodeVertex2D(new NAPLPS.NaplpsOperands(seq.Command.Operands.GetRange(0, mv)), multiByteValue: mv);
            result.Add((x, y));
        }
        return result;
    }

    private int HitTestHandle(float normX, float normY)
    {
        var handles = GetSelectedHandles();
        for (int i = 0; i < handles.Count; i++)
        {
            if (MathF.Abs(handles[i].X - normX) <= HandleHitRadius && MathF.Abs(handles[i].Y - normY) <= HandleHitRadius)
            {
                return i;
            }
        }
        return -1;
    }

    public override void OnPointerPressed(float normX, float normY, bool isRightButton)
    {
        if (Format == null)
        {
            SelectedIndices.Clear();
            return;
        }

        StartX = normX;
        StartY = normY;
        CurrentX = normX;
        CurrentY = normY;
        _isRubberBanding = false;
        _pendingEditAction = null;

        // Check vertex-handle hits FIRST so the user can grab a handle that lies over
        // another command without deselecting. Only fires when we already have a selection.
        if (SelectedIndex >= 0)
        {
            int handle = HitTestHandle(normX, normY);
            if (handle >= 0)
            {
                _handleDragIndex = handle;
                IsDragging = true;
                return;
            }
        }
        _handleDragIndex = -1;

        var hit = CommandHitTester.HitTest(Format, normX, normY);

        if (hit < 0)
        {
            // Empty space: start a rubber-band drag.
            _isRubberBanding = true;
            IsDragging = true;

            if (!AdditiveModifier)
            {
                SelectedIndices.Clear();
            }

            return;
        }

        if (AdditiveModifier)
        {
            // Toggle: remove if already selected, otherwise add.
            if (!SelectedIndices.Remove(hit))
            {
                SelectedIndices.Add(hit);
            }
        }
        else
        {
            SelectedIndices.Clear();
            SelectedIndices.Add(hit);
        }
    }

    public override void OnPointerMoved(float normX, float normY)
    {
        CurrentX = normX;
        CurrentY = normY;
    }

    public override List<(byte opcode, NaplpsOperands operands)> OnPointerReleased(float normX, float normY)
    {
        // Handle-drag commit: build a ReplaceCommandAction for the new vertex position.
        // The ViewModel's OnEditorPointerReleased reads PendingEditAction after this return
        // and executes it via the undo manager. Return empty so the add-commands path is a no-op.
        if (_handleDragIndex >= 0 && Format != null && SelectedIndex >= 0 && SelectedIndex < Format.Commands.Count)
        {
            var seq = Format.Commands[SelectedIndex];
            int mv = Math.Max(1, (int)seq.State.MultiByteValue);
            var newOps = NAPLPS.NaplpsEncoder.EncodeVertex2D(normX, normY, mv);
            _pendingEditAction = new ReplaceCommandAction(Format, SelectedIndex, seq.Command.OpCode, newOps);
            _handleDragIndex = -1;
            IsDragging = false;
            return [];
        }

        if (_isRubberBanding && Format != null)
        {
            // Only consider it a real rubber-band if the drag distance crosses a small threshold;
            // otherwise treat it as a click-on-empty (which already cleared selection).
            float dx = normX - StartX;
            float dy = normY - StartY;

            if (MathF.Abs(dx) > 0.005f || MathF.Abs(dy) > 0.005f)
            {
                var indices = CommandHitTester.HitTestRect(Format, StartX, StartY, normX, normY);

                foreach (var i in indices)
                {
                    if (!SelectedIndices.Contains(i))
                    {
                        SelectedIndices.Add(i);
                    }
                }
            }
        }

        _isRubberBanding = false;
        IsDragging = false;
        return [];
    }

    /// <summary>Select every command in the format. Bound to Ctrl+A.</summary>
    public void SelectAll()
    {
        if (Format == null)
        {
            return;
        }

        SelectedIndices.Clear();

        for (int i = 0; i < Format.Commands.Count; i++)
        {
            SelectedIndices.Add(i);
        }
    }

    public override ToolPreview? GetPreview()
    {
        // Live rubber-band rectangle while dragging.
        if (_isRubberBanding && IsDragging)
        {
            return new ToolPreview
            {
                Shape = PreviewShape.Rectangle,
                X1 = StartX,
                Y1 = StartY,
                X2 = CurrentX,
                Y2 = CurrentY,
                IsSelection = true,
            };
        }

        if (SelectedIndices.Count == 0 || Format == null)
        {
            return null;
        }

        // Show bbox of the primary (last) selection. The canvas can iterate SelectedIndices
        // separately to outline every selected command.
        var primary = SelectedIndices[^1];
        var bbox = CommandHitTester.GetBoundingBox(Format, primary);

        if (bbox == null)
        {
            return null;
        }

        var (x, y, w, h) = bbox.Value;

        // Expose draggable vertex handles on the primary selection. During an active
        // handle drag we override the handle's position with the current pointer so
        // the user sees live feedback.
        var handles = GetSelectedHandles();
        if (_handleDragIndex >= 0 && _handleDragIndex < handles.Count)
        {
            handles[_handleDragIndex] = (CurrentX, CurrentY);
        }

        return new ToolPreview
        {
            Shape = PreviewShape.Rectangle,
            X1 = x,
            Y1 = y,
            X2 = x + w,
            Y2 = y + h,
            IsSelection = true,
            Handles = handles,
        };
    }
}
