// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using Telidraw.Services;

namespace Telidraw.Views;

public partial class TextureDesignerView : UserControl
{
    public TextureDesignerView()
    {
        InitializeComponent();
        DataContext = new TextureDesignerViewModel();
    }

    /// <summary>
    /// Open the designer modally. Returns (maskId, patternBytes, maskBytes) when the user
    /// commits; null on cancel/close.
    /// </summary>
    public static async Task<(byte maskId, byte[] pattern, byte[] mask)?> PromptAsync()
    {
        var view = new TextureDesignerView();

        if (view.DataContext is TextureDesignerViewModel closeVm)
        {
            closeVm.RequestClose += () => Shell.CloseHost(view);
        }

        await Shell.ShowDialogAsync(view, new ChildShellOptions("Texture Mask Designer") { Width = 560, Height = 420 });

        if (view.DataContext is TextureDesignerViewModel vm && vm.IsCommitted)
        {
            return (vm.MaskId, vm.EncodePattern(), vm.EncodeMask());
        }

        return null;
    }
}
