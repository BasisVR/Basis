/*
 * fuzz_caption - libFuzzer target for the in-band CEA-608 caption scanner
 * (basis_caption_scan_au).
 *
 * Captions ride inside the coded video as SEI user-data, so this scanner walks
 * every H.26x access unit on the demux thread -- always on, over
 * attacker-controlled bytes, on every transport that carries the video. Unlike
 * the demuxers it takes an AU buffer directly, so the fuzz input IS the AU. We
 * run both SEI layouts (H.264 and H.265) and the poll path each iteration.
 *
 * Build: see ../build.sh (clang -fsanitize=fuzzer,address,undefined).
 */
#include <stdint.h>
#include <stddef.h>

#include "protocol/basis_caption.h"

static void run_one(const uint8_t* data, int len, int hevc) {
    basis_caption_ctx_t* c = basis_caption_create();
    if (!c) return;
    basis_caption_scan_au(c, data, len, hevc, 1000);
    char buf[256];
    int64_t start = 0, end = 0;
    basis_caption_poll(c, 1000, buf, (int)sizeof(buf), &start, &end);
    basis_caption_destroy(c);
}

int LLVMFuzzerTestOneInput(const uint8_t* data, size_t size) {
    if (size > (size_t)INT32_MAX) return 0;
    run_one(data, (int)size, 0); /* H.264 SEI layout */
    run_one(data, (int)size, 1); /* H.265 SEI layout */
    return 0;
}
