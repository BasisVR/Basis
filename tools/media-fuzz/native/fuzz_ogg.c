/*
 * fuzz_ogg - libFuzzer target for the Ogg demuxer (basis_ogg_run).
 *
 * Ogg is page framing (OggS capture pattern, 27-byte header, segment table,
 * body), packets reassembled across pages by lacing, CRC-32 per page, and
 * granulepos/TOC timing -- all length-driven and attacker-controlled. Same
 * in-memory read + reseek + contract-complete sink as the other targets.
 *
 * Build: see ../build.sh (clang -fsanitize=fuzzer,address,undefined).
 */
#include <stdint.h>
#include <stddef.h>
#include <string.h>
#include <stdlib.h>

#include "basis_media_internal.h"
#include "protocol/basis_ogg.h"

#define FUZZ_AU_CAP 200000

typedef struct {
    const uint8_t* data;
    size_t size;
    size_t pos;
    long long aus;
    int seeked;
} fuzz_ctx;

static volatile uint8_t g_sink_byte;

static int fz_read(void* ctx, uint8_t* buf, int len) {
    fuzz_ctx* c = (fuzz_ctx*)ctx;
    if (len <= 0) return 0;
    size_t avail = c->pos < c->size ? c->size - c->pos : 0;
    size_t take = (size_t)len < avail ? (size_t)len : avail;
    if (take) {
        memcpy(buf, c->data + c->pos, take);
        c->pos += take;
    }
    return (int)take;
}

static int fz_reseek(void* ctx, int64_t abs_offset) {
    fuzz_ctx* c = (fuzz_ctx*)ctx;
    if (abs_offset < 0 || (uint64_t)abs_offset > c->size) return -1;
    c->pos = (size_t)abs_offset;
    return 0;
}

static void touch(const uint8_t* p, int len) {
    if (len < 0 || (len > 0 && p == NULL)) abort(); /* sink contract: reject NULL+len, negative len */
    uint8_t acc = 0;
    for (int i = 0; i < len; i++) acc ^= p[i];
    g_sink_byte ^= acc;
}

static void s_audio_frame(void* u, const uint8_t* data, int len, int64_t pts) {
    fuzz_ctx* c = (fuzz_ctx*)u;
    (void)pts;
    touch(data, len);
    c->aus++;
}
static void s_audio_format(void* u, basis_codec_t codec, int rate, int ch,
                           const uint8_t* asc, int asc_len) {
    (void)u; (void)codec; (void)rate; (void)ch;
    touch(asc, asc_len);
}
static void s_video_format(void* u, basis_codec_t c, const uint8_t* e, int el, int w, int h) {
    (void)u; (void)c; (void)e; (void)el; (void)w; (void)h;
}
static void s_video_au(void* u, const uint8_t* a, int l, int64_t p, int64_t d, int k) {
    (void)u; (void)a; (void)l; (void)p; (void)d; (void)k;
}
static void s_state(void* u, basis_media_state_t s) { (void)u; (void)s; }
static void s_error(void* u, const char* m) { (void)u; (void)m; }
static void s_eos(void* u) { (void)u; }
static int s_is_running(void* u) { fuzz_ctx* c = (fuzz_ctx*)u; return c->aus < FUZZ_AU_CAP; }
/* Request one seek so the granule bisection path is exercised; the target is
 * derived from the input so it varies across cases. */
static int s_take_seek(void* u, int64_t* out) {
    fuzz_ctx* c = (fuzz_ctx*)u;
    if (!c->seeked) { c->seeked = 1; *out = (int64_t)(c->size ? (c->data[0] * 100000) : 0); return 1; }
    return 0;
}

int LLVMFuzzerTestOneInput(const uint8_t* data, size_t size) {
    fuzz_ctx c;
    c.data = data; c.size = size; c.pos = 0; c.aus = 0; c.seeked = 0;

    basis_media_sink_t sink;
    memset(&sink, 0, sizeof(sink));
    sink.user = &c;
    sink.on_video_format = s_video_format;
    sink.on_video_au = s_video_au;
    sink.on_audio_format = s_audio_format;
    sink.on_audio_frame = s_audio_frame;
    sink.on_state = s_state;
    sink.on_error = s_error;
    sink.on_end_of_stream = s_eos;
    sink.take_seek = s_take_seek;
    sink.is_running = s_is_running;

    basis_ogg_run(&sink, fz_read, &c, fz_reseek, &c, (int64_t)size);
    return 0;
}
