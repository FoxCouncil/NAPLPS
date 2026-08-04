// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSApp.ViewModels;

public partial class SequenceWindowViewModel : ViewModelBase
{
    [ObservableProperty]
    private ObservableCollection<CommandInfo> commands = [];

    [ObservableProperty]
    private NaplpsSequence? currentSequence;

    [ObservableProperty]
    private NaplpsState? state;

    [ObservableProperty]
    private CommandInfo? selectedCommand;

    [ObservableProperty]
    private SolidColorBrush? colorBackground;

    [ObservableProperty]
    private SolidColorBrush? colorForeground;

    [ObservableProperty]
    private SolidColorBrush? colorBackgroundText;

    [ObservableProperty]
    private SolidColorBrush? colorForegroundText;

    [ObservableProperty]
    private int selectedIndex;

    [ObservableProperty]
    private int currentFrame;

    [ObservableProperty]
    private int totalFrames;

    [ObservableProperty]
    private string detailPaneIcon = "fa-solid fa-rectangle-list";

    [ObservableProperty]
    private bool isDetailPaneVisible = true;

    [ObservableProperty]
    private string timelineSyncIcon = "fa-solid padlock-open";

    [ObservableProperty]
    private bool isTimelineSyncLocked = false;

    [ObservableProperty]
    private bool isColorInfoVisible = false;

    [ObservableProperty]
    private bool isPlotviewVisible = true;

    [ObservableProperty]
    private string bitWidth = "7-Bit";

    [ObservableProperty]
    private string operandsDetails = string.Empty;

    [ObservableProperty]
    private string extraDetails = string.Empty;

    [ObservableProperty]
    private string searchText = string.Empty;

    /// <summary>Toggle between raw hex operand display and decoded human-readable form.</summary>
    [ObservableProperty]
    private bool isDecodedFormVisible = false;

    /// <summary>
    /// Set of command indices the user has bookmarked. F2 toggles, NextBookmark / PreviousBookmark
    /// cycle through. Lives only as long as this ViewModel \u2014 not persisted across sessions yet.
    /// </summary>
    public HashSet<int> Bookmarks { get; } = [];

    private readonly AvaPlot avaPlot;

    private readonly DrawContext context;

    private readonly UndoManager? undoManager;

    private NaplpsFormat loadedFile => context.NAPLPS;

    private Window? addCommandWindow;

    public event EventHandler<int>? FrameChanged;

    public SequenceWindowViewModel()
    {
        this.context = new DrawContext();
        this.avaPlot = new AvaPlot();
    }

    public SequenceWindowViewModel(DrawContext context, AvaPlot avaPlot, UndoManager? undoManager = null)
    {
        this.context = context;
        this.avaPlot = avaPlot;
        this.undoManager = undoManager;

        this.avaPlot.Plot.Axes.SetLimits(0, 1, 0, 1);

        TotalFrames = (int)context.TotalFrames + 1;

        bitWidth = loadedFile.Is7Bit ? "7-Bit" : "8-Bit";

        LoadCommands();

        SelectedIndex = (int)context.CurrentIndex;

        SelectionChanged();
    }

    public void SyncToCurrentFrame()
    {
        if (IsTimelineSyncLocked)
        {
            SelectedIndex = (int)context.CurrentIndex;
        }
    }

