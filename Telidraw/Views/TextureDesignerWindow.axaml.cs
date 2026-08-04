// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSApp.Views;

public partial class TextureDesignerWindow : Window
{
    public TextureDesignerWindow()
    {
        InitializeComponent();
        DataContext = new TextureDesignerViewModel();
    }

    /// <summary>
    /// Open the designer modally. Returns (maskId, patternBytes, maskBytes) when the user
    /// commits; null on cancel/close.
    /// </summary>
    public static async Task<(byte maskId, byte[] pattern, byte[] mask)?> PromptAsync(Window owner)
    {
        var dialog = new TextureDesignerWindow();
        await dialog.ShowDialog(owner);

        if (dialog.DataContext is TextureDesignerViewModel vm && vm.IsCommitted)
        {
            return (vm.MaskId, vm.EncodePattern(), vm.EncodeMask());
        }

        return null;
    }
}
