// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;

namespace NAPLPS.Drawing;

public class DrawContext : IDisposable
{
    private bool disposedValue;

    private readonly MemoryStream memoryStream = new();

    // Track last displayed character for Repeat command
    private AsciiCharCommand? _lastDisplayedChar;

    /// <summary>Blink animator for palette animation. Initialized after render completes.</summary>
    public BlinkAnimator? BlinkAnimator { get; private set; }

    public NaplpsFormat NAPLPS { get; }

    public Size Size { get; }

    public Image<Rgba32> Image { get; }

    /// <summary>
    /// Invoked after every command is rendered, with that command's index, the command itself, and
    /// the live canvas. Null during normal rendering. Exists so the canvas can be hashed
    /// command-by-command on two machines and the FIRST diverging command identified, rather than
    /// only learning that a finished frame differs somewhere. See issue #45.
    /// </summary>
    public Action<int, NaplpsCommand, Image<Rgba32>>? CommandRendered { get; set; }

    public event Action? OnImageUpdated;

    public uint CurrentIndex { get; set; }

    public uint TotalFrames { get; set; }

    /// <summary>
    /// When true, rendering uses the final parsed state's ColorMap as a live palette.
    /// All drawables resolve colors from this shared palette instead of their historical snapshots.
    /// This enables palette animation effects (blink, palette cycling) where previously-drawn
    /// objects retroactively change color when palette entries are modified.
    /// </summary>
    public bool PaletteAnimationMode { get; set; }

    /// <summary>
    /// Display-model RGB gun width in bits (see <see cref="Drawable.Options.ColorGunWidth"/>).
    /// Defaults to 2 for Prodigy-detected files and null (full precision) otherwise, but can be
    /// overridden by the caller — e.g. when rendering a known-Prodigy corpus whose files do not
    /// carry the A1 C8 domain marker and so are not auto-detected as Prodigy.
    /// </summary>
    public int? ColorGunWidth { get; set; }

    /// <summary>
    /// Vertical display ratio for this render (see <see cref="NaplpsUtils.DisplayRatio"/>).
    /// Defaults to the Prodigy-calibrated ratio for Prodigy-detected files and the standard
    /// ratio otherwise; overridable by the caller (e.g. a known-Prodigy corpus not auto-detected).
    /// </summary>
    public float DisplayRatio { get; set; } = NaplpsUtils.DefaultDisplayRatio;

    /// <summary>
    /// Render text with hard pixel edges (see <see cref="Drawable.Options.HardText"/>).
    /// Defaults on for Prodigy-detected files, off (anti-aliased) otherwise.
    /// </summary>
    public bool HardText { get; set; }

    /// <summary>
    /// Render text with MVDI's vector-stroke font (see <see cref="MvdiFont"/>).
    /// Defaults on for Prodigy (its authentic character generator), off otherwise.
    /// </summary>
    public bool UseMvdiFont { get; set; }

    /// <summary>
    /// Render geometry with the integer pel plotter and no anti-aliasing
    /// (see <see cref="Drawable.Options.AuthenticGeometry"/>).
    /// </summary>
    public bool AuthenticGeometry { get; set; }

    /// <summary>
    /// Smooth shape edges instead of the device's hard pixel edges. Defaults to FALSE: no hardware
    /// this renders for anti-aliased anything, and ImageSharp's anti-aliased concave-polygon fill
    /// is not reproducible across CPU architectures (issue #45), so enabling it makes output
    /// machine-dependent. Opt in for an on-screen preview; leave off for anything compared or
    /// archived - the visual baseline suite deliberately runs with it off.
    /// </summary>
    public bool Antialias { get; set; }

    public DrawContext() { }

