// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

namespace NAPLPSApp.Editor;

/// <summary>
/// Converts between Avalonia pointer position and NAPLPS normalized coordinates,
/// accounting for the Image control's Stretch mode and layout bounds.
/// </summary>
public class CoordinateMapper
{
    /// <summary>
    /// Converts an Avalonia pointer position (relative to the overlay canvas)
    /// to NAPLPS normalized coordinates [0,1] x [0,0.75].
    /// </summary>
    public static (float normX, float normY) ScreenToNaplps(
        Avalonia.Point pointerPos,
        Avalonia.Size controlSize,
        SixLabors.ImageSharp.Size canvasSize,
        Stretch stretch)
    {
        // Calculate the actual image rect within the control based on stretch mode
        var (offsetX, offsetY, renderWidth, renderHeight) = GetImageRect(controlSize, canvasSize, stretch);

        // Map pointer position to canvas pixel coordinates
        float relX = (float)(pointerPos.X - offsetX);
        float relY = (float)(pointerPos.Y - offsetY);

        // Clamp to image bounds
        relX = Math.Clamp(relX, 0, (float)renderWidth);
        relY = Math.Clamp(relY, 0, (float)renderHeight);

        // Scale to canvas pixels
        float screenX = relX / (float)renderWidth * canvasSize.Width;
        float screenY = relY / (float)renderHeight * canvasSize.Height;

        // Convert screen pixels to NAPLPS normalized coords
        var (normX, normY) = NaplpsUtils.ConvertScreenToNormalizedF(canvasSize, screenX, screenY);

        return (normX, normY);
    }

    /// <summary>
    /// Converts NAPLPS normalized coordinates back to Avalonia pointer position.
    /// </summary>
    public static Avalonia.Point NaplpsToScreen(
        float normX, float normY,
        Avalonia.Size controlSize,
        SixLabors.ImageSharp.Size canvasSize,
        Stretch stretch)
    {
        // Convert NAPLPS to screen pixels
        var (screenX, screenY) = NaplpsUtils.ConvertNormalizedToScreenF(canvasSize, normX, normY);

        // Get image rect
        var (offsetX, offsetY, renderWidth, renderHeight) = GetImageRect(controlSize, canvasSize, stretch);

        // Scale from canvas pixels to control coordinates
        double x = screenX / canvasSize.Width * renderWidth + offsetX;
        double y = screenY / canvasSize.Height * renderHeight + offsetY;

        return new Avalonia.Point(x, y);
    }

    /// <summary>
    /// Calculates the rendered image rectangle within the control based on stretch mode.
    /// Returns (offsetX, offsetY, renderWidth, renderHeight). Public so the reference-image
    /// overlay can place itself against the exact same rect the pointer mapper uses.
    /// </summary>
    public static (double offsetX, double offsetY, double renderWidth, double renderHeight) GetImageRect(
        Avalonia.Size controlSize,
        SixLabors.ImageSharp.Size canvasSize,
        Stretch stretch)
    {
        double controlW = controlSize.Width;
        double controlH = controlSize.Height;
        double imageW = canvasSize.Width;
        double imageH = canvasSize.Height;

        double renderW, renderH, offsetX, offsetY;

        switch (stretch)
        {
            case Stretch.None:
            renderW = imageW;
            renderH = imageH;
            offsetX = (controlW - renderW) / 2;
            offsetY = (controlH - renderH) / 2;
            break;

            case Stretch.Fill:
            renderW = controlW;
            renderH = controlH;
            offsetX = 0;
            offsetY = 0;
            break;

            case Stretch.Uniform:
            {
                double scale = Math.Min(controlW / imageW, controlH / imageH);
                renderW = imageW * scale;
                renderH = imageH * scale;
                offsetX = (controlW - renderW) / 2;
                offsetY = (controlH - renderH) / 2;
                break;
            }

            case Stretch.UniformToFill:
            {
                double scale = Math.Max(controlW / imageW, controlH / imageH);
                renderW = imageW * scale;
                renderH = imageH * scale;
                offsetX = (controlW - renderW) / 2;
                offsetY = (controlH - renderH) / 2;
                break;
            }

            default:
            renderW = imageW;
            renderH = imageH;
            offsetX = 0;
            offsetY = 0;
            break;
        }

        return (offsetX, offsetY, renderW, renderH);
    }
}
