/* naplps.h — C header for the NAPLPS NativeAOT library.
 *
 * Link against NAPLPS.dll (Windows), libNAPLPS.so (Linux), or libNAPLPS.dylib
 * (macOS) produced by:
 *
 *     dotnet publish NAPLPS/NAPLPS.csproj -c Release -r <rid> --property:PublishAot=true
 *
 * where <rid> is one of win-x64, linux-x64, osx-x64, osx-arm64, linux-arm64.
 *
 * All functions are thread-safe; each call builds its own internal render state.
 *
 * Error codes (negative return values):
 *   -1  Parse error or exception.
 *   -2  Output buffer too small. Call again with a larger buffer.
 *   -3  Invalid input (null pointer or non-positive length).
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
 * Painted pixels persist; exec steps are idempotent with respect to already-painted
 * commands.
 *
 * Additional error codes for context calls:
 *   -4  Stream exhausted (naplps_ctx_exec_next only; not an error).
 *   -5  Bad handle.
 *
 * Caveat: mid-stream palette redefinition (generic NAPLPS CLUT animation) is applied
 * retroactively by the one-shot PNG renders but NOT by stepped execution. Prodigy
 * mode is unaffected (fixed hardware palette).
 *
 * Thread safety: the handle table is internally locked, but a single context must
 * not be used from multiple threads concurrently.
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
 * may split anywhere, including mid-command. Returns the new total parsed command
 * count, or a negative error code. */
NAPLPS_IMPORT int32_t   naplps_ctx_append(NaplpsCtx ctx, const uint8_t* bytes, int32_t len);
NAPLPS_IMPORT int32_t   naplps_ctx_command_count(NaplpsCtx ctx);

/* --- Execute / step --- */
/* Paint the framebuffer up through (and including) cmd_index. Idempotent for
 * already-painted commands. Returns the highest painted index, or negative error. */
NAPLPS_IMPORT int32_t   naplps_ctx_exec_to(NaplpsCtx ctx, int32_t cmd_index);

/* Execute exactly one command (the next unpainted one). Optionally reports the
 * changed rectangle via out_dirty (pass NULL to skip; v1 reports the full canvas).
 * Returns the index just executed, NAPLPS_CTX_EXHAUSTED when the stream is fully
 * painted, or a negative error. */
NAPLPS_IMPORT int32_t   naplps_ctx_exec_next(NaplpsCtx ctx, NaplpsRect* out_dirty);

/* --- Pixels --- */
/* Return a pointer to the current RGBA8888 framebuffer (refreshed at call time).
 * The pointer stays valid for the lifetime of the context. Returns NULL on error. */
NAPLPS_IMPORT const uint8_t* naplps_ctx_framebuffer(NaplpsCtx ctx,
                                                    int32_t* out_w, int32_t* out_h,
                                                    int32_t* out_stride);

#ifdef __cplusplus
}
#endif

#endif /* NAPLPS_H */
