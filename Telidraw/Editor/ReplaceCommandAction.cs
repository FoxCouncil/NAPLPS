// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSApp.Editor;

/// <summary>
/// Replace the command at <c>_index</c> with new opcode + operands. Snapshots the
/// previous opcode + operands so undo restores byte-exact. Used by the arrow-nudge
/// action where the user repositions a geometric command's coordinates without
/// inserting a new entry into the stream.
/// </summary>
public class ReplaceCommandAction : IEditorAction
{
    private readonly int _index;
    private readonly byte _newOpcode;
    private readonly NaplpsOperands _newOperands;
    private byte _prevOpcode;
    private NaplpsOperands _prevOperands;

    public ReplaceCommandAction(NaplpsFormat format, int index, byte newOpcode, NaplpsOperands newOperands)
    {
        _index = index;
        _newOpcode = newOpcode;
        _newOperands = newOperands;

        if (index >= 0 && index < format.Commands.Count)
        {
            var c = format.Commands[index].Command;
            _prevOpcode = c.OpCode;
            _prevOperands = [.. c.Operands];
        }
        else
        {
            _prevOperands = [];
        }
    }

    public void Execute(NaplpsFormat format)
    {
        if (_index < 0 || _index >= format.Commands.Count) { return; }
        format.RemoveCommand(_index);
        format.InsertCommand(_index, _newOpcode, _newOperands);
    }

    public void Undo(NaplpsFormat format)
    {
        if (_index < 0 || _index >= format.Commands.Count) { return; }
        format.RemoveCommand(_index);
        format.InsertCommand(_index, _prevOpcode, _prevOperands);
    }
}
