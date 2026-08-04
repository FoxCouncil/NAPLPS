// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using Telidraw.Resources;

namespace Telidraw.ViewModels.Menus;

/// <summary>
/// Builds the complete menu tree that drives both the in-window <c>Menu</c> and
/// the macOS <c>NativeMenu</c>. Single source of truth — keep this in sync as
/// the menu evolves.
///
/// <paramref name="windowForCommandParameter"/> stands in for the old XAML
/// <c>{Binding $parent[Window]}</c> pattern used by dialog-host commands.
/// </summary>
public static class MenuTreeBuilder
{
    public static IReadOnlyList<MenuNode> Build(MainWindowViewModel vm, PlatformGestureSet gestures, object? windowForCommandParameter = null)
    {
        Func<MainWindowViewModel, bool> fileLoaded = v => v.IsFileLoaded;
        Func<MainWindowViewModel, bool> macroRecording = v => v.IsMacroRecording;

        return new[]
        {
            new MenuNode
            {
                Header = "File",
                Children = new[]
                {
                    new MenuNode { Header = "New", Command = vm.NewCommand, FontAwesomeIcon = "fa-solid fa-file" },
                    MenuNode.Separator,
                    new MenuNode { Header = "Open", Command = vm.OpenCommand, FontAwesomeIcon = "fa-solid fa-folder-open" },
                    new MenuNode { Header = "Save", Command = vm.SaveCommand, Gesture = gestures.Save, FontAwesomeIcon = "fa-solid fa-floppy-disk", IsEnabledFn = fileLoaded },
                    new MenuNode { Header = "Save As...", Command = vm.SaveAsCommand, Gesture = gestures.SaveAs, FontAwesomeIcon = "fa-solid fa-floppy-disk", IsEnabledFn = fileLoaded },
                    new MenuNode { Header = "Close", Command = vm.CloseCommand, FontAwesomeIcon = "fa-solid fa-circle-xmark", IsEnabledFn = fileLoaded },
                    MenuNode.Separator,
                    new MenuNode
                    {
                        Header = "Import",
                        FontAwesomeIcon = "fa-solid fa-file-import",
                        Children = new[]
                        {
                            new MenuNode { Header = "SVG...", Command = vm.ImportSvgCommand, FontAwesomeIcon = "fa-solid fa-bezier-curve", ToolTip = "Convert an SVG file's paths to Telidraw line commands" },
                            new MenuNode { Header = "Bitmap...", Command = vm.ImportBitmapCommand, FontAwesomeIcon = "fa-solid fa-image", ToolTip = "Quantize a raster image to the 16-color palette and emit as filled cells" }
                        }
                    },
                    new MenuNode { Header = "Export...", Command = vm.ExportCommand, FontAwesomeIcon = "fa-solid fa-file-export", IsEnabledFn = fileLoaded },
                    MenuNode.Separator,
                    new MenuNode { Header = "Reference Image...", Command = vm.LoadReferenceImageFileCommand, FontAwesomeIcon = "fa-solid fa-image", IsEnabledFn = fileLoaded, ToolTip = "Overlay a photo/sketch behind the canvas as a drawing reference. Saved only in .td source." },
                    new MenuNode { Header = "Clear Reference Image", Command = vm.ClearReferenceImageCommand, FontAwesomeIcon = "fa-solid fa-ban", IsEnabledFn = v => v.IsReferenceImageLoaded },
                    MenuNode.Separator,
                    new MenuNode { Header = "Quit", Command = vm.QuitCommand, FontAwesomeIcon = "fa-solid fa-door-open" }
                }
            },
            new MenuNode
            {
                Header = "Edit",
                Children = new[]
                {
                    new MenuNode { Header = "Undo", Command = vm.UndoCommand, Gesture = gestures.Undo, FontAwesomeIcon = "fa-solid fa-rotate-left" },
                    new MenuNode { Header = "Redo", Command = vm.RedoCommand, Gesture = gestures.Redo, FontAwesomeIcon = "fa-solid fa-rotate-right" },
                    MenuNode.Separator,
                    new MenuNode { Header = "Group Layers" },     // placeholder (no command = disabled)
                    new MenuNode { Header = "Ungroup Layers" }
                }
            },
            new MenuNode
            {
                Header = "NAPLPS",
                Children = new[]
                {
                    new MenuNode
                    {
                        Header = "Palette",
                        Children = new[]
                        {
                            new MenuNode { Header = "Load NAPLPS Default Palette", Command = vm.LoadDefaultPaletteCommand, FontAwesomeIcon = "fa-solid fa-palette", IsEnabledFn = fileLoaded, ToolTip = "Replace palette with NAPLPS spec 3-bit GRB ramp (emits SET COLOR commands, undoable)" },
                            new MenuNode { Header = "Load Prodigy Palette", Command = vm.LoadProdigyPaletteCommand, FontAwesomeIcon = "fa-solid fa-swatchbook", IsEnabledFn = fileLoaded, ToolTip = "Replace palette with Prodigy's canonical CLUT" }
                        }
                    },
                    new MenuNode { Header = "DRCS Character...", Command = vm.OpenDrcsDesignerCommand, CommandParameter = windowForCommandParameter, FontAwesomeIcon = "fa-solid fa-keyboard", IsEnabledFn = fileLoaded, ToolTip = "Design a custom 8\u00d710 bitmap character and commit it as a DEF DRCS command" },
                    new MenuNode { Header = "Texture Mask...", Command = vm.OpenTextureDesignerCommand, CommandParameter = windowForCommandParameter, FontAwesomeIcon = "fa-solid fa-grip", IsEnabledFn = fileLoaded, ToolTip = "Design a fill pattern + mask and commit as a DEF TEXTURE command" },
                    MenuNode.Separator,
                    new MenuNode
                    {
                        Header = "Macro Recording",
                        Children = new[]
                        {
                            new MenuNode { Header = "Start Recording", Command = vm.StartMacroRecordingCommand, FontAwesomeIcon = "fa-solid fa-circle", IsEnabledFn = fileLoaded },
                            new MenuNode { Header = "Stop & Save Macro", Command = vm.StopMacroRecordingCommand, FontAwesomeIcon = "fa-solid fa-stop", IsEnabledFn = macroRecording },
                            new MenuNode { Header = "Cancel Recording", Command = vm.CancelMacroRecordingCommand, FontAwesomeIcon = "fa-solid fa-ban", IsEnabledFn = macroRecording }
                        }
                    },
                    MenuNode.Separator,
                    new MenuNode
                    {
                        Header = "Network",
                        FontAwesomeIcon = "fa-solid fa-network-wired",
                        Children = new[]
                        {
                            new MenuNode { Header = "Start Listener", Command = vm.StartNetworkListenerCommand, FontAwesomeIcon = "fa-solid fa-circle-play", IsEnabledFn = v => !v.IsNetworkListening, ToolTip = "Begin accepting incoming NAPLPS streams on the configured port (default 5510)" },
                            new MenuNode { Header = "Stop Listener", Command = vm.StopNetworkListenerCommand, FontAwesomeIcon = "fa-solid fa-circle-stop", IsEnabledFn = v => v.IsNetworkListening },
                            MenuNode.Separator,
                            new MenuNode { Header = "Send Document to Remote", Command = vm.SendDocumentToRemoteCommand, FontAwesomeIcon = "fa-solid fa-paper-plane", IsEnabledFn = fileLoaded, ToolTip = "Push the current .nap byte stream to the configured remote host:port" }
                        }
                    },
                    MenuNode.Separator,
                    new MenuNode { Header = "Re-render", Command = vm.RerenderCommand, FontAwesomeIcon = "fa-solid fa-recycle", IsEnabledFn = fileLoaded },
                    new MenuNode { Header = "Animate", Command = vm.ToggleAnimateCommand, Toggle = MenuToggle.CheckBox, IsCheckedFn = v => v.IsAnimated, FontAwesomeIcon = "fa-solid fa-check" },
                    new MenuNode { Header = "Loop", Command = vm.ToggleLoopCommand, Toggle = MenuToggle.CheckBox, IsCheckedFn = v => v.IsLooping, FontAwesomeIcon = "fa-solid fa-repeat" },
                    new MenuNode { Header = "Palette Animation", Command = vm.TogglePaletteAnimationCommand, Toggle = MenuToggle.CheckBox, IsCheckedFn = v => v.IsPaletteAnimationMode, FontAwesomeIcon = "fa-solid fa-palette" },
                    BuildSpeedMenu(vm)
                }
            },
            new MenuNode
            {
                Header = "View",
                Children = new[]
                {
                    new MenuNode { Header = "Toolbox", Command = vm.ToggleToolboxCommand, FontAwesomeIcon = "fa-solid fa-pen-ruler", IsEnabledFn = fileLoaded },
                    new MenuNode { Header = "Properties", Command = vm.PropertiesCommand, FontAwesomeIcon = "fa-solid fa-circle-info", IsEnabledFn = fileLoaded },
                    new MenuNode { Header = "Layers", Command = vm.ToggleLayersCommand, FontAwesomeIcon = "fa-solid fa-layer-group", IsEnabledFn = fileLoaded },
                    new MenuNode { Header = "Sequence", Command = vm.ToggleSequenceCommand, FontAwesomeIcon = "fa-solid fa-shoe-prints", IsEnabledFn = fileLoaded },
                    new MenuNode { Header = "Telidraw Source", Command = vm.ToggleTelidrawPaneCommand, FontAwesomeIcon = "fa-solid fa-code", Toggle = MenuToggle.CheckBox, IsCheckedFn = v => v.IsTelidrawPaneVisible, IsEnabledFn = fileLoaded },
                    MenuNode.Separator,
                    BuildCanvasSizeMenu(vm),
                    BuildDisplayRatioMenu(vm),
                    BuildStretchMenu(vm),
                    new MenuNode { Header = "Fit to Window", Command = vm.FitToWindowCommand, FontAwesomeIcon = "fa-solid fa-expand", IsEnabledFn = fileLoaded },
                    MenuNode.Separator,
                    new MenuNode
                    {
                        Header = "Debug",
                        FontAwesomeIcon = "fa-solid fa-bug",
                        Children = new[]
                        {
                            new MenuNode { Header = "Text Debug", Command = vm.ToggleDebugTextDrawingCommand, FontAwesomeIcon = "fa-solid fa-font", Toggle = MenuToggle.CheckBox, IsCheckedFn = v => v.DebugTextDrawing, IsEnabledFn = fileLoaded }
                        }
                    }
                }
            },
            new MenuNode
            {
                Header = "Help",
                Children = new[]
                {
                    new MenuNode { Header = "Help (github.com)", Command = vm.HelpCommand, FontAwesomeIcon = "fa-solid fa-question" },
                    MenuNode.Separator,
                    new MenuNode { Header = "GitHub Code", Command = vm.GitHubCommand, FontAwesomeIcon = "fa-brand fa-github" },
                    MenuNode.Separator,
                    new MenuNode { Header = "About", Command = vm.AboutCommand, FontAwesomeIcon = "fa-solid fa-circle-info" }
                }
            }
        };
    }

