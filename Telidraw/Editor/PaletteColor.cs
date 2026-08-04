// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSApp.Editor;

/// <summary>
/// One entry in the 16-slot NAPLPS color map. Exposes R/G/B as observable 0-255 bytes
/// that the Palette Editor binds to; mutating them recomputes <see cref="Brush"/> for
/// the swatch thumbnail and fires <see cref="RgbChanged"/> so the ViewModel can emit a
/// SET COLOR command.
/// </summary>
public partial class PaletteColor : ObservableObject
{
    /// <summary>Fires when R, G, or B is mutated by the UI. Hook this in MainWindowViewModel.</summary>
    public event EventHandler? RgbChanged;

    [ObservableProperty]
    private byte index;

    [ObservableProperty]
    private byte red;

    [ObservableProperty]
    private byte green;

    [ObservableProperty]
    private byte blue;

    [ObservableProperty]
    private IBrush brush = Brushes.Black;

    /// <summary>
    /// Build the swatch brush from the current (Red, Green, Blue) values. Called by
    /// the change-handlers below; also callable externally to force a refresh.
    /// </summary>
    public void RefreshBrush()
    {
        Brush = new SolidColorBrush(Avalonia.Media.Color.FromRgb(Red, Green, Blue));
    }

    partial void OnRedChanged(byte value)
    {
        RefreshBrush();
        RgbChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnGreenChanged(byte value)
    {
        RefreshBrush();
        RgbChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnBlueChanged(byte value)
    {
        RefreshBrush();
        RgbChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Seed the entry from a <see cref="NaplpsColor"/> without firing RgbChanged
    /// (used during bulk InitializePalette bootstrapping).
    /// </summary>
    public void LoadFromNaplpsColor(NaplpsColor color)
    {
        // Bypass the OnXChanged events by assigning backing fields directly is tempting
        // but the generated code doesn't expose them. Instead, temporarily detach the
        // event so bulk loads don't trigger N SetColor commits.
        var saved = RgbChanged;
        RgbChanged = null;

        Red = color.Red;
        Green = color.Green;
        Blue = color.Blue;

        RgbChanged = saved;

        RefreshBrush();
    }
}
