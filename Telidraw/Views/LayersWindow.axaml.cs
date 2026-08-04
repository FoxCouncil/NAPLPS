// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using Telidraw.Editor;

namespace Telidraw.Views;

public partial class LayersWindow : Window
{
    public LayersWindow()
    {
        InitializeComponent();
    }

    public LayersWindow(LayerManager manager) : this()
    {
        DataContext = new LayersWindowViewModel(manager);
    }
}
