// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSApp.Views;

public partial class DrcsDesignerWindow : Window
{
    public DrcsDesignerWindow()
    {
        InitializeComponent();
        DataContext = new DrcsDesignerViewModel();
    }

    /// <summary>
    /// Open the designer as a modal dialog. Returns a non-null tuple of (slot char, bitmap bytes)
    /// when the user clicked Commit; returns null if they cancelled or closed the window.
    /// </summary>
    public static async Task<(char slot, byte[] bitmap)?> PromptAsync(Window owner)
    {
        var dialog = new DrcsDesignerWindow();
        await dialog.ShowDialog(owner);

        if (dialog.DataContext is DrcsDesignerViewModel vm && vm.IsCommitted)
        {
            return (vm.SlotCharacter, vm.EncodeBitmap());
        }

        return null;
    }
}
