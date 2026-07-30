// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using SixLabors.ImageSharp.Drawing.Processing;
using Brush = SixLabors.ImageSharp.Drawing.Processing.Brush;
using Brushes = SixLabors.ImageSharp.Drawing.Processing.Brushes;
using Pens = SixLabors.ImageSharp.Drawing.Processing.Pens;
using SolidBrush = SixLabors.ImageSharp.Drawing.Processing.SolidBrush;
using SixLabors.ImageSharp.Processing;
using TexturePatterns = NAPLPS.NaplpsTexture.TexturePatterns;

namespace NAPLPS.Drawing;

/// <summary>Used on every drawable shape or command.</summary>
public class Drawable
{
    public static class Options
    {
        public static bool DebugTextDrawing { get; set; } = false;

        /// <summary>
        /// When true, uses bitmap font rendering (VGA 8x16 style, nearest-neighbor scaling)
        /// for pixel-accurate PP3-matching output. When false, uses TrueType font rendering
        /// with system font fallback for high-resolution output.
        /// </summary>
        public static bool UseBitmapFont { get; set; } = false;

        /// <summary>
        /// Display-model RGB gun width in bits, mirroring the "RGB color gun width" concept
        /// from period NAPLPS decoders (MVDI drivers): at display time each gun is reduced
        /// to its top N bits and expanded back by bit replication. Null renders full-precision
        /// color. Prodigy's display drivers used width 2 (levels 0/85/170/255), confirmed
        /// empirically against DAC captures of the original renderer.
        /// ThreadStatic and re-established per render (DrawContext.BeginRender) so parallel and
        /// batch renders of files with differing gun widths do not contaminate each other.
        /// </summary>
        [ThreadStatic]
        public static int? ColorGunWidth;

        /// <summary>
        /// Render text with hard pixel edges (anti-aliasing off), matching a device-resolution
        /// character generator instead of the smooth TrueType preview. ThreadStatic and
        /// re-established per render (DrawContext.BeginRender). Note: even with hard edges the
        /// bundled font is the Prodigy *Windows*-engine TTF, whose glyph shapes differ from the
        /// DOS MVDI character generator; matching shapes needs the vector-stroke font.
        /// </summary>
        [ThreadStatic]
        public static bool HardText;

        /// <summary>
        /// Render text with MVDI's vector-stroke glyphs (see <see cref="MvdiFont"/>) drawn on
        /// the integer pel plotter to match the device character generator, instead of the anti-aliased
        /// TrueType path. Defaults on for Prodigy. ThreadStatic, re-established per render.
        /// </summary>
        [ThreadStatic]
        public static bool UseMvdiFont;

        /// <summary>
        /// Authentic geometry mode: draw lines/outlines with an integer pel plotter and no
        /// anti-aliasing, reproducing the hard staircase of the original device line rasterizer
        /// instead of the anti-aliased swept-pel polygon fill. ThreadStatic and re-established
        /// per render (DrawContext.BeginRender). Since MVDI draws glyphs as vector strokes, this
        /// is also the basis for authentic text.
        /// </summary>
        [ThreadStatic]
        public static bool AuthenticGeometry;

        /// <summary>
        /// Smooth shape edges instead of the device's hard pixel edges. OFF by default, because
        /// none of the hardware this renders for anti-aliased anything - and because ImageSharp's
        /// anti-aliased fill of a CONCAVE polygon is not bit-reproducible across x64 and arm64
        /// (issue #45), so leaving it on makes rendered output architecture-dependent and the
        /// visual baselines unshareable. Turn it on for a smoother on-screen preview; leave it off
        /// for anything whose pixels are compared, exported or archived.
        /// ThreadStatic and re-established per render (DrawContext.BeginRender).
        /// </summary>
        [ThreadStatic]
        public static bool Antialias;
    }

    /// <summary>
    /// When set alongside UseLivePalette, color resolution uses this palette instead of
    /// the per-command state's ColorMap. This enables palette animation (blink, palette cycling)
    /// — modifications to LivePalette are immediately visible on re-render without changing
    /// historical state snapshots.
    /// ThreadStatic ensures each thread gets its own palette for safe parallel rendering.
    /// </summary>
    [ThreadStatic]
    public static Dictionary<byte, NaplpsColor>? LivePalette;

