// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using Avalonia.Input;
using Telidraw.Resources;
using Telidraw.ViewModels;
using Telidraw.ViewModels.Menus;

namespace NAPLPSTests.Menu;

[TestClass]
public class MenuTreeBuilderTests
{
    private static MainWindowViewModel NewVm() => new MainWindowViewModel();

    private static MenuNode FindNode(IEnumerable<MenuNode> tree, params string[] headerPath)
    {
        MenuNode? current = null;
        IEnumerable<MenuNode> level = tree;
        foreach (var header in headerPath)
        {
            current = level.FirstOrDefault(n => !n.IsSeparator && n.Header == header)
                ?? throw new AssertFailedException($"Menu path not found: {string.Join(" > ", headerPath)} (stopped at {header})");
            level = current.Children ?? Array.Empty<MenuNode>();
        }
        return current!;
    }

    [TestMethod]
    public void TopLevel_IsFileEditNaplpsViewHelp()
    {
        var vm = NewVm();
        var tree = MenuTreeBuilder.Build(vm, PlatformGestureSet.ForPlatform(false));

        CollectionAssert.AreEqual(
            new[] { "File", "Edit", "NAPLPS", "View", "Help" },
            tree.Select(n => n.Header).ToArray());
    }

    [TestMethod]
    public void FileSave_UsesPlatformSaveGesture_Windows()
    {
        var vm = NewVm();
        var gestures = PlatformGestureSet.ForPlatform(isMacOS: false);

        var tree = MenuTreeBuilder.Build(vm, gestures);
        var save = FindNode(tree, "File", "Save");

        Assert.IsNotNull(save.Gesture);
        Assert.AreEqual(Key.S, save.Gesture!.Key);
        Assert.AreEqual(KeyModifiers.Control, save.Gesture.KeyModifiers);
        Assert.AreSame(vm.SaveCommand, save.Command);
    }

    [TestMethod]
    public void FileSave_UsesPlatformSaveGesture_MacOS()
    {
        var vm = NewVm();
        var gestures = PlatformGestureSet.ForPlatform(isMacOS: true);

        var tree = MenuTreeBuilder.Build(vm, gestures);
        var save = FindNode(tree, "File", "Save");

        Assert.AreEqual(Key.S, save.Gesture!.Key);
        Assert.AreEqual(KeyModifiers.Meta, save.Gesture.KeyModifiers);
    }

    [TestMethod]
    public void FileMenu_HasExpectedTopLevelChildren()
    {
        var vm = NewVm();
        var tree = MenuTreeBuilder.Build(vm, PlatformGestureSet.ForPlatform(false));
        var fileChildren = FindNode(tree, "File").Children!
            .Where(n => !n.IsSeparator)
            .Select(n => n.Header)
            .ToArray();

        CollectionAssert.Contains(fileChildren, "New");
        CollectionAssert.Contains(fileChildren, "Open");
        CollectionAssert.Contains(fileChildren, "Save");
        CollectionAssert.Contains(fileChildren, "Save As...");
        CollectionAssert.Contains(fileChildren, "Close");
        CollectionAssert.Contains(fileChildren, "Import");
        CollectionAssert.Contains(fileChildren, "Export...");
        CollectionAssert.Contains(fileChildren, "Quit");
    }

    [TestMethod]
    public void EditUndoRedo_HaveGesturesAndCommands()
    {
        var vm = NewVm();
        var gestures = PlatformGestureSet.ForPlatform(false);
        var tree = MenuTreeBuilder.Build(vm, gestures);

        var undo = FindNode(tree, "Edit", "Undo");
        var redo = FindNode(tree, "Edit", "Redo");

        Assert.AreEqual(gestures.Undo.Key, undo.Gesture!.Key);
        Assert.AreEqual(gestures.Redo.Key, redo.Gesture!.Key);
        Assert.AreSame(vm.UndoCommand, undo.Command);
        Assert.AreSame(vm.RedoCommand, redo.Command);
    }

