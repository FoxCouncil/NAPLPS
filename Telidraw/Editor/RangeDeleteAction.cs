// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSApp.Editor;

/// <summary>
/// Editor action that removes a contiguous range of commands [startIndex, startIndex + count).
/// Snapshots the opcode+operands of each removed command so undo can re-insert them at the
/// original positions in the original order.
/// </summary>
public class RangeDeleteAction : IEditorAction
{
    private readonly int _startIndex;
    private readonly int _count;
    private readonly List<(byte opcode, NaplpsOperands operands)> _snapshot = [];

    public RangeDeleteAction(NaplpsFormat format, int startIndex, int count)
    {
        _startIndex = startIndex;
        _count = count;

        if (startIndex < 0 || count <= 0)
        {
            return;
        }

        var endExclusive = Math.Min(startIndex + count, format.Commands.Count);

        for (int i = startIndex; i < endExclusive; i++)
        {
            var command = format.Commands[i].Command;
            _snapshot.Add((command.OpCode, [.. command.Operands]));
        }
    }

    public void Execute(NaplpsFormat format)
    {
        // Remove from highest index down so each RemoveCommand call sees stable indices.
        var endExclusive = Math.Min(_startIndex + _count, format.Commands.Count);

        for (int i = endExclusive - 1; i >= _startIndex; i--)
        {
            format.RemoveCommand(i);
        }
    }

    public void Undo(NaplpsFormat format)
    {
        // Re-insert each snapshotted command at its original index, lowest first.
        for (int i = 0; i < _snapshot.Count; i++)
        {
            var (opcode, operands) = _snapshot[i];
            format.InsertCommand(_startIndex + i, opcode, operands);
        }
    }
}