    /// <summary>
    /// When true, drawing commands use LivePalette for color resolution (palette animation mode).
    /// When false, drawing commands use their historical per-command state ColorMap.
    /// Only set to true during blink/palette animation re-renders.
    /// </summary>
    [ThreadStatic]
    public static bool UseLivePalette;

    private readonly NaplpsCommand _baseCommand;
    private readonly NaplpsState _state;

    public Drawable(NaplpsCommand baseCommand)
    {
        _baseCommand = baseCommand;
        _state = _baseCommand.State ?? new();
    }

    public System.Drawing.Point GetScaledLogicalPel(Size size)
    {
        var drawingCommand = (GeometricDrawingCommandBase)_baseCommand;
        var logicalPel = drawingCommand.LogicalPel;

        // The logical pel maps ISOTROPICALLY to device pixels at the X (width) scale on BOTH axes.
        // Position mapping keeps the Prodigy DisplayRatio (vertical shrink), but the pel SIZE does
        // NOT: verified pixel-exact against the reference render, a 1/256 pel -> round(2.5)=3 px and a
        // 3/256 pel -> round(7.5)=8 px in BOTH X and Y. Applying the 0.80 vertical shrink to the pel
        // height (the old ConvertNormalizedToScreenScale path) lost 1 px whenever logPel.Y*640 landed
        // on a half-integer (e.g. 3/256 -> 7.03 -> 7 instead of 8), thinning every stroke's top edge.
        int pelX = (int)Math.Floor(Math.Abs(logicalPel.X) * size.Width + 0.5);
        int pelY = (int)Math.Floor(Math.Abs(logicalPel.Y) * size.Width + 0.5);

        return new System.Drawing.Point(Math.Max(1, pelX), Math.Max(1, pelY));
    }

    /// <summary>Hard pixel edges, like the device rasterizer. The default everywhere.</summary>
    internal static DrawingOptions HardEdgedDrawingOptions { get; } = new()
    {
        GraphicsOptions = new GraphicsOptions { Antialias = false }
    };

    /// <summary>Smoothed edges, for the modern preview. Only used when opted in.</summary>
    internal static DrawingOptions SmoothDrawingOptions { get; } = new()
    {
        GraphicsOptions = new GraphicsOptions { Antialias = true }
    };

    /// <summary>
    /// The drawing options every shape fill and outline goes through. Hard-edged unless
    /// <see cref="Options.Antialias"/> has been turned on.
    /// </summary>
    internal static DrawingOptions FillOptions()
        => Options.Antialias ? SmoothDrawingOptions : HardEdgedDrawingOptions;

    /// <summary>
    /// Fills a closed shape. Hard-edged fills go through <see cref="ScanlineFiller"/> rather than
    /// ImageSharp, because ImageSharp's non-antialiased fill is not reproducible across CPU
    /// architectures (issue #45). Anti-aliased fills are preview-only and stay on ImageSharp.
    /// </summary>
    internal static void FillShape(Image<Rgba32> image, ReadOnlySpan<PointF> points, Brush brush, in FillSource source)
    {
        if (Options.Antialias)
        {
            var array = points.ToArray();

            image.Mutate(x => x.FillPolygon(SmoothDrawingOptions, brush, array));

            return;
        }

        ScanlineFiller.Fill(image, points, source);
    }

    /// <summary>Flat-colour variant of <see cref="FillShape"/>.</summary>
    internal static void FillShape(Image<Rgba32> image, ReadOnlySpan<PointF> points, ISColor color)
    {
        if (Options.Antialias)
        {
            var array = points.ToArray();

            image.Mutate(x => x.FillPolygon(SmoothDrawingOptions, color, array));

            return;
        }

        ScanlineFiller.Fill(image, points, new FillSource(color.ToPixel<Rgba32>()));
    }

