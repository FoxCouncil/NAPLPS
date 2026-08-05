// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using Telidraw.Services;

namespace Telidraw.Views;

public partial class ExportDialogView : UserControl
{
    public ExportDialogView()
    {
        InitializeComponent();
        DataContext = new ExportDialogViewModel();

        if (DataContext is ExportDialogViewModel vm)
        {
            vm.RequestClose += () => Shell.CloseHost(this);
        }
    }

    /// <summary>
    /// Show the export dialog modally, seeding the source canvas dimensions so the Output
    /// preview is accurate. Returns the VM if the user accepted (so the caller can read
    /// Format/Scale/Quality), or null if cancelled.
    /// </summary>
    public static async Task<ExportDialogViewModel?> PromptAsync(int sourceWidth, int sourceHeight, int estimatedApngFrames = 0)
    {
        var view = new ExportDialogView();
        if (view.DataContext is ExportDialogViewModel vm)
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

        await Shell.ShowDialogAsync(view, new ChildShellOptions("Export Image") { Width = 440, SizeToContentHeight = true, CanResize = false });

        if (view.DataContext is ExportDialogViewModel vm2 && vm2.IsCommitted)
        {
            return vm2;
        }

        return null;
    }
}
