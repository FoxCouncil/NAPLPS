// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using System.Runtime.InteropServices;

namespace NAPLPS;

/// <summary>
/// Stateful decoder-context C ABI: thin [UnmanagedCallersOnly] shims over
/// <see cref="NaplpsStreamSession"/> plus a pinned RGBA8888 framebuffer, so native
/// consumers (the Prodigy reception-system) can append bytes, step command-by-command,
/// and blit raw pixels without PNG round-trips. Semantics live on the session class;
/// the C contract lives in tools/aot/include/naplps.h.
///
/// Error codes: -1 exception/parse failure (the context is left unchanged - appends are
/// transactional), -3 invalid argument or invalid state, -5 bad handle.
/// naplps_ctx_exec_next returns -4 when the stream is exhausted (status, not an error).
///
/// Thread safety: the handle table is locked; a single context must not be used from
/// multiple threads concurrently (one context per thread of use). The framebuffer
/// pointer is only coherent between the caller's own calls.
/// </summary>
public static unsafe class NativeExportsCtx
{
    private const int ModeProdigy = 0x0001;
    private const int ModeTransparent = 0x0002;
    private const int ErrException = -1;
    private const int ErrInvalid = -3;
    private const int Exhausted = -4;
    private const int ErrBadHandle = -5;

    private sealed class Ctx(NaplpsStreamSession session, byte[] framebuffer)
    {
        public NaplpsStreamSession Session { get; } = session;

        public byte[] Framebuffer { get; } = framebuffer;

        public GCHandle Pin;
    }

    private static readonly Dictionary<nint, Ctx> _ctxs = new();
    private static readonly object _lock = new();
    private static nint _nextHandle = 1;

    private static Ctx? Get(nint handle)
    {
        lock (_lock) { return _ctxs.GetValueOrDefault(handle); }
    }

    /// <summary>
    /// Create a decoder context with a width x height RGBA8888 framebuffer. Flags bit 0
    /// (NAPLPS_MODE_PRODIGY) forces the Prodigy pipeline; bit 1 (NAPLPS_MODE_TRANSPARENT)
    /// clears the canvas to fully transparent so only painted pixels carry alpha - the
    /// window-overlay compositing model. Returns an opaque handle, or 0 on failure.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "naplps_ctx_create")]
    public static nint Create(int width, int height, int flags)
    {
        if (width <= 0 || height <= 0) { return 0; }

        // int math would wrap for pathological dimensions (e.g. 32769 x 32768) and
        // allocate a too-small buffer with a clean return; reject before it can.
        if ((long)width * height * 4 > int.MaxValue) { return 0; }

        try
        {
            var fb = new byte[width * height * 4];
            var session = new NaplpsStreamSession(width, height,
                (flags & ModeProdigy) != 0,
                (flags & ModeTransparent) != 0);
            session.CopyFramebufferTo(fb);   // the mode's clear color from the start
            var ctx = new Ctx(session, fb)
            {
                Pin = GCHandle.Alloc(fb, GCHandleType.Pinned),
            };

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

    /// <summary>Destroy a context and release its framebuffer. Safe to call twice.</summary>
    [UnmanagedCallersOnly(EntryPoint = "naplps_ctx_destroy")]
    public static void Destroy(nint handle)
    {
        Ctx? ctx;
        lock (_lock)
        {
            if (!_ctxs.Remove(handle, out ctx)) { return; }
        }

        // A managed exception cannot unwind across the UnmanagedCallersOnly boundary -
        // it would terminate the host process. Teardown failures are swallowed like
        // every other entry point's; the handle is already unregistered either way.
        try
        {
            ctx.Session.Dispose();
        }
        catch
        {
        }

        if (ctx.Pin.IsAllocated) { ctx.Pin.Free(); }
    }

    /// <summary>Clear the byte stream, decoder state, and framebuffer for a fresh page.</summary>
    [UnmanagedCallersOnly(EntryPoint = "naplps_ctx_reset")]
    public static void Reset(nint handle)
    {
        var ctx = Get(handle);
        if (ctx is null) { return; }

        try
        {
            ctx.Session.Reset();
            ctx.Session.CopyFramebufferTo(ctx.Framebuffer);
        }
        catch
        {
            // Reset cannot meaningfully fail; swallow to honor the void contract.
        }
    }

    /// <summary>
    /// Append bytes to the command stream; decoding continues from the current state.
    /// Returns the new total count of COMPLETE commands, or a negative error code. A chunk
    /// ending mid-command leaves that command uncounted until the byte terminating it
    /// arrives or <see cref="Flush"/> declares the stream over.
    /// </summary>
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
            return ctx.Session.Append(chunk);
        }
        catch
        {
            return ErrException;
        }
    }

    /// <summary>
    /// Declare the appended stream complete, releasing a trailing command whose operand
    /// list ran to the last byte. Returns the new total command count or a negative error
    /// code. Only the caller knows a stream has ended - see NaplpsStreamSession.Flush.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "naplps_ctx_flush")]
    public static int Flush(nint handle)
    {
        var ctx = Get(handle);
        if (ctx is null) { return ErrBadHandle; }

        try
        {
            return ctx.Session.Flush();
        }
        catch
        {
            return ErrException;
        }
    }

