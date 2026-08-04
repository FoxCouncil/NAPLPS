// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSApp.Editor;

/// <summary>
/// One editor-side layer. Layers are a .td-only concept (binary NAPLPS has no layer
/// semantics); they group commands for visual organization and round-trip through
/// structured comments in Telidraw source.
/// </summary>
public partial class LayerInfo : ObservableObject
{
    [ObservableProperty]
    private int id;

    [ObservableProperty]
    private string name = "Layer";

    [ObservableProperty]
    private bool isVisible = true;

    [ObservableProperty]
    private bool isLocked;
}
