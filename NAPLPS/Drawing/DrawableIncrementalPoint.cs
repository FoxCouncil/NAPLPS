// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Processing;

namespace NAPLPS.Drawing;

/// <summary>
/// Renders INCREMENTAL POINT (bitmap) commands. The raster walk - row capacity, the
/// end-of-row byte flush, the signed pel steps - is resolved at parse time by
/// <see cref="IncrementalPointCommand"/> where the field/pel/drawing-point state is exact;
/// this drawable just deposits the resolved pels.
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
        if (!_command.IsValid || (_command.Deposits.Count == 0 && _command.ScrollBreaks.Count == 0))
        {
            return;
        }

        float dx = _command.PelSize.X;
        float dy = _command.PelSize.Y;

        // The pel-to-pixel mapping needs the spec's preexecution rescaling (5.3.3.6.3): a
        // fractional pel (1/512 on a 640-wide raster is 1.25px) drawn at its floor leaves a
        // lattice of uncovered pixels. The ceiling overlaps neighboring cells the way the
        // device's own placement does (15.1% vs 19.8% against the 8197_CHIEF capture for
        // ceiling vs exact edge-rounded tiling - the residue is dither-phase level).
        float pelWidth = MathF.Max(1f, MathF.Ceiling(MathF.Abs(dx) * size.Width));
        float pelHeight = MathF.Max(1f, MathF.Ceiling(MathF.Abs(dy) / (float)NaplpsUtils.DisplayRatio * size.Height));

        // Deposits are drawn in segments split at the recorded scroll events: a row step
        // that would exceed the field holds Y and instead shifts the display image lying
        // within the field by -dy (5.3.3.6.3 step 3), carrying content already drawn -
        // both by earlier commands and by this command's own earlier rows.
        var deposits = _command.Deposits;
        var breaks = _command.ScrollBreaks;
        int drawn = 0;
        int breakIndex = 0;

        while (true)
        {
            int until = breakIndex < breaks.Count ? Math.Min(breaks[breakIndex], deposits.Count) : deposits.Count;

            if (until > drawn)
            {
                int from = drawn;

                image.Mutate(ctx =>
                {
                    for (int i = from; i < until; i++)
                    {
                        var deposit = deposits[i];

                        // The pel extends dx/dy from the drawing point with their signs; the rect's
                        // top-left in screen coordinates is the min-X / max-Y normalized corner.
                        float cornerX = dx > 0 ? deposit.X : deposit.X + dx;
                        float cornerY = dy > 0 ? deposit.Y + dy : deposit.Y;

                        var point = ConvertNormalizedToPoint(size, cornerX, cornerY);
                        var color = GetColorForDeposit(state, deposit.ColorValue, _command.BitsPerPixel);
                        ctx.Fill(FillOptions(), color, new RectangleF(point.X, point.Y, pelWidth, pelHeight));
                    }
                });

                drawn = until;
            }

            if (breakIndex >= breaks.Count)
            {
                break;
            }

            ScrollFieldRegion(image, state, size, (int)pelHeight, dy);
            breakIndex++;
        }
    }

    /// <summary>
    /// Shifts the display image lying within the active field one pel height opposite the
    /// row direction (-dy), the way a real terminal makes room for the held row. In the
    /// framebuffer (Y down), a positive dy walks rows up the screen, so the content moves
    /// toward larger Y. The strip the shift vacates - the held row's home - clears to
    /// nominal black in color modes 0/1 and the background color in mode 2, matching the
    /// text scroll rule (6.2.7.13).
    /// </summary>
    private void ScrollFieldRegion(Image<Rgba32> image, NaplpsState state, Size size, int shiftPixels, float dy)
    {
        var topLeft = ConvertNormalizedToPoint(size, _command.FieldMin.X, _command.FieldMax.Y);
        var bottomRight = ConvertNormalizedToPoint(size, _command.FieldMax.X, _command.FieldMin.Y);

        int x0 = Math.Clamp(topLeft.X, 0, image.Width);
        int x1 = Math.Clamp(bottomRight.X, 0, image.Width);
        int y0 = Math.Clamp(topLeft.Y, 0, image.Height);
        int y1 = Math.Clamp(bottomRight.Y, 0, image.Height);

        if (x1 <= x0 || y1 <= y0)
        {
            return;
        }

        var palette = (Drawable.UseLivePalette && Drawable.LivePalette != null) ? Drawable.LivePalette : state.ColorMap;
        var clearColor = state.ColorMode == 2 && palette.TryGetValue(state.ColorMapBackground, out var background)
            ? new Rgba32(background.Red, background.Green, background.Blue, 255)
            : new Rgba32(0, 0, 0, 255);

        int width = x1 - x0;

        image.ProcessPixelRows(accessor =>
        {
            if (dy > 0)
            {
                for (int y = y1 - 1; y >= y0 + shiftPixels; y--)
                {
                    accessor.GetRowSpan(y - shiftPixels).Slice(x0, width).CopyTo(accessor.GetRowSpan(y).Slice(x0, width));
                }

                for (int y = y0; y < Math.Min(y0 + shiftPixels, y1); y++)
                {
                    accessor.GetRowSpan(y).Slice(x0, width).Fill(clearColor);
                }
            }
            else
            {
                for (int y = y0; y < y1 - shiftPixels; y++)
                {
                    accessor.GetRowSpan(y + shiftPixels).Slice(x0, width).CopyTo(accessor.GetRowSpan(y).Slice(x0, width));
                }

                for (int y = Math.Max(y0, y1 - shiftPixels); y < y1; y++)
                {
                    accessor.GetRowSpan(y).Slice(x0, width).Fill(clearColor);
                }
            }
        });
    }

    private static ISColor GetColorForDeposit(NaplpsState state, int colorValue, int bitsPerPixel)
    {
        if (state.ColorMode == 0)
        {
            // Direct color: the specification's bits are G,R,B rounds interleaved
            // most-significant-first - g,r,b,g,r,b... - the same convention SET COLOR's
            // operand bits use (5.3.2.5.2). A packing count not divisible by three gives
            // the leading channels one extra bit; each channel scales to full intensity
            // over its own bit width.
            int g = 0, r = 0, b = 0, gBits = 0, rBits = 0, bBits = 0;

            for (int i = 0; i < bitsPerPixel; i++)
            {
                int bit = (colorValue >> (bitsPerPixel - 1 - i)) & 1;

                switch (i % 3)
                {
                    case 0: g = g << 1 | bit; gBits++; break;
                    case 1: r = r << 1 | bit; rBits++; break;
                    default: b = b << 1 | bit; bBits++; break;
                }
            }

            static byte Scale(int v, int bits) => bits == 0 ? (byte)0 : (byte)(v * 255 / ((1 << bits) - 1));

            return ISColor.FromRgb(Scale(r, rBits), Scale(g, gBits), Scale(b, bBits));
        }

        // Color modes 1 and 2, 1-bit packing: device-verified on the 8197_CHIEF capture,
        // MVDI treats the bit like text ink - set means the CURRENT drawing color (the
        // color map address selected by SELECT COLOR), clear means the background address.
        if (bitsPerPixel == 1)
        {
            byte addr = colorValue != 0 ? state.ColorMapForeground : state.ColorMapBackground;

            if (state.ColorMap.TryGetValue(addr, out var bitColor))
            {
                return bitColor.ToColor().ToISColor();
            }
        }

        // Wider packings: the specification is a color map address.
        byte paletteIndex = (byte)(colorValue & 0xFF);

        if (state.ColorMap.TryGetValue(paletteIndex, out var naplpsColor))
        {
            return naplpsColor.ToColor().ToISColor();
        }

        return state.ColorMode == 1
            ? state.ColorMap.GetValueOrDefault(state.ColorMapForeground, NaplpsColor.White).ToColor().ToISColor()
            : state.Foreground.ToColor().ToISColor();
    }
}
