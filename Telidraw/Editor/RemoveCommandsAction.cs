// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSApp.Editor;

/// <summary>
/// Editor action that removes a command at a given index.
/// Snapshots the opcode+operands for undo (re-insertion).
/// </summary>
public class RemoveCommandsAction : IEditorAction
{
    private readonly int _index;
    private byte _opcode;
    private NaplpsOperands _operands;

    public RemoveCommandsAction(NaplpsFormat format, int index)
    {
        _index = index;

        // Snapshot the command data for undo
        if (index >= 0 && index < format.Commands.Count)
        {
            var command = format.Commands[index].Command;
            _opcode = command.OpCode;
            _operands = [.. command.Operands];
        }
    }

    public void Execute(NaplpsFormat format)
    {
        format.RemoveCommand(_index);
    }

    public void Undo(NaplpsFormat format)
    {
        format.InsertCommand(_index, _opcode, _operands);
    }
}
