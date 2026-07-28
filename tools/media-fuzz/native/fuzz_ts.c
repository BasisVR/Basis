/*
 * fuzz_ts - libFuzzer target for the MPEG-TS demuxer (basis_ts_run).
 *
 * Feeds a fuzzer-supplied byte buffer to the real protocol/basis_ts.c parser
 * through an in-memory read callback, with a sink that reads back every access
 * unit the demuxer emits so a bogus (pointer, len) the parser produces faults
 * under AddressSanitizer instead of passing silently. No decoder, no Media
 * Foundation, no Unity - this is the container parser in isolation, which is
 * the layer attacker-controlled bytes hit first.
 *
 * Build: see ../build.sh (clang -fsanitize=fuzzer,address,undefined).
 */
#include <stdint.h>
#include <stddef.h>
#include <string.h>
#include <stdlib.h>

#include "basis_media_internal.h"
#include "protocol/basis_ts.h"

/* A malformed stream must not let the parser spin forever emitting AUs; the
 * sink unwinds it through is_running once this many have gone by. libFuzzer's
 * -timeout catches genuine infinite loops that never reach a callback. */
#define FUZZ_AU_CAP 200000

typedef struct {
    const uint8_t* data;
    size_t size;
    size_t pos;
    long long aus;
} fuzz_ctx;

/* Force the emitted payload to be read so ASan validates the (ptr,len) the
 * demuxer reports. volatile keeps the compiler from eliding the loads. */
static volatile uint8_t g_sink_byte;

static int fz_read(void* ctx, uint8_t* buf, int len) {
    fuzz_ctx* c = (fuzz_ctx*)ctx;
    if (len <= 0) return 0;
    size_t avail = c->size - c->pos;
    size_t take = (size_t)len < avail ? (size_t)len : avail;
    if (take) {
        memcpy(buf, c->data + c->pos, take);
        c->pos += take;
    }
    return (int)take;
}

static void touch(const uint8_t* p, int len) {
    if (len < 0 || (len > 0 && p == NULL)) abort(); /* sink contract: reject NULL+len, negative len */
    uint8_t acc = 0;
    for (int i = 0; i < len; i++) acc ^= p[i];
    g_sink_byte ^= acc;
}

static void s_video_au(void* u, const uint8_t* annexb, int len,
                       int64_t pts, int64_t dts, int key) {
    fuzz_ctx* c = (fuzz_ctx*)u;
    (void)pts; (void)dts; (void)key;
    touch(annexb, len);
    c->aus++;
}

static void s_audio_frame(void* u, const uint8_t* data, int len, int64_t pts) {
    fuzz_ctx* c = (fuzz_ctx*)u;
    (void)pts;
    touch(data, len);
    c->aus++;
}

static void s_video_format(void* u, basis_codec_t codec, const uint8_t* ed, int el,
                           int w, int h) {
    (void)u; (void)codec; (void)w; (void)h;
    touch(ed, el);
}

static void s_audio_format(void* u, basis_codec_t codec, int rate, int ch,
                           const uint8_t* asc, int asc_len) {
    (void)u; (void)codec; (void)rate; (void)ch;
    touch(asc, asc_len);
}

/* Required by the sink contract (not marked "may be NULL"): the parser calls
 * these without a NULL check, so the harness must supply them. */
static void s_state(void* u, basis_media_state_t s) { (void)u; (void)s; }
static void s_error(void* u, const char* msg) { (void)u; (void)msg; }
static void s_eos(void* u) { (void)u; }

static int s_is_running(void* u) {
    fuzz_ctx* c = (fuzz_ctx*)u;
    return c->aus < FUZZ_AU_CAP;
}

int LLVMFuzzerTestOneInput(const uint8_t* data, size_t size) {
    fuzz_ctx c;
    c.data = data;
    c.size = size;
    c.pos = 0;
    c.aus = 0;

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
    sink.is_running = s_is_running;
    /* on_duration/on_transport/take_seek may be NULL per the contract. */

    basis_ts_run(&sink, fz_read, &c);
    return 0;
}
