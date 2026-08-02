/* naplps.h — C header for the NAPLPS NativeAOT library.
 *
 * Link against NAPLPS.dll (Windows), libNAPLPS.so (Linux), or libNAPLPS.dylib
 * (macOS) produced by:
 *
 *     dotnet publish NAPLPS/NAPLPS.csproj -c Release -r <rid> --property:PublishAot=true
 *
 * where <rid> is one of win-x64, linux-x64, osx-x64, osx-arm64, linux-arm64.
 *
 * Thread safety: the stateless render/query functions below are thread-safe; each
 * call builds its own internal render state. The naplps_ctx_* context functions are
 * different: creating/destroying/looking up handles is safe from any thread, but a
 * given context must not be used from two threads at the same time (one context per
 * thread of use, or synchronize externally).
 *
 * Return codes (negative values):
 *   -1  Parse error or exception. For the stateless functions below the call has no
 *       effect; for naplps_ctx_* see the context section's failure model - an append
 *       is not transactional.
 *   -2  Output buffer too small. Call again with a larger buffer.
 *   -3  Invalid input (null pointer, non-positive length, bad argument or state).
 *   -4  Stream exhausted (naplps_ctx_exec_next only; a status, not an error).
 *   -5  Bad context handle.
 */

#ifndef NAPLPS_H
#define NAPLPS_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#ifdef _WIN32
    #define NAPLPS_IMPORT __declspec(dllimport)
#else
    #define NAPLPS_IMPORT
#endif

/* Render a NAPLPS byte stream to a PNG image and copy the PNG bytes into out_buf.
 * Returns the PNG byte count on success, or a negative error code.
 *
 * To query the required buffer size, call with out_buf=NULL and out_buf_len=0;
 * the return value is the byte count needed. Then allocate a buffer of that size
 * and call again.
 */
NAPLPS_IMPORT int32_t naplps_render_png(
    const uint8_t* nap_bytes,
    int32_t nap_len,
    int32_t width,
    int32_t height,
    uint8_t* out_buf,
    int32_t out_buf_len);

/* Return the count of parsed NAPLPS commands in the byte stream, or a negative
 * error code on parse failure. Useful for sanity-checking that a file loaded.
 */
NAPLPS_IMPORT int32_t naplps_command_count(
    const uint8_t* nap_bytes,
    int32_t nap_len);

/* Return the count of parse errors recorded during load. Zero means a clean parse.
 */
NAPLPS_IMPORT int32_t naplps_error_count(
    const uint8_t* nap_bytes,
    int32_t nap_len);

/* Like naplps_render_png but forces the Prodigy pipeline (canonical CLUT + device
 * metrics, 2-bit color guns, MVDI vector text, authentic integer geometry, Prodigy
 * display ratio) regardless of stream auto-detection.
 */
NAPLPS_IMPORT int32_t naplps_render_png_prodigy(
    const uint8_t* nap_bytes,
    int32_t nap_len,
    int32_t width,
    int32_t height,
    uint8_t* out_buf,
    int32_t out_buf_len);

/* Write the library version string (ASCII, null-terminated) into out_buf.
 * Returns the written length excluding the terminator, or a negative error code.
 * Call with out_buf=NULL, out_buf_len=0 to get the required size (including terminator).
 */
NAPLPS_IMPORT int32_t naplps_version(uint8_t* out_buf, int32_t out_buf_len);

/* ====================================================================================
 * Stateful decoder context
 * ====================================================================================
 *
 * A persistent decoder + framebuffer for consumers that append bytes over time and
 * paint command-by-command (the Prodigy reception-system display device).
 *
 * Model: forward-only. The context owns one decoder and one framebuffer for its whole
 * life. Decoder state (character sets, DRCS glyph definitions, position, attributes)
 * carries across appends, so "define character sets, then draw field text with them"
 * works across calls. An append decodes only its own bytes and nothing is ever
 * repainted, so append cost is proportional to the chunk rather than to the stream so
 * far; there is no reason to batch appends. The byte history is NOT retained.
 *
 * Chunks may split anywhere, including mid-command. A command whose operand list runs
 * to the end of the received bytes is WITHHELD rather than half-painted, so a blit
 * taken at any moment shows only complete commands and pixels never change
 * retroactively. The cost is that naplps_ctx_command_count does not count a command
 * that is still waiting on operands.
 *
 * That withholding also applies at end of stream: an X3.110 operand list is terminated
 * by the next non-numeric byte and never by a length, so the last command of a complete
 * stream is byte-identical to a truncated one and only the caller knows which it is.
 * Call naplps_ctx_flush once a page is complete, or its final command may stay
 * unpainted. naplps_ctx_draw_text and naplps_ctx_fill_rect flush themselves.
 *
 * Failure model: an append decodes but does not paint, and the parse layer records
 * stream errors rather than failing the call, so a malformed stream leaves the
 * framebuffer untouched. An append is NOT transactional - it consumes bytes into live
 * decoder state as it goes and cannot be rolled back. A render failure (a library bug,
 * not a stream condition) surfaces from naplps_ctx_exec_to / naplps_ctx_exec_next and
 * may leave the framebuffer partially painted at the reported index.
 *
 * Caveat: mid-stream palette redefinition (generic NAPLPS CLUT animation) is applied
 * retroactively by the one-shot PNG renders but NOT by stepped execution, which pins
 * each command's palette to that command's own snapshot - by design, since a
 * forward-only painter can never revisit an earlier command. That pinning is what makes
 * stepped output independent of where chunk boundaries fall. Prodigy mode is unaffected
 * either way (fixed hardware palette).
 *
 * Thread safety: see the top of this header - one context per thread of use. The
 * framebuffer pointer is only coherent between the caller's own calls.
 */

