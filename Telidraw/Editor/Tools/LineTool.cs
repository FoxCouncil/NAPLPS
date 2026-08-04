// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSApp.Editor.Tools;

/// <summary>
/// Click start point, drag to end point, release to commit.
/// Creates PointSetAbsolute (move pen) + LineAbsolute (draw line).
/// </summary>
public class LineTool : EditorToolBase
{
    public override string Name => "Line";

    public override void OnPointerPressed(float normX, float normY, bool isRightButton)
    {
        StartX = normX;
        StartY = normY;
        CurrentX = normX;
        CurrentY = normY;
        IsDragging = true;
    }

    public override void OnPointerMoved(float normX, float normY)
    {
        if (IsDragging)
        {
            CurrentX = normX;
            CurrentY = normY;
        }
    }

    public override ToolPreview? GetPreview()
    {
        if (!IsDragging)
        {
            return null;
        }

        return new ToolPreview
        {
            Shape = PreviewShape.Line,
            X1 = StartX,
            Y1 = StartY,
            X2 = CurrentX,
            Y2 = CurrentY
        };
    }

    public override List<(byte opcode, NaplpsOperands operands)> OnPointerReleased(float normX, float normY)
    {
        IsDragging = false;

        return
        [
            NaplpsCommandBuilder.BuildPointSetAbsolute(StartX, StartY),
            NaplpsCommandBuilder.BuildLineAbsolute(normX, normY)
        ];
    }
}