    private static MenuNode BuildSpeedMenu(MainWindowViewModel vm)
    {
        // The canonical rate list lives in the library so the menu, the export dialog, the CLI
        // and the corpus site cannot drift apart.
        var rates = NAPLPS.NaplpsBaud.Rates.Select(r => (label: NAPLPS.NaplpsBaud.Describe(r), rate: (uint)r)).ToArray();

        return new MenuNode
        {
            Header = "Speed",
            FontAwesomeIcon = "fa-solid fa-gauge",
            Children = rates.Select(r => new MenuNode
            {
                Header = r.label,
                Command = vm.SetBaudRateCommand,
                CommandParameter = r.rate.ToString(),
                Toggle = MenuToggle.CheckBox,
                IsCheckedFn = v => v.BaudRate == r.rate
            }).ToArray()
        };
    }

    private static MenuNode BuildCanvasSizeMenu(MainWindowViewModel vm)
    {
        string[] sizes =
        [
            "160x120", "320x200", "320x240", "640x480", "800x600",
            "1024x768", "1280x960", "1600x1200", "2048x1536", "4096x3072"
        ];

        return new MenuNode
        {
            Header = "Canvas Size",
            FontAwesomeIcon = "fa-solid fa-display",
            Children = sizes.Select(s => new MenuNode
            {
                Header = s,
                Command = vm.SetCanvasSizeCommand,
                CommandParameter = s,
                Toggle = MenuToggle.CheckBox,
                IsCheckedFn = v => v.CanvasSize == s
            }).ToArray()
        };
    }

