// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSApp.Editor;

/// <summary>
/// Backing state for an optional raster reference image shown behind the NAPLPS canvas
/// so the user can draw on top of a photo / sketch. Position and size are stored in
/// NAPLPS-normalized coords (X: 0..1, Y: 0..0.75) so they track the drawing regardless
/// of the canvas's on-screen size. Only persisted to Telidraw source (.td) — binary .nap
/// has no comment facility, so the overlay is source-format-only.
/// </summary>
public partial class ReferenceImage : ObservableObject
{
    /// <summary>Absolute path on disk. Serialized in the .td comment so reload finds it.</summary>
    [ObservableProperty]
    private string sourcePath = string.Empty;

    [ObservableProperty]
    private Bitmap? bitmap;

    [ObservableProperty]
    private float x = 0f;

    [ObservableProperty]
    private float y = 0f;

    [ObservableProperty]
    private float width = 1f;

    [ObservableProperty]
    private float height = 0.75f;

    [ObservableProperty]
    private double opacity = 0.5;

    [ObservableProperty]
    private bool isVisible = true;
}
