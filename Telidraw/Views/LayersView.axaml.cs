// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using Telidraw.Editor;

namespace Telidraw.Views;

public partial class LayersView : UserControl
{
    public LayersView()
    {
        InitializeComponent();
    }

    public LayersView(LayerManager manager) : this()
    {
        DataContext = new LayersWindowViewModel(manager);
    }
}
