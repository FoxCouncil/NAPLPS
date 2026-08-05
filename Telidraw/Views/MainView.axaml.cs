// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using Avalonia.Input;
using Avalonia.Interactivity;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using Telidraw.Editor;
using Telidraw.Editor.Tools;
using Telidraw.Resources;
using Telidraw.Services;
using Telidraw.ViewModels.Menus;
using Telidraw.Views.Menus;

namespace Telidraw.Views;

/// <summary>
/// The whole editor as a lifetime-agnostic view: on desktop MainWindow wraps it, under a
/// single-view lifetime (browser, mobile) it IS the application. Keyboard dispatch attaches
/// to whatever TopLevel it lands in, and child windows go through <see cref="Shell"/>.
/// </summary>
public partial class MainView : UserControl
{
    private TextEditor? _telidrawEditor;
    private bool _suppressEditorTextSync;
    private bool _menusBuilt;
    private TopLevel? _attachedTopLevel;

    public MainView()
    {
        InitializeComponent();
        InitializeTelidrawEditor();

        Focusable = true;

        DataContextChanged += (_, _) => BuildMenusFromViewModel();
        BuildMenusFromViewModel();
    }

    /// <summary>
    /// Populates the in-window <see cref="Menu"/> from the <see cref="MenuTreeBuilder"/>
    /// tree. On macOS the in-window menu hides instead - MainWindow builds the
    /// <see cref="NativeMenu"/> from the same tree. Re-runs on DataContext assignment;
    /// idempotent thereafter.
    /// </summary>
    private void BuildMenusFromViewModel()
    {
        if (_menusBuilt || DataContext is not MainWindowViewModel vm) { return; }

        if (OperatingSystem.IsMacOS())
        {
            if (InWindowMenu is not null)
            {
                InWindowMenu.IsVisible = false;
            }
        }
        else if (InWindowMenu is not null)
        {
            var tree = MenuTreeBuilder.Build(vm, PlatformGestureSet.Current);
            MenuRenderer.PopulateInWindowMenu(InWindowMenu, vm, tree);
        }

        _menusBuilt = true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Register this view as the app's shell surface: pickers, launcher, clipboard and
        // message boxes resolve against this TopLevel; single-view child windows go into
        // the overlay layer.
        _attachedTopLevel = TopLevel.GetTopLevel(this);
        Shell.TopLevel = _attachedTopLevel;
        Shell.Overlay = WindowOverlay;

        // Keyboard dispatch lives on the TopLevel so it works whether this view sits in a
        // desktop Window or is itself the application root, and regardless of what has
        // focus. KeyBindings on the UserControl only see routes through focused children.
        if (_attachedTopLevel is not null)
        {
            _attachedTopLevel.AddHandler(KeyDownEvent, OnTopLevelKeyDown, RoutingStrategies.Bubble);
            _attachedTopLevel.AddHandler(TextInputEvent, OnTopLevelTextInput, RoutingStrategies.Bubble);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (_attachedTopLevel is not null)
        {
            _attachedTopLevel.RemoveHandler(KeyDownEvent, OnTopLevelKeyDown);
            _attachedTopLevel.RemoveHandler(TextInputEvent, OnTopLevelTextInput);
            _attachedTopLevel = null;
        }
    }

    /// <summary>
    /// Wire up AvaloniaEdit for the Telidraw text pane: load the syntax-highlighting
    /// definition from embedded XSHD, hook two-way text binding to ViewModel.TelidrawSource
    /// (with reentrancy guard), and register an F5 recompile keybind.
    /// </summary>
    private void InitializeTelidrawEditor()
    {
        _telidrawEditor = this.FindControl<TextEditor>("TelidrawEditor");
        if (_telidrawEditor == null) { return; }

        // Load Telidraw syntax highlighting from the embedded XSHD resource.
        var asm = typeof(MainView).Assembly;
        using (var stream = asm.GetManifestResourceStream("Telidraw.Assets.TelidrawHighlighting.xshd"))
        {
            if (stream != null)
            {
                using var reader = new System.Xml.XmlTextReader(stream);
                _telidrawEditor.SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            }
        }

        // Two-way bridge between editor.Text and VM.TelidrawSource. AvaloniaEdit's
        // TextEditor.Text isn't a normal AvaloniaProperty, so we wire it manually.
        _telidrawEditor.TextChanged += (_, _) =>
        {
            if (_suppressEditorTextSync) { return; }
            if (DataContext is MainWindowViewModel vm)
            {
                vm.TelidrawSource = _telidrawEditor.Text;
            }
        };

        DataContextChanged += (_, _) => HookViewModelForEditor();
        HookViewModelForEditor();
    }

    private void HookViewModelForEditor()
    {
        if (DataContext is not MainWindowViewModel vm || _telidrawEditor == null) { return; }

        // Seed editor with current source.
        _suppressEditorTextSync = true;
        _telidrawEditor.Text = vm.TelidrawSource ?? string.Empty;
        _suppressEditorTextSync = false;

        // Push VM-side changes (e.g. file load → decompile) back into the editor.
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainWindowViewModel.TelidrawSource) && _telidrawEditor != null)
            {
                if (_telidrawEditor.Text != vm.TelidrawSource)
                {
                    _suppressEditorTextSync = true;
                    _telidrawEditor.Text = vm.TelidrawSource ?? string.Empty;
                    _suppressEditorTextSync = false;
                }
            }
        };
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        // Give the view initial focus so plain-key shortcuts route through it before the
        // user has clicked anything - matters most in the browser where nothing else grabs
        // focus at startup.
        Focus();

        var overlay = this.FindControl<Canvas>("EditorOverlay");

        if (overlay != null)
        {
            overlay.PointerPressed += OnEditorPointerPressed;
            overlay.PointerMoved += OnEditorPointerMoved;
            overlay.PointerReleased += OnEditorPointerReleased;
        }

        // Ctrl+scroll zooms the canvas toward the cursor. Attached to the clipping cell so it
        // fires anywhere over the canvas area regardless of which layer is hit-tested.
        var canvasCell = this.FindControl<Grid>("CanvasCell");
        if (canvasCell != null)
        {
            canvasCell.PointerWheelChanged += OnCanvasWheel;
        }

        // Listen for grid visibility changes
        if (DataContext is MainWindowViewModel vm)
        {
            vm.GridSettings.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(Editor.GridSettings.IsVisible) or nameof(Editor.GridSettings.SpacingX) or nameof(Editor.GridSettings.SpacingY))
                {
                    var ov = this.FindControl<Canvas>("EditorOverlay");

                    if (ov != null)
                    {
                        RenderGrid(vm, ov);
                    }
                }
            };

            // Re-render grid when the canvas is resized so lines track the layout.
            if (overlay != null)
            {
                overlay.SizeChanged += (_, _) => RenderGrid(vm, overlay);
                // Initial paint.
                RenderGrid(vm, overlay);
            }

            // Re-render the selection outline whenever EditorPreview changes — including
            // programmatic changes (e.g. sequence-panel click → SelectedCommandIndex change).
            // Without this, the outline only updates on the pointer-event path.
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainWindowViewModel.EditorPreview))
                {
                    var ov = this.FindControl<Canvas>("EditorOverlay");
                    if (ov == null) { return; }
                    if (vm.EditorPreview == null)
                    {
                        ClearPreviewOverlay();
                    }
                    else
                    {
                        UpdatePreviewOverlay(vm, ov);
                    }
                }

                if (args.PropertyName == nameof(MainWindowViewModel.ReferenceImage))
                {
                    HookReferenceImageChanges(vm);
                    UpdateReferenceImagePlacement(vm);
                }
            };

            // Initial reference-image hookup + placement.
            HookReferenceImageChanges(vm);

            // The reference image lives in coordinates relative to the NAPLPS canvas's
            // actual rendered rect; when that rect changes (resize, stretch change, source
            // swap), reposition the overlay.
            var canvasCtrl = this.FindControl<Avalonia.Controls.Image>("CanvasImageControl");
            if (canvasCtrl != null)
            {
                canvasCtrl.LayoutUpdated += (_, _) => UpdateReferenceImagePlacement(vm);
            }
        }
    }

    private void OnTopLevelTextInput(object? sender, TextInputEventArgs e)
    {
        // Only forward typing to the canvas TextTool when the user ISN'T currently typing
        // into a text control (Telidraw pane, TextBox fields, etc.). Otherwise the same
        // character goes to both places and tool-switching shortcuts can steal keys.
        if (IsFocusOnTextInputControl())
        {
            return;
        }

        if (DataContext is MainWindowViewModel vm && vm.IsEditorMode && e.Text?.Length > 0)
        {
            foreach (var c in e.Text)
            {
                vm.OnEditorTextInput(c);
            }
            e.Handled = true;
        }
    }

    private void OnTopLevelKeyDown(object? sender, KeyEventArgs e)
    {
        var vm = DataContext as MainWindowViewModel;

        // Are we typing? Two cases:
        //   1. The canvas Text tool has a live insertion point (chars feed its buffer).
        //   2. Focus is inside a text-input control (Telidraw AvaloniaEdit, plain TextBox).
        bool textEntry =
            (vm != null && vm.IsEditorMode && vm.ActiveTool is TextTool tt && tt.HasInsertionPoint)
            || IsFocusOnTextInputControl();

        if (textEntry)
        {
            // Enter commits the canvas Text tool's buffer; everything else is left for the
            // focused control to handle. The plain-key app shortcuts are dispatched below,
            // which we skip entirely while typing — that's the whole point of moving them
            // out of Window.KeyBindings.
            if (e.Key == Key.Enter && vm?.ActiveTool is TextTool tte && tte.HasInsertionPoint)
            {
                vm.OnEditorTextCommit();
                e.Handled = true;
            }

            return;
        }

        // Not typing: dispatch the plain-key shortcuts that used to be Window.KeyBindings.
        // Ctrl/Alt/Meta combos stay declared in XAML and are handled by KeyboardDevice.
        if (vm != null && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) == 0)
        {
            if ((e.KeyModifiers & KeyModifiers.Shift) != 0)
            {
                // Shift+Arrow nudges the selected geometric command's coordinates by 1 pel.
                string? dir = e.Key switch
                {
                    Key.Left  => "Left",
                    Key.Right => "Right",
                    Key.Up    => "Up",
                    Key.Down  => "Down",
                    _         => null
                };
                if (dir != null && vm.NudgeSelectedCommand.CanExecute(dir))
                {
                    vm.NudgeSelectedCommand.Execute(dir);
                    e.Handled = true;
                    return;
                }
            }
            else
            {
                // Tool-selection hotkeys (editor mode only).
                if (vm.IsEditorMode)
                {
                    string? tool = e.Key switch
                    {
                        Key.V => "Select",
                        Key.M => "MovePen",
                        Key.L => "Line",
                        Key.R => "Rectangle",
                        Key.P => "Polygon",
                        Key.A => "Arc",
                        Key.T => "Text",
                        Key.F => "Fill",
                        _     => null
                    };
                    if (tool != null)
                    {
                        vm.SetActiveToolCommand.Execute(tool);
                        e.Handled = true;
                        return;
                    }
                }

                // Frame navigation + delete (work in viewer and editor). Plain arrows stay on
                // frame nav so animation playback isn't disrupted.
                switch (e.Key)
                {
                    case Key.Left   when vm.PreviousFrameCommand.CanExecute(null): vm.PreviousFrameCommand.Execute(null); e.Handled = true; return;
                    case Key.Right  when vm.NextFrameCommand.CanExecute(null):     vm.NextFrameCommand.Execute(null);     e.Handled = true; return;
                    case Key.Home   when vm.FirstFrameCommand.CanExecute(null):    vm.FirstFrameCommand.Execute(null);    e.Handled = true; return;
                    case Key.End    when vm.LastFrameCommand.CanExecute(null):     vm.LastFrameCommand.Execute(null);     e.Handled = true; return;
                    case Key.Delete when vm.DeleteSelectedCommand.CanExecute(null): vm.DeleteSelectedCommand.Execute(null); e.Handled = true; return;
                }
            }
        }
    }

    /// <summary>True when the currently-focused element is a text-input control
    /// (TextBox, AvaloniaEdit TextEditor/TextArea, AutoCompleteBox, etc.). Used to
    /// suppress top-level tool-shortcut dispatch while the user is typing.</summary>
    private bool IsFocusOnTextInputControl()
    {
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        if (focused == null) { return false; }

        // Walk up so that focus on a TextBox's inner TextPresenter still counts.
        object? current = focused;
        while (current != null)
        {
            if (current is TextBox) { return true; }
            if (current is AvaloniaEdit.TextEditor) { return true; }
            if (current is AvaloniaEdit.Editing.TextArea) { return true; }
            if (current is AutoCompleteBox) { return true; }

            current = current is Visual v ? Avalonia.VisualTree.VisualExtensions.GetVisualParent(v) : null;
        }
        return false;
    }

    /// <summary>Ctrl+scroll zoom, anchored at the cursor so the content point under the pointer
    /// stays put (screen = content*zoom + pan). Plain scroll is left untouched.</summary>
    private void OnCanvasWheel(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) { return; }
        if ((e.KeyModifiers & KeyModifiers.Control) == 0) { return; }

        var cell = this.FindControl<Grid>("CanvasCell");
        if (cell == null) { return; }

        double oldZoom = vm.ZoomFactor;
        double newZoom = Math.Clamp(oldZoom * Math.Pow(1.15, e.Delta.Y), MainWindowViewModel.MinZoom, MainWindowViewModel.MaxZoom);
        e.Handled = true;
        if (Math.Abs(newZoom - oldZoom) < 1e-9) { return; }

        var cursor = e.GetPosition(cell);
        double contentX = (cursor.X - vm.PanX) / oldZoom;
        double contentY = (cursor.Y - vm.PanY) / oldZoom;

        vm.ZoomFactor = newZoom;
        vm.PanX = cursor.X - contentX * newZoom;
        vm.PanY = cursor.Y - contentY * newZoom;
    }

    private void OnEditorPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && sender is Canvas canvas)
        {
            var pos = e.GetPosition(canvas);
            var controlSize = canvas.Bounds.Size;
            var additive = (e.KeyModifiers & (KeyModifiers.Shift | KeyModifiers.Control)) != 0;
            var ctrlHeld = (e.KeyModifiers & KeyModifiers.Control) != 0;
            var shiftHeld = (e.KeyModifiers & KeyModifiers.Shift) != 0;
            vm.SetClickCount(e.ClickCount);
            vm.OnEditorPointerPressed(pos, controlSize, e.GetCurrentPoint(canvas).Properties.IsRightButtonPressed, additive, ctrlHeld, shiftHeld);
            UpdatePreviewOverlay(vm, canvas);
        }
    }

    private void OnEditorPointerMoved(object? sender, PointerEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && sender is Canvas canvas)
        {
            var pos = e.GetPosition(canvas);
            var controlSize = canvas.Bounds.Size;
            vm.OnEditorPointerMoved(pos, controlSize);
            UpdatePreviewOverlay(vm, canvas);
        }
    }

    private void OnEditorPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && sender is Canvas canvas)
        {
            var pos = e.GetPosition(canvas);
            var controlSize = canvas.Bounds.Size;
            var shiftHeld = (e.KeyModifiers & KeyModifiers.Shift) != 0;
            vm.OnEditorPointerReleased(pos, controlSize, shiftHeld);

            // SelectTool keeps a preview (selection outline) after release, and multi-click
            // tools like Polygon keep one during the click sequence. Only clear when the VM
            // has confirmed nothing's to show.
            if (vm.EditorPreview == null)
            {
                ClearPreviewOverlay();
            }
            else
            {
                UpdatePreviewOverlay(vm, canvas);
            }
        }
    }

    private void UpdatePreviewOverlay(MainWindowViewModel vm, Canvas canvas)
    {
        var overlay = this.FindControl<Canvas>("EditorOverlay");

        if (overlay == null)
        {
            return;
        }

        var preview = vm.EditorPreview;
        if (preview == null || preview.Shape == PreviewShape.None)
        {
            ClearPreviewOverlay();
            return;
        }

        // Clear previous preview shapes (keep any non-preview children if they exist)
        overlay.Children.Clear();

        var controlSize = canvas.Bounds.Size;
        var canvasSize = vm.GetSizeObj();
        var stretch = vm.ImageStretch;

        // Faint full-circle ghost (the circle the arc lies on) — added first so the dashed arc
        // and cyan handles paint on top. The mapper is non-square (Y is 0..0.75), so probe the
        // X and Y radii separately rather than assuming a circle stays circular on screen.
        if (preview.GhostCenter is { } ghostC && preview.GhostRadius > 0)
        {
            var gcp = CoordinateMapper.NaplpsToScreen(ghostC.X, ghostC.Y, controlSize, canvasSize, stretch);
            var gex = CoordinateMapper.NaplpsToScreen(ghostC.X + preview.GhostRadius, ghostC.Y, controlSize, canvasSize, stretch);
            var gey = CoordinateMapper.NaplpsToScreen(ghostC.X, ghostC.Y + preview.GhostRadius, controlSize, canvasSize, stretch);
            double grx = Math.Abs(gex.X - gcp.X), gry = Math.Abs(gey.Y - gcp.Y);
            if (grx > 0 && gry > 0)
            {
                var ghost = new Avalonia.Controls.Shapes.Ellipse
                {
                    Width = grx * 2,
                    Height = gry * 2,
                    Stroke = new SolidColorBrush(Avalonia.Media.Colors.White),
                    StrokeThickness = 1,
                    StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 2, 4 },
                    Opacity = 0.25,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(ghost, gcp.X - grx);
                Canvas.SetTop(ghost, gcp.Y - gry);
                overlay.Children.Add(ghost);
            }
        }

        var dashStyle = new Avalonia.Media.DashStyle([4, 4], 0);
        var strokeBrush = preview.IsSelection
            ? new SolidColorBrush(Avalonia.Media.Colors.Cyan)
            : new SolidColorBrush(Avalonia.Media.Colors.White);

        switch (preview.Shape)
        {
            case PreviewShape.Line:
            {
                var p1 = CoordinateMapper.NaplpsToScreen(preview.X1, preview.Y1, controlSize, canvasSize, stretch);
                var p2 = CoordinateMapper.NaplpsToScreen(preview.X2, preview.Y2, controlSize, canvasSize, stretch);

                var line = new Avalonia.Controls.Shapes.Line
                {
                    StartPoint = p1,
                    EndPoint = p2,
                    Stroke = strokeBrush,
                    StrokeThickness = 1,
                    StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 4 },
                    Opacity = 0.8
                };
                overlay.Children.Add(line);
                break;
            }

            case PreviewShape.Rectangle:
            {
                var p1 = CoordinateMapper.NaplpsToScreen(preview.X1, preview.Y1, controlSize, canvasSize, stretch);
                var p2 = CoordinateMapper.NaplpsToScreen(preview.X2, preview.Y2, controlSize, canvasSize, stretch);

                var x = Math.Min(p1.X, p2.X);
                var y = Math.Min(p1.Y, p2.Y);
                var w = Math.Abs(p2.X - p1.X);
                var h = Math.Abs(p2.Y - p1.Y);

                var rect = new Avalonia.Controls.Shapes.Rectangle
                {
                    Width = w,
                    Height = h,
                    Stroke = strokeBrush,
                    StrokeThickness = 1,
                    StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 4 },
                    Opacity = 0.8,
                    Fill = preview.IsFilled
                        ? new SolidColorBrush(Avalonia.Media.Colors.White) { Opacity = 0.1 }
                        : null
                };
                Canvas.SetLeft(rect, x);
                Canvas.SetTop(rect, y);
                overlay.Children.Add(rect);
                break;
            }

            case PreviewShape.Polygon:
            {
                if (preview.Points.Count < 2) goto paint_handles;

                for (int i = 0; i < preview.Points.Count - 1; i++)
                {
                    var pa = CoordinateMapper.NaplpsToScreen(preview.Points[i].X, preview.Points[i].Y, controlSize, canvasSize, stretch);
                    var pb = CoordinateMapper.NaplpsToScreen(preview.Points[i + 1].X, preview.Points[i + 1].Y, controlSize, canvasSize, stretch);

                    var line = new Avalonia.Controls.Shapes.Line
                    {
                        StartPoint = pa,
                        EndPoint = pb,
                        Stroke = strokeBrush,
                        StrokeThickness = 1,
                        StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 4 },
                        Opacity = 0.8
                    };
                    overlay.Children.Add(line);
                }
                break;
            }
        }

        paint_handles:
        // Vertex handles — small filled cyan squares on top of the selection outline.
        // Draw LAST so they sit on top of the bbox stroke and any preview dashes.
        if (preview.Handles.Count > 0)
        {
            var handleBrush = new SolidColorBrush(Avalonia.Media.Colors.Cyan);
            var handleStroke = new SolidColorBrush(Avalonia.Media.Colors.Black);
            const double handleSize = 8;
            foreach (var (hx, hy) in preview.Handles)
            {
                var hp = CoordinateMapper.NaplpsToScreen(hx, hy, controlSize, canvasSize, stretch);
                var square = new Avalonia.Controls.Shapes.Rectangle
                {
                    Width = handleSize,
                    Height = handleSize,
                    Fill = handleBrush,
                    Stroke = handleStroke,
                    StrokeThickness = 1,
                };
                Canvas.SetLeft(square, hp.X - handleSize / 2);
                Canvas.SetTop(square, hp.Y - handleSize / 2);
                overlay.Children.Add(square);
            }
        }
    }

    private ReferenceImage? _hookedReferenceImage;

    private void HookReferenceImageChanges(MainWindowViewModel vm)
    {
        // Detach from the previous instance so we don't leak handlers on every swap.
        if (_hookedReferenceImage != null)
        {
            _hookedReferenceImage.PropertyChanged -= OnReferenceImagePropertyChanged;
        }

        _hookedReferenceImage = vm.ReferenceImage;

        if (_hookedReferenceImage != null)
        {
            _hookedReferenceImage.PropertyChanged += OnReferenceImagePropertyChanged;
        }
    }

    private void OnReferenceImagePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            UpdateReferenceImagePlacement(vm);
        }
    }

    /// <summary>Translate ReferenceImage.X/Y/W/H (normalized NAPLPS coords — X: 0..1,
    /// Y: 0..0.75) into pixel Margin + Width/Height on ReferenceImageControl, keyed to
    /// the actual rendered bounds of CanvasImageControl. Called on any change to either.</summary>
    private void UpdateReferenceImagePlacement(MainWindowViewModel vm)
    {
        var refCtrl = this.FindControl<Avalonia.Controls.Image>("ReferenceImageControl");
        var overlay = this.FindControl<Canvas>("EditorOverlay");

        if (refCtrl == null || overlay == null) { return; }

        var ri = vm.ReferenceImage;
        if (ri == null)
        {
            refCtrl.IsVisible = false;
            return;
        }

        var size = overlay.Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) { return; }

        // Place against the exact rendered rect the pointer mapper uses (same control size,
        // same Stretch), so the overlay stays aligned with the canvas under any stretch mode —
        // not just None. The zoom/pan RenderTransform on the shared parent scales this along.
        var (offsetX, offsetY, renderW, renderH) = CoordinateMapper.GetImageRect(size, vm.GetSizeObj(), vm.ImageStretch);
        if (renderW <= 0 || renderH <= 0) { return; }

        // Y normalized range is 0..0.75 (4:3 aspect). So one unit of Y spans renderH/0.75 px.
        double yScale = renderH / 0.75;

        refCtrl.Margin = new Thickness(
            offsetX + ri.X * renderW,
            offsetY + ri.Y * yScale,
            0, 0);
        refCtrl.Width  = ri.Width  * renderW;
        refCtrl.Height = ri.Height * yScale;
    }

    private void ClearPreviewOverlay()
    {
        var overlay = this.FindControl<Canvas>("EditorOverlay");

        if (overlay == null)
        {
            return;
        }

        // Remove only non-grid children (grid lines have Tag="grid")
        for (int i = overlay.Children.Count - 1; i >= 0; i--)
        {
            if (overlay.Children[i] is not Avalonia.Controls.Control ctrl || ctrl.Tag as string != "grid")
            {
                overlay.Children.RemoveAt(i);
            }
        }
    }

    private void RenderGrid(MainWindowViewModel vm, Canvas overlay)
    {
        // Remove existing grid lines
        for (int i = overlay.Children.Count - 1; i >= 0; i--)
        {
            if (overlay.Children[i] is Avalonia.Controls.Control ctrl && ctrl.Tag as string == "grid")
            {
                overlay.Children.RemoveAt(i);
            }
        }

        if (!vm.GridSettings.IsVisible || overlay.Bounds.Width <= 0 || overlay.Bounds.Height <= 0)
        {
            return;
        }

        var controlSize = overlay.Bounds.Size;
        var canvasSize = vm.GetSizeObj();
        var stretch = vm.ImageStretch;
        var minorBrush = new SolidColorBrush(Avalonia.Media.Colors.Gray) { Opacity = 0.20 };
        var majorBrush = new SolidColorBrush(Avalonia.Media.Colors.Cyan) { Opacity = 0.35 };

        const int MajorEvery = 8;
        int xCount = 0;

        // Vertical lines (every SpacingX), with cyan major every Nth
        for (float x = 0; x <= 1.0f + 1e-5f; x += vm.GridSettings.SpacingX, xCount++)
        {
            var top = CoordinateMapper.NaplpsToScreen(x, 0.75f, controlSize, canvasSize, stretch);
            var bottom = CoordinateMapper.NaplpsToScreen(x, 0f, controlSize, canvasSize, stretch);
            bool isMajor = xCount % MajorEvery == 0;
            var line = new Avalonia.Controls.Shapes.Line
            {
                StartPoint = top,
                EndPoint = bottom,
                Stroke = isMajor ? majorBrush : minorBrush,
                StrokeThickness = isMajor ? 1.0 : 0.5,
                Tag = "grid",
                IsHitTestVisible = false
            };
            overlay.Children.Insert(0, line); // Insert behind preview shapes
        }

        int yCount = 0;

        // Horizontal lines
        for (float y = 0; y <= 0.75f + 1e-5f; y += vm.GridSettings.SpacingY, yCount++)
        {
            var left = CoordinateMapper.NaplpsToScreen(0f, y, controlSize, canvasSize, stretch);
            var right = CoordinateMapper.NaplpsToScreen(1.0f, y, controlSize, canvasSize, stretch);
            bool isMajor = yCount % MajorEvery == 0;
            var line = new Avalonia.Controls.Shapes.Line
            {
                StartPoint = left,
                EndPoint = right,
                Stroke = isMajor ? majorBrush : minorBrush,
                StrokeThickness = isMajor ? 1.0 : 0.5,
                Tag = "grid",
                IsHitTestVisible = false
            };
            overlay.Children.Insert(0, line);
        }
    }
}
