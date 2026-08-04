// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace Telidraw.Views;

public partial class AddCommandWindow : Window
{
    public AddCommandWindow()
    {
        InitializeComponent();

        DataContext = new AddCommandViewModel();
    }
}