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
        if (!_command.IsValid || _command.Deposits.Count == 0)
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

        image.Mutate(ctx =>
        {
            foreach (var deposit in _command.Deposits)
            {
                // The pel extends dx/dy from the drawing point with their signs; the rect's
                // top-left in screen coordinates is the min-X / max-Y normalized corner.
                float cornerX = dx > 0 ? deposit.X : deposit.X + dx;
                float cornerY = dy > 0 ? deposit.Y + dy : deposit.Y;

                var point = ConvertNormalizedToPoint(size, cornerX, cornerY);
                var color = GetColorForDeposit(state, deposit.ColorValue, _command.BitsPerPixel);
                ctx.Fill(FillOptions(), color, new RectangleF(point.X, point.Y, pelWidth, pelHeight));
            }
        });
    }

    private static ISColor GetColorForDeposit(NaplpsState state, int colorValue, int bitsPerPixel)
    {
        if (state.ColorMode == 0)
        {
            // Direct color: the packed bits are G,R,B three-tuples, first tuple = MSBs
            // (5.3.3.6.3). Distribute the packing count across the three primaries.
            int bitsPerChannel = Math.Max(1, bitsPerPixel / 3);
            int maxValue = (1 << bitsPerChannel) - 1;

            int g = (colorValue >> (bitsPerChannel * 2)) & maxValue;
            int r = (colorValue >> bitsPerChannel) & maxValue;
            int b = colorValue & maxValue;

            return ISColor.FromRgb(
                (byte)(r * 255 / maxValue),
                (byte)(g * 255 / maxValue),
                (byte)(b * 255 / maxValue));
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