    [RelayCommand]
    private void SelectionChanged()
    {
        if (SelectedIndex >= loadedFile.Commands.Count)
        {
            SelectedIndex = loadedFile.Commands.Count - 1;
        }
        else if (SelectedIndex < 0)
        {
            SelectedIndex = 0;
        }

        if (IsTimelineSyncLocked)
        {
            FrameChanged?.Invoke(this, SelectedIndex);
        }

        CurrentFrame = SelectedIndex + 1;

        CurrentSequence = loadedFile.Commands[SelectedIndex];

        var command = CurrentSequence.Command;
        // Get the state AFTER this command by looking at the next sequence's state
        // (the current sequence's state is from BEFORE the command ran)
        var state = SelectedIndex + 1 < loadedFile.Commands.Count
            ? loadedFile.Commands[SelectedIndex + 1].State
            : CurrentSequence.State;

        OperandsDetails = command.Operands.Any() ? string.Join(" ", command.Operands.Select(operand => $"{operand:X2}")) + " " : string.Empty;
        ExtraDetails = string.Empty;
        IsColorInfoVisible = false;

        avaPlot.Plot.Clear();

        if (command is ShiftInCommand shiftInCommand)
        {
            ExtraDetails = "FIX ME"; // shiftInCommand.Text;
        }
        else if (command is EscCommand escCommand)
        {
            ExtraDetails = DecodeEscSequence(escCommand);
        }
        else if (command is SelectColorCommand selectColorCommand)
        {
            IsColorInfoVisible = true;

            var str = $" ColorMode: {state.ColorMode}\n";
            ExtraDetails += $"Foreground: {state.ColorMapForeground} | {state.ColorMap[state.ColorMapForeground]}\n";
            ExtraDetails += $"Background: {state.ColorMapBackground} | {state.ColorMap[state.ColorMapBackground]}\n\n";

            var colorBg = state.ColorMap[state.ColorMapBackground].ToColor();

            ColorBackground = new SolidColorBrush(Color.FromArgb(colorBg.A, colorBg.R, colorBg.G, colorBg.B));
            ColorBackgroundText = new SolidColorBrush(GetContrastingColor(colorBg));

            var colorFg = state.ColorMap[state.ColorMapForeground].ToColor();

            ColorForeground = new SolidColorBrush(Color.FromArgb(colorFg.A, colorFg.R, colorFg.G, colorFg.B));
            ColorForegroundText = new SolidColorBrush(GetContrastingColor(colorFg));
        }
        else if (command is TextCommand textCommand)
        {
            ExtraDetails = $"Text Field Size: {textCommand.State.CharSize}";

            var point = new Coordinates(state.Pen.X, state.Pen.Y);
            var size = new Coordinates(state.Pen.X + state.CharSize.X, state.Pen.Y + state.CharSize.Y);

            avaPlot.Plot.Add.Rectangle(
                point.X,
                point.Y,
                point.X + state.CharSize.X,
                point.Y + state.CharSize.Y
            );

            avaPlot.Plot.Add.Marker(point, color: ScottPlot.Color.Gray(0));
            avaPlot.Plot.Add.Marker(size);
        }
        else if (command is DomainCommand domainCommand)
        {
            ExtraDetails = $" Multibyte: {state.MultiByteValue}\n";
            ExtraDetails += $"Singlebyte: {state.SingleByteValue}";
        }
        else if (command is TextureCommand textureCommand)
        {
            ExtraDetails = $"   LINE: {textureCommand.LineTexture}\n";
            ExtraDetails += $"HIGHLGT: {textureCommand.ShouldHighlight}\n";
            ExtraDetails += $"TEXTURE: {textureCommand.TexturePattern}\n\n";
            ExtraDetails += $"MASK SZ: {textureCommand.MaskSize}";
        }
        else if (command is IncrementalFieldCommand incrementalFieldCommand)
        {
            ExtraDetails = $"Origin Size: {incrementalFieldCommand.Origin}\n";
            ExtraDetails += $" Field Size: {incrementalFieldCommand.Dimensions}";

            avaPlot.Plot.Add.Marker(new Coordinates(state.Pen.X, state.Pen.Y), color: ScottPlot.Color.Gray(0));

            var point = new Coordinates(incrementalFieldCommand.Origin.X, incrementalFieldCommand.Origin.Y);
            var size = new Coordinates(incrementalFieldCommand.Dimensions.X, incrementalFieldCommand.Dimensions.Y);

            avaPlot.Plot.Add.Rectangle(
                point.X,
                point.Y,
                point.X + size.X,
                point.Y + size.Y);

            avaPlot.Plot.Add.Marker(point);
            avaPlot.Plot.Add.Marker(size);
        }
        else if (command is ControlCommand controlCommand &&
                 (controlCommand.Command == NaplpsControlCommands.Repeat ||
                  controlCommand.Command == NaplpsControlCommands.RepeatToEOL))
        {
            if (controlCommand.Command == NaplpsControlCommands.Repeat && command.Operands.Count > 0)
            {
                var countByte = command.Operands[0];
                var repeatCount = countByte >= 0xC0 ? countByte - 0x40 : countByte;
                ExtraDetails = $"Repeat Count: {repeatCount} (0x{countByte:X2})";
            }
            else if (controlCommand.Command == NaplpsControlCommands.RepeatToEOL)
            {
                var fieldEndX = state.Field.Origin.X + state.Field.Dimensions.X;
                var charsToEnd = Math.Max(0, (int)((fieldEndX - state.Pen.X) / state.CharSize.X));
                ExtraDetails = $"Repeat to EOL: ~{charsToEnd} chars";
            }
        }
        else if (command is WaitCommand waitCommand)
        {
            ExtraDetails = $"  Wait Time: {waitCommand.WaitTime} ({waitCommand.WaitTime * 100}ms)\n";
            ExtraDetails += $"    IsValid: {waitCommand.IsValid}\n";

            if (waitCommand.WaitTimes.Count > 0)
            {
                ExtraDetails += $"Extra Waits: {string.Join(", ", waitCommand.WaitTimes.Select(w => $"{w} ({w * 100}ms)"))}";
            }
        }
        else if (command is SetColorCommand setColorCommand)
        {
            IsColorInfoVisible = true;

            var entry = state.ColorMapForeground;
            ExtraDetails = $"     Entry: [{entry}]\n";
            ExtraDetails += $" New Color: R={setColorCommand.Color.Red} G={setColorCommand.Color.Green} B={setColorCommand.Color.Blue}\n";
            ExtraDetails += $"ColorMode: {state.ColorMode}\n";

            var newColor = setColorCommand.Color.ToColor();

            ColorForeground = new SolidColorBrush(Color.FromArgb(newColor.A, newColor.R, newColor.G, newColor.B));
            ColorForegroundText = new SolidColorBrush(GetContrastingColor(newColor));

            if (state.ColorMap.TryGetValue(entry, out var prevNaplpsColor))
            {
                var prevColor = prevNaplpsColor.ToColor();
                ExtraDetails += $" Old Color: R={prevNaplpsColor.Red} G={prevNaplpsColor.Green} B={prevNaplpsColor.Blue}\n";
                ColorBackground = new SolidColorBrush(Color.FromArgb(prevColor.A, prevColor.R, prevColor.G, prevColor.B));
                ColorBackgroundText = new SolidColorBrush(GetContrastingColor(prevColor));
            }
        }
        else if (command is AsciiCharCommand asciiCharCommand)
        {
            var ch = asciiCharCommand.AsciiCharacter;
            ExtraDetails = $"Character: '{ch}' (0x{asciiCharCommand.OpCode:X2})\n";
            ExtraDetails += $" Discarded: {asciiCharCommand.IsDiscarded}\n";
            ExtraDetails += $"NonSpacing: {asciiCharCommand.IsNonSpacing}\n";
            ExtraDetails += $"      Pen: ({state.Pen.X:F4}, {state.Pen.Y:F4})\n";
            ExtraDetails += $" CharSize: ({state.CharSize.X:F4}, {state.CharSize.Y:F4})\n";
            ExtraDetails += $" TextPath: {state.TextPath}";
        }
        else if (command is ControlCommand ctrlCmd &&
                 ctrlCmd.Command != NaplpsControlCommands.Repeat &&
                 ctrlCmd.Command != NaplpsControlCommands.RepeatToEOL)
        {
            ExtraDetails = $"Command: {ctrlCmd.Command}\n";

            switch (ctrlCmd.Command)
            {
                case NaplpsControlCommands.ClearScreen:
                {
                    ExtraDetails += "Clears the screen to nominal black";
                }
                break;

                case NaplpsControlCommands.ActivePositionDown:
                {
                    ExtraDetails += $"Scroll Mode: {state.IsScrollMode}\n";
                    ExtraDetails += $"   Scroll ▼: {state.ScrollEventOccurred}";
                }
                break;

                case NaplpsControlCommands.ActivePositionReturn:
                {
                    ExtraDetails += $"Pen: ({state.Pen.X:F4}, {state.Pen.Y:F4})";
                }
                break;

                case NaplpsControlCommands.ScrollOn:
                {
                    ExtraDetails += "Enables scroll mode";
                }
                break;

                case NaplpsControlCommands.ScrollOff:
                {
                    ExtraDetails += "Disables scroll mode";
                }
                break;
            }
        }
        else if (command is ResetCommand)
        {
            ExtraDetails = "Resets NAPLPS state to defaults\n";
            ExtraDetails += $"ColorMode: {state.ColorMode}\n";
            ExtraDetails += $"  Texture: {state.Texture.TexturePattern}\n";
            ExtraDetails += $"      Pen: ({state.Pen.X:F4}, {state.Pen.Y:F4})";
        }
        else if (command is GeometricDrawingCommandBase baseDrawCommand)
        {
            ExtraDetails = "Draw Point(s):\n";

            var coords = new List<Coordinates>();

            foreach (var point in baseDrawCommand.Points)
            {
                ExtraDetails += $"{point}\n";

                coords.Add(new Coordinates(point.X, point.Y));
            }

            ExtraDetails += "Vertice(s):\n";

            foreach (var vert in baseDrawCommand.Vertices)
            {
                ExtraDetails += $"{vert}\n";
            }

            avaPlot.Plot.Clear();

            if (baseDrawCommand is PolygonCommand)
            {
                avaPlot.Plot.Add.Polygon([.. coords]);
            }
            //else (baseDrawCommand is LineCommand) 
            //{
            //    _plot.Plot.Add.Line()
            //}
            else if (baseDrawCommand is RectangleCommand rectangleCmd)
            {
                var point = new Coordinates(state.Pen.X, state.Pen.Y);
                var size = new Coordinates(rectangleCmd.Dimensions.X, rectangleCmd.Dimensions.Y);

                avaPlot.Plot.Clear();
                avaPlot.Plot.Add.Rectangle(
                    point.X,
                    point.Y,
                    point.X + size.X,
                    point.Y + size.Y
                );

                avaPlot.Plot.Add.Marker(point);
                avaPlot.Plot.Add.Marker(size);
            }

            var markers = avaPlot.Plot.Add.Markers(coords);

            markers.MarkerShape = MarkerShape.FilledCircle;
            markers.MarkerSize = 10;
        }

        IsPlotviewVisible = avaPlot.Plot.PlottableList.Count != 0;
    }

