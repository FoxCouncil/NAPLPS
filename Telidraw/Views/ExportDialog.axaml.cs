// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSApp.Views;

public partial class ExportDialog : Window
{
    public ExportDialog()
    {
        InitializeComponent();
        DataContext = new ExportDialogViewModel();

        if (DataContext is ExportDialogViewModel vm)
        {
            vm.RequestClose += Close;
        }
    }

    /// <summary>
    /// Show the export dialog modally over <paramref name="owner"/>, seeding the source
    /// canvas dimensions so the Output preview is accurate. Returns the VM if the user
    /// accepted (so MainWindow can read Format/Scale/Quality), or null if cancelled.
    /// </summary>
    public static async Task<ExportDialogViewModel?> PromptAsync(Window owner, int sourceWidth, int sourceHeight, int estimatedApngFrames = 0)
    {
        var dialog = new ExportDialog();
        if (dialog.DataContext is ExportDialogViewModel vm)
        {
            vm.SourceWidth = sourceWidth;
            vm.SourceHeight = sourceHeight;
            vm.ApngEstimatedFrames = estimatedApngFrames;
            // Default the end-frame slider to the last frame so the clip range covers the
            // full sequence by default; user can narrow it before commit.
            if (estimatedApngFrames > 0 && vm.ApngEndFrame == 0)
            {
                vm.ApngEndFrame = estimatedApngFrames;
            }
        }

        await dialog.ShowDialog(owner);

        if (dialog.DataContext is ExportDialogViewModel vm2 && vm2.IsCommitted)
        {
            return vm2;
        }

        return null;
    }
}