/* Opaque handle to a stateful decoder + its framebuffer. NULL/0 on failure. */
typedef intptr_t NaplpsCtx;

/* Flags for naplps_ctx_create. */
#define NAPLPS_MODE_PRODIGY      0x0001  /* 2-bit color guns, MVDI font, Prodigy aspect */
#define NAPLPS_MODE_TRANSPARENT  0x0002  /* clear to (0,0,0,0); painted pixels get alpha
                                          * 255 - the window-overlay model: composite the
                                          * context by alpha and the page below shows
                                          * through everything the stream did not paint.
                                          * A window wanting an opaque backdrop draws a
                                          * filled rectangle. */

/* Sentinel returned by naplps_ctx_exec_next when all commands are painted. */
#define NAPLPS_CTX_EXHAUSTED (-4)

/* A changed-region report, in framebuffer pixels. */
typedef struct { int32_t x, y, w, h; } NaplpsRect;

/* --- Lifecycle --- */
NAPLPS_IMPORT NaplpsCtx naplps_ctx_create(int32_t width, int32_t height, int32_t flags);
NAPLPS_IMPORT void      naplps_ctx_destroy(NaplpsCtx ctx);

/* Clear the framebuffer AND all decoder/drawing state for a fresh page. Re-append
 * character-set / DRCS definition bytes after reset if the next page needs them. */
NAPLPS_IMPORT void      naplps_ctx_reset(NaplpsCtx ctx);

/* --- Feed --- */
/* Append bytes to the command stream. Does not reset drawing state or the
 * framebuffer: decoding continues from the current state. Byte chunks may split
 * anywhere, including mid-command. Returns the new total count of COMPLETE commands,
 * or a negative error code; a chunk ending mid-command leaves that command uncounted
 * until the byte that terminates it arrives, or until naplps_ctx_flush. */
NAPLPS_IMPORT int32_t   naplps_ctx_append(NaplpsCtx ctx, const uint8_t* bytes, int32_t len);

/* Declare the appended stream complete, releasing a trailing command whose operand
 * list ran to the last byte (see the section comment). Returns the new total command
 * count, or a negative error code. Idempotent, and a no-op on a stream that ends on a
 * command boundary. Do NOT call it on a stream that is merely paused mid-command: that
 * would release a truncated command as though it were whole. */
NAPLPS_IMPORT int32_t   naplps_ctx_flush(NaplpsCtx ctx);

NAPLPS_IMPORT int32_t   naplps_ctx_command_count(NaplpsCtx ctx);

/* --- Execute / step --- */
/* Paint the framebuffer up through (and including) cmd_index, clamped to the
 * stream end. Idempotent for already-painted commands. Returns the highest painted
 * index, -3 for a negative cmd_index, or a negative error code. */
NAPLPS_IMPORT int32_t   naplps_ctx_exec_to(NaplpsCtx ctx, int32_t cmd_index);

/* Execute exactly one command (the next unpainted one). Optionally reports the
 * changed rectangle via out_dirty (pass NULL to skip; v1 reports the full canvas).
 * Returns the index just executed, NAPLPS_CTX_EXHAUSTED when the stream is fully
 * painted, or a negative error. */
NAPLPS_IMPORT int32_t   naplps_ctx_exec_next(NaplpsCtx ctx, NaplpsRect* out_dirty);

