// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using System.ComponentModel;
using Avalonia.Controls;
using NAPLPSApp.ViewModels.Menus;
using Menu = Avalonia.Controls.Menu;

namespace NAPLPSApp.Views.Menus;

/// <summary>
/// Renders a <see cref="MenuNode"/> tree into two concrete Avalonia surfaces:
/// an in-window <see cref="Menu"/> and a macOS <see cref="NativeMenu"/>.
///
/// Dynamic state (IsEnabled, IsChecked, IsVisible) is driven by predicates on
/// <see cref="MenuNode"/> that read the ViewModel. We subscribe once to the VM's
/// <see cref="INotifyPropertyChanged.PropertyChanged"/> and refresh every node's
/// state on any change. Over-notification is fine — menus are tiny and state
/// changes are infrequent.
/// </summary>
public static class MenuRenderer
{
    public static void PopulateInWindowMenu(Menu target, MainWindowViewModel vm, IReadOnlyList<MenuNode> tree)
    {
        var refreshers = new List<Action>();
        target.ItemsSource = tree.Select(n => BuildMenuItem(n, vm, refreshers)).ToList();

        HookRefreshers(vm, refreshers);
    }

    public static NativeMenu BuildNativeMenu(MainWindowViewModel vm, IReadOnlyList<MenuNode> tree)
    {
        var refreshers = new List<Action>();
        var native = new NativeMenu();

        foreach (var node in tree)
        {
            native.Items.Add(BuildNativeMenuItem(node, vm, refreshers));
        }

        HookRefreshers(vm, refreshers);
        return native;
    }

    private static void HookRefreshers(MainWindowViewModel vm, List<Action> refreshers)
    {
        // Prime state, then subscribe. Over-notification is deliberate — cheap for ~60 items.
        foreach (var r in refreshers) { r(); }
        vm.PropertyChanged += (_, _) =>
        {
            foreach (var r in refreshers) { r(); }
        };
    }

    private static Control BuildMenuItem(MenuNode node, MainWindowViewModel vm, List<Action> refreshers)
    {
        if (node.IsSeparator)
        {
            return new Separator();
        }

        var item = new MenuItem { Header = node.Header };

        if (node.Command is not null)          { item.Command = node.Command; }
        if (node.CommandParameter is not null) { item.CommandParameter = node.CommandParameter; }
        if (node.Gesture is not null)          { item.InputGesture = node.Gesture; }

        switch (node.Toggle)
        {
            case MenuToggle.CheckBox: item.ToggleType = MenuItemToggleType.CheckBox; break;
            case MenuToggle.Radio:    item.ToggleType = MenuItemToggleType.Radio;    break;
        }

        if (node.FontAwesomeIcon is not null) { Icons.MenuItem.SetIcon(item, node.FontAwesomeIcon); }
        if (node.ToolTip is not null)         { ToolTip.SetTip(item, node.ToolTip); }

        if (node.IsEnabledFn is { } isEnabled) { refreshers.Add(() => item.IsEnabled = isEnabled(vm)); }
        if (node.IsCheckedFn is { } isChecked) { refreshers.Add(() => item.IsChecked = isChecked(vm)); }
        if (node.IsVisibleFn is { } isVisible) { refreshers.Add(() => item.IsVisible = isVisible(vm)); }

        if (node.Children is { Count: > 0 } children)
        {
            item.ItemsSource = children.Select(c => BuildMenuItem(c, vm, refreshers)).ToList();
        }
        else if (node.Command is null)
        {
            // Placeholder items like "Group Layers" stay disabled (matches prior XAML behavior).
            item.IsEnabled = false;
        }

        return item;
    }

    private static NativeMenuItemBase BuildNativeMenuItem(MenuNode node, MainWindowViewModel vm, List<Action> refreshers)
    {
        if (node.IsSeparator)
        {
            return new NativeMenuItemSeparator();
        }

        var item = new NativeMenuItem(node.Header);

        if (node.Command is not null)          { item.Command = node.Command; }
        if (node.CommandParameter is not null) { item.CommandParameter = node.CommandParameter; }
        if (node.Gesture is not null)          { item.Gesture = node.Gesture; }

        // Avalonia 12 uses the same MenuItemToggleType enum for both MenuItem and NativeMenuItem.
        switch (node.Toggle)
        {
            case MenuToggle.CheckBox: item.ToggleType = MenuItemToggleType.CheckBox; break;
            case MenuToggle.Radio:    item.ToggleType = MenuItemToggleType.Radio;    break;
        }

        if (node.IsEnabledFn is { } isEnabled) { refreshers.Add(() => item.IsEnabled = isEnabled(vm)); }
        if (node.IsCheckedFn is { } isChecked) { refreshers.Add(() => item.IsChecked = isChecked(vm)); }
        // NativeMenuItem has no IsVisible; skip on macOS.

        if (node.Children is { Count: > 0 } children)
        {
            var submenu = new NativeMenu();
            foreach (var child in children)
            {
                submenu.Items.Add(BuildNativeMenuItem(child, vm, refreshers));
            }
            item.Menu = submenu;
        }
        else if (node.Command is null)
        {
            item.IsEnabled = false;
        }

        return item;
    }
}
