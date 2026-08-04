// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSApp.Editor;

/// <summary>
/// Tracks which NaplpsSequence belongs to which layer, plus which layer is active for
/// new commits. Assignments are keyed by object identity (NaplpsSequence reference) so
/// index shifts from inserts/removes don't matter. Unassigned commands report as being
/// in the first layer (kept as the perpetual "Background" fallback).
/// </summary>
public partial class LayerManager : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<LayerInfo> layers = [];

    /// <summary>Layer that receives newly-committed commands. Also the highlighted row
    /// in the Layers window.</summary>
    [ObservableProperty]
    private LayerInfo? activeLayer;

    private readonly Dictionary<NaplpsSequence, int> _assignment = new();
    private int _nextId = 1;

    /// <summary>Drop all layers + assignments — called on file close / new / load so each
    /// session starts from a known state.</summary>
    public void Reset()
    {
        Layers.Clear();
        _assignment.Clear();
        ActiveLayer = null;
        _nextId = 1;
    }

    /// <summary>Create a fresh layer and make it active.</summary>
    public LayerInfo AddLayer(string name)
    {
        var layer = new LayerInfo { Id = _nextId++, Name = name, IsVisible = true };
        Layers.Add(layer);
        ActiveLayer = layer;
        return layer;
    }

    public void RemoveLayer(LayerInfo layer)
    {
        // Reassign the layer's commands back to the first remaining layer so the command
        // stream never has dangling ids — otherwise decompile would lose their membership.
        int? target = Layers.FirstOrDefault(l => l != layer)?.Id;
        if (target != null)
        {
            foreach (var key in _assignment.Where(kvp => kvp.Value == layer.Id).Select(kvp => kvp.Key).ToList())
            {
                _assignment[key] = target.Value;
            }
        }

        Layers.Remove(layer);

        if (ActiveLayer == layer)
        {
            ActiveLayer = Layers.FirstOrDefault();
        }
    }

    public void AssignCommand(NaplpsSequence seq, int layerId)
    {
        _assignment[seq] = layerId;
    }

    public void AssignCommand(NaplpsSequence seq)
    {
        if (ActiveLayer != null)
        {
            _assignment[seq] = ActiveLayer.Id;
        }
    }

    public bool HasAssignment(NaplpsSequence seq) => _assignment.ContainsKey(seq);

    /// <summary>Assignment for a command, falling back to the first layer's id when the
    /// command predates the layer system (e.g. opened an older file).</summary>
    public int GetLayerId(NaplpsSequence seq)
    {
        if (_assignment.TryGetValue(seq, out var id)) { return id; }
        return Layers.Count > 0 ? Layers[0].Id : 0;
    }

    public LayerInfo? GetLayer(int id) => Layers.FirstOrDefault(l => l.Id == id);

    /// <summary>Walk the format's command list and assign anything that doesn't yet have
    /// a layer to the currently-active layer. Cheap self-healing so every AddCommandsAction
    /// path doesn't need explicit assignment plumbing.</summary>
    public void SyncAssignments(NaplpsFormat? format)
    {
        if (format == null || ActiveLayer == null) { return; }

        foreach (var seq in format.Commands)
        {
            if (!_assignment.ContainsKey(seq))
            {
                _assignment[seq] = ActiveLayer.Id;
            }
        }
    }
}
