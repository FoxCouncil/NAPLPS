// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSApp.Editor.Tools;

/// <summary>
/// Approximation of a fill/flood tool. NAPLPS has no true flood operation, so this tool
/// hit-tests the click point and, if it lands on an outlined rectangle/polygon/arc, emits
/// a filled-variant duplicate of that shape immediately after the selected command. The
/// new filled copy uses the currently-active foreground color that tool output gets
/// decorated with in MainWindowViewModel.CommitToolCommands. If the click lands on a shape
/// that's already filled, on something non-fillable, or on empty space, the tool emits
/// nothing.
/// </summary>
public class FillTool : EditorToolBase
{
    public override string Name => "Fill";

    public override bool EmitsFilledGeometry => true;

    /// <summary>The NaplpsFormat to hit-test against. Set by the ViewModel on tool activation.</summary>
    public NaplpsFormat? Format { get; set; }

    public override void OnPointerPressed(float normX, float normY, bool isRightButton)
    {
        StartX = normX;
        StartY = normY;
        CurrentX = normX;
        CurrentY = normY;
    }

    public override void OnPointerMoved(float normX, float normY)
    {
        CurrentX = normX;
        CurrentY = normY;
    }

    public override List<(byte opcode, NaplpsOperands operands)> OnPointerReleased(float normX, float normY)
    {
        if (Format == null)
        {
            return [];
        }

        var hit = CommandHitTester.HitTest(Format, normX, normY);

        if (hit < 0)
        {
            return [];
        }

        var target = Format.Commands[hit];
        var src = target.Command;
        var penAtTarget = target.State.Pen;

        var commands = new List<(byte opcode, NaplpsOperands operands)>();

        // Non-Set outlined shapes need an explicit PointSetAbsolute to anchor the fill at
        // the pen position the original shape was drawn from (captured in target.State.Pen).
        // Set variants carry the absolute start inside their own operands, so no prefix.
        switch (src)
        {
            case RectangleOutlinedCommand:
            {
                commands.Add(NaplpsCommandBuilder.BuildPointSetAbsolute(penAtTarget.X, penAtTarget.Y));
                commands.Add((NaplpsCommandBuilder.OpRectangleFilled, new NaplpsOperands(src.Operands)));
                break;
            }

            case RectangleSetOutlinedCommand:
            {
                commands.Add((NaplpsCommandBuilder.OpRectangleSetFilled, new NaplpsOperands(src.Operands)));
                break;
            }

            case PolygonOutlinedCommand:
            {
                commands.Add(NaplpsCommandBuilder.BuildPointSetAbsolute(penAtTarget.X, penAtTarget.Y));
                commands.Add((NaplpsCommandBuilder.OpPolygonFilled, new NaplpsOperands(src.Operands)));
                break;
            }

            case PolygonSetOutlinedCommand:
            {
                commands.Add((NaplpsCommandBuilder.OpPolygonSetFilled, new NaplpsOperands(src.Operands)));
                break;
            }

            case ArcOutlinedCommand:
            {
                commands.Add(NaplpsCommandBuilder.BuildPointSetAbsolute(penAtTarget.X, penAtTarget.Y));
                commands.Add((NaplpsCommandBuilder.OpArcFilled, new NaplpsOperands(src.Operands)));
                break;
            }

            case ArcSetOutlinedCommand:
            {
                commands.Add((NaplpsCommandBuilder.OpArcSetFilled, new NaplpsOperands(src.Operands)));
                break;
            }
        }

        return commands;
    }

    public override ToolPreview? GetPreview()
    {
        // Hover crosshair at the click target for discoverability.
        if (Format == null)
        {
            return null;
        }

        var hit = CommandHitTester.HitTest(Format, CurrentX, CurrentY);

        if (hit < 0)
        {
            return null;
        }

        var bbox = CommandHitTester.GetBoundingBox(Format, hit);

        if (bbox == null)
        {
            return null;
        }

        var (x, y, w, h) = bbox.Value;

        return new ToolPreview
        {
            Shape = PreviewShape.Rectangle,
            X1 = x,
            Y1 = y,
            X2 = x + w,
            Y2 = y + h,
            IsSelection = true,
        };
    }
}
