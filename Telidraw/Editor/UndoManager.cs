// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSApp.Editor;

public class UndoManager
{
    private readonly Stack<IEditorAction> _undoStack = new();
    private readonly Stack<IEditorAction> _redoStack = new();

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public void Execute(IEditorAction action, NaplpsFormat format)
    {
        action.Execute(format);
        _undoStack.Push(action);
        _redoStack.Clear(); // New action invalidates redo history
    }

    public void Undo(NaplpsFormat format)
    {
        if (!CanUndo)
        {
            return;
        }

        var action = _undoStack.Pop();
        action.Undo(format);
        _redoStack.Push(action);
    }

    public void Redo(NaplpsFormat format)
    {
        if (!CanRedo)
        {
            return;
        }

        var action = _redoStack.Pop();
        action.Execute(format);
        _undoStack.Push(action);
    }

    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
    }
}