    public DrawContext(NaplpsFormat naplps, Size size)
    {
        NAPLPS = naplps ?? throw new ArgumentNullException(nameof(naplps));
        Size = size;
        Image = new(Size.Width, Size.Height);
        CurrentIndex = 0;
        TotalFrames = (uint)NAPLPS.Commands.Count - 1;

        // Prodigy display drivers had a 2-bit-per-gun DAC mapping; other systems
        // render full-precision color until their gun widths are established.
        ColorGunWidth = NAPLPS.SystemType == NaplpsSystemType.Prodigy ? 2 : null;

        // Prodigy's display driver used a slightly taller vertical mapping than the
        // NAPLPS default, calibrated against the reference render.
        DisplayRatio = NAPLPS.SystemType == NaplpsSystemType.Prodigy
            ? NaplpsUtils.ProdigyDisplayRatio
            : NaplpsUtils.DefaultDisplayRatio;

        // Prodigy's device-resolution character generator produced hard-edged text drawn from
        // MVDI's own vector-stroke font.
        UseMvdiFont = NAPLPS.SystemType == NaplpsSystemType.Prodigy;

        // The original device line rasterizer drew hard staircased strokes; match it for Prodigy.
        AuthenticGeometry = NAPLPS.SystemType == NaplpsSystemType.Prodigy;
    }

    public void Render(uint sequenceNumber = uint.MaxValue)
    {
        BeginRender();

        // Build running CLUT palette to detect mid-stream palette changes
        var clutPalette = new Dictionary<byte, NaplpsColor>(NAPLPS.Commands[0].State.ColorMap);
        bool clutDirty = false;

        foreach (var seq in NAPLPS.Commands)
        {
            var command = seq.Command;
            var state = seq.State;

            // Track palette changes for CLUT animation. Prodigy MVDI has a fixed hardware
            // palette and ignores SET COLOR redefinition, so never rebind the CLUT there.
            if (command is SetColorCommand setColor &&
                NAPLPS.SystemType != NaplpsSystemType.Prodigy &&
                (state.ColorMode == 1 || state.ColorMode == 2) &&
                setColor.Operands.Count > 0)
            {
                clutPalette[state.ColorMapForeground] = setColor.Color;
                clutDirty = true;
            }

            RenderCommand(command, state);

            if (CurrentIndex == sequenceNumber)
            {
                break;
            }

            CurrentIndex++;
        }

        // If palette changed during rendering, do a final CLUT-correct re-render
        // so the output shows retroactive palette effects (e.g., eye blink).
        if (clutDirty && !PaletteAnimationMode)
        {
            ClutReRender(clutPalette, (int)Math.Min(sequenceNumber, TotalFrames));
        }

        EndRender();
        OnImageUpdated?.Invoke();
    }

