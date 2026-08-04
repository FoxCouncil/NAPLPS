// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using SixLabors.ImageSharp.Formats.Bmp;

namespace NAPLPSApp;

public static class Extensions
{
    public static Bitmap ToBitmap(this DrawContext ctx)
    {
        using var memoryStream = new MemoryStream();

        ctx.Image.Save(memoryStream, new BmpEncoder());

        memoryStream.Position = 0;

        return new Bitmap(memoryStream);
    }
}
