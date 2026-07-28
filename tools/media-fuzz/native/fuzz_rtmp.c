/*
 * fuzz_rtmp - libFuzzer target for the RTMP chunk / AMF / FLV parsers.
 *
 * Like fuzz_rtsp, RTMP owns its socket, so this compiles the real basis_rtmp.c in
 * (via #include) and reaches its parsers directly:
 *   - amf_find_stream_id (server _result AMF walk — the M2 bug lived here);
 *   - handle_video / handle_audio (FLV tag -> AVC/AAC extraction) on a synthetic
 *     chunk;
 *   - rtmp_read_message (the chunk-header assembler) fed the fuzz bytes through a
 *     link-time basis_io stub in place of a socket.
 *
 * Build: see ../build.sh (clang -fsanitize=fuzzer,address,undefined).
 */
#include <stdint.h>
#include <stddef.h>
#include <string.h>
#include <stdlib.h>

#include "basis_media_internal.h"
#include "protocol/basis_io.h"

static const uint8_t* g_data;
static size_t g_size;
static size_t g_pos;

/* ---- basis_io stub: serve fuzz bytes; the rest are no-ops for the link ----- */
int basis_io_read_full(basis_io_t* io, uint8_t* buf, int len) {
    (void)io;
    int got = 0;
    while (got < len && g_pos < g_size) buf[got++] = g_data[g_pos++];
    return got;
}
int basis_io_write_full(basis_io_t* io, const uint8_t* b, int len) { (void)io; (void)b; return len; }
basis_io_t* basis_io_connect(const char* h, int p, int t) { (void)h; (void)p; (void)t; return NULL; }
void basis_io_close(basis_io_t* io) { (void)io; }

#include "protocol/basis_rtmp.c"

/* ---- counting sink -------------------------------------------------------- */
static volatile uint8_t g_sink_byte;
static void touch(const uint8_t* p, int len) {
    if (len < 0 || (len > 0 && p == NULL)) abort();
    uint8_t acc = 0;
    for (int i = 0; i < len; i++) acc ^= p[i];
    g_sink_byte ^= acc;
}
static void s_vau(void* u, const uint8_t* d, int n, int64_t a, int64_t b, int k) { (void)u; (void)a; (void)b; (void)k; touch(d, n); }
static void s_afr(void* u, const uint8_t* d, int n, int64_t a) { (void)u; (void)a; touch(d, n); }
static void s_vfmt(void* u, basis_codec_t c, const uint8_t* e, int el, int w, int h) { (void)u; (void)c; (void)w; (void)h; touch(e, el); }
static void s_afmt(void* u, basis_codec_t c, int r, int ch, const uint8_t* a, int al) { (void)u; (void)c; (void)r; (void)ch; touch(a, al); }
static void s_state(void* u, basis_media_state_t s) { (void)u; (void)s; }
static void s_err(void* u, const char* m) { (void)u; (void)m; }
static void s_eos(void* u) { (void)u; }
static int s_run(void* u) { (void)u; return 1; }

static void make_sink(basis_media_sink_t* sink) {
    memset(sink, 0, sizeof(*sink));
    sink->on_video_format = s_vfmt;
    sink->on_video_au = s_vau;
    sink->on_audio_format = s_afmt;
    sink->on_audio_frame = s_afr;
    sink->on_state = s_state;
    sink->on_error = s_err;
    sink->on_end_of_stream = s_eos;
    sink->is_running = s_run;
}

int LLVMFuzzerTestOneInput(const uint8_t* data, size_t size) {
    if (size > (1u << 20)) return 0;
    g_data = data; g_size = size; g_pos = 0;

    basis_media_sink_t sink;
    make_sink(&sink);

    /* AMF _result walk. */
    amf_find_stream_id(data, (int)size);

    /* FLV video/audio tag parsers. Copy to a writable heap buffer (the parsers
     * treat the chunk as read-only, but never hand a fuzzer's read-only mapping
     * to code that might write). */
    {
        uint8_t* copy = (uint8_t*)malloc(size ? size : 1);
        if (copy) {
            if (size) memcpy(copy, data, size);
            rtmp_t r; memset(&r, 0, sizeof(r));
            r.in_chunk_size = RTMP_DEFAULT_CHUNK; r.video_nls = 4; r.video_codec = BASIS_CODEC_H264;
            chunk_state_t c; memset(&c, 0, sizeof(c));
            c.buf = copy; c.have = (int)size; c.len = (uint32_t)size;
            c.type = 9; handle_video(&r, &sink, &c);
            c.type = 8; handle_audio(&r, &sink, &c);
            free(copy);
        }
    }

    /* Chunk-header assembler, fed the fuzz bytes through the io stub. */
    {
        rtmp_t r; memset(&r, 0, sizeof(r));
        r.in_chunk_size = RTMP_DEFAULT_CHUNK;
        r.io = (basis_io_t*)&r;
        g_pos = 0;
        for (int i = 0; i < 4096; i++) {
            if (rtmp_read_message(&r) < 0) break;
        }
        for (int i = 0; i < MAX_CSID; i++) free(r.cs[i].buf);
    }
    return 0;
}
