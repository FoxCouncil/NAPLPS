// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using NAPLPSApp.Resources;

namespace NAPLPSApp;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }

    // A file handed to us by the OS (Finder "Open With", double-click, Quick Look "Open with")
    // can arrive before the main window exists; stash it and flush once the window is ready.
    private string? _pendingOpenPath;

    public override void Initialize()
    {
        DataContext = new AppViewModel();

        AvaloniaXamlLoader.Load(this);

        PlatformGestures.Register(this);

        // macOS delivers "Open With" / double-clicked documents via an Apple Event, not argv,
        // surfaced by Avalonia as an IActivatableLifetime File activation. Subscribe early so a
        // launch-time file isn't missed.
        if (this.TryGetFeature<IActivatableLifetime>() is { } activatable)
        {
            activatable.Activated += OnActivated;
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel()
            };

            desktop.MainWindow = MainWindow;
        }

        base.OnFrameworkInitializationCompleted();

        // Flush a file that arrived during launch, now that the window and its VM exist.
        if (_pendingOpenPath is { } pending)
        {
            _pendingOpenPath = null;
            OpenFileInMainWindow(pending);
        }
    }

    private void OnActivated(object? sender, ActivatedEventArgs e)
    {
        if (e is not FileActivatedEventArgs fileArgs)
        {
            return;
        }

        var path = fileArgs.Files.FirstOrDefault()?.Path.LocalPath;

        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        if (MainWindow?.DataContext is MainWindowViewModel)
        {
            OpenFileInMainWindow(path);
        }
        else
        {
            // Window not built yet (launch-time activation): defer to OnFrameworkInitializationCompleted.
            _pendingOpenPath = path;
        }
    }

    private static void OpenFileInMainWindow(string path)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            if (MainWindow?.DataContext is MainWindowViewModel vm)
            {
                await vm.OpenExternalFile(path);
            }
        });
    }
}