    [RelayCommand]
    private void FramePrevious()
    {
        CurrentFrame--;

        if (CurrentFrame < 1)
        {
            CurrentFrame = TotalFrames;
        }

        SelectedIndex = CurrentFrame - 1;
    }

    [RelayCommand]
    private void FrameNext()
    {
        CurrentFrame++;

        if (CurrentFrame > TotalFrames)
        {
            CurrentFrame = 1;
        }

        SelectedIndex = CurrentFrame - 1;
    }

    [RelayCommand]
    private void Add(Window parent)
    {
        if (addCommandWindow == null)
        {
            addCommandWindow = new AddCommandWindow();

            addCommandWindow.Closed += (s, e) => addCommandWindow = null;
            addCommandWindow.ShowDialog(parent);
        }
        else
        {
            addCommandWindow.Close();
            addCommandWindow = null;
        }
    }

    [RelayCommand]
    private async Task Delete(Window parent)
    {
        if (SelectedCommand == null)
        {
            return;
        }

        if (!await Program.ShowQuestionDialogBox(
            parent,
            "Delete Command " + SelectedCommand.ToString(),
            "Are you sure you want to delete this command?\n\n" + SelectedCommand.ToString())
        )
        {
            return;
        }

        var index = SelectedCommand.Index - 1;

        if (index < 0 || index >= loadedFile.Commands.Count)
        {
            return;
        }

        // Route through the shared UndoManager when available so this delete is undoable
        // alongside MainWindow tool actions. Falls back to direct removal only if no
        // UndoManager was passed (legacy parameterless ctor path).
        if (undoManager != null)
        {
            undoManager.Execute(new RemoveCommandsAction(loadedFile, index), loadedFile);
        }
        else
        {
            loadedFile.Commands.RemoveAt(index);
        }

        LoadCommands();

        if (CurrentFrame > TotalFrames)
        {
            CurrentFrame = TotalFrames;
        }

        SelectedIndex = CurrentFrame - 1;
    }

