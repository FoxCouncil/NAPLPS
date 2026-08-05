// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using Telidraw.Resources;
using Telidraw.ViewModels.Menus;
using Telidraw.Views.Menus;

namespace Telidraw.Views;

/// <summary>
/// Desktop chrome around <see cref="MainView"/>: window title, icon, the macOS
/// <see cref="NativeMenu"/> (which must hang off a Window), and close-time cleanup.
/// Everything else - editor surface, menus, keyboard dispatch - lives in MainView so the
/// same UI runs under single-view lifetimes (browser, mobile) without a Window at all.
/// </summary>
public partial class MainWindow : Window
{
    private bool _nativeMenuBuilt;

    public MainWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => BuildNativeMenuFromViewModel();
        BuildNativeMenuFromViewModel();
    }

    /// <summary>
    /// Builds the macOS <see cref="NativeMenu"/> from the same <see cref="MenuTreeBuilder"/>
    /// tree MainView uses for the in-window menu. Re-runs on DataContext assignment;
    /// idempotent thereafter.
    /// </summary>
    private void BuildNativeMenuFromViewModel()
    {
        if (_nativeMenuBuilt || DataContext is not MainWindowViewModel vm) { return; }

        if (OperatingSystem.IsMacOS())
        {
            var tree = MenuTreeBuilder.Build(vm, PlatformGestureSet.Current);
            NativeMenu.SetMenu(this, MenuRenderer.BuildNativeMenu(vm, tree));
        }

        _nativeMenuBuilt = true;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        if (DataContext is MainWindowViewModel vm)
        {
            vm.CloseChildWindows();
        }
    }
}
