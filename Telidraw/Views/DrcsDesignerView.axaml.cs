// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using Telidraw.Services;

namespace Telidraw.Views;

public partial class DrcsDesignerView : UserControl
{
    public DrcsDesignerView()
    {
        InitializeComponent();
        DataContext = new DrcsDesignerViewModel();
    }

    /// <summary>
    /// Open the designer as a modal dialog. Returns a non-null tuple of (slot char, bitmap bytes)
    /// when the user clicked Commit; returns null if they cancelled or closed it.
    /// </summary>
    public static async Task<(char slot, byte[] bitmap)?> PromptAsync()
    {
        var view = new DrcsDesignerView();

        if (view.DataContext is DrcsDesignerViewModel closeVm)
        {
            closeVm.RequestClose += () => Shell.CloseHost(view);
        }

        await Shell.ShowDialogAsync(view, new ChildShellOptions("DRCS Character Designer") { Width = 360, Height = 480 });

        if (view.DataContext is DrcsDesignerViewModel vm && vm.IsCommitted)
        {
            return (vm.SlotCharacter, vm.EncodeBitmap());
        }

        return null;
    }
}
