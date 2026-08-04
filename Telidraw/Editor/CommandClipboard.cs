// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSApp.Editor;

/// <summary>
/// Process-static clipboard for NAPLPS commands. Holds a snapshot of opcode+operands tuples
/// so a Paste can re-create the commands at any insertion point. Survives across SequenceWindow
/// instances within the running app.
/// </summary>
public static class CommandClipboard
{
    private static List<(byte opcode, NaplpsOperands operands)> _buffer = [];

    public static bool HasContent => _buffer.Count > 0;

    public static int Count => _buffer.Count;

    /// <summary>
    /// Snapshot the given commands into the clipboard. Operand bytes are deep-copied so
    /// later mutations to the source format don't affect what gets pasted.
    /// </summary>
    public static void Copy(IEnumerable<NaplpsSequence> sequences)
    {
        _buffer = sequences
            .Where(s => s?.Command != null)
            .Select(s => (s.Command.OpCode, new NaplpsOperands(s.Command.Operands)))
            .ToList();
    }

    /// <summary>Replace the clipboard contents with raw (opcode, operands) tuples.</summary>
    public static void CopyRaw(IEnumerable<(byte opcode, NaplpsOperands operands)> commands)
    {
        _buffer = commands
            .Select(c => (c.opcode, new NaplpsOperands(c.operands)))
            .ToList();
    }

    /// <summary>
    /// Build an InsertAtAction that inserts the clipboard contents at the given index.
    /// Caller is responsible for executing the action through UndoManager so the paste
    /// is undoable. Returns null when the clipboard is empty.
    /// </summary>
    public static InsertAtAction? BuildPasteAction(int insertIndex)
    {
        if (!HasContent)
        {
            return null;
        }

        // Deep-copy the operand bytes again so the action holds independent buffers.
        var snapshot = _buffer
            .Select(c => (c.opcode, new NaplpsOperands(c.operands)))
            .ToList();

        return new InsertAtAction(insertIndex, snapshot);
    }

    /// <summary>
    /// One-shot paste helper that wraps BuildPasteAction in an UndoManager.Execute
    /// call. Returns true if a paste happened, false if the clipboard was empty.
    /// </summary>
    public static bool Paste(NaplpsFormat format, int insertIndex, UndoManager undoManager)
    {
        var action = BuildPasteAction(insertIndex);

        if (action == null)
        {
            return false;
        }

        undoManager.Execute(action, format);

        return true;
    }

    /// <summary>Empty the clipboard.</summary>
    public static void Clear()
    {
        _buffer.Clear();
    }
}