    /// <summary>
    /// Open the operand editor for the selected command. When the user commits, replace
    /// the operands via CompositeAction(Remove + InsertAt) so the change is undoable and
    /// the command gets re-instantiated with the new bytes.
    /// </summary>
    [RelayCommand]
    private async Task EditOperands(Window parent)
    {
        if (!TryGetSelectedIndex(out var index))
        {
            return;
        }

        var src = loadedFile.Commands[index].Command;
        var newOperands = await OperandEditWindow.PromptAsync(parent, src.OpCode, new NaplpsOperands(src.Operands));

        if (newOperands == null)
        {
            return;
        }

        // Swap the command: remove the old one and insert a fresh instance with new operands.
        var remove = new RemoveCommandsAction(loadedFile, index);
        var insert = new InsertAtAction(index, [(src.OpCode, newOperands)]);
        var composite = CompositeAction.Compose(remove, insert);

        if (undoManager != null)
        {
            undoManager.Execute(composite, loadedFile);
        }
        else
        {
            composite.Execute(loadedFile);
        }

        LoadCommands();
        SelectedIndex = index;
    }

    /// <summary>
    /// Move the selected command one position toward the start of the sequence.
    /// Composes a RemoveCommandsAction + InsertAtAction so it's a single undo step.
    /// </summary>
    [RelayCommand]
    private void MoveUp()
    {
        if (!TryGetSelectedIndex(out var index) || index <= 0)
        {
            return;
        }

        ExecuteMoveAction(index, index - 1);
        SelectedIndex = index - 1;
    }

