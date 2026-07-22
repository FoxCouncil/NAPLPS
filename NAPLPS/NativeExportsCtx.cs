// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using System.Runtime.InteropServices;
using NAPLPS.Drawing;

namespace NAPLPS;

/// <summary>
/// Stateful decoder-context C ABI: a persistent handle wrapping a parsed stream plus a
/// DrawContext and a pinned RGBA8888 framebuffer, so native consumers (the Prodigy
/// reception-system) can append bytes, step command-by-command, and blit raw pixels
/// without PNG round-trips. See tools/aot/include/naplps.h for the C declarations.
///
/// Model: the accumulated BYTES are the source of truth. Each append re-parses the whole
/// buffer (parsing is deterministic, so decoder state - character sets, DRCS, position,
/// attributes - is identical to an incremental parse, and command indices of previously
/// painted commands are stable because the stream only grows at its end). Painted pixels
/// persist on the context's canvas; append then re-establishes them by replaying the
/// already-executed prefix onto a fresh parse (deterministic, pixel-identical).
///
/// Error codes: -1 exception/parse failure, -3 invalid argument, -5 bad handle.
/// naplps_ctx_exec_next returns -4 when the stream is exhausted (not an error).
///
/// Thread safety: the handle table is locked; a single context must not be used from
/// multiple threads concurrently.
/// </summary>
public static unsafe class NativeExportsCtx
{
    private const int ModeProdigy = 0x0001;
    private const int ErrException = -1;
    private const int ErrInvalid = -3;
    private const int Exhausted = -4;
    private const int ErrBadHandle = -5;

    private sealed class Ctx
    {
        public required int Width;
        public required int Height;
        public required bool Prodigy;
        public List<byte> Bytes = [];
        public NaplpsFormat? Format;
        public DrawContext? Draw;
        public int Cursor;                       // commands already painted
        public byte[] Framebuffer = [];
        public GCHandle Pin;

        public int CommandCount => Format?.Commands.Count ?? 0;
    }

    private static readonly Dictionary<nint, Ctx> _ctxs = new();
    private static readonly object _lock = new();
    private static nint _nextHandle = 1;

    private static Ctx? Get(nint handle)
    {
        lock (_lock) { return _ctxs.GetValueOrDefault(handle); }
    }

    [UnmanagedCallersOnly(EntryPoint = "naplps_ctx_create")]
    public static nint Create(int width, int height, int flags)
    {
        if (width <= 0 || height <= 0) { return 0; }

        // int math would wrap for pathological dimensions (e.g. 32769 x 32768) and
        // allocate a too-small buffer with a clean return; reject before it can.
        if ((long)width * height * 4 > int.MaxValue) { return 0; }

        try
        {
            var ctx = new Ctx
            {
                Width = width,
                Height = height,
                Prodigy = (flags & ModeProdigy) != 0,
                Framebuffer = new byte[width * height * 4],
            };
            ctx.Pin = GCHandle.Alloc(ctx.Framebuffer, GCHandleType.Pinned);

            lock (_lock)
            {
                var h = _nextHandle++;
                _ctxs[h] = ctx;
                return h;
            }
        }
        catch
        {
            return 0;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "naplps_ctx_destroy")]
    public static void Destroy(nint handle)
    {
        Ctx? ctx;
        lock (_lock)
        {
            if (!_ctxs.Remove(handle, out ctx)) { return; }
        }

        ctx.Draw?.Dispose();
        if (ctx.Pin.IsAllocated) { ctx.Pin.Free(); }
    }

    [UnmanagedCallersOnly(EntryPoint = "naplps_ctx_reset")]
    public static void Reset(nint handle)
    {
        var ctx = Get(handle);
        if (ctx is null) { return; }

        ctx.Bytes.Clear();
        ctx.Draw?.Dispose();
        ctx.Draw = null;
        ctx.Format = null;
        ctx.Cursor = 0;
        Array.Clear(ctx.Framebuffer);
    }

