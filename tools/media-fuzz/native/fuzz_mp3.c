/*
 * fuzz_mp3 - libFuzzer target for the standalone MP3 demuxer (basis_mp3_run).
 *
 * MP3 is header-driven with no container: the demuxer skips a leading ID3v2 tag
 * (attacker-controlled syncsafe size) and locks onto the frame sync, deriving
 * each frame's length from its header. The false-sync guard, the buffer slip,
 * and the ID3 skip are all length-arithmetic on hostile bytes. The harness also
 * hands the demuxer a couple of seek requests to drive the reseek/resync path,
 * all through a contract-complete sink.
 *
 * Build: see ../build.sh (clang -fsanitize=fuzzer,address,undefined).
 */
#include <stdint.h>
#include <stddef.h>
#include <string.h>

#include "basis_media_internal.h"
#include "protocol/basis_mp3.h"

#define FUZZ_AU_CAP 200000

typedef struct {
    const uint8_t* data;
    size_t size;
    size_t pos;
    long long aus;
    int seeks_left;   /* hand out a couple of seek requests to exercise resync */
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

/* Drive the seek path: derive a target from the input's own bytes so it varies. */
static int fz_take_seek(void* u, int64_t* out_target_us) {
    fuzz_ctx* c = (fuzz_ctx*)u;
    if (c->seeks_left <= 0) return 0;
    c->seeks_left--;
    *out_target_us = (c->data && c->size)
        ? (int64_t)c->data[c->pos % c->size] * 100000
        : 0;
    return 1;
}

static void touch(const uint8_t* p, int len) {
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
    if (asc && asc_len > 0) touch(asc, asc_len);
}

/* Required by the sink contract (not marked "may be NULL"): the parser calls
 * these without a NULL check, so the harness must supply them. */
static void s_state(void* u, basis_media_state_t s) { (void)u; (void)s; }
static void s_error(void* u, const char* msg) { (void)u; (void)msg; }
static void s_eos(void* u) { (void)u; }

static void s_duration(void* u, int64_t d) { (void)u; (void)d; }

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
    c.seeks_left = 2;

    basis_media_sink_t sink;
    memset(&sink, 0, sizeof(sink));
    sink.user = &c;
    sink.on_audio_format = s_audio_format;
    sink.on_audio_frame = s_audio_frame;
    sink.on_state = s_state;
    sink.on_error = s_error;
    sink.on_end_of_stream = s_eos;
    sink.on_duration = s_duration;
    sink.take_seek = fz_take_seek;
    sink.is_running = s_is_running;

    basis_mp3_run(&sink, fz_read, &c, fz_reseek, &c);
    return 0;
}