    /// <summary>
    /// Strokes a path the way ImageSharp's solid pen would, but deterministically: ImageSharp still
    /// COMPUTES the stroke outline (pure geometry, IEEE-exact ops), and <see cref="ScanlineFiller"/>
    /// rasterizes it. It is the rasterization - thresholding exactly-0.5 coverage - that differs
    /// between x64 and arm64 (issue #45), not the geometry, so this keeps the pen's appearance
    /// while removing the platform dependence. Replacing the geometry instead (pel sweep, or a
    /// hand-rolled quad stroke) changed 64 corpus files by hundreds of millions of pixels.
    /// </summary>
    internal static void StrokePathHard(Image<Rgba32> image, IPath path, float width, ISColor color)
    {
        // Byte-for-byte what DrawPathProcessor does for a solid pen: translate the path by
        // (+0.5, +0.5) FIRST ("align drawing outlines to pixel centers"), THEN outline it with the
        // pen's default joint and cap. Translating after outlining is geometrically identical but
        // produces different float bits, which lands snapped vertices on different subrows.
        var outline = path.Transform(System.Numerics.Matrix3x2.CreateTranslation(0.5f, 0.5f))
                          .GenerateOutline(width, JointStyle.Square, EndCapStyle.Butt);
        var contours = new List<PointF[]>();

        foreach (var simple in outline.Flatten())
        {
            contours.Add(simple.Points.ToArray());
        }

        // PenRasterizer, not ScanlineFiller: the centre-sampled filler visibly reshapes thin
        // strokes (a 2px ring is ALL boundary), while PenRasterizer reproduces ImageSharp's
        // coverage rasterization with a single cross-platform tie rule.
        PenRasterizer.FillEvenOdd(image, contours, color.ToPixel<Rgba32>());
    }

    /// <summary>Axis-aligned rectangle as four corners, so it goes through the same filler.</summary>
    internal static void FillRect(Image<Rgba32> image, RectangleF rect, ISColor color)
    {
        Span<PointF> corners =
        [
            new(rect.Left, rect.Top),
            new(rect.Right, rect.Top),
            new(rect.Right, rect.Bottom),
            new(rect.Left, rect.Bottom),
        ];

        FillShape(image, corners, color);
    }

    /// <summary>
    /// The hard-edged twin of <see cref="GetFillBrush"/> - same rules, expressed as flat data the
    /// scanline filler can consume. Kept adjacent to it so the two cannot drift apart.
    /// </summary>
    internal FillSource GetFillSource(Size size, Color fgColor, Color bgColor)
    {
        var fillableCommand = (GeometricDrawingCommandBase)_baseCommand;
        var texturePattern = fillableCommand.Texture.TexturePattern;
        var logicalPel = fillableCommand.LogicalPel;

        var fg = fgColor.ToISColor().ToPixel<Rgba32>();

        if (texturePattern == TexturePatterns.Solid || (logicalPel.X == 0 && logicalPel.Y == 0))
        {
            return new FillSource(fg);
        }

        // Modes 0/1: background is transparent, so the gaps are left untouched.
        Rgba32? bg = fillableCommand.ColorMode != 2 ? null : bgColor.ToISColor().ToPixel<Rgba32>();

        var scaledLogicalPel = GetScaledLogicalPel(size);

        if (Options.AuthenticGeometry)
        {
            int rx = Math.Max(1, (int)Math.Round(Math.Abs(logicalPel.X) * size.Width, MidpointRounding.AwayFromZero));
            int ry = Math.Max(1, (int)Math.Round(Math.Abs(logicalPel.Y) / NaplpsUtils.DisplayRatio * size.Height, MidpointRounding.AwayFromZero));
            scaledLogicalPel = new System.Drawing.Point(rx, ry);
        }

        switch (texturePattern)
        {
            case TexturePatterns.MaskA:
            case TexturePatterns.VerticalHatching:
            {
                var pattern = new bool[1, scaledLogicalPel.X * 2];

                for (var i = 0; i < pattern.Length; ++i)
                {
                    pattern[0, i] = Options.AuthenticGeometry ? i < pattern.Length / 2 : i >= pattern.Length / 2;
                }

                return new FillSource(fg, bg, pattern);
            }

            case TexturePatterns.MaskB:
            case TexturePatterns.HorizontalHatching:
            {
                var pelY = Math.Max(1, scaledLogicalPel.Y);
                var pattern = new bool[pelY * 2, 1];

                for (var i = 0; i < pattern.Length; ++i)
                {
                    pattern[i, 0] = i >= pelY;
                }

                return new FillSource(fg, bg, pattern);
            }

            case TexturePatterns.MaskC:
            case TexturePatterns.CrossHatching:
            {
                var pelX = Math.Max(1, scaledLogicalPel.X);
                var pelY = Math.Max(1, scaledLogicalPel.Y);
                var width = pelX * 2;
                var height = pelY * 2;
                var pattern = new bool[height, width];

                for (var y = 0; y < height; ++y)
                {
                    for (var x = 0; x < width; ++x)
                    {
                        pattern[y, x] = y >= pelY || x < pelX;
                    }
                }

                return new FillSource(fg, bg, pattern);
            }

            default:
            {
                return new FillSource(fg);
            }
        }
    }

