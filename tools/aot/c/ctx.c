/* ctx.c - smoke test for the stateful naplps_ctx_* API.
 *
 * Usage: ctx <file.nap>
 *
 * Appends the file in two chunks split mid-stream, steps every command, verifies the
 * framebuffer lights up, draws a field-text run, and resets. Exit 0 on success.
 *
 * Build (macOS):
 *   cc ctx.c -I ../include ../publish/NAPLPS.dylib -Wl,-rpath,$(pwd)/../publish -o ctx
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "naplps.h"

int main(int argc, char** argv) {
    if (argc < 2) { fprintf(stderr, "usage: %s <file.nap>\n", argv[0]); return 2; }

    unsigned char ver[64];
    naplps_version(ver, sizeof ver);
    printf("lib version: %s\n", ver);

    FILE* f = fopen(argv[1], "rb");
    if (!f) { perror("open"); return 1; }
    fseek(f, 0, SEEK_END);
    long n = ftell(f);
    fseek(f, 0, SEEK_SET);
    uint8_t* buf = malloc(n);
    fread(buf, 1, n, f);
    fclose(f);

    NaplpsCtx ctx = naplps_ctx_create(640, 480, NAPLPS_MODE_PRODIGY);
    if (!ctx) { printf("create failed\n"); return 1; }

    /* split mid-stream: decoder state must carry across the boundary */
    int32_t half = (int32_t)(n / 2);
    int32_t c1 = naplps_ctx_append(ctx, buf, half);
    int32_t c2 = naplps_ctx_append(ctx, buf + half, (int32_t)(n - half));
    printf("append: %d then %d commands\n", c1, c2);
    if (c2 <= 0) { return 1; }

    int steps = 0;
    NaplpsRect dirty;
    for (;;) {
        int32_t r = naplps_ctx_exec_next(ctx, &dirty);
        if (r == NAPLPS_CTX_EXHAUSTED) { break; }
        if (r < 0) { printf("exec error %d\n", r); return 1; }
        steps++;
    }

    int32_t w, h, stride;
    const uint8_t* fb = naplps_ctx_framebuffer(ctx, &w, &h, &stride);
    if (!fb) { printf("framebuffer null\n"); return 1; }
    long lit = 0;
    for (long i = 0; i < (long)w * h; i++) {
        const uint8_t* p = fb + i * 4;
        if (p[0] > 8 || p[1] > 8 || p[2] > 8) { lit++; }
    }

    const char* s = "FIELD TEXT";
    int32_t after = naplps_ctx_draw_text(ctx, 0.1, 0.1, 7, 3, 0.025, 0.0390625,
                                         (const uint8_t*)s, (int32_t)strlen(s));
    if (after <= c2) { printf("draw_text failed: %d\n", after); return 1; }
    if (naplps_ctx_exec_to(ctx, after - 1) != after - 1) { printf("exec_to failed\n"); return 1; }

    naplps_ctx_reset(ctx);
    fb = naplps_ctx_framebuffer(ctx, &w, &h, &stride);
    long lit2 = 0;
    for (long i = 0; i < (long)w * h; i++) {
        const uint8_t* p = fb + i * 4;
        if (p[0] | p[1] | p[2]) { lit2++; }
        if (p[3] != 255) { printf("non-opaque alpha after reset\n"); return 1; }
    }

    naplps_ctx_destroy(ctx);
    free(buf);
    printf("stepped %d commands; %ld lit pixels; reset lit %ld\n", steps, lit, lit2);
    int ok = steps > 10 && lit > 1000 && lit2 == 0;
    printf(ok ? "CTX SMOKE PASS\n" : "CTX SMOKE FAIL\n");
    return ok ? 0 : 1;
}