    /// <summary>Move the selected command one position toward the end.</summary>
    [RelayCommand]
    private void MoveDown()
    {
        if (!TryGetSelectedIndex(out var index) || index >= loadedFile.Commands.Count - 1)
        {
            return;
        }

        ExecuteMoveAction(index, index + 1);
        SelectedIndex = index + 1;
    }

    /// <summary>Duplicate the selected command immediately after itself.</summary>
    [RelayCommand]
    private void DuplicateSelected()
    {
        if (!TryGetSelectedIndex(out var index))
        {
            return;
        }

        var src = loadedFile.Commands[index].Command;
        var clone = (src.OpCode, new NaplpsOperands(src.Operands));

        var action = new InsertAtAction(index + 1, [clone]);

        if (undoManager != null)
        {
            undoManager.Execute(action, loadedFile);
        }
        else
        {
            action.Execute(loadedFile);
        }

        LoadCommands();
        SelectedIndex = index + 1;
    }

    /// <summary>Copy the selected command (or all bookmarked commands) to the clipboard.</summary>
    [RelayCommand]
    private void Copy()
    {
        var indices = GetActionTargetIndices();

        if (indices.Count == 0)
        {
            return;
        }

        CommandClipboard.Copy(indices.Select(i => loadedFile.Commands[i]));
    }

