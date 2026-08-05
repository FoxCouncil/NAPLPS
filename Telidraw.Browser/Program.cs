// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using Avalonia;
using Avalonia.Browser;
using Telidraw;

internal sealed partial class Program
{
    private static Task Main(string[] args)
    {
        // main.js passes location.href as the single argument; the shell uses it to fetch
        // site-relative resources like the example corpus manifest.
        if (args.Length > 0 && Uri.TryCreate(args[0], UriKind.Absolute, out var baseUri))
        {
            Telidraw.Services.Shell.BaseUri = baseUri;
        }

        return BuildAvaloniaApp()
            .WithInterFont()
            .StartBrowserAppAsync("out");
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>();
    }
}
