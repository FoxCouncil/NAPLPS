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
 *   -1  Parse error or exception. Context calls are transactional: the context is
 *       left unchanged.
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
 * Model: appended bytes accumulate as the source of truth; decoder state (character
 * sets, DRCS glyph definitions, position, attributes) carries across appends, so
 * "define character sets, then draw field text with them" works across calls.
 * Appends are transactional (failure leaves the context unchanged). Each append
 * re-parses the accumulated stream and re-establishes the painted prefix, so append
 * cost grows with total stream size; batch appends where convenient.
 *
 * Chunks may split anywhere, including mid-command. A chunk that ends mid-command
 * paints that command from its truncated operands if executed; the completing append
 * repaints it correctly. In other words: pixels are exact once the appended bytes
 * form a complete stream, but a blit taken between a mid-command append and its
 * completion can show a transient partial stroke. Callers that append complete
 * streams (the recommended model) never observe this.
 *
 * Caveat: mid-stream palette redefinition (generic NAPLPS CLUT animation) is applied
 * retroactively by the one-shot PNG renders but NOT by stepped execution. Prodigy
 * mode is unaffected (fixed hardware palette).
 *
 * Thread safety: see the top of this header - one context per thread of use. The
 * framebuffer pointer is only coherent between the caller's own calls.
 */

/* Opaque handle to a stateful decoder + its framebuffer. NULL/0 on failure. */
typedef intptr_t NaplpsCtx;

/* Flags for naplps_ctx_create. */
#define NAPLPS_MODE_PRODIGY  0x0001  /* 2-bit color guns, MVDI font, Prodigy aspect */

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
 * framebuffer: parsing and painting continue from the current state. Byte chunks
 * may split anywhere, including mid-command (see the section comment for the
 * transient-repaint consequence). Transactional: a negative return leaves the
 * context unchanged. Returns the new total parsed command count, or a negative
 * error code. */
NAPLPS_IMPORT int32_t   naplps_ctx_append(NaplpsCtx ctx, const uint8_t* bytes, int32_t len);
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
 * Returns the new total command count; -3 when the stream currently ends inside an
 * unfinished macro/DRCS/texture definition (the bytes would be swallowed into the
 * definition); or a negative error code. */
NAPLPS_IMPORT int32_t   naplps_ctx_draw_text(NaplpsCtx ctx,
                                             double x, double y,
                                             int32_t fg, int32_t bg,
                                             double char_w, double char_h,
                                             const uint8_t* ascii, int32_t len);

/* --- Pixels --- */
/* Return a pointer to the current RGBA8888 framebuffer (refreshed at call time;
 * opaque black before any append). The pointer stays valid for the lifetime of the
 * context; contents are coherent only between the caller's own calls. Returns NULL
 * on error. */
NAPLPS_IMPORT const uint8_t* naplps_ctx_framebuffer(NaplpsCtx ctx,
                                                    int32_t* out_w, int32_t* out_h,
                                                    int32_t* out_stride);

#ifdef __cplusplus
}
#endif

#endif /* NAPLPS_H */