    /// <summary>Copy + delete the selected command in one undoable composite action.</summary>
    [RelayCommand]
    private void Cut()
    {
        var indices = GetActionTargetIndices();

        if (indices.Count == 0)
        {
            return;
        }

        CommandClipboard.Copy(indices.Select(i => loadedFile.Commands[i]));

        // Delete in descending order so each remove sees stable indices.
        var toRemove = indices.OrderByDescending(i => i).ToList();

        if (undoManager != null)
        {
            var actions = toRemove.Select(i => (IEditorAction)new RemoveCommandsAction(loadedFile, i)).ToArray();
            undoManager.Execute(CompositeAction.Compose(actions), loadedFile);
        }
        else
        {
            foreach (var i in toRemove)
            {
                loadedFile.Commands.RemoveAt(i);
            }
        }

        LoadCommands();
    }

    /// <summary>Paste clipboard contents immediately after the selected command (or at end if none).</summary>
    [RelayCommand]
    private void Paste()
    {
        if (!CommandClipboard.HasContent)
        {
            return;
        }

        int insertAt = TryGetSelectedIndex(out var index)
            ? index + 1
            : loadedFile.Commands.Count;

        var action = CommandClipboard.BuildPasteAction(insertAt);

        if (action == null)
        {
            return;
        }

        if (undoManager != null)
        {
            undoManager.Execute(action, loadedFile);
        }
        else
        {
            action.Execute(loadedFile);
        }

        LoadCommands();
        SelectedIndex = insertAt;
    }

    /// <summary>Toggle a bookmark on the selected command.</summary>
    [RelayCommand]
    private void ToggleBookmark()
    {
        if (!TryGetSelectedIndex(out var index))
        {
            return;
        }

        if (!Bookmarks.Add(index))
        {
            Bookmarks.Remove(index);
        }

        OnPropertyChanged(nameof(Bookmarks));
    }

    /// <summary>Jump to the next bookmark after the selected index, wrapping around.</summary>
    [RelayCommand]
    private void NextBookmark()
    {
        if (Bookmarks.Count == 0)
        {
            return;
        }

        var next = Bookmarks.Where(b => b > SelectedIndex).DefaultIfEmpty(Bookmarks.Min()).Min();
        SelectedIndex = next;
    }

    /// <summary>Jump to the previous bookmark before the selected index, wrapping around.</summary>
    [RelayCommand]
    private void PreviousBookmark()
    {
        if (Bookmarks.Count == 0)
        {
            return;
        }

        var prev = Bookmarks.Where(b => b < SelectedIndex).DefaultIfEmpty(Bookmarks.Max()).Max();
        SelectedIndex = prev;
    }

    /// <summary>Clear all bookmarks.</summary>
    [RelayCommand]
    private void ClearBookmarks()
    {
        Bookmarks.Clear();
        OnPropertyChanged(nameof(Bookmarks));
    }

    private bool TryGetSelectedIndex(out int index)
    {
        if (SelectedCommand == null)
        {
            index = -1;
            return false;
        }

        index = SelectedCommand.Index - 1;

        if (index < 0 || index >= loadedFile.Commands.Count)
        {
            index = -1;
            return false;
        }

        return true;
    }

    private List<int> GetActionTargetIndices()
    {
        // For now, single-selection only. Multi-select via canvas / shift-click will route
        // through SelectedIndices once the SelectTool change in Phase 3.4 lands.
        return TryGetSelectedIndex(out var i) ? [i] : [];
    }

    private void ExecuteMoveAction(int from, int to)
    {
        if (from == to)
        {
            return;
        }

        var src = loadedFile.Commands[from].Command;
        var snapshot = (src.OpCode, new NaplpsOperands(src.Operands));

        var remove = new RemoveCommandsAction(loadedFile, from);
        var insert = new InsertAtAction(to, [snapshot]);
        var composite = CompositeAction.Compose(remove, insert);

        if (undoManager != null)
        {
            undoManager.Execute(composite, loadedFile);
        }
        else
        {
            composite.Execute(loadedFile);
        }

        LoadCommands();
    }

    [RelayCommand]
    private void ToggleTimelineSyncLock()
    {
        IsTimelineSyncLocked = !IsTimelineSyncLocked;
        TimelineSyncIcon = IsTimelineSyncLocked ? "fa-solid padlock" : "fa-solid padlock-open";
    }

