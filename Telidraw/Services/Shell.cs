// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using MsBox.Avalonia;
using MsBox.Avalonia.Dto;
using MsBox.Avalonia.Enums;
using System.Runtime.CompilerServices;
using Telidraw.Views;

namespace Telidraw.Services;

/// <summary>
/// A child "window" as the rest of the app sees it: closable, activatable, and observable.
/// On desktop this wraps a real <see cref="Window"/>; under a single-view lifetime
/// (browser, mobile) it wraps a floating pane in the <see cref="OverlayWindowHost"/>.
/// </summary>
public interface IChildShell
{
    /// <summary>The hosted view; its DataContext carries the dialog state.</summary>
    Control View { get; }

    event EventHandler? Closed;

    void Close();

    void Activate();
}

/// <summary>How a child shell should present: chrome text, size, and modality.</summary>
public sealed record ChildShellOptions(string Title)
{
    public double Width { get; init; } = double.NaN;

    public double Height { get; init; } = double.NaN;

    public bool CanResize { get; init; } = true;

    /// <summary>Auto-size height to content (ExportDialog-style forms).</summary>
    public bool SizeToContentHeight { get; init; }

    public bool Modal { get; init; }
}

/// <summary>
/// The app's window-system seam. Everything that used to reach for <c>App.MainWindow</c>
/// goes through here instead: pickers, clipboard, launcher, message boxes and child
/// windows all resolve against the current <see cref="TopLevel"/>, which is the main
/// <see cref="Window"/> on desktop and the browser/mobile view root under a single-view
/// lifetime. Child windows become real windows or overlay panes accordingly, which is
/// what lets the identical editor run on WASM (and later iOS/Android) unchanged.
/// </summary>
public static class Shell
{
    private static readonly ConditionalWeakTable<Control, IChildShell> ShellsByView = new();

    /// <summary>The single top level everything anchors to. Set by MainWindow (desktop)
    /// or MainView's attach (single-view).</summary>
    public static TopLevel? TopLevel { get; set; }

    /// <summary>The overlay layer inside MainView that hosts single-view child panes.</summary>
    public static OverlayWindowHost? Overlay { get; set; }

    /// <summary>The one MainWindowViewModel, reachable without a Window. Set by App.</summary>
    public static MainWindowViewModel? MainViewModel { get; set; }

    /// <summary>
    /// Where the app is served from, when it is served at all: the browser head sets this
    /// to the page URL before Avalonia starts. Null on desktop. Site-relative resources
    /// (the example corpus manifest) resolve against it.
    /// </summary>
    public static Uri? BaseUri { get; set; }

    /// <summary>A query parameter from <see cref="BaseUri"/>, unescaped, or null.</summary>
    public static string? GetBaseUriQueryParam(string name)
    {
        if (BaseUri?.Query is not { Length: > 1 } query)
        {
            return null;
        }

        foreach (var pair in query.TrimStart('?').Split('&'))
        {
            var eq = pair.IndexOf('=');

            if (eq > 0 && pair[..eq] == name)
            {
                return Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        }

        return null;
    }

    /// <summary>True when there is no OS window system to put child windows in.</summary>
    public static bool IsSingleView => TopLevel is not null and not Window;

    public static IStorageProvider? StorageProvider => TopLevel?.StorageProvider;

    public static ILauncher? Launcher => TopLevel?.Launcher;

    public static IClipboard? Clipboard => TopLevel?.Clipboard;

    /// <summary>Shows a child shell and returns immediately with its handle.</summary>
    public static IChildShell Show(Control view, ChildShellOptions options)
    {
        IChildShell shell;

        if (TopLevel is Window owner)
        {
            var window = new Window
            {
                Title = options.Title,
                Content = view,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = options.CanResize,
                Icon = AppIcon,
            };

            if (!double.IsNaN(options.Width)) { window.Width = options.Width; }

            if (options.SizeToContentHeight)
            {
                window.SizeToContent = SizeToContent.Height;
            }
            else if (!double.IsNaN(options.Height))
            {
                window.Height = options.Height;
            }

            shell = new WindowChildShell(window, view);

            if (options.Modal)
            {
                _ = window.ShowDialog(owner);
            }
            else
            {
                window.Show(owner);
            }
        }
        else if (Overlay is { } overlay)
        {
            shell = overlay.AddPane(view, options);
        }
        else
        {
            throw new InvalidOperationException("no window system available: neither a desktop owner window nor an overlay host is attached");
        }

        ShellsByView.AddOrUpdate(view, shell);

        return shell;
    }

    /// <summary>Shows a child shell modally and completes when it closes. The result
    /// lives in the view's DataContext, exactly as with Window.ShowDialog.</summary>
    public static Task ShowDialogAsync(Control view, ChildShellOptions options)
    {
        var shell = Show(view, options with { Modal = true });
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        shell.Closed += (_, _) => tcs.TrySetResult();

        return tcs.Task;
    }

    /// <summary>Closes the child shell hosting the given view. Views call this from their
    /// own Close/Cancel/Commit paths instead of walking up to a Window.</summary>
    public static void CloseHost(Control view)
    {
        if (ShellsByView.TryGetValue(view, out var shell))
        {
            shell.Close();
        }
    }

    /// <summary>Standard OK message box (errors, info) over whatever top level exists.</summary>
    public static Task<ButtonResult> ShowMessageAsync(string title, string message)
    {
        var box = MessageBoxManager.GetMessageBoxStandard(title, message);

        return TopLevel switch
        {
            Window w => box.ShowWindowDialogAsync(w),
            { Content: ContentControl host } => box.ShowAsPopupAsync(host),
            _ => box.ShowAsync(),
        };
    }

    /// <summary>Custom message box routed the same way (desktop dialog vs in-view popup).</summary>
    public static Task<string> ShowCustomMessageAsync(MessageBoxCustomParams parameters)
    {
        var box = MessageBoxManager.GetMessageBoxCustom(parameters);

        return TopLevel switch
        {
            Window w => box.ShowWindowDialogAsync(w),
            { Content: ContentControl host } => box.ShowAsPopupAsync(host),
            _ => box.ShowAsync(),
        };
    }

    private static WindowIcon? _appIcon;

    /// <summary>
    /// The app icon as a window icon, or null when there is no window system to show it
    /// in. Constructing a WindowIcon requires the platform icon loader, which single-view
    /// backends (browser, mobile) do not register - it would throw, which is why every
    /// icon assignment must come through here instead of newing one up.
    /// </summary>
    public static WindowIcon? AppIcon => TopLevel is Window ? TryLoadAppIcon() : null;

    private static WindowIcon? TryLoadAppIcon()
    {
        try
        {
            return _appIcon ??= new WindowIcon(new Bitmap(AssetLoader.Open(new Uri("avares://Telidraw/Assets/naplps.ico"))));
        }
        catch
        {
            return null;
        }
    }

    private sealed class WindowChildShell : IChildShell
    {
        private readonly Window _window;

        public WindowChildShell(Window window, Control view)
        {
            _window = window;
            View = view;
            _window.Closed += (_, _) => Closed?.Invoke(this, EventArgs.Empty);
        }

        public Control View { get; }

        public event EventHandler? Closed;

        public void Close()
        {
            _window.Close();
        }

        public void Activate()
        {
            _window.Activate();
        }
    }
}