/* --- Field text --- */
/* Append a field-text run built by the library's own NAPLPS encoder:
 * Point Set Absolute -> SELECT COLOR -> (optional TEXT character size) -> text bytes.
 * Executable via exec_next/exec_to like any appended bytes.
 *   x, y            normalized unit-screen coordinates (y up; Prodigy visible area
 *                   is y in [0, 0.78125], one 40x20 text cell = 0.025 x 0.0390625).
 *                   Rounded to the coordinate wire grid (1/256 at the default
 *                   precision).
 *   fg, bg          palette indices 0-15 (clamped); bg < 0 emits the foreground-only
 *                   SELECT COLOR form
 *   char_w, char_h  character field size in normalized units, rounded to the wire
 *                   grid; < 0 keeps the current size. Passing a size also resets the
 *                   TEXT attributes (spacing/path/rotation/interrow) to defaults.
 *   ascii           text bytes appended verbatim (0x20-0x7E; codes with DRCS
 *                   definitions render the custom glyphs)
 *
 * Independent of the decoder state it lands in, and neutral with respect to it: call it
 * over any prior stream that is at a command boundary (see the -3 rule below), with no
 * prefix bytes of your own. The drawing
 * commands are coded so they resolve whatever the prior stream shifted into GL, the text
 * is shifted into a character set so it draws rather than executing as drawing commands,
 * and the incoming GL invocation is put back afterwards - so a caller that paints a field
 * between two chunks of one presentation keeps its shift state. Do NOT prepend SO, SI or
 * NSR; there is nothing to compensate for.
 *
 * The one piece of state it does not re-establish is the G0 DESIGNATION: a stream that
 * pointed G0 at a set other than the primary characters and left it there gets the text
 * drawn with that set's glyphs.
 *
 * Other state footprint: pen, color and (when a size is passed) character size, as the
 * emitted commands imply.
 *
 * Returns the new total command count; -3 when the stream currently ends inside an
 * unfinished macro/DRCS/texture definition (the bytes would be swallowed into the
 * definition); or a negative error code. */
NAPLPS_IMPORT int32_t   naplps_ctx_draw_text(NaplpsCtx ctx,
                                             double x, double y,
                                             int32_t fg, int32_t bg,
                                             double char_w, double char_h,
                                             const uint8_t* ascii, int32_t len);

/* --- Filled rectangle --- */
/* Append a solid filled rectangle in the given palette color (0-15, clamped):
 * TEXTURE (solid fill) -> SELECT COLOR -> RECTANGLE SET FILLED. Position (lower-left)
 * and size are rounded to the coordinate wire grid; size is floored at one grid step.
 *
 * Cell alignment: nominal pitches like 1/40 are NOT grid-representable, so do not
 * address cells as x + col * 0.025 - that drifts ~1 px per column against a text run.
 * Quantize first: cw_q = round(cw*256)/256 (6/256 for the 40-column cell), then
 * cell_x(i) = x_q + i * cw_q. Every such position is exactly grid-representable, and a
 * rect at the same quantized position/size as a draw_text cell covers it exactly - the
 * block-cursor / cell-repaint primitive.
 *
 * Decoder-state footprint of the emitted commands (affects later RAW appends only;
 * draw_text/fill_rect always re-establish what they need): texture state becomes
 * solid fill, solid line, highlight off, zero mask size; color mode becomes 1
 * (foreground) with the given color; the pen ends at (x + w, y) per the X3.110
 * rectangle pen advance. Shift state is not in that footprint, and is not depended on
 * either - like draw_text, call it over any prior stream at a command boundary.
 *
 * Returns the new total command count; -3 for a non-positive or non-finite argument
 * or when the stream ends inside an unfinished definition; or a negative error code. */
NAPLPS_IMPORT int32_t   naplps_ctx_fill_rect(NaplpsCtx ctx,
                                             double x, double y,
                                             double w, double h,
                                             int32_t color);

/* Append a one-pel rectangle OUTLINE (RECTANGLE SET OUTLINED) at (x, y) lower-left,
 * normalized size (w, h), palette color 0-15. Unlike fill_rect this draws the four
 * edges as X3.110 lines: a true one-device-pel hairline with no fill boundary halo -
 * for focus/cursor borders that must not carry the >=2-pel + halo footprint of a
 * filled rect. Size is grid-rounded (floored at one wire step); same decoder-state
 * footprint as fill_rect except the texture's line form stays solid; pen ends at
 * (x + w, y). Returns the new total command count, or -3 / a negative error as
 * fill_rect. */
NAPLPS_IMPORT int32_t   naplps_ctx_stroke_rect(NaplpsCtx ctx,
                                               double x, double y,
                                               double w, double h,
                                               int32_t color);

/* --- Pixels --- */
/* Return a pointer to the current RGBA8888 framebuffer (refreshed at call time;
 * opaque black before any append - fully transparent instead when the context was
 * created with NAPLPS_MODE_TRANSPARENT). The pointer stays valid for the lifetime of the
 * context; contents are coherent only between the caller's own calls. Returns NULL
 * on error. */
NAPLPS_IMPORT const uint8_t* naplps_ctx_framebuffer(NaplpsCtx ctx,
                                                    int32_t* out_w, int32_t* out_h,
                                                    int32_t* out_stride);

#ifdef __cplusplus
}
#endif

#endif /* NAPLPS_H */