    /// <summary>
    /// Authentic pel for dotted/dashed line texture: the pel footprint with BOTH axes scaled by
    /// the render width (the reference render's convention, e.g. an isotropic 1/256 pel -> 3x3),
    /// plus the per-axis dash units. Offsets follow the sign of the logical pel. Callers stepping
    /// a dash counter along a stroke use the unit for the stroke's dominant axis - TL80TB10's
    /// venetian beams (a 255/256 x 1/256 pel swept a short way vertically) stamp the full-width
    /// flat pel every |pel.Y| rows on the device, producing 3-on/3-off stripes, not a square stamp.
    /// Returns (ox0, ox1, oy0, oy1, pelX, pelY).
    /// </summary>
    internal (int ox0, int ox1, int oy0, int oy1, int pelX, int pelY) GetDashPel(Size size)
    {
        var lp = ((GeometricDrawingCommandBase)_baseCommand).LogicalPel;
        int px = Math.Max(1, (int)MathF.Round(Math.Abs(lp.X) * size.Width, MidpointRounding.AwayFromZero));
        int py = Math.Max(1, (int)MathF.Round(Math.Abs(lp.Y) * size.Width, MidpointRounding.AwayFromZero));
        int ox0 = lp.X >= 0 ? 0 : -px;
        int ox1 = lp.X >= 0 ? px : 0;
        int oy0 = lp.Y >= 0 ? -py : 0;
        int oy1 = lp.Y >= 0 ? 0 : py;
        return (ox0, ox1, oy0, oy1, px, py);
    }

    /// <summary>
    /// The dash unit for a stroke: the dash-pel dimension along the stroke's dominant axis
    /// (X for a mostly-horizontal stroke, Y for a mostly-vertical one). Isotropic pels are
    /// unaffected; only anisotropic pels (the venetian-beam pattern) differ per axis.
    /// </summary>
    internal static int DashUnitForStroke(IReadOnlyList<SixLabors.ImageSharp.PointF> points, int pelX, int pelY)
    {
        float spanX = 0, spanY = 0;

        for (var i = 0; i + 1 < points.Count; i++)
        {
            spanX += Math.Abs(points[i + 1].X - points[i].X);
            spanY += Math.Abs(points[i + 1].Y - points[i].Y);
        }

        return spanX >= spanY ? pelX : pelY;
    }