    public async Task RenderAsync(CancellationToken cancellationToken, uint delay)
    {
        BeginRender();

        foreach (var sequence in NAPLPS.Commands)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (command, state) = sequence;
            var drawable = RenderCommand(command, state);

            OnImageUpdated?.Invoke();
            CurrentIndex++;

            if (command is WaitCommand waitCmd && waitCmd.IsValid)
            {
                // PP3 doesn't parse WAIT timing — it processes data bytes at CPU speed.
                // Use a short fixed delay for visual feedback (e.g., CLUT eye blink).
                await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);
            }
            else if (drawable != null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(delay), cancellationToken);
            }
        }

        EndRender();
    }

    private void BeginRender()
    {
        CurrentIndex = 0;
        _lastDisplayedChar = null;

        // Re-establish per-render display options on this thread right before drawing, so
        // parallel/batch renders of files with different settings do not contaminate.
        Drawable.Options.ColorGunWidth = ColorGunWidth;
        Drawable.Options.HardText = HardText;
        Drawable.Options.UseMvdiFont = UseMvdiFont;
        Drawable.Options.AuthenticGeometry = AuthenticGeometry;
        Drawable.Options.Antialias = Antialias;
        NaplpsUtils.DisplayRatio = DisplayRatio;

        // Clear canvas (important for loop restarts and re-renders)
        Image.Mutate(ctx => ctx.Fill(ISColor.Black));

        // LivePalette holds the final palette for scroll clear and palette animation.
        // UseLivePalette controls whether drawing commands use it:
        //   false (normal render) → commands use their historical per-command ColorMap
        //   true  (palette animation) → commands use LivePalette for CLUT blink effects
        Drawable.LivePalette = NAPLPS.State.ColorMap;
        Drawable.UseLivePalette = PaletteAnimationMode;
    }

    private void EndRender()
    {
        if (CurrentIndex >= TotalFrames)
        {
            CurrentIndex = TotalFrames;
        }

        Drawable.LivePalette = null;
        Drawable.UseLivePalette = false;
    }

    /// <summary>
    /// Renders a single command. Returns the drawable if one was created (for delay timing).
    /// </summary>
    private IDrawable? RenderCommand(NaplpsCommand command, NaplpsState state)
    {
        // Handle scroll: shift image pixels up when scroll event occurs.
        // Scroll = paper feed: shift pixels, then draw at same coords fills fresh area.
        if (state.ScrollEventOccurred)
        {
            ScrollImage(state);
        }

        var drawable = ConvertToDrawable(command, state);

        // Track last displayed character for Repeat (skip discarded chars from word wrap)
        if (command is AsciiCharCommand asciiChar && !asciiChar.IsDiscarded)
        {
            _lastDisplayedChar = asciiChar;
        }

        // Handle Repeat specially
        if (drawable is DrawableRepeat repeatDrawable)
        {
            RenderRepeat(repeatDrawable, state);
        }
        else
        {
            drawable?.Draw(Image, state, Size);
        }

        return drawable;
    }

    /// <summary>
    /// CLUT re-render: replays all commands from 0 to upToIndex using the given palette
    /// for color resolution. This produces CLUT-correct output where palette changes
    /// retroactively affect previously-drawn shapes (e.g., eye blink animation).
    /// </summary>
    private void ClutReRender(Dictionary<byte, NaplpsColor> clutPalette, int upToIndex)
    {
        // Save state that gets mutated during rendering
        var savedLastChar = _lastDisplayedChar;

        // Save pen positions for states with Repeat commands (RenderRepeat mutates state.Pen)
        var savedPens = new Dictionary<int, Vector3>();
        int scanIdx = 0;

        foreach (var (cmd, cmdState) in NAPLPS.Commands)
        {
            if (scanIdx > upToIndex)
            {
                break;
            }

            if (cmd is ControlCommand cc &&
                (cc.Command == NaplpsControlCommands.Repeat || cc.Command == NaplpsControlCommands.RepeatToEOL))
            {
                savedPens[scanIdx] = cmdState.Pen;
            }

            scanIdx++;
        }

        // Clear and re-render with CLUT palette
        Image.Mutate(ctx => ctx.Fill(ISColor.Black));
        Drawable.LivePalette = clutPalette;
        Drawable.UseLivePalette = true;

        _lastDisplayedChar = null;
        int idx = 0;

        foreach (var (cmd, cmdState) in NAPLPS.Commands)
        {
            if (idx > upToIndex)
            {
                break;
            }

            if (cmdState.ScrollEventOccurred)
            {
                ScrollImage(cmdState);
            }

            var drawable = ConvertToDrawable(cmd, cmdState);

            if (cmd is AsciiCharCommand asciiChar && !asciiChar.IsDiscarded)
            {
                _lastDisplayedChar = asciiChar;
            }

            if (drawable is DrawableRepeat repeatDrawable)
            {
                RenderRepeat(repeatDrawable, cmdState);
            }
            else
            {
                drawable?.Draw(Image, cmdState, Size);
            }

            idx++;
        }

        // Restore mutated pen positions
        idx = 0;

        foreach (var (cmd, cmdState) in NAPLPS.Commands)
        {
            if (idx > upToIndex)
            {
                break;
            }

            if (savedPens.TryGetValue(idx, out var savedPen))
            {
                cmdState.Pen = savedPen;
            }

            idx++;
        }

        // Restore rendering state
        Drawable.LivePalette = NAPLPS.State.ColorMap;
        Drawable.UseLivePalette = false;
        _lastDisplayedChar = savedLastChar;
    }

    private void RenderRepeat(DrawableRepeat repeatDrawable, NaplpsState state)
    {
        if (_lastDisplayedChar == null)
        {
            return;
        }

        // ANSI X3.110: REPEAT can only repeat spacing characters from the ASCII,
        // supplementary, DRCS, or mosaic sets. Non-spacing accents cannot be repeated.
        // If the preceding character is not allowed, REPEAT is discarded.
        if (_lastDisplayedChar.IsNonSpacing)
        {
            return;
        }

        var repeatCount = repeatDrawable.GetRepeatCount(state);
        var charToRepeat = _lastDisplayedChar.AsciiCharacter;

        // For Repeat (0x86) the parser advances the pen across the run (so following text starts
        // after it), and this command's state snapshot already reflects that post-run pen: rewind
        // by the run's total advance so the repeated glyphs draw at the run's true positions, then
        // re-advance. RepeatToEOL (0x87) computes its count at render time and the parser never
        // advances, so its snapshot pen already sits at the run start and must not be rewound.
        // Either way, restore the snapshot pen at the end so nothing downstream shifts.
        var snapshotPen = state.Pen;
        if (!repeatDrawable.IsRepeatToEOL)
        {
            for (int i = 0; i < repeatCount; i++)
            {
                RewindPen(state, charToRepeat);
            }
        }

        for (int i = 0; i < repeatCount; i++)
        {
            IDrawable charDrawable;
            if (state.DrcsCharacters.TryGetValue(_lastDisplayedChar.OpCode, out var bitmap))
            {
                charDrawable = new DrawableDrcsChar(_lastDisplayedChar, bitmap, state.Pen);
            }
            else
            {
                charDrawable = new DrawableAsciiChar(_lastDisplayedChar, state.Pen);
            }
            charDrawable.Draw(Image, state, Size);

            AsciiCharCommand.AdvancePen(state, charToRepeat);
        }

        state.Pen = snapshotPen;
    }

    /// <summary>Reverses one character advance (mirror of AsciiCharCommand.AdvancePen).</summary>
    private static void RewindPen(NaplpsState state, char character)
    {
        var before = state.Pen;
        AsciiCharCommand.AdvancePen(state, character);
        var delta = state.Pen - before;
        state.Pen = before - delta;
    }

    /// <summary>
    /// ANSI X3.110: Scrolls pixels up by one text line height.
    /// If the cursor is within the active field, only the field region scrolls.
    /// Otherwise, the entire display scrolls.
    /// </summary>
    private void ScrollImage(NaplpsState state)
    {
        float charHeightNorm = state.CharSize.Y * GetInterrowMultiplier(state.TextInterrowSpacing);
        int shiftPixels = Math.Max(1, (int)(charHeightNorm / 0.80f * Size.Height));

        // ANSI X3.110 §6.2.7.13: cleared pixels are nominal black in modes 0/1,
        // background color in mode 2.
        // Normal render: use historical state palette (paper color at time of scroll).
        // Palette animation: use LivePalette to stay consistent with filled shapes.
        var palette = (Drawable.UseLivePalette && Drawable.LivePalette != null)
            ? Drawable.LivePalette
            : state.ColorMap;
        var clearColor = state.ColorMode == 2 && palette.ContainsKey(state.ColorMapBackground)
            ? new Rgba32(palette[state.ColorMapBackground].Red, palette[state.ColorMapBackground].Green, palette[state.ColorMapBackground].Blue, 255)
            : new Rgba32(0, 0, 0, 255);

        // Determine scroll direction based on pen position.
        // Pen in lower half of screen → scroll UP (paper feeds up, new content at bottom)
        // Pen in upper half of screen → scroll DOWN (new content appears at top, old pushed down)
        bool scrollDown = state.Pen.Y > 0.4f;

        Image.ProcessPixelRows(accessor =>
        {
            if (scrollDown)
            {
                for (int y = accessor.Height - 1; y >= shiftPixels; y--)
                {
                    var srcRow = accessor.GetRowSpan(y - shiftPixels);
                    var dstRow = accessor.GetRowSpan(y);
                    srcRow.CopyTo(dstRow);
                }

                for (int y = 0; y < Math.Min(shiftPixels, accessor.Height); y++)
                {
                    accessor.GetRowSpan(y).Fill(clearColor);
                }
            }
            else
            {
                for (int y = 0; y < accessor.Height - shiftPixels; y++)
                {
                    var srcRow = accessor.GetRowSpan(y + shiftPixels);
                    var dstRow = accessor.GetRowSpan(y);
                    srcRow.CopyTo(dstRow);
                }

                for (int y = accessor.Height - shiftPixels; y < accessor.Height; y++)
                {
                    accessor.GetRowSpan(y).Fill(clearColor);
                }
            }
        });
    }

    private static float GetInterrowMultiplier(TextInterrowSpacing spacing) => spacing switch
    {
        TextInterrowSpacing.One => 1.0f,
        TextInterrowSpacing.FiveQuarters => 1.25f,
        TextInterrowSpacing.ThreeHalves => 1.5f,
        TextInterrowSpacing.Two => 2.0f,
        _ => 1.0f
    };

    /// <summary>
    /// Initializes the blink animator after rendering completes.
    /// Call this if the parsed state has active blink processes.
    /// </summary>
    public void InitializeBlinkAnimator()
    {
        if (NAPLPS.State.BlinkProcesses.Count > 0)
        {
            BlinkAnimator = new BlinkAnimator(NAPLPS.State.BlinkProcesses, NAPLPS.State.ColorMap);
        }
    }

    /// <summary>
    /// Ticks the blink animator and re-renders if colors changed.
    /// Returns true if a re-render occurred.
    /// </summary>
    public bool TickBlink(int deltaMs)
    {
        if (BlinkAnimator == null || !BlinkAnimator.HasActiveProcesses)
        {
            return false;
        }

        bool changed = BlinkAnimator.Tick(deltaMs);

        if (changed)
        {
            // Re-render with palette animation mode to use the updated live palette
            var oldMode = PaletteAnimationMode;
            PaletteAnimationMode = true;
            Render();
            PaletteAnimationMode = oldMode;
            return true;
        }

        return false;
    }

    public void SaveAsPng(string filepath)
    {
        Render();
        Image.SaveAsPng(filepath);
    }

    internal static IDrawable? ConvertToDrawable(NaplpsCommand command, NaplpsState? state = null)
    {
        switch (command)
        {
            case PolygonCommand polygonCommand:
            {
                return new DrawablePolygon(polygonCommand);
            }

            case RectangleSetFilledCommand rectangleCommand:
            {
                return new DrawableRectangleSetFilled(rectangleCommand);
            }

            case RectangleSetOutlinedCommand rectangleCommand:
            {
                return new DrawableRectangleSetOutlined(rectangleCommand);
            }

            case RectangleFilledCommand rectangleCommand:
            {
                return new DrawableRectangleFilled(rectangleCommand);
            }

            case RectangleOutlinedCommand rectangleCommand:
            {
                return new DrawableRectangleOutlined(rectangleCommand);
            }

            case LineSetAbsoluteCommand:
            case LineSetRelativeCommand:
            {
                return new DrawableLineSet((LineCommand)command);
            }

            case LineAbsoluteCommand:
            case LineRelativeCommand:
            case LineCommand lineCommand:
            {
                return new DrawableLine((LineCommand)command);
            }

            // Arc commands - all variants
            case ArcSetFilledCommand:
            case ArcSetOutlinedCommand:
            case ArcFilledCommand:
            case ArcOutlinedCommand:
            {
                return new DrawableArc((ArcCommand)command);
            }

            case ResetCommand resetCommand:
            {
                return new DrawableResetCommand(resetCommand);
            }

            case AsciiCharCommand asciiCharCommand:
            {
                // Discarded characters (trailing spaces in word wrap) produce no drawable
                if (asciiCharCommand.IsDiscarded)
                {
                    return null;
                }

                // Check if this character has a DRCS replacement
                if (state != null && state.DrcsCharacters.TryGetValue(asciiCharCommand.OpCode, out var bitmap))
                {
                    return new DrawableDrcsChar(asciiCharCommand, bitmap);
                }
                return new DrawableAsciiChar(asciiCharCommand);
            }

            case PointCommand pointCommand:
            {
                return new DrawablePoint(pointCommand);
            }

            // Incremental commands
            case IncrementalPointCommand incPointCommand:
            {
                return new DrawableIncrementalPoint(incPointCommand);
            }

            case IncrementalLineCommand incLineCommand:
            {
                return new DrawableIncrementalLine(incLineCommand);
            }

            case IncrementalPolygonFilledCommand incPolyCommand:
            {
                return new DrawableIncrementalPolygonFilled(incPolyCommand);
            }

            case MosaicElementCommand mosaicCommand:
            {
                return new DrawableMosaicElement(mosaicCommand);
            }

            case ControlCommand controlCommand when
                controlCommand.Command == NaplpsControlCommands.ClearScreen:
            {
                return new DrawableClearScreen(controlCommand);
            }

            case ControlCommand controlCommand when
                controlCommand.Command == NaplpsControlCommands.Repeat ||
                controlCommand.Command == NaplpsControlCommands.RepeatToEOL:
            {
                return new DrawableRepeat(controlCommand);
            }

            default:
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Renders all command frames into a deduped APNG. Identical consecutive frames
    /// are collapsed into a single frame with an extended delay.
    /// Caller owns the returned image and must dispose it.
    /// </summary>
    /// <param name="delayHundredths">Base frame delay in hundredths of a second (default 5 = 50ms)</param>
    /// <param name="blinkCycles">Number of blink animation cycles to append after drawing (0 = none)</param>
    public Image<Rgba32> RenderToApng(int delayHundredths = 5, bool loop = false, int blinkCycles = 0)
    {
        var apng = new Image<Rgba32>(Size.Width, Size.Height);
        var pngMeta = apng.Metadata.GetFormatMetadata(PngFormat.Instance);
        pngMeta.RepeatCount = loop ? (uint)0 : 1;

        var baseDelay = new Rational((uint)delayHundredths, 1000);

        bool firstFrame = true;

        RenderFrameSequence(delayHundredths, (frame, multiplier) =>
        {
            AddApngFrame(apng, frame, baseDelay, multiplier, firstFrame);
            firstFrame = false;
        });

        // Append blink animation frames if requested and blink processes exist
        if (blinkCycles > 0 && NAPLPS.State.BlinkProcesses.Count > 0)
        {
            AppendBlinkFrames(apng, blinkCycles);
        }

        Drawable.LivePalette = null;

        return apng;
    }

    /// <summary>
    /// Renders straight to an APNG file, holding only the current and previous canvas rather than
    /// every frame. See <see cref="ApngWriter"/> for why that matters - the in-memory path is
    /// O(frames) and the corpus contains files that need gigabytes on their own.
    /// Blink frames are not supported here; use <see cref="RenderToApng"/> for those.
    /// </summary>
    /// <returns>The number of frames written.</returns>
    public int RenderApngToFile(string path, int delayHundredths = 5, bool loop = false)
    {
        using var file = System.IO.File.Create(path);
        using var writer = new ApngWriter(file, Size.Width, Size.Height, loop ? 0u : 1u);

        var canvas = new byte[Size.Width * Size.Height * 4];
        var previous = new byte[Size.Width * Size.Height * 4];
        bool first = true;
        int frames = 0;

        RenderFrameSequence(delayHundredths, (frame, multiplier) =>
        {
            frame.CopyPixelDataTo(canvas);

            // The first frame becomes the PNG's own image and must cover the whole canvas.
            var rect = first
                ? new Rectangle(0, 0, Size.Width, Size.Height)
                : ApngWriter.DirtyRect(canvas, previous, Size.Width, Size.Height);

            writer.WriteFrame(canvas, rect, (ushort)(delayHundredths * multiplier), 1000);

            canvas.CopyTo(previous.AsSpan());
            first = false;
            frames++;
        });

        Drawable.LivePalette = null;

        return frames;
    }

    /// <summary>
    /// Walks the command stream and hands each completed frame to <paramref name="emitFrame"/> with
    /// the number of base delays it should be held for. The frame is disposed once emitted, so a
    /// consumer that needs to keep it must copy. Shared by the in-memory and streaming APNG paths
    /// so the CLUT re-render and identical-frame coalescing cannot drift between them.
    /// </summary>
    private void RenderFrameSequence(int delayHundredths, Action<Image<Rgba32>, int> emitFrame)
    {
        Image<Rgba32>? previousFrame = null;
        int currentFrameDelayMultiplier = 1;

        // Running CLUT palette — tracks palette changes for CLUT animation.
        // When SetColorCommand modifies an entry followed by WaitCommand,
        // we re-render from scratch with this palette to show the CLUT effect
        // (e.g., girl.nap eye blink: entries 11/12/13 toggle between eye and skin colors).
        var clutPalette = new Dictionary<byte, NaplpsColor>(NAPLPS.Commands[0].State.ColorMap);
        bool clutDirty = false;

        BeginRender();

        int commandIndex = 0;

        foreach (var sequence in NAPLPS.Commands)
        {
            var (cmd, cmdState) = sequence;

            // Track palette changes for CLUT animation. Prodigy MVDI ignores SET COLOR
            // redefinition (fixed hardware palette), so never rebind the CLUT there.
            if (cmd is SetColorCommand setColor && NAPLPS.SystemType != NaplpsSystemType.Prodigy)
            {
                if ((cmdState.ColorMode == 1 || cmdState.ColorMode == 2) && setColor.Operands.Count > 0)
                {
                    clutPalette[cmdState.ColorMapForeground] = setColor.Color;
                    clutDirty = true;
                }
            }

            var drawable = RenderCommand(cmd, cmdState);

            CommandRendered?.Invoke(commandIndex, cmd, Image);

            // WaitCommand: capture current frame with the wait duration as delay.
            // CLUT animation: if palette changed before this wait, re-render first.
            if (cmd is WaitCommand waitCmd && waitCmd.IsValid)
            {
                if (clutDirty)
                {
                    ClutReRender(clutPalette, commandIndex);
                    clutDirty = false;
                }

                // Commit the previous frame, then capture current state with wait delay
                if (previousFrame != null)
                {
                    emitFrame(previousFrame, currentFrameDelayMultiplier);
                    previousFrame.Dispose();
                }

                previousFrame = Image.Clone();
                // PP3 doesn't parse WAIT timing — short fixed delay for CLUT animation frames.
                // 150ms ≈ 15 hundredths → multiplier = 15 / delayHundredths
                currentFrameDelayMultiplier = Math.Max(1, 15 / delayHundredths);
            }
            // Only check for frame changes when something was actually drawn
            else if (drawable != null)
            {
                if (previousFrame != null && FramesAreIdentical(Image, previousFrame))
                {
                    currentFrameDelayMultiplier++;
                }
                else
                {
                    if (previousFrame != null)
                    {
                        emitFrame(previousFrame, currentFrameDelayMultiplier);
                        previousFrame.Dispose();
                    }

                    previousFrame = Image.Clone();
                    currentFrameDelayMultiplier = 1;
                }
            }
            else
            {
                currentFrameDelayMultiplier++;
            }

            CurrentIndex++;
            commandIndex++;
        }

        if (previousFrame != null)
        {
            emitFrame(previousFrame, currentFrameDelayMultiplier);
            previousFrame.Dispose();
        }
    }

    /// <summary>
    /// Appends blink/palette animation frames to an APNG after the main render.
    /// Ticks the blink animator at 100ms intervals and captures each state change.
    /// </summary>
    private void AppendBlinkFrames(Image<Rgba32> apng, int cycles)
    {
        InitializeBlinkAnimator();

        if (BlinkAnimator == null || !BlinkAnimator.HasActiveProcesses)
        {
            return;
        }

        // Calculate total animation time from blink processes
        // Each cycle = (OnInterval + OffInterval) * 100ms
        int maxCycleMs = 0;

        foreach (var process in NAPLPS.State.BlinkProcesses)
        {
            int cycleMs = (process.OnInterval + process.OffInterval) * 100;
            maxCycleMs = Math.Max(maxCycleMs, cycleMs);
        }

        int totalMs = maxCycleMs * cycles;
        const int tickMs = 100; // 100ms per tick (matches NAPLPS blink time unit)
        var blinkDelay = new Rational(10, 1000); // 100ms = 10/100s

        var oldMode = PaletteAnimationMode;
        PaletteAnimationMode = true;

        Image<Rgba32>? lastBlinkFrame = null;
        int frameAccumulator = 1;

        for (int elapsed = 0; elapsed < totalMs; elapsed += tickMs)
        {
            bool changed = BlinkAnimator.Tick(tickMs);

            if (changed)
            {
                // Re-render with updated palette
                Render();

                if (lastBlinkFrame != null && FramesAreIdentical(Image, lastBlinkFrame))
                {
                    frameAccumulator++;
                }
                else
                {
                    if (lastBlinkFrame != null)
                    {
                        AddApngFrame(apng, lastBlinkFrame, blinkDelay, frameAccumulator, replaceRoot: false);
                        lastBlinkFrame.Dispose();
                    }

                    lastBlinkFrame = Image.Clone();
                    frameAccumulator = 1;
                }
            }
            else
            {
                frameAccumulator++;
            }
        }

        if (lastBlinkFrame != null)
        {
            AddApngFrame(apng, lastBlinkFrame, blinkDelay, frameAccumulator, replaceRoot: false);
            lastBlinkFrame.Dispose();
        }

        PaletteAnimationMode = oldMode;
        BlinkAnimator.Reset();
    }

    /// <summary>
    /// Appends a frame. <paramref name="replaceRoot"/> overwrites the placeholder frame that
    /// <c>new Image&lt;Rgba32&gt;</c> creates, instead of adding alongside it, and must be true for
    /// exactly the first frame written.
    ///
    /// This used to be decided by inspecting whether the root looked empty, which was wrong twice
    /// over: the placeholder is transparent black so it never matched, leaving a wasted blank frame
    /// at the front of every APNG; and once it did match, any run of blank frames collapsed into
    /// one because the test kept passing after the root had been filled in.
    /// </summary>
    private static void AddApngFrame(Image<Rgba32> apng, Image<Rgba32> frame, Rational baseDelay, int delayMultiplier, bool replaceRoot)
    {
        var frameDelay = new Rational(baseDelay.Numerator * (uint)delayMultiplier, baseDelay.Denominator);

        if (replaceRoot)
        {
            apng.Frames.RootFrame.ProcessPixelRows(frame.Frames.RootFrame, (dst, src) =>
            {
                for (int y = 0; y < dst.Height; y++)
                {
                    src.GetRowSpan(y).CopyTo(dst.GetRowSpan(y));
                }
            });

            apng.Frames.RootFrame.Metadata.GetFormatMetadata(PngFormat.Instance).FrameDelay = frameDelay;
        }
        else
        {
            var added = apng.Frames.AddFrame(frame.Frames.RootFrame);
            added.Metadata.GetFormatMetadata(PngFormat.Instance).FrameDelay = frameDelay;
        }
    }


    private static bool FramesAreIdentical(Image<Rgba32> a, Image<Rgba32> b)
    {
        if (a.Width != b.Width || a.Height != b.Height)
        {
            return false;
        }

        bool identical = true;

        a.ProcessPixelRows(b, (accessorA, accessorB) =>
        {
            for (int y = 0; y < accessorA.Height && identical; y++)
            {
                var rowA = accessorA.GetRowSpan(y);
                var rowB = accessorB.GetRowSpan(y);

                if (!rowA.SequenceEqual(rowB))
                {
                    identical = false;
                }
            }
        });

        return identical;
    }

    #region IDisposable

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                memoryStream.Dispose();

                // Dispose managed state here (managed objects)
                Image.Dispose();
            }

            // Free unmanaged resources here (unmanaged objects) and override finalizer
            // Also set large fields to null!
            disposedValue = true;
        }
    }

    // ~DrawContext()
    // {
    //     // Only use this if we're freeing unmanaged resources...
    //     Dispose(disposing: false);
    // }

    public void Dispose()
    {
        Dispose(disposing: true);

        GC.SuppressFinalize(this);
    }

    #endregion
}
