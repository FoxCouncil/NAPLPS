// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;

namespace NAPLPS.Drawing;

/// <summary>
/// Renders INCREMENTAL POINT (bitmap) commands.
/// Draws pixels within the active field using logical pel size.
/// </summary>
public class DrawableIncrementalPoint : Drawable, IDrawable
{
    private readonly IncrementalPointCommand _command;

    public DrawableIncrementalPoint(IncrementalPointCommand command) : base(command)
    {
        _command = command;
    }

    public void Draw(Image<Rgba32> image, NaplpsState state, Size size)
    {
        if (!_command.IsValid || _command.Pixels.Count == 0)
        {
            return;
        }

        // Get the active field bounds
        var field = state.Field;
        var fieldOrigin = ConvertNormalizedToPoint(size, field.Left, field.Bottom);
        var (fieldWidth, fieldHeight) = ConvertNormalizedToScreenScale(size, field.Width, field.Height);

        // Get logical pel size
        var (pelWidth, pelHeight) = ConvertNormalizedToScreenScale(size, state.LogicalPel.X, state.LogicalPel.Y);
        pelWidth = Math.Max(1, Math.Abs(pelWidth));
        pelHeight = Math.Max(1, Math.Abs(pelHeight));

        // Starting position at field origin
        float currentX = fieldOrigin.X;
        float currentY = fieldOrigin.Y - pelHeight; // Start at top of field (Y is inverted)

        // Calculate pels per row
        int pelsPerRow = Math.Max(1, Math.Abs(fieldWidth) / pelWidth);
        int currentPelInRow = 0;

        image.Mutate(ctx =>
        {
            foreach (var pixel in _command.Pixels)
            {
                if (pixel.IsRepositioning)
                {
                    // Handle repositioning codes
                    if (pixel.RepositionCode == 1 || pixel.RepositionCode == 3) // dy
                    {
                        currentY += pixel.DeltaY * pelHeight;
                    }
                    if (pixel.RepositionCode == 2 || pixel.RepositionCode == 3) // dx
                    {
                        currentX += pixel.DeltaX * pelWidth;
                    }
                    continue;
                }

                // Get color for this pixel
                var color = GetColorForPixel(state, pixel.ColorValue, _command.BitsPerPixel);

                // Draw the pixel as a filled rectangle of logical pel size
                var rect = new RectangleF(currentX, currentY, pelWidth, pelHeight);
                ctx.Fill(FillOptions(), color, rect);

                // Move to next position
                currentPelInRow++;
                currentX += pelWidth;

                // Check if we need to wrap to next row
                if (currentPelInRow >= pelsPerRow)
                {
                    currentPelInRow = 0;
                    currentX = fieldOrigin.X;
                    currentY += pelHeight; // Move down (positive in screen coords)
                }
            }
        });

        // Reset pen to field origin after drawing
        state.Pen = field.Origin;
    }

    private static ISColor GetColorForPixel(NaplpsState state, int colorValue, int bitsPerPixel)
    {
        if (state.ColorMode == 0)
        {
            // Direct RGB - extract R, G, B from colorValue based on bits per pixel
            // Simplified: assume equal bits per channel
            int bitsPerChannel = bitsPerPixel / 3;
            if (bitsPerChannel < 1) bitsPerChannel = 1;

            int maxValue = (1 << bitsPerChannel) - 1;
            if (maxValue == 0) maxValue = 1;

            int r = (colorValue >> (bitsPerChannel * 2)) & maxValue;
            int g = (colorValue >> bitsPerChannel) & maxValue;
            int b = colorValue & maxValue;

            // Scale to 0-255
            r = r * 255 / maxValue;
            g = g * 255 / maxValue;
            b = b * 255 / maxValue;

            return ISColor.FromRgb((byte)r, (byte)g, (byte)b);
        }
        else
        {
            // Palette mode - colorValue is palette index
            byte paletteIndex = (byte)(colorValue & 0xFF);
            if (state.ColorMap.TryGetValue(paletteIndex, out var naplpsColor))
            {
                return naplpsColor.ToColor().ToISColor();
            }

            // Default to foreground color
            return state.ColorMode == 1
                ? state.ColorMap.GetValueOrDefault(state.ColorMapForeground, NaplpsColor.White).ToColor().ToISColor()
                : state.Foreground.ToColor().ToISColor();
        }
    }
}