    /// <summary>Re-parse the accumulated bytes and rebuild the canvas up to the cursor.</summary>
    private static void Reparse(Ctx ctx)
    {
        ctx.Format = NaplpsFormat.FromBytes(
            [.. ctx.Bytes],
            ctx.Prodigy ? NaplpsSystemType.Prodigy : null);

        ctx.Draw?.Dispose();
        ctx.Draw = new DrawContext(ctx.Format, new SixLabors.ImageSharp.Size(ctx.Width, ctx.Height));
        if (ctx.Prodigy)
        {
            // Match naplps_render_png_prodigy: the ctor derives gun width / MVDI font /
            // display ratio from SystemType; authentic geometry is set explicitly.
            ctx.Draw.AuthenticGeometry = true;
        }

        ctx.Draw.ClearCanvas();
        if (ctx.Cursor > ctx.Format.Commands.Count)
        {
            ctx.Cursor = ctx.Format.Commands.Count;
        }

        for (var i = 0; i < ctx.Cursor && i < ctx.Format.Commands.Count; i++)
        {
            ctx.Draw.RenderStep(i);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "naplps_ctx_append")]
    public static int Append(nint handle, byte* bytes, int len)
    {
        var ctx = Get(handle);
        if (ctx is null) { return ErrBadHandle; }
        if (bytes == null || len <= 0) { return ErrInvalid; }

        try
        {
            var chunk = new byte[len];
            Marshal.Copy((nint)bytes, chunk, 0, len);
            ctx.Bytes.AddRange(chunk);
            Reparse(ctx);
            return ctx.CommandCount;
        }
        catch
        {
            return ErrException;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "naplps_ctx_command_count")]
    public static int CommandCount(nint handle)
    {
        var ctx = Get(handle);
        return ctx is null ? ErrBadHandle : ctx.CommandCount;
    }

    [UnmanagedCallersOnly(EntryPoint = "naplps_ctx_exec_to")]
    public static int ExecTo(nint handle, int cmdIndex)
    {
        var ctx = Get(handle);
        if (ctx is null) { return ErrBadHandle; }
        if (ctx.Draw is null || ctx.Format is null) { return ErrInvalid; }

        try
        {
            var target = Math.Min(cmdIndex, ctx.CommandCount - 1);
            while (ctx.Cursor <= target)
            {
                ctx.Draw.RenderStep(ctx.Cursor);
                ctx.Cursor++;
            }

            return ctx.Cursor - 1;
        }
        catch
        {
            return ErrException;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "naplps_ctx_exec_next")]
    public static int ExecNext(nint handle, int* outDirty)
    {
        var ctx = Get(handle);
        if (ctx is null) { return ErrBadHandle; }
        if (ctx.Draw is null || ctx.Format is null) { return ErrInvalid; }

        try
        {
            if (ctx.Cursor >= ctx.CommandCount) { return Exhausted; }

            ctx.Draw.RenderStep(ctx.Cursor);
            var executed = ctx.Cursor;
            ctx.Cursor++;

            if (outDirty != null)
            {
                // v1: report the full canvas; per-command bounds refinement can come later.
                outDirty[0] = 0;
                outDirty[1] = 0;
                outDirty[2] = ctx.Width;
                outDirty[3] = ctx.Height;
            }

            return executed;
        }
        catch
        {
            return ErrException;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "naplps_ctx_framebuffer")]
    public static byte* Framebuffer(nint handle, int* outW, int* outH, int* outStride)
    {
        var ctx = Get(handle);
        if (ctx is null) { return null; }

        try
        {
            if (ctx.Draw is not null)
            {
                ctx.Draw.Image.CopyPixelDataTo(ctx.Framebuffer);
            }

            if (outW != null) { *outW = ctx.Width; }
            if (outH != null) { *outH = ctx.Height; }
            if (outStride != null) { *outStride = ctx.Width * 4; }
            return (byte*)ctx.Pin.AddrOfPinnedObject();
        }
        catch
        {
            return null;
        }
    }
}
