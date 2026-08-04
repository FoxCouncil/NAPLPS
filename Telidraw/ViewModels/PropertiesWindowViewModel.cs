// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSApp.ViewModels;

public partial class PropertiesWindowViewModel : ViewModelBase
{
    [ObservableProperty]
    private string fileName = "";

    [ObservableProperty]
    private string filePath = "";

    [ObservableProperty]
    private string fileSize = "";

    [ObservableProperty]
    private string systemType = "";

    [ObservableProperty]
    private string bitWidth = "";

    [ObservableProperty]
    private int commandCount;

    [ObservableProperty]
    private bool isValid;

    [ObservableProperty]
    private int errorCount;

    [ObservableProperty]
    private int warningCount;

    [ObservableProperty]
    private ObservableCollection<DiagnosticItem> diagnosticItems = [];

    [ObservableProperty]
    private int selectedTabIndex;

    [RelayCommand]
    private void CopyDiagnostics()
    {
        var text = string.Join(Environment.NewLine, DiagnosticItems.Select(d => $"[{d.Severity}] {d.Type}: {d.Message} {d.Details}"));

        if (App.MainWindow?.Clipboard != null)
        {
            App.MainWindow.Clipboard.SetTextAsync(text);
        }
    }

    [RelayCommand]
    private void CopyDiagnosticLine(DiagnosticItem? item)
    {
        if (item == null)
        {
            return;
        }

        var text = $"[{item.Severity}] {item.Type}: {item.Message} {item.Details}";

        if (App.MainWindow?.Clipboard != null)
        {
            App.MainWindow.Clipboard.SetTextAsync(text);
        }
    }

    public static PropertiesWindowViewModel FromFile(NaplpsFormat naplps, string filePath, ObservableCollection<DiagnosticItem> diagnostics, int startTab = 0)
    {
        var fileInfo = new System.IO.FileInfo(filePath);

        return new PropertiesWindowViewModel
        {
            FileName = fileInfo.Name,
            FilePath = fileInfo.FullName,
            FileSize = FormatFileSize(fileInfo.Length),
            SystemType = naplps.SystemType.ToString(),
            BitWidth = naplps.Is7Bit ? "7-Bit" : "8-Bit",
            CommandCount = naplps.Commands.Count,
            IsValid = naplps.IsValid,
            ErrorCount = diagnostics.Count(d => d.IsError),
            WarningCount = diagnostics.Count(d => !d.IsError),
            DiagnosticItems = diagnostics,
            SelectedTabIndex = startTab
        };
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} bytes";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0:F1} KB";
        }

        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }
}
