// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace Telidraw.Views;

public partial class AddCommandView : UserControl
{
    public AddCommandView()
    {
        InitializeComponent();

        DataContext = new AddCommandViewModel();
    }
}
