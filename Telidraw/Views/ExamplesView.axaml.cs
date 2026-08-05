// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using Avalonia.Input;
using Telidraw.Services;

namespace Telidraw.Views;

public partial class ExamplesView : UserControl
{
    public ExamplesView()
    {
        InitializeComponent();
        DataContext = new ExamplesViewModel();
    }

    private void OnListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is ExamplesViewModel vm && vm.OpenCommand.CanExecute(null))
        {
            vm.OpenCommand.Execute(null);
        }
    }

    /// <summary>
    /// Show the picker and return the chosen example's site-relative path, or null on cancel.
    /// </summary>
    public static async Task<string?> PromptAsync()
    {
        var view = new ExamplesView();

        if (view.DataContext is not ExamplesViewModel vm)
        {
            return null;
        }

        vm.RequestClose += () => Shell.CloseHost(view);

        var dialog = Shell.ShowDialogAsync(view, new ChildShellOptions("Open Example") { Width = 420, Height = 480 });
        await vm.LoadAsync();
        await dialog;

        return vm.IsCommitted ? vm.SelectedExample?.RelativePath : null;
    }
}