    /// <summary>
    /// Gets the pel X/Y offsets for the convex hull sweep, respecting the logical pel origin.
    /// ANSI X3.110: The pel is NOT centered. The drawing point sits at a corner determined
    /// by the sign of the pel dimensions:
    ///   +W, +H → drawing point at lower-left  → pel extends RIGHT and UP
    ///   +W, -H → drawing point at upper-left  → pel extends RIGHT and DOWN
    ///   -W, +H → drawing point at lower-right → pel extends LEFT and UP
    ///   -W, -H → drawing point at upper-right → pel extends LEFT and DOWN
    /// Returns (dxMin, dxMax, dyMin, dyMax) offsets from the drawing point in SCREEN coords.
    /// </summary>
    internal (float dxMin, float dxMax, float dyMin, float dyMax) GetPelOffsets(Size size)
    {
        var drawingCommand = (GeometricDrawingCommandBase)_baseCommand;
        var logicalPel = drawingCommand.LogicalPel;
        var scaledPel = GetScaledLogicalPel(size);
        float pelW = scaledPel.X;
        float pelH = scaledPel.Y;

        // In screen coords (Y-down):
        // Positive NAPLPS width  → extends RIGHT → dxMin=0, dxMax=+pelW
        // Negative NAPLPS width  → extends LEFT  → dxMin=-pelW, dxMax=0
        // Positive NAPLPS height → extends UP (screen Y decreases) → dyMin=-pelH, dyMax=0
        // Negative NAPLPS height → extends DOWN (screen Y increases) → dyMin=0, dyMax=+pelH
        float dxMin = logicalPel.X >= 0 ? 0 : -pelW;
        float dxMax = logicalPel.X >= 0 ? pelW : 0;
        float dyMin = logicalPel.Y >= 0 ? -pelH : 0;
        float dyMax = logicalPel.Y >= 0 ? 0 : pelH;

        return (dxMin, dxMax, dyMin, dyMax);
    }

    public float GetPenWidth(Size size)
    {
        var scaledLogicalPel = GetScaledLogicalPel(size);

        return Math.Max(scaledLogicalPel.X, scaledLogicalPel.Y);
    }

    /// <summary>
    /// Gets the pen width using float precision (avoids integer truncation artifacts
    /// for outlines that need to align precisely with subsequent fills).
    /// </summary>
    public float GetPenWidthF(Size size)
    {
        var drawingCommand = (GeometricDrawingCommandBase)_baseCommand;
        var logicalPel = drawingCommand.LogicalPel;
        float pelX = MathF.Abs(logicalPel.X * size.Width);
        float pelY = MathF.Abs(logicalPel.Y / 0.80f * size.Height);
        return MathF.Max(MathF.Max(pelX, pelY), 1f);
    }

    internal (Brush, Pen) GetBrushAndPenFromFillableCommand(Size size, NaplpsState? renderState = null)
    {
        var fillableCommand = (FillableGeometricDrawingCommandBase)_baseCommand;

        var (fgColor, bgColor) = fillableCommand.GetColors(renderState ?? _state);

        var brush = GetFillBrush(size, fgColor, bgColor);

        var penWidth = GetPenWidth(size);
        Pen pen;

        if (fillableCommand.ShouldFill && fillableCommand.Texture.ShouldHighlight)
        {
            // ANSI X3.110: Highlighted filled shapes are outlined with SOLID line texture
            // (ignoring current line texture), using nominal black in modes 0/1,
            // or background color in mode 2. Use the command's OWN color-mode snapshot: _state is
            // the shared, final (EOF) parse state, so its ColorMode is not this command's.
            var highlightColor = fillableCommand.ColorMode == 2 ? bgColor : Color.Black;
            pen = Pens.Solid(highlightColor.ToISColor(), penWidth);
        }
        else
        {
            var penColor = fillableCommand.ShouldFill ? bgColor : fgColor;
            pen = GetTexturedPen(penColor.ToISColor(), penWidth);
        }

        return (brush, pen);
    }

