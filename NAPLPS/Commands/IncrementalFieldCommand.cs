// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPS.Commands;

[AddCommand(200, "Incremental Field", "Define an active field region for subsequent text and incremental drawing.", Category = CommandCategory.Incremental, DslKeyword = "field")]
public class IncrementalFieldCommand : GeometricDrawingCommandBase
{
    public static new readonly NaplpsOperandType OperandType = NaplpsOperandType.MultiValue;

    public Vector3 Origin { get; }

    public Vector3 Dimensions { get; }

    public IncrementalFieldCommand(NaplpsState state, byte opcode, NaplpsOperands operands) : base(state, opcode, operands)
    {
        var vertices = ProcessVertices(Operands);

        if (operands.Count == 0)
        {
            // If no data bytes follow the FIELD opcode, the active field is set to the full unit
            // screen and the origin point is (0, 0).
            state.Field = new NaplpsField();

            return;
        }

        if (vertices.Count == 1)
        {
            Origin = State.Pen;
            Dimensions = vertices[0];
        }
        else
        {
            Origin = vertices[0];
            Dimensions = vertices[1];
        }

        // Field dimensions keep their SIGN: X3.110 5.3.3.6.2 says they "may be positive or
        // negative, so that the origin point may be placed in any of the four corners of the
        // field". Device-verified: MVDI treats a negative dy as a field extending BELOW the
        // origin, and text at that origin row does not arm the field wrap. Extent math goes
        // through NaplpsField's edge accessors.
        state.Field = new NaplpsField(Origin, Dimensions);

        // X3.110 5.3.3.6.2: "The drawing point is set to the origin of the field after FIELD
        // has been executed." Device-verified on MVDI twice: the Eaasy Sabre button labels
        // (our text was exactly one field height too high when the pen was placed at the top
        // edge) and the FLDPOS probe (the first text row draws on the origin row of a tall
        // field). Gated to Prodigy: the historical generic behavior places the pen at the
        // top edge computed with absolute dimensions, and generic content (icosamp) is
        // authored against it.
        if (state.SystemType == NaplpsSystemType.Prodigy)
        {
            state.Pen = Origin;
        }
        else
        {
            var pen = Origin;
            pen.Y = Origin.Y + Math.Abs(Dimensions.Y);
            state.Pen = pen;
        }
    }
}
