// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSApp.Editor;

/// <summary>
/// Aggregates multiple IEditorAction instances into a single atomic undo step.
/// Execute runs them in declaration order; Undo runs them in reverse so dependent
/// state unwinds correctly. Use for tools that emit multiple logical actions in
/// one user gesture — Polygon's N click points, Telidraw compile-paste, paste-with-color, etc.
/// </summary>
public class CompositeAction : IEditorAction
{
    private readonly List<IEditorAction> _actions;

    public CompositeAction(params IEditorAction[] actions)
    {
        _actions = [.. actions];
    }

    public CompositeAction(IEnumerable<IEditorAction> actions)
    {
        _actions = [.. actions];
    }

    /// <summary>Convenience factory matching the plan's naming.</summary>
    public static IEditorAction Compose(params IEditorAction[] actions)
    {
        if (actions.Length == 0)
        {
            throw new ArgumentException("Compose requires at least one action.", nameof(actions));
        }

        if (actions.Length == 1)
        {
            return actions[0];
        }

        return new CompositeAction(actions);
    }

    public void Execute(NaplpsFormat format)
    {
        foreach (var action in _actions)
        {
            action.Execute(format);
        }
    }

    public void Undo(NaplpsFormat format)
    {
        for (int i = _actions.Count - 1; i >= 0; i--)
        {
            _actions[i].Undo(format);
        }
    }
}