    internal Pen GetTexturedPen(SixLabors.ImageSharp.Color color, float penWidth)
    {
        var fillableCommand = (GeometricDrawingCommandBase)_baseCommand;
        var lineTexture = fillableCommand.Texture.LineTexture;

        // ANSI X3.110 line texture patterns are defined in terms of logical pel size:
        // - Dot: 1 pel on, 1 pel off
        // - Dash: 3 pels on, 1 pel off
        // - Dot-Dash: 1 pel on, 1 pel off, 3 pels on, 1 pel off
        // PatternPen multiplies each entry by the pen width (which is the pel size).
        switch (lineTexture)
        {
            case NaplpsTexture.LineTextures.Dotted:
            {
                return new PatternPen(color, penWidth, new float[] { 1f, 1f });
            }

            case NaplpsTexture.LineTextures.Dashed:
            {
                return new PatternPen(color, penWidth, new float[] { 3f, 1f });
            }

            case NaplpsTexture.LineTextures.DottedDashed:
            {
                return new PatternPen(color, penWidth, new float[] { 1f, 1f, 3f, 1f });
            }

            default:
            {
                return Pens.Solid(color, penWidth);
            }
        }
    }

    internal Brush GetFillBrush(Size size, Color fgColor, Color bgColor)
    {
        var fillableCommand = (GeometricDrawingCommandBase)_baseCommand;
        var texturePattern = fillableCommand.Texture.TexturePattern;

        var fgColorImageSharp = fgColor.ToISColor();
        var bgColorImageSharp = bgColor.ToISColor();

        // ANSI X3.110 §5.3.3.5: "Even when the logical pel size is (0,0), the solid fill
        // is still drawn." At pel (0,0), hatching patterns produce solid fills.
        // Only non-zero pel sizes produce visible hatching.
        var logicalPel = fillableCommand.LogicalPel;

        if (texturePattern == TexturePatterns.Solid || (logicalPel.X == 0 && logicalPel.Y == 0))
        {
            return Brushes.Solid(fgColorImageSharp);
        }

        // Modes 0/1: background is transparent (gaps show underlying canvas)
        if (fillableCommand.ColorMode != 2)
        {
            bgColorImageSharp = ISColor.Transparent;
        }

        var scaledLogicalPel = GetScaledLogicalPel(size);

        // In authentic mode the hatch stripe width uses round-half-up of the device pel (e.g. a
        // 1/256 pel -> 2.5px -> 3px), matching the reference render's stripes, rather than the truncated
        // GetScaledLogicalPel (which yields 2px). Line thickness keeps the truncated pel.
        if (Options.AuthenticGeometry)
        {
            int rx = Math.Max(1, (int)Math.Round(Math.Abs(logicalPel.X) * size.Width, MidpointRounding.AwayFromZero));
            int ry = Math.Max(1, (int)Math.Round(Math.Abs(logicalPel.Y) / NaplpsUtils.DisplayRatio * size.Height, MidpointRounding.AwayFromZero));
            scaledLogicalPel = new System.Drawing.Point(rx, ry);
        }

        switch (texturePattern)
        {
            case TexturePatterns.MaskA:
            case TexturePatterns.VerticalHatching:
            {
                var pattern = new bool[1, scaledLogicalPel.X * 2];

                for (var i = 0; i < pattern.Length; ++i)
                {
                    pattern[0, i] = Options.AuthenticGeometry ? i < pattern.Length / 2 : i >= pattern.Length / 2;
                }

                return new PatternBrush(fgColorImageSharp, bgColorImageSharp, pattern);
            }

            case TexturePatterns.MaskB:
            case TexturePatterns.HorizontalHatching:
            {
                var pelY = Math.Max(1, scaledLogicalPel.Y);
                var pattern = new bool[pelY * 2, 1];

                for (var i = 0; i < pattern.Length; ++i)
                {
                    pattern[i, 0] = i >= pelY;
                }

                return new PatternBrush(fgColorImageSharp, bgColorImageSharp, pattern);
            }

            case TexturePatterns.MaskC:
            case TexturePatterns.CrossHatching:
            {
                var pelX = Math.Max(1, scaledLogicalPel.X);
                var pelY = Math.Max(1, scaledLogicalPel.Y);
                var width = pelX * 2;
                var height = pelY * 2;
                var pattern = new bool[height, width];

                for (var y = 0; y < height; ++y)
                {
                    for (var x = 0; x < width; ++x)
                    {
                        pattern[y, x] = y >= pelY || x < pelX;
                    }
                }

                return new PatternBrush(fgColorImageSharp, bgColorImageSharp, pattern);
            }

            default:
            {
                return Brushes.Solid(fgColorImageSharp);
            }
        }
    }

