// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSApp.Editor;

/// <summary>
/// Settings for the editor grid overlay and snap-to-grid behavior.
/// Default spacing matches NAPLPS character cell dimensions.
/// </summary>
public partial class GridSettings : ObservableObject
{
    [ObservableProperty]
    private bool isVisible;

    [ObservableProperty]
    private bool isSnapEnabled;

    /// <summary>Grid spacing in NAPLPS normalized X units. Default: 1/40 (character width).</summary>
    [ObservableProperty]
    private float spacingX = 1.0f / 40.0f;

    /// <summary>Grid spacing in NAPLPS normalized Y units. Default: 5/128 (character height).</summary>
    [ObservableProperty]
    private float spacingY = 5.0f / 128.0f;

    /// <summary>Snaps a normalized X value to the nearest grid point.</summary>
    public float SnapX(float x) => IsSnapEnabled ? MathF.Round(x / SpacingX) * SpacingX : x;

    /// <summary>Snaps a normalized Y value to the nearest grid point.</summary>
    public float SnapY(float y) => IsSnapEnabled ? MathF.Round(y / SpacingY) * SpacingY : y;
}
