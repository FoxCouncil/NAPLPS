// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using System.Windows.Input;
using Avalonia.Input;

namespace NAPLPSApp.ViewModels.Menus;

public enum MenuToggle
{
    None,
    CheckBox,
    Radio
}

/// <summary>
/// Platform-agnostic menu tree node. Rendered by <c>MenuRenderer</c> into both
/// an in-window Avalonia <c>Menu</c> and a macOS <c>NativeMenu</c>.
///
/// Static fields (Header, Command, Gesture, ...) are set at build time.
/// Dynamic state (IsEnabled, IsChecked, IsVisible) is expressed as predicates
/// over the ViewModel. The renderer subscribes to the VM's PropertyChanged
/// event and re-evaluates these predicates on every change (over-notification
/// is fine for menus — ~60 items, infrequent changes).
/// </summary>
public sealed class MenuNode
{
    public string Header { get; init; } = string.Empty;
    public ICommand? Command { get; init; }
    public object? CommandParameter { get; init; }
    public KeyGesture? Gesture { get; init; }
    public string? FontAwesomeIcon { get; init; }
    public string? ToolTip { get; init; }
    public MenuToggle Toggle { get; init; } = MenuToggle.None;

    public Func<MainWindowViewModel, bool>? IsEnabledFn { get; init; }
    public Func<MainWindowViewModel, bool>? IsCheckedFn { get; init; }
    public Func<MainWindowViewModel, bool>? IsVisibleFn { get; init; }

    public IReadOnlyList<MenuNode>? Children { get; init; }
    public bool IsSeparator { get; init; }

    public static MenuNode Separator { get; } = new MenuNode { IsSeparator = true };
}
