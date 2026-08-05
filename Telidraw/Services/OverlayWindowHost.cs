// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Telidraw.Services;

/// <summary>
/// The single-view stand-in for an OS window manager: a full-size layer over MainView
/// that hosts child "windows" as draggable floating panes. Modal panes get a dimmed
/// backdrop that blocks input to everything beneath, mirroring Window.ShowDialog.
/// Desktop never uses this - <see cref="Shell"/> creates real windows there.
/// </summary>
public class OverlayWindowHost : Canvas
{
    private int _zCounter;
    private int _paneCount;

    public OverlayWindowHost()
    {
        // Invisible to hit-testing while empty so the editor underneath stays interactive.
        Background = null;
        ClipToBounds = true;
    }

    internal IChildShell AddPane(Control view, ChildShellOptions options)
    {
        var pane = new OverlayPane(this, view, options);

        if (options.Modal)
        {
            pane.Backdrop = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0)),
                [ZIndexProperty] = ++_zCounter,
            };

            // Backdrop spans the whole host and swallows input to whatever is beneath.
            pane.Backdrop.PointerPressed += (_, e) => e.Handled = true;
            Children.Add(pane.Backdrop);
            SizeBackdrop(pane.Backdrop);
        }

        pane.Root[ZIndexProperty] = ++_zCounter;
        Children.Add(pane.Root);

        // Cascade new panes; clamp keeps them reachable in a small browser viewport.
        var offset = 48 + 32 * (_paneCount++ % 6);
        SetLeft(pane.Root, Math.Max(8, Math.Min(offset, Math.Max(8, Bounds.Width - 240))));
        SetTop(pane.Root, Math.Max(8, Math.Min(offset, Math.Max(8, Bounds.Height - 160))));

        return pane;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);

        foreach (var child in Children)
        {
            if (child is Border { Child: null } backdrop)
            {
                SizeBackdrop(backdrop);
            }
            else if (child is Control pane)
            {
                // Keep pane title bars reachable after a viewport shrink.
                SetLeft(pane, Math.Max(0, Math.Min(GetLeft(pane), Math.Max(0, Bounds.Width - 60))));
                SetTop(pane, Math.Max(0, Math.Min(GetTop(pane), Math.Max(0, Bounds.Height - 32))));
            }
        }
    }

    private void SizeBackdrop(Border backdrop)
    {
        backdrop.Width = Bounds.Width;
        backdrop.Height = Bounds.Height;
        SetLeft(backdrop, 0);
        SetTop(backdrop, 0);
    }

    private void BringToFront(Control root)
    {
        root[ZIndexProperty] = ++_zCounter;
    }

    private sealed class OverlayPane : IChildShell
    {
        private readonly OverlayWindowHost _host;
        private bool _closed;
        private bool _dragging;
        private Avalonia.Point _dragStart;
        private Avalonia.Point _paneStart;

        public OverlayPane(OverlayWindowHost host, Control view, ChildShellOptions options)
        {
            _host = host;
            View = view;

            var closeButton = new Button
            {
                Content = "✕",
                FontSize = 11,
                Padding = new Thickness(6, 2),
                Background = Brushes.Transparent,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };

            closeButton.Click += (_, _) => Close();

            var titleText = new TextBlock
            {
                Text = options.Title,
                FontWeight = Avalonia.Media.FontWeight.Bold,
                FontSize = 12,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0),
            };

            var titleBar = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Background = new SolidColorBrush(Color.FromRgb(0x2d, 0x2d, 0x2d)),
                Height = 28,
            };

            titleBar.Children.Add(titleText);
            Grid.SetColumn(closeButton, 1);
            titleBar.Children.Add(closeButton);

            titleBar.PointerPressed += OnTitleBarPressed;
            titleBar.PointerMoved += OnTitleBarMoved;
            titleBar.PointerReleased += OnTitleBarReleased;

            var content = new ContentControl { Content = view };

            if (!double.IsNaN(options.Width)) { content.Width = options.Width; }

            if (!options.SizeToContentHeight && !double.IsNaN(options.Height)) { content.Height = options.Height; }

            var body = new DockPanel();
            DockPanel.SetDock(titleBar, Dock.Top);
            body.Children.Add(titleBar);
            body.Children.Add(content);

            Root = new Border
            {
                Child = body,
                Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                BoxShadow = new BoxShadows(new BoxShadow { Blur = 24, OffsetY = 6, Color = Color.FromArgb(0xA0, 0, 0, 0) }),
            };

            Root.PointerPressed += (_, _) => _host.BringToFront(Root);
        }

        internal Border Root { get; }

        internal Border? Backdrop { get; set; }

        public Control View { get; }

        public event EventHandler? Closed;

        public void Close()
        {
            if (_closed) { return; }

            _closed = true;
            _host.Children.Remove(Root);

            if (Backdrop is { } backdrop)
            {
                _host.Children.Remove(backdrop);
            }

            Closed?.Invoke(this, EventArgs.Empty);
        }

        public void Activate()
        {
            _host.BringToFront(Root);
        }

        private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
        {
            _dragging = true;
            _dragStart = e.GetPosition(_host);
            _paneStart = new Avalonia.Point(GetLeft(Root), GetTop(Root));
            e.Pointer.Capture(sender as IInputElement);
            _host.BringToFront(Root);
        }

        private void OnTitleBarMoved(object? sender, PointerEventArgs e)
        {
            if (!_dragging) { return; }

            var pos = e.GetPosition(_host);
            var x = _paneStart.X + (pos.X - _dragStart.X);
            var y = _paneStart.Y + (pos.Y - _dragStart.Y);

            SetLeft(Root, Math.Max(0, Math.Min(x, Math.Max(0, _host.Bounds.Width - 60))));
            SetTop(Root, Math.Max(0, Math.Min(y, Math.Max(0, _host.Bounds.Height - 32))));
        }

        private void OnTitleBarReleased(object? sender, PointerReleasedEventArgs e)
        {
            _dragging = false;
            e.Pointer.Capture(null);
        }
    }
}
