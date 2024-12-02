// Copyright (c) 2024 FoxCouncil - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPS.Commands;

public abstract class PointCommand : FillableGeometricDrawingCommandBase
{
    public bool IsVisible { get; internal set; }

    public PointCommand(NaplpsState state, NaplpsCommands opcode, NaplpsOperands operands) : base(state, opcode, operands)
    {
        var vertices = ProcessVertices(operands);

        foreach (var point in vertices)
        {
            if (opcode == POINT_ABS || opcode == POINT_SET_ABS)
            {
                SetPen(point);
            }
            else
            {
                SetPen(state.Pen + point);
            }
        }

        IsVisible = opcode == POINT_ABS || opcode == POINT_REL;
    }
}