    private static MenuNode BuildDisplayRatioMenu(MainWindowViewModel vm)
    {
        (string label, string value)[] ratios =
        [
            ("0.75 (ANSI X3.110 Spec)",   "0.75"),
            ("0.78 (Byte Magazine 1983)", "0.78"),
            ("0.80 (PP3/Prodigy)",        "0.80")
        ];

        return new MenuNode
        {
            Header = "Display Ratio",
            FontAwesomeIcon = "fa-solid fa-tv",
            Children = ratios.Select(r => new MenuNode
            {
                Header = r.label,
                Command = vm.SetDisplayRatioCommand,
                CommandParameter = r.value,
                Toggle = MenuToggle.Radio,
                IsCheckedFn = v => v.DisplayRatio == r.value
            }).ToArray()
        };
    }

    private static MenuNode BuildStretchMenu(MainWindowViewModel vm)
    {
        (string label, Stretch value)[] modes =
        [
            ("None",            Stretch.None),
            ("Fill",            Stretch.Fill),
            ("Uniform",         Stretch.Uniform),
            ("Uniform To Fill", Stretch.UniformToFill)
        ];

        return new MenuNode
        {
            Header = "Stretch",
            FontAwesomeIcon = "fa-solid fa-maximize",
            Children = modes.Select(m => new MenuNode
            {
                Header = m.label,
                Command = vm.SetStretchModeCommand,
                CommandParameter = m.value.ToString(),
                Toggle = MenuToggle.Radio,
                IsCheckedFn = v => v.ImageStretch == m.value
            }).ToArray()
        };
    }
}
