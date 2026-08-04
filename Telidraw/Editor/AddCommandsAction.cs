// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSApp.Editor;

/// <summary>
/// Editor action that adds one or more commands to the format.
/// Undo removes them; redo re-adds them.
/// </summary>
public class AddCommandsAction : IEditorAction
{
    private readonly List<(byte opcode, NaplpsOperands operands)> _commands;
    private int _insertIndex;

    public AddCommandsAction(List<(byte opcode, NaplpsOperands operands)> commands)
    {
        _commands = commands;
    }

    public void Execute(NaplpsFormat format)
    {
        _insertIndex = format.Commands.Count;

        foreach (var (opcode, operands) in _commands)
        {
            format.AddCommand(opcode, operands);
        }
    }

    public void Undo(NaplpsFormat format)
    {
        // Remove the commands we added (from end to start to maintain indices)
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
