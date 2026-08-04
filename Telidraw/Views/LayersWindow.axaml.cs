// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using NAPLPSApp.Editor;

namespace NAPLPSApp.Views;

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
