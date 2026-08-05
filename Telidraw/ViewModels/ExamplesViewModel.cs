// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using System.Net.Http;
using System.Text.Json;
using Telidraw.Services;

namespace Telidraw.ViewModels;

/// <summary>
/// Drives the Open Example dialog: fetches the site's example-corpus manifest
/// (examples.json, written by the Pages build next to the WASM bundle) and lets the
/// user pick a .nap to load. Browser-only in practice - the manifest resolves against
/// <see cref="Shell.BaseUri"/>, which only the browser head sets.
/// </summary>
public partial class ExamplesViewModel : ObservableObject
{
    /// <summary>An entry in the manifest: display name plus the site-relative path.</summary>
    public sealed record ExampleEntry(string Name, string RelativePath)
    {
        public override string ToString() => Name;
    }

    public ObservableCollection<ExampleEntry> Examples { get; } = [];

    [ObservableProperty]
    private ExampleEntry? selectedExample;

    [ObservableProperty]
    private string status = "Loading example list…";

    public bool IsCommitted { get; private set; }

    /// <summary>Raised when the dialog should close; the hosting view routes it to its shell.</summary>
    public event Action? RequestClose;

    [RelayCommand]
    private void Open()
    {
        if (SelectedExample == null)
        {
            return;
        }

        IsCommitted = true;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        RequestClose?.Invoke();
    }

    public async Task LoadAsync()
    {
        if (Shell.BaseUri is not { } baseUri)
        {
            Status = "No example corpus available here.";
            return;
        }

        try
        {
            using var http = new HttpClient();
            var json = await http.GetStringAsync(new Uri(baseUri, "examples.json"));

            // JsonDocument, not JsonSerializer: reflection-based serialization is disabled
            // in the WASM runtime and this is a flat string array anyway.
            using var doc = JsonDocument.Parse(json);
            var paths = doc.RootElement.EnumerateArray().Select(e => e.GetString()).OfType<string>().ToArray();

            Examples.Clear();

            foreach (var path in paths)
            {
                // Manifest entries are repo-relative ("Examples/blinky.nap"); display them
                // without the shared prefix.
                var name = path.StartsWith("Examples/", StringComparison.OrdinalIgnoreCase) ? path["Examples/".Length..] : path;
                Examples.Add(new ExampleEntry(name, path));
            }

            Status = $"{Examples.Count} examples from the repository corpus.";
        }
        catch (Exception ex)
        {
            Status = $"Could not load the example list: {ex.Message}";
        }
    }
}