    /// <summary>Total parsed command count, or a negative error code.</summary>
    [UnmanagedCallersOnly(EntryPoint = "naplps_ctx_command_count")]
    public static int CommandCount(nint handle)
    {
        var ctx = Get(handle);
        return ctx is null ? ErrBadHandle : ctx.Session.CommandCount;
    }

    /// <summary>
    /// Paint up through (and including) cmd_index, clamped to the stream end. Idempotent
    /// for already-painted commands. Returns the highest painted index, or a negative
    /// error code (-3 for a negative cmd_index).
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "naplps_ctx_exec_to")]
    public static int ExecTo(nint handle, int cmdIndex)
    {
        var ctx = Get(handle);
        if (ctx is null) { return ErrBadHandle; }
        if (cmdIndex < 0) { return ErrInvalid; }

        try
        {
            return ctx.Session.ExecTo(cmdIndex);
        }
        catch (InvalidOperationException)
        {
            return ErrInvalid;
        }
        catch
        {
            return ErrException;
        }
    }

    /// <summary>
    /// Execute exactly one command (the next unpainted one). Optionally reports the
    /// changed rectangle via out_dirty (full canvas in this version). Returns the index
    /// just executed, -4 when the stream is exhausted, or a negative error code.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "naplps_ctx_exec_next")]
    public static int ExecNext(nint handle, int* outDirty)
    {
        var ctx = Get(handle);
        if (ctx is null) { return ErrBadHandle; }

        try
        {
            var executed = ctx.Session.ExecNext();
            if (executed is null) { return Exhausted; }

            if (outDirty != null)
            {
                outDirty[0] = 0;
                outDirty[1] = 0;
                outDirty[2] = ctx.Session.Width;
                outDirty[3] = ctx.Session.Height;
            }

            return executed.Value;
        }
        catch (InvalidOperationException)
        {
            return ErrInvalid;
        }
        catch
        {
            return ErrException;
        }
    }

    /// <summary>
    /// Append a field-text run built by the library's own encoder (Point Set Absolute,
    /// SELECT COLOR, optional TEXT character size, text bytes); execute it via
    /// exec_next/exec_to like any appended bytes. Returns the new total command count,
    /// -3 when the stream ends inside an unfinished macro/DRCS/texture definition, or a
    /// negative error code. See naplps.h for parameter semantics.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "naplps_ctx_draw_text")]
    public static int DrawText(nint handle, double x, double y, int fg, int bg,
        double charW, double charH, byte* ascii, int len)
    {
        var ctx = Get(handle);
        if (ctx is null) { return ErrBadHandle; }
        if (ascii == null || len <= 0) { return ErrInvalid; }

        try
        {
            var chunk = new byte[len];
            Marshal.Copy((nint)ascii, chunk, 0, len);
            return ctx.Session.DrawText(x, y, fg, bg, charW, charH, chunk);
        }
        catch (InvalidOperationException)
        {
            return ErrInvalid;
        }
        catch
        {
            return ErrException;
        }
    }

    /// <summary>
    /// Append a solid filled rectangle (TEXTURE solid fill, SELECT COLOR, RECTANGLE SET
    /// FILLED) at (x, y) lower-left with size (w, h), all rounded to the wire grid -
    /// the block-cursor / cell-repaint primitive. Executes via exec_next/exec_to like any
    /// appended bytes. Returns the new total command count, -3 for a non-positive size or
    /// inside an unfinished definition, or a negative error code.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "naplps_ctx_fill_rect")]
    public static int FillRect(nint handle, double x, double y, double w, double h, int color)
    {
        var ctx = Get(handle);
        if (ctx is null) { return ErrBadHandle; }
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(w) || !double.IsFinite(h)
            || w <= 0 || h <= 0)
        {
            return ErrInvalid;
        }

        try
        {
            return ctx.Session.FillRect(x, y, w, h, color);
        }
        catch (InvalidOperationException)
        {
            return ErrInvalid;
        }
        catch
        {
            return ErrException;
        }
    }

    /// <summary>
    /// Refresh and return the pinned RGBA8888 framebuffer pointer (stride = width * 4).
    /// The pointer stays valid for the context's lifetime; contents are coherent only
    /// between the caller's own calls. Returns null on error.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "naplps_ctx_framebuffer")]
    public static byte* Framebuffer(nint handle, int* outW, int* outH, int* outStride)
    {
        var ctx = Get(handle);
        if (ctx is null) { return null; }

        try
        {
            ctx.Session.CopyFramebufferTo(ctx.Framebuffer);
            if (outW != null) { *outW = ctx.Session.Width; }
            if (outH != null) { *outH = ctx.Session.Height; }
            if (outStride != null) { *outStride = ctx.Session.Width * 4; }
            return (byte*)ctx.Pin.AddrOfPinnedObject();
        }
        catch
        {
            return null;
        }
    }
}
