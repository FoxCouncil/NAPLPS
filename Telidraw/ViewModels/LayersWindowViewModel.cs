// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using NAPLPSApp.Editor;

namespace NAPLPSApp.ViewModels;

public partial class LayersWindowViewModel : ViewModelBase
{
    private readonly LayerManager manager;

    public LayersWindowViewModel(LayerManager manager)
    {
        this.manager = manager;
    }

    public ObservableCollection<LayerInfo> Layers => manager.Layers;

    public LayerInfo? ActiveLayer
    {
        get => manager.ActiveLayer;
        set
        {
            if (value != null && manager.ActiveLayer != value)
            {
                manager.ActiveLayer = value;
                OnPropertyChanged();
            }
        }
    }

    [RelayCommand]
    private void AddLayer()
    {
        var existing = Layers.Count;
        manager.AddLayer($"Layer {existing + 1}");
        OnPropertyChanged(nameof(ActiveLayer));
    }

    [RelayCommand]
    private void RemoveLayer(LayerInfo? layer)
    {
        if (layer == null) { return; }
        // Keep at least one layer around so every command always has a home.
        if (Layers.Count <= 1) { return; }
        manager.RemoveLayer(layer);
        OnPropertyChanged(nameof(ActiveLayer));
    }

    [RelayCommand]
    private void MoveLayerUp(LayerInfo? layer)
    {
        if (layer == null) { return; }
        var idx = Layers.IndexOf(layer);
        if (idx > 0) { Layers.Move(idx, idx - 1); }
    }

    [RelayCommand]
    private void MoveLayerDown(LayerInfo? layer)
    {
        if (layer == null) { return; }
        var idx = Layers.IndexOf(layer);
        if (idx >= 0 && idx < Layers.Count - 1) { Layers.Move(idx, idx + 1); }
    }
}
