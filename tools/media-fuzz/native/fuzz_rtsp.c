/*
 * fuzz_rtsp - libFuzzer target for the RTSP response/RTP parsers.
 *
 * RTSP opens its own socket, so it can't be driven by a read_fn the way the
 * demuxers are. This target reaches the parsers two ways, both against the real
 * shipping basis_rtsp.c (compiled in via #include, so a find is a find in the
 * shipping parser):
 *   - directly on the buffer-taking parsers: parse_sdp (the DESCRIBE body),
 *     depkt_video / depkt_audio (RTP depacketisation + FU/AP/afrag reassembly —
 *     where the reassembly-cap and afrag-drop paths live);
 *   - through rtsp_recv (status line / headers / interleaved framing), fed by a
 *     link-time basis_io stub that serves the fuzz bytes in place of a socket.
 *
 * The other basis_io entry points are stubbed no-ops purely to satisfy the link
 * (basis_rtsp_run references them but is never called here).
 *
 * Build: see ../build.sh (clang -fsanitize=fuzzer,address,undefined).
 */
#include <stdint.h>
#include <stddef.h>
#include <string.h>
#include <stdlib.h>

#include "basis_media_internal.h"
#include "protocol/basis_io.h"

/* Fuzz bytes served to the byte-reading parsers (rtsp_recv) via the io stub. */
static const uint8_t* g_data;
static size_t g_size;
static size_t g_pos;

/* ---- basis_io stub: serve fuzz bytes; everything else is a no-op ---------- */
int basis_io_read_full(basis_io_t* io, uint8_t* buf, int len) {
    (void)io;
    int got = 0;
    while (got < len && g_pos < g_size) buf[got++] = g_data[g_pos++];
    return got; /* <len at EOF, which the callers treat as a closed connection */
}
int basis_io_read(basis_io_t* io, uint8_t* buf, int len) { return basis_io_read_full(io, buf, len); }
int basis_io_write_full(basis_io_t* io, const uint8_t* b, int len) { (void)io; (void)b; return len; }
int basis_io_send(basis_io_t* io, const uint8_t* b, int len) { (void)io; (void)b; return len; }
basis_io_t* basis_io_connect(const char* h, int p, int t) { (void)h; (void)p; (void)t; return NULL; }
void basis_io_close(basis_io_t* io) { (void)io; }
int basis_io_peer_addr(basis_io_t* io, char* b, int c) { (void)io; if (c > 0) b[0] = 0; return -1; }
int basis_io_poll_read(basis_io_t** i, int n, int t) { (void)i; (void)n; (void)t; return 0; }
void basis_io_set_read_timeout(basis_io_t* io, int t) { (void)io; (void)t; }
int basis_io_udp_connect(basis_io_t* io, const char* h, int p) { (void)io; (void)h; (void)p; return -1; }
int basis_io_udp_open_pair(const char* h, basis_io_t** a, basis_io_t** b, int* p) {
    (void)h; if (a) *a = NULL; if (b) *b = NULL; if (p) *p = 0; return -1;
}

/* Pull in the real parser (statics become visible in this TU). */
#include "protocol/basis_rtsp.c"

/* ---- counting sink: force ASan to read every emitted payload -------------- */
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

static void run_depkt(basis_media_sink_t* sink, basis_codec_t vcodec, const uint8_t* data, int len) {
    depkt_t d;
    memset(&d, 0, sizeof(d));
    sdp_media_t vid, aud;
    memset(&vid, 0, sizeof(vid)); vid.codec = vcodec; vid.clock = 90000;
    memset(&aud, 0, sizeof(aud)); aud.codec = BASIS_CODEC_AAC; aud.clock = 48000; aud.channels = 2;
    d.sink = sink; d.video = &vid; d.audio = &aud;
    depkt_video(&d, data, len);
    depkt_audio(&d, data, len);
    free(d.au); free(d.fu); free(d.afrag);
}

int LLVMFuzzerTestOneInput(const uint8_t* data, size_t size) {
    if (size > (1u << 20)) return 0; /* keep individual runs bounded */
    g_data = data; g_size = size; g_pos = 0;

    basis_media_sink_t sink;
    make_sink(&sink);

    /* SDP body parser (server-controlled DESCRIBE response text). */
    { sdp_media_t v, a; parse_sdp((const char*)data, (int)size, &v, &a); }

    /* RTP depacketisation for both NAL layouts (FU/AP/afrag reassembly). */
    run_depkt(&sink, BASIS_CODEC_H264, data, (int)size);
    run_depkt(&sink, BASIS_CODEC_H265, data, (int)size);

    /* RTSP response reader: status line / headers / body / interleaved framing,
     * fed the fuzz bytes through the io stub. */
    {
        rtsp_t r;
        memset(&r, 0, sizeof(r));
        r.io = (basis_io_t*)&r; /* non-NULL; the stub ignores it */
        char body[8192];
        int blen = 0;
        g_pos = 0;
        rtsp_recv(&r, body, (int)sizeof(body) - 1, &blen);
    }
    return 0;
}