    [RelayCommand]
    private void ToggleDetailPane()
    {
        IsDetailPaneVisible = !IsDetailPaneVisible;
        DetailPaneIcon = IsDetailPaneVisible ? "fa-solid fa-rectangle-list" : "fa-regular fa-rectangle-list";
    }

    private void LoadCommands()
    {
        Commands.Clear();

        var index = 0;
        var filter = SearchText?.Trim() ?? string.Empty;

        foreach (var sequence in loadedFile.Commands)
        {
            ++index;

            var opcodeHex = sequence.Command.OpCode.ToString("X2");
            var commandType = sequence.Command.ToString().Replace("Command", string.Empty);

            // Filter by hex opcode, command type name, or DSL keyword from the registry.
            if (filter.Length > 0)
            {
                var keyword = CommandRegistry.GetByType(sequence.Command.GetType())?.DslKeyword ?? string.Empty;

                if (!opcodeHex.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    && !commandType.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    && !keyword.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            Commands.Add(new CommandInfo
            {
                Index = index,
                OpCode = opcodeHex,
                CommandType = commandType,
                State = sequence.State.ToString(),
            });
        }
    }

    /// <summary>
    /// Generated by CommunityToolkit.Mvvm when SearchText changes.
    /// Re-runs the LoadCommands filter so the grid reflects the new query.
    /// </summary>
    partial void OnSearchTextChanged(string value)
    {
        LoadCommands();
    }

    private static string DecodeEscSequence(EscCommand escCommand)
    {
        var ops = escCommand.Operands;

        if (ops.Count == 0)
        {
            return "ESC (no operands)";
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Raw: ESC {string.Join(" ", ops.Select(b => $"0x{b:X2}"))}");
        sb.AppendLine();

        var firstByte = ops[0];

        // ISO 2022 intermediate bytes (0x20-0x2F) + final byte (0x30-0x7E)
        if (firstByte >= 0x20 && firstByte <= 0x2F)
        {
            var designation = firstByte switch
            {
                0x21 => "Designate C0 control set",
                0x22 => "Designate C1 control set",
                0x23 => "Designate single-shift 2",
                0x24 => "Designate multi-byte character set",
                0x25 => "Designate other coding system",
                0x28 => "Designate G0 character set",
                0x29 => "Designate G1 character set",
                0x2A => "Designate G2 character set",
                0x2B => "Designate G3 character set",
                0x2D => "Designate G1 (96-char) set",
                0x2E => "Designate G2 (96-char) set",
                0x2F => "Designate G3 (96-char) set",
                _ => $"Intermediate byte"
            };

            sb.AppendLine($"Type: {designation}");

            if (ops.Count > 1)
            {
                var finalByte = ops[1];
                var setName = DecodeCharacterSetFinal(firstByte, finalByte);
                sb.AppendLine($" Set: 0x{finalByte:X2} → {setName}");
            }
        }
        // 7-bit C1 control codes (ESC 0x40-0x5F → equivalent to 0x80-0x9F)
        else if (firstByte >= 0x40 && firstByte <= 0x5F)
        {
            var c1Code = (byte)(firstByte + 0x40);
            var c1Name = c1Code switch
            {
                0x80 => "Padding Character (PAD)",
                0x81 => "High Octet Preset (HOP)",
                0x82 => "Break Permitted Here (BPH)",
                0x83 => "No Break Here (NBH)",
                0x84 => "Index (IND)",
                0x85 => "Next Line (NEL)",
                0x86 => "Start of Selected Area (SSA)",
                0x87 => "End of Selected Area (ESA)",
                0x88 => "Horizontal Tab Set (HTS)",
                0x89 => "Horizontal Tab with Justification (HTJ)",
                0x8A => "Vertical Tab Set (VTS)",
                0x8B => "Partial Line Down (PLD)",
                0x8C => "Partial Line Up (PLU)",
                0x8D => "Reverse Index (RI)",
                0x8E => "Single Shift 2 (SS2)",
                0x8F => "Single Shift 3 (SS3)",
                0x90 => "Device Control String (DCS)",
                0x91 => "Private Use 1 (PU1)",
                0x92 => "Private Use 2 (PU2)",
                0x93 => "Set Transmit State (STS)",
                0x94 => "Cancel Character (CCH)",
                0x95 => "Message Waiting (MW)",
                0x96 => "Start of Guarded Area (SPA)",
                0x97 => "End of Guarded Area (EPA)",
                0x98 => "Start of String (SOS)",
                0x99 => "Single Graphic Char Introducer (SGCI)",
                0x9A => "Single Character Introducer (SCI)",
                0x9B => "Control Sequence Introducer (CSI)",
                0x9C => "String Terminator (ST)",
                0x9D => "Operating System Command (OSC)",
                0x9E => "Privacy Message (PM)",
                0x9F => "Application Program Command (APC)",
                _ => $"Unknown C1 (0x{c1Code:X2})"
            };

            sb.AppendLine($"  C1: ESC {(char)firstByte} → 0x{c1Code:X2}");
            sb.AppendLine($"Name: {c1Name}");

            // NAPLPS-specific C1 mappings
            if (firstByte == 0x45)
            {
                sb.AppendLine("\nNAPLPS: Next Line (moves to start of next line)");
            }
            else if (firstByte == 0x4E)
            {
                sb.AppendLine("\nNAPLPS: SS2 → invoke G2 for next character");
            }
            else if (firstByte == 0x4F)
            {
                sb.AppendLine("\nNAPLPS: SS3 → invoke G3 for next character");
            }
            else if (firstByte == 0x57)
            {
                sb.AppendLine("\nNAPLPS: Scroll On (EPA equivalent)");
            }
        }
        else
        {
            sb.AppendLine($"Byte: 0x{firstByte:X2} ({firstByte}/{firstByte >> 4}:{firstByte & 0xF})");
        }

        // Show any additional operand bytes
        if (ops.Count > 2)
        {
            sb.AppendLine();
            sb.Append("Extra: ");

            foreach (var b in ops.Skip(2))
            {
                sb.Append($"0x{b:X2} ");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string DecodeCharacterSetFinal(byte intermediate, byte finalByte)
    {
        // NAPLPS character set designations (ANSI X3.110)
        if (intermediate == 0x28 || intermediate == 0x29 || intermediate == 0x2A || intermediate == 0x2B)
        {
            return finalByte switch
            {
                0x40 => "NAPLPS Primary Character Set",
                0x42 => "ASCII (ISO 646)",
                0x43 => "NAPLPS PDI (Picture Description Instructions)",
                0x44 => "NAPLPS Supplementary Set",
                0x45 => "NAPLPS Mosaic Set",
                0x46 => "NAPLPS DRCS (Dynamically Redefined Character Set)",
                0x47 => "NAPLPS Macro Set",
                _ => $"Set 0x{finalByte:X2} (F={finalByte - 0x30})"
            };
        }

        if (intermediate == 0x21)
        {
            return finalByte switch
            {
                0x40 => "NAPLPS C0 Control Set",
                0x42 => "Default C0 Set",
                _ => $"C0 Set 0x{finalByte:X2}"
            };
        }

        if (intermediate == 0x22)
        {
            return finalByte switch
            {
                0x40 => "NAPLPS C1 Control Set",
                0x41 => "NAPLPS PDI C1 Set",
                _ => $"C1 Set 0x{finalByte:X2}"
            };
        }

        return $"Final 0x{finalByte:X2}";
    }

    private static Color GetContrastingColor(System.Drawing.Color backgroundColor)
    {
        // Convert RGB to YIQ (a color space used for broadcast color television).
        double yiq = ((backgroundColor.R * 299) + (backgroundColor.G * 587) + (backgroundColor.B * 114)) / 1000;

        // Determine whether the background color is light or dark.
        // YIQ value greater than 128 means it's a light color, so return dark color (black), otherwise return light color (white).
        return yiq >= 128 ? Colors.Black : Colors.White;
    }
}