    internal (NaplpsColor, NaplpsColor) GetNaplpsColorFromState(NaplpsState? state)
    {
        if (state == null)
        {
            state = _state;
        }

        // Normal rendering: use historical per-command palette snapshot.
        // Palette animation (blink): use LivePalette so CLUT changes are visible.
        var palette = (UseLivePalette && LivePalette != null) ? LivePalette : state.ColorMap;
        var fgColor = state.ColorMode == 0 ? state.Foreground : palette[state.ColorMapForeground];
        var bgColor = state.ColorMode == 0 ? state.Background : palette[state.ColorMapBackground];

        return (fgColor, bgColor);
    }

    internal (Color, Color) GetColorFromState(NaplpsState? state)
    {
        var (fgColor, bgColor) = GetNaplpsColorFromState(state);

        return (fgColor.ToColor(), bgColor.ToColor());
    }

    internal (ISColor, ISColor) GetISColorFromState(NaplpsState? state)
    {
        var (fgColor, bgColor) = GetColorFromState(state);

        return (fgColor.ToISColor(), bgColor.ToISColor());
    }

    internal (SolidBrush, SolidPen) GetBrushAndPenFromState(NaplpsState? state)
    {
        var (fgColor, bgColor) = GetISColorFromState(state);

        return (Brushes.Solid(bgColor), Pens.Solid(fgColor, state.LogicalPel.X == 0 ? 1 : state.LogicalPel.X));
    }

    /// <summary>
    /// Draws a shape outline using rectangular logical pel sweep.
    /// Sweeps the pel along each edge of the given polygon points.
    /// </summary>
    internal void DrawOutlineWithPelSweep(Image<Rgba32> image, PointF[] points, ISColor color, Size size, bool closePath = true)
    {
        var (dxMin, dxMax, dyMin, dyMax) = GetPelOffsets(size);

        int edgeCount = closePath ? points.Length : points.Length - 1;

        for (int i = 0; i < edgeCount; i++)
        {
            var p1 = points[i];
            var p2 = points[(i + 1) % points.Length];

            FillShape(image, DrawableLine.PerpendicularHullOfSweptPel(p1, p2, dxMin, dxMax, dyMin, dyMax), color);
        }
    }

    /// <summary>
    /// The color a dotted or dashed stroke's GAPS are painted with, or null when they are left
    /// transparent. Under color mode 2 the device paints them with the background color, so the
    /// stroke covers its whole path; under modes 0/1 the canvas shows through. This is the same
    /// rule <see cref="GetFillBrush"/> already applies to the texture PATTERNS of a fill.
    /// </summary>
    internal static ISColor? TextureGapColor(int colorMode, Color backgroundColor)
    {
        return colorMode == 2 ? backgroundColor.ToISColor() : null;
    }

    /// <summary>
    /// The gap color for a command that carries no color-mode snapshot of its own and is drawn
    /// straight from the render state it is handed.
    /// </summary>
    internal ISColor? TextureGapColor(NaplpsState state)
    {
        var (_, bgColor) = GetColorFromState(state);

        return TextureGapColor(state.ColorMode, bgColor);
    }

    /// <summary>
    /// Gets the outline color for a fillable command per NAPLPS spec.
    /// Non-filled: foreground color. Filled+highlight: nominal black (modes 0/1) or background (mode 2).
    /// </summary>
    internal ISColor GetOutlineColor()
    {
        var fillableCommand = (FillableGeometricDrawingCommandBase)_baseCommand;
        var (fgColor, bgColor) = fillableCommand.GetColors(_state);

        if (fillableCommand.ShouldFill && fillableCommand.Texture.ShouldHighlight)
        {
            // Use the command's own color-mode snapshot, not the shared final parse state.
            return (fillableCommand.ColorMode == 2 ? bgColor : Color.Black).ToISColor();
        }

        return (fillableCommand.ShouldFill ? bgColor : fgColor).ToISColor();
    }
}