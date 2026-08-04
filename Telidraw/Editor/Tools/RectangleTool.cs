// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSApp.Editor.Tools;

/// <summary>
/// Click-drag for corner + dimensions.
/// Creates PointSetAbsolute + RectangleFilled or RectangleOutlined.
/// Toggle filled/outlined via the IsFilled property.
/// </summary>
public class RectangleTool : EditorToolBase
{
    public override string Name => "Rectangle";

    /// <summary>Whether to draw filled or outlined rectangles.</summary>
    public bool IsFilled { get; set; } = true;

    public override bool EmitsFilledGeometry => IsFilled;

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
            Shape = PreviewShape.Rectangle,
            X1 = StartX,
            Y1 = StartY,
            X2 = CurrentX,
            Y2 = CurrentY,
            IsFilled = IsFilled
        };
    }

    public override List<(byte opcode, NaplpsOperands operands)> OnPointerReleased(float normX, float normY)
    {
        IsDragging = false;

        // Rectangle dimensions are relative to the pen position
        float width = normX - StartX;
        float height = normY - StartY;

        var commands = new List<(byte opcode, NaplpsOperands operands)>
        {
            NaplpsCommandBuilder.BuildPointSetAbsolute(StartX, StartY)
        };

        if (IsFilled)
        {
            commands.Add(NaplpsCommandBuilder.BuildRectangleFilled(width, height));
        }
        else
        {
            commands.Add(NaplpsCommandBuilder.BuildRectangleOutlined(width, height));
        }

        return commands;
    }
}
