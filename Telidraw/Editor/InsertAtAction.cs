// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSApp.Editor;

/// <summary>
/// Editor action that inserts one or more commands at a specific index in the format.
/// Differs from AddCommandsAction (which always appends) by allowing arbitrary insertion
/// points — used by paste, drag-reorder, and SequenceWindow's insert-before/after operations.
/// Undo removes the inserted commands; redo re-inserts them at the same index.
/// </summary>
public class InsertAtAction : IEditorAction
{
    private readonly List<(byte opcode, NaplpsOperands operands)> _commands;
    private readonly int _insertIndex;

    public InsertAtAction(int insertIndex, List<(byte opcode, NaplpsOperands operands)> commands)
    {
        _insertIndex = insertIndex;
        _commands = commands;
    }

    public InsertAtAction(int insertIndex, IEnumerable<(byte opcode, NaplpsOperands operands)> commands)
        : this(insertIndex, [.. commands])
    {
    }

    public void Execute(NaplpsFormat format)
    {
        // Insert in order so the first command lands at _insertIndex and the rest follow.
        for (int i = 0; i < _commands.Count; i++)
        {
            var (opcode, operands) = _commands[i];
            format.InsertCommand(_insertIndex + i, opcode, operands);
        }
    }

    public void Undo(NaplpsFormat format)
    {
        // Remove from end to start to maintain stable indices.
        for (int i = _commands.Count - 1; i >= 0; i--)
        {
            var removeIdx = _insertIndex + i;

            if (removeIdx < format.Commands.Count)
            {
                format.RemoveCommand(removeIdx);
            }
        }
    }
}
