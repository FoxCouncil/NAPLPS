// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSApp.Editor.Tools;

/// <summary>
/// Click to place pen position. Creates PointSetAbsoluteCommand.
/// Simplest tool — validates the entire editor pipeline end-to-end.
/// </summary>
public class MovePenTool : EditorToolBase
{
    public override string Name => "Move Pen";

    public override void OnPointerPressed(float normX, float normY, bool isRightButton)
    {
        StartX = normX;
        StartY = normY;
    }

    public override void OnPointerMoved(float normX, float normY) { }

    public override List<(byte opcode, NaplpsOperands operands)> OnPointerReleased(float normX, float normY)
    {
        return [NaplpsCommandBuilder.BuildPointSetAbsolute(normX, normY)];
    }
}
