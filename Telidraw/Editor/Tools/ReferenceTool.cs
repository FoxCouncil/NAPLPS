// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSApp.Editor.Tools;

/// <summary>
/// Interact with the reference image overlay: click-drag anywhere inside the image to
/// move it, drag the bottom-right corner handle to resize. Commits no NAPLPS commands —
/// mutates the bound <see cref="ReferenceImage"/> in place so the overlay follows live.
/// </summary>
public class ReferenceTool : EditorToolBase
{
    public override string Name => "Reference";

    /// <summary>The reference image to manipulate. Set by the VM on tool activation.</summary>
    public ReferenceImage? Image { get; set; }

    private const float HandleRadius = 0.02f;

    private enum DragMode { None, Move, ResizeBR }
    private DragMode _mode;
    private float _moveOffsetX;
    private float _moveOffsetY;

    public override void OnPointerPressed(float normX, float normY, bool isRightButton)
    {
        _mode = DragMode.None;
        if (Image == null || isRightButton) { return; }

        float brX = Image.X + Image.Width;
        float brY = Image.Y + Image.Height;

        if (MathF.Abs(normX - brX) <= HandleRadius && MathF.Abs(normY - brY) <= HandleRadius)
        {
            _mode = DragMode.ResizeBR;
            IsDragging = true;
            return;
        }

        if (normX >= Image.X && normX <= Image.X + Image.Width
            && normY >= Image.Y && normY <= Image.Y + Image.Height)
        {
            _mode = DragMode.Move;
            _moveOffsetX = normX - Image.X;
            _moveOffsetY = normY - Image.Y;
            IsDragging = true;
        }
    }

    public override void OnPointerMoved(float normX, float normY)
    {
        if (Image == null) { return; }

        switch (_mode)
        {
            case DragMode.Move:
                Image.X = normX - _moveOffsetX;
                Image.Y = normY - _moveOffsetY;
                break;

            case DragMode.ResizeBR:
                // Clamp to a visible minimum so the overlay can't collapse off-screen.
                Image.Width  = MathF.Max(0.02f, normX - Image.X);
                Image.Height = MathF.Max(0.02f, normY - Image.Y);
                break;
        }
    }

    public override List<(byte opcode, NaplpsOperands operands)> OnPointerReleased(float normX, float normY)
    {
        _mode = DragMode.None;
        IsDragging = false;
        return [];
    }

    public override ToolPreview? GetPreview()
    {
        if (Image == null) { return null; }

        // Dashed bbox around the image plus a corner handle so the user can see the
        // draggable target even when the image is transparent.
        var preview = new ToolPreview
        {
            Shape = PreviewShape.Rectangle,
            X1 = Image.X,
            Y1 = Image.Y,
            X2 = Image.X + Image.Width,
            Y2 = Image.Y + Image.Height,
            IsSelection = true,
        };
        preview.Handles.Add((Image.X + Image.Width, Image.Y + Image.Height));
        return preview;
    }
}
