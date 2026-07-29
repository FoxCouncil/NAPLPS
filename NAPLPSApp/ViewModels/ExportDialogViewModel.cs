namespace NAPLPSApp.ViewModels;

public enum ExportFormat
{
    Png,
    Jpeg,
    Bmp,
    Gif,
    Apng,
}

/// <summary>
/// VM for the Export dialog. Captures the user's choice of format, output resolution, and
/// background treatment. Consumed by MainWindowViewModel.Export when the dialog is
/// accepted — it passes the format-specific options down to the draw context's encoder.
/// </summary>
public partial class ExportDialogViewModel : ObservableObject
{
    /// <summary>Format list shown in the dropdown. Order matches <see cref="ExportFormat"/> enum.</summary>
    public string[] FormatOptions { get; } = ["PNG", "JPEG", "BMP", "GIF", "APNG (animated)"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSetJpegQuality))]
    [NotifyPropertyChangedFor(nameof(CanSetTransparentBackground))]
    [NotifyPropertyChangedFor(nameof(IsApng))]
    private int formatIndex;

    public ExportFormat Format => (ExportFormat)FormatIndex;
    public bool IsApng => Format == ExportFormat.Apng;

    /// <summary>Multiplier applied to the source canvas dimensions. 1x = native draw-context size.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputWidth))]
    [NotifyPropertyChangedFor(nameof(OutputHeight))]
    private double scale = 1.0;

    /// <summary>Base canvas dimensions (DrawContext.Width/Height) captured when the dialog opened.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputWidth))]
    [NotifyPropertyChangedFor(nameof(OutputHeight))]
    private int sourceWidth = 640;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputWidth))]
    [NotifyPropertyChangedFor(nameof(OutputHeight))]
    private int sourceHeight = 480;

    public int OutputWidth => (int)System.Math.Round(SourceWidth * Scale);
    public int OutputHeight => (int)System.Math.Round(SourceHeight * Scale);

    /// <summary>Transparent background is only meaningful for formats that support alpha (PNG, GIF).</summary>
    [ObservableProperty]
    private bool transparentBackground;

    public bool CanSetTransparentBackground => Format == ExportFormat.Png || Format == ExportFormat.Gif;

    /// <summary>JPEG-only: quality 1-100. Default 90 matches common export tools.</summary>
    [ObservableProperty]
    private int jpegQuality = 90;

    public bool CanSetJpegQuality => Format == ExportFormat.Jpeg;

    /// <summary>
    /// APNG: the line speed the exported animation is paced at, so it draws the way the picture
    /// would have arrived. Shares its rate list with the editor's Speed menu and the CLI.
    /// </summary>
    [ObservableProperty]
    private int apngBaudRate = NAPLPS.NaplpsBaud.Default;

    /// <summary>Selectable draw speeds, for the dialog's dropdown.</summary>
    public IReadOnlyList<BaudChoice> ApngBaudChoices { get; } =
        [.. NAPLPS.NaplpsBaud.Rates.Select(r => new BaudChoice(r, NAPLPS.NaplpsBaud.Describe(r)))];

    /// <summary>A selectable line speed. Record so the ComboBox can match on value.</summary>
    public sealed record BaudChoice(int Rate, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>APNG: loop forever vs. play once.</summary>
    [ObservableProperty]
    private bool apngLoop = true;

    /// <summary>APNG: number of additional blink-animation cycles to append after the static draw frames.</summary>
    [ObservableProperty]
    private int apngBlinkCycles;

    /// <summary>APNG: total frames produced by the most recent render preview (informational, set by caller).</summary>
    [ObservableProperty]
    private int apngEstimatedFrames;

    /// <summary>APNG: clip output to frames [start..end] of the produced sequence (1-based, inclusive). 0 = no clip.</summary>
    [ObservableProperty]
    private int apngStartFrame;

    [ObservableProperty]
    private int apngEndFrame;

    /// <summary>Whether the dialog was accepted (OK pressed) vs cancelled.</summary>
    public bool IsCommitted { get; private set; }

    [RelayCommand]
    private void Ok() { IsCommitted = true; RequestClose?.Invoke(); }

    [RelayCommand]
    private void Cancel() { IsCommitted = false; RequestClose?.Invoke(); }

    [RelayCommand]
    private void SetScale(string factor)
    {
        if (double.TryParse(factor, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s))
        {
            Scale = s;
        }
    }

    public event System.Action? RequestClose;
}