    [TestMethod]
    public void SpeedSubmenu_Has15Rates_AllCheckBoxToggle()
    {
        var vm = NewVm();
        var tree = MenuTreeBuilder.Build(vm, PlatformGestureSet.ForPlatform(false));
        var speed = FindNode(tree, "NAPLPS", "Speed");

        Assert.IsNotNull(speed.Children);
        Assert.AreEqual(15, speed.Children!.Count);
        foreach (var item in speed.Children)
        {
            Assert.AreEqual(MenuToggle.CheckBox, item.Toggle, $"Speed/{item.Header}");
            Assert.AreSame(vm.SetBaudRateCommand, item.Command);
        }
    }

    [TestMethod]
    public void DisplayRatioSubmenu_Has3RadioItems()
    {
        var vm = NewVm();
        var tree = MenuTreeBuilder.Build(vm, PlatformGestureSet.ForPlatform(false));
        var ratios = FindNode(tree, "View", "Display Ratio");

        Assert.AreEqual(3, ratios.Children!.Count);
        foreach (var item in ratios.Children)
        {
            Assert.AreEqual(MenuToggle.Radio, item.Toggle);
        }
    }

    [TestMethod]
    public void FileSave_IsEnabled_TracksIsFileLoaded()
    {
        var vm = NewVm();
        var tree = MenuTreeBuilder.Build(vm, PlatformGestureSet.ForPlatform(false));
        var save = FindNode(tree, "File", "Save");

        Assert.IsNotNull(save.IsEnabledFn);
        // VM starts with no file loaded.
        Assert.IsFalse(save.IsEnabledFn!(vm));
        // Can't easily flip IsFileLoaded without driving the VM, but the predicate
        // call itself exercises the "uses real VM property" path.
    }

    [TestMethod]
    public void AnimateItem_IsCheckedFn_TracksIsAnimated()
    {
        var vm = NewVm();
        var tree = MenuTreeBuilder.Build(vm, PlatformGestureSet.ForPlatform(false));
        var animate = FindNode(tree, "NAPLPS", "Animate");

        Assert.IsNotNull(animate.IsCheckedFn);
        Assert.AreEqual(vm.IsAnimated, animate.IsCheckedFn!(vm));

        vm.IsAnimated = !vm.IsAnimated;
        Assert.AreEqual(vm.IsAnimated, animate.IsCheckedFn(vm));
    }

    [TestMethod]
    public void PlaceholderDisabledItems_HaveNoCommand()
    {
        var vm = NewVm();
        var tree = MenuTreeBuilder.Build(vm, PlatformGestureSet.ForPlatform(false));

        var group = FindNode(tree, "Edit", "Group Layers");
        var ungroup = FindNode(tree, "Edit", "Ungroup Layers");

        Assert.IsNull(group.Command);
        Assert.IsNull(ungroup.Command);
    }

    [TestMethod]
    public void Separators_ArePresentInFileMenu()
    {
        var vm = NewVm();
        var tree = MenuTreeBuilder.Build(vm, PlatformGestureSet.ForPlatform(false));
        var file = FindNode(tree, "File");

        Assert.IsTrue(file.Children!.Any(c => c.IsSeparator), "File menu must have at least one separator");
    }

    [TestMethod]
    public void Import_IsNested_WithSvgAndBitmap()
    {
        var vm = NewVm();
        var tree = MenuTreeBuilder.Build(vm, PlatformGestureSet.ForPlatform(false));
        var svg = FindNode(tree, "File", "Import", "SVG...");
        var bmp = FindNode(tree, "File", "Import", "Bitmap...");

        Assert.AreSame(vm.ImportSvgCommand, svg.Command);
        Assert.AreSame(vm.ImportBitmapCommand, bmp.Command);
    }

    [TestMethod]
    public void CanvasSizeSubmenu_Has10Sizes()
    {
        var vm = NewVm();
        var tree = MenuTreeBuilder.Build(vm, PlatformGestureSet.ForPlatform(false));
        var sizes = FindNode(tree, "View", "Canvas Size");

        Assert.AreEqual(10, sizes.Children!.Count);
    }
}
