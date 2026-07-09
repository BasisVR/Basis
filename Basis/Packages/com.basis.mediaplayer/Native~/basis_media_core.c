/*
 * basis_media_core.c — engine lifecycle, demux thread, state machine, and the
 * sink that bridges the portable demuxers to the platform decode backend.
 *
 * One basis_media_engine owns:
 *   - a parsed URL and a platform basis_decoder (OS hardware decode -> GPU texture)
 *   - a demux thread that connects + parses the live stream into elementary
 *     H.264/H.265 + AAC and pushes them through a basis_media_sink
 *   - thread-safe state + last-error string read by the public ABI getters
 *
 * The public ABI getters/setters just delegate to the decoder or read state.
 */

#include "basis_media_native.h"
#include "basis_media_internal.h"

#include "protocol/basis_url.h"
#include "protocol/basis_io.h"
#include "protocol/basis_rtsp.h"
#include "protocol/basis_rtmp.h"
#include "protocol/basis_ts.h"
#include "protocol/basis_mp4.h"
#include "protocol/basis_http.h"
#include "protocol/basis_hls.h"
#include "protocol/basis_rist.h"
#include "protocol/basis_caption.h"

#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <time.h>

#if defined(_WIN32)
  #include <windows.h>
  #include "windows/basis_win_http.h"
  typedef HANDLE       basis_thread_t;
  typedef CRITICAL_SECTION basis_mutex_t;
#else
  #include <pthread.h>
  #include <unistd.h>
  typedef pthread_t        basis_thread_t;
  typedef pthread_mutex_t  basis_mutex_t;
#endif
#if defined(__ANDROID__)
  #include "android/basis_jni_https.h"
  #include <android/log.h>
  #define BASIS_LOGI(...) __android_log_print(ANDROID_LOG_INFO, "basis_media", __VA_ARGS__)
#else
  #define BASIS_LOGI(...) ((void)0)
#endif

/* ---- tiny thread/mutex/sleep shims -------------------------------------- */

static void mutex_init(basis_mutex_t* m) {
#if defined(_WIN32)
    InitializeCriticalSection(m);
#else
    pthread_mutex_init(m, NULL);
#endif
}
static void mutex_destroy(basis_mutex_t* m) {
#if defined(_WIN32)
    DeleteCriticalSection(m);
#else
    pthread_mutex_destroy(m);
#endif
}
static void mutex_lock(basis_mutex_t* m) {
#if defined(_WIN32)
    EnterCriticalSection(m);
#else
    pthread_mutex_lock(m);
#endif
}
static void mutex_unlock(basis_mutex_t* m) {
#if defined(_WIN32)
    LeaveCriticalSection(m);
#else
    pthread_mutex_unlock(m);
#endif
}
static void sleep_ms(int ms) {
#if defined(_WIN32)
    Sleep((DWORD)ms);
#else
    usleep((useconds_t)ms * 1000);
#endif
}
static int64_t now_us(void) {
#if defined(_WIN32)
    /* Frequency is fixed for the session; cache it (the first-call write is idempotent
     * across threads). Split the conversion so c.QuadPart * 1000000 can't overflow int64
     * at long uptime (a plain multiply wraps after ~10 days at a 10 MHz QPC). */
    static int64_t freq;
    LARGE_INTEGER c;
    if (!freq) { LARGE_INTEGER f; QueryPerformanceFrequency(&f); freq = f.QuadPart ? f.QuadPart : 1; }
    QueryPerformanceCounter(&c);
    return (c.QuadPart / freq) * 1000000LL + (c.QuadPart % freq) * 1000000LL / freq;
#else
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (int64_t)ts.tv_sec * 1000000LL + ts.tv_nsec / 1000;
#endif
}

/* ---- engine ------------------------------------------------------------- */

struct basis_media_engine {
    char  url[2048];
    basis_url_t parts;

    basis_decoder_t* decoder;

    basis_thread_t thread;
    int thread_started;
    volatile int running;
    volatile int paused;

    basis_mutex_t lock;
    /* Serialises decoder submit/format from the two demux threads (video + audio leg) so
     * the "two demux threads -> one decoder" model is safe by construction on every backend,
     * not just where the decoder happens to be internally concurrent-safe. Held only around
     * the submit/format calls, never around pace_gate (which sleeps). */
    basis_mutex_t submit_lock;
    basis_media_state_t state;
    char error[512];

    basis_media_sink_t sink;

    /* Optional separate audio-only stream (split-stream playback). When url_audio
     * is set, the primary URL is treated as video-only and this carries audio; a
     * second demux thread feeds the same decoder, so both share one clock. Empty
     * url_audio => single muxed stream and the second thread never starts (the
     * single-stream path is byte-for-byte unchanged). */
    char  url_audio[2048];
    basis_url_t parts_audio;
    basis_media_sink_t audio_sink;
    basis_thread_t audio_thread;
    int audio_thread_started;

    /* Delivery + presentation pacing.
     *   paced         — present on a fixed 1x clock from the first PTS (VOD); off => live edge.
     *   pace_delivery — throttle AU delivery to ~1x (pace_gate) so a faster-than-real-time
     *                   source can't flood the decoder. On for VOD AND for live HLS, which
     *                   buffers segments and would otherwise burst; live HLS keeps paced=0
     *                   so it still presents at — and converges to — the live edge.
     * paced_hint is the caller's request (0=auto, 1=live, 2=on-demand); the protocol handler
     * resolves paced/pace_delivery once it has inspected the source (run_http_like/run_hls).
     * The pace anchor (first AU's wall time + PTS) is engine-wide so a split source's two
     * legs pace against one timeline.
     * Thread-safety: paced/pace_delivery/paced_hint are set during run setup (run_http_like/
     * run_hls) before the demux and audio threads start, then only read — effectively
     * immutable while pacing (thread creation publishes them to the new threads). The anchor
     * (pace_started/wall0/base_pts) is initialised and read under e->lock in pace_gate, so a
     * split source's two demux threads share one timeline correctly on any memory model. */
    int paced;
    int pace_delivery;
    int paced_hint;
    int pace_started;
    int64_t pace_wall0_us;
    int64_t pace_base_pts;

    /* In-band CEA-608 caption extraction. video_hevc selects the SEI NAL layout,
     * set from the video format; the context owns the 608 decoder + cue store and
     * is scanned per AU on the demux thread, polled from the main thread. */
    basis_caption_ctx_t* captions;
    int video_hevc;

    /* diagnostics (demux thread writes, main thread reads; minor races OK) */
    volatile long video_au_count;
    volatile long audio_frame_count;

    /* Total media duration reported by the demuxer (VOD); 0 = unknown/live.
     * Demux thread writes once, main thread reads — a torn read on 32-bit is
     * the worst case and Windows/Android are 64-bit. */
    volatile int64_t duration_us;

    /* Absolute-seek handshake. The main thread posts target+seq under e->lock;
     * each demux leg takes a posted request once (its own taken counter — a
     * split source's two legs both reposition). HLS repositions at the segment
     * source instead: active_hls is set while run_hls owns a context. */
    long seek_seq;
    int64_t seek_target_us;
    volatile long seek_taken_main;
    volatile long seek_taken_audio;
    void* active_hls;   /* guarded by e->lock */
};

/* ---- state/error helpers (exported to internal) ------------------------- */

void basis_engine_set_state(basis_media_engine_t* e, basis_media_state_t s) {
    if (!e) return;
    mutex_lock(&e->lock);
    /* don't clobber a terminal error with a later non-error state */
    if (e->state != BASIS_MEDIA_STATE_ERROR || s == BASIS_MEDIA_STATE_ERROR)
        e->state = s;
    mutex_unlock(&e->lock);
}

void basis_engine_set_error(basis_media_engine_t* e, const char* msg) {
    if (!e) return;
    mutex_lock(&e->lock);
    if (msg) {
        strncpy(e->error, msg, sizeof(e->error) - 1);
        e->error[sizeof(e->error) - 1] = 0;
    }
    e->state = BASIS_MEDIA_STATE_ERROR;
    mutex_unlock(&e->lock);
}

basis_decoder_t* basis_engine_get_decoder(basis_media_engine_t* e) { return e ? e->decoder : NULL; }
int basis_engine_is_paused(basis_media_engine_t* e) { return e ? e->paused : 0; }
int basis_engine_is_running(basis_media_engine_t* e) { return e ? e->running : 0; }
int basis_engine_is_paced(basis_media_engine_t* e) { return e ? e->paced : 0; }

/* Real-time delivery pacing. Blocks the demux thread so an access unit is handed to the
 * decoder no more than BASIS_PACE_LEAD_US ahead of a fixed 1x clock anchored to the first
 * AU — stalling the socket read (TCP backpressure) so a faster-than-real-time source can't
 * flood the decoder and fast-forward. Metering by PTS tracks VBR exactly, and the
 * wall-clock anchor lets a post-stall backlog drain immediately to re-converge to the
 * edge. The anchor is engine-wide so a split source's two legs pace against one timeline.
 * Lead stays under the decode ring's span, so no ring backpressure is needed. No-op unless
 * pace_delivery is set (VOD, or live HLS — whose own byte-rate metering is disabled). */
#define BASIS_PACE_LEAD_US 400000

static void pace_gate(basis_media_engine_t* e, int64_t pts_us) {
    if (!e->pace_delivery) return;
    /* Init-or-read the anchor under the lock, once, into locals — the anchor is immutable
     * after the first AU, so the wait loop runs lock-free on the locals. Reading under the
     * lock makes the two demux threads agree on one timeline regardless of memory model. */
    int64_t wall0, base;
    mutex_lock(&e->lock);
    if (!e->pace_started) {
        e->pace_wall0_us = now_us();
        e->pace_base_pts = pts_us;
        e->pace_started = 1;
    }
    wall0 = e->pace_wall0_us;
    base = e->pace_base_pts;
    mutex_unlock(&e->lock);
    while (e->running) {
        int64_t media_now = base + (now_us() - wall0);
        int64_t ahead = pts_us - (media_now + BASIS_PACE_LEAD_US);
        if (ahead <= 0) return;
        int ms = (int)(ahead / 1000);
        if (ms > 50) ms = 50;   /* cap so a stop is observed promptly */
        if (ms < 1) ms = 1;
        sleep_ms(ms);
    }
}

/* ---- sink callbacks (run on the demux thread) --------------------------- */

/* Decoder submit/format calls go through e->submit_lock: the video and audio legs run on
 * separate demux threads but feed one decoder, so serialise their decoder access here (and
 * only here — not pace_gate, which sleeps) rather than relying on each backend being
 * internally concurrent-safe. */
static void sink_video_format(void* user, basis_codec_t codec, const uint8_t* ed, int ed_len, int w, int h) {
    basis_media_engine_t* e = (basis_media_engine_t*)user;
    e->video_hevc = (codec == BASIS_CODEC_H265);
    mutex_lock(&e->submit_lock);
    basis_decoder_set_video_format(e->decoder, codec, ed, ed_len, w, h);
    mutex_unlock(&e->submit_lock);
}
static void sink_video_au(void* user, const uint8_t* au, int len, int64_t pts, int64_t dts, int key) {
    basis_media_engine_t* e = (basis_media_engine_t*)user;
    if (!e->running) return;
    /* Pace on the decode timestamp: gating on pts would sleep out a composition
     * offset the decoder still needs the AU inside of, and starve the other
     * track's earlier samples queued behind this one on the demux thread. */
    pace_gate(e, dts);              /* paced mode: hold until ~real time; no-op otherwise */
    if (!e->running) return;        /* may have been stopped while pacing */
    e->video_au_count++;
    mutex_lock(&e->submit_lock);
    basis_decoder_submit_video(e->decoder, au, len, pts, key);
    mutex_unlock(&e->submit_lock);
    /* Extract in-band captions from the same Annex B AU. Independent of the
     * decoder, so outside submit_lock; the caption context locks its own store. */
    basis_caption_scan_au(e->captions, au, len, e->video_hevc, pts);
    /* CONNECTING/BUFFERING -> PLAYING once the OS decoder is actually producing
     * frames (a few buffered), so the state doesn't sit at Buffering forever. */
    if ((e->state == BASIS_MEDIA_STATE_CONNECTING || e->state == BASIS_MEDIA_STATE_BUFFERING) &&
        basis_decoder_get_frame_counter(e->decoder) >= 4)
        basis_engine_set_state(e, BASIS_MEDIA_STATE_PLAYING);
}
static void sink_audio_format(void* user, basis_codec_t codec, int rate, int ch, const uint8_t* asc, int asc_len) {
    basis_media_engine_t* e = (basis_media_engine_t*)user;
    mutex_lock(&e->submit_lock);
    basis_decoder_set_audio_format(e->decoder, codec, rate, ch, asc, asc_len);
    mutex_unlock(&e->submit_lock);
}
static void sink_audio_frame(void* user, const uint8_t* data, int len, int64_t pts) {
    basis_media_engine_t* e = (basis_media_engine_t*)user;
    if (!e->running) return;
    pace_gate(e, pts);              /* paced mode: hold until ~real time; no-op otherwise */
    if (!e->running) return;
    e->audio_frame_count++;
    mutex_lock(&e->submit_lock);
    basis_decoder_submit_audio(e->decoder, data, len, pts);
    mutex_unlock(&e->submit_lock);
}
static void sink_state(void* user, basis_media_state_t s) { basis_engine_set_state((basis_media_engine_t*)user, s); }
static void sink_error(void* user, const char* m) { basis_engine_set_error((basis_media_engine_t*)user, m); }
static void sink_eos(void* user) { basis_engine_set_state((basis_media_engine_t*)user, BASIS_MEDIA_STATE_ENDED); }
static void sink_duration(void* user, int64_t us) { basis_media_engine_t* e = (basis_media_engine_t*)user; if (us > 0) e->duration_us = us; }
/* A raised error is fatal to the current demux run: the reconnect loop already
 * treats an error state as non-retryable, so stopping here makes the protocol
 * demuxer leave promptly instead of streaming a still-decodable track on past a
 * fatal parse error (e.g. an unsupported stz2 track alongside a valid one). The
 * unlocked state read matches the lock-free `running` checks throughout the hot
 * demux/pace loops; a stale read only costs one extra iteration. */
static int  sink_is_running(void* user) { basis_media_engine_t* e = (basis_media_engine_t*)user; return e->running && e->state != BASIS_MEDIA_STATE_ERROR; }

static int take_seek_common(basis_media_engine_t* e, volatile long* taken, int64_t* out_target_us) {
    if (*taken == e->seek_seq) return 0;
    mutex_lock(&e->lock);
    long seq = e->seek_seq;
    int64_t us = e->seek_target_us;
    /* Re-anchor delivery pacing: only post-seek samples flow on this leg from
     * here, and against the old anchor they'd read as far-future (a forward
     * seek stalls the demux thread for the jump distance) or as late (a
     * backward seek floods through unpaced and fast-forwards back). The next
     * paced sample re-establishes base/wall from its own timestamp. */
    if (*taken != seq) e->pace_started = 0;
    mutex_unlock(&e->lock);
    if (*taken == seq) return 0;
    *taken = seq;
    *out_target_us = us;
    return 1;
}
static int sink_take_seek(void* user, int64_t* out_target_us) {
    basis_media_engine_t* e = (basis_media_engine_t*)user;
    return take_seek_common(e, &e->seek_taken_main, out_target_us);
}
static int audio_sink_take_seek(void* user, int64_t* out_target_us) {
    basis_media_engine_t* e = (basis_media_engine_t*)user;
    return take_seek_common(e, &e->seek_taken_audio, out_target_us);
}

static void install_sink(basis_media_engine_t* e) {
    e->sink.user = e;
    e->sink.on_video_format = sink_video_format;
    e->sink.on_video_au = sink_video_au;
    e->sink.on_audio_format = sink_audio_format;
    e->sink.on_audio_frame = sink_audio_frame;
    e->sink.on_state = sink_state;
    e->sink.on_error = sink_error;
    e->sink.on_end_of_stream = sink_eos;
    e->sink.on_duration = sink_duration;
    e->sink.take_seek = sink_take_seek;
    e->sink.is_running = sink_is_running;
}

/* The split-stream audio leg feeds only audio into the shared decoder. The video
 * leg owns the state machine and end-of-stream, so this sink drops video, state
 * and EOS callbacks; a hard error still surfaces (a dead audio leg breaks
 * playback), and is_running shares the engine flag so the audio thread stops with
 * the engine. */
static void audio_sink_video_format(void* user, basis_codec_t codec, const uint8_t* ed, int ed_len, int w, int h) {
    (void)user; (void)codec; (void)ed; (void)ed_len; (void)w; (void)h;
}
static void audio_sink_video_au(void* user, const uint8_t* au, int len, int64_t pts, int64_t dts, int key) {
    (void)user; (void)au; (void)len; (void)pts; (void)dts; (void)key;
}
static void audio_sink_state(void* user, basis_media_state_t s) { (void)user; (void)s; }
static void audio_sink_eos(void* user) { (void)user; }

static void install_audio_sink(basis_media_engine_t* e) {
    e->audio_sink.user = e;
    e->audio_sink.on_video_format = audio_sink_video_format;
    e->audio_sink.on_video_au = audio_sink_video_au;
    e->audio_sink.on_audio_format = sink_audio_format; /* routes to the shared decoder's audio path */
    e->audio_sink.on_audio_frame = sink_audio_frame;
    e->audio_sink.on_state = audio_sink_state;
    e->audio_sink.on_error = sink_error;               /* a failed audio leg is an engine error */
    e->audio_sink.on_end_of_stream = audio_sink_eos;
    e->audio_sink.on_duration = sink_duration;         /* either leg may know the timeline */
    e->audio_sink.take_seek = audio_sink_take_seek;    /* both legs reposition on a seek */
    e->audio_sink.is_running = sink_is_running;
}

/* ---- demux thread ------------------------------------------------------- */

static int ends_with_ci(const char* s, const char* suffix) {
    size_t ls = strlen(s), lf = strlen(suffix);
    if (lf > ls) return 0;
    const char* p = s + (ls - lf);
    for (size_t i = 0; i < lf; ++i) {
        char a = p[i], b = suffix[i];
        if (a >= 'A' && a <= 'Z') a += 32;
        if (b >= 'A' && b <= 'Z') b += 32;
        if (a != b) return 0;
    }
    return 1;
}

/* One demux pipeline: which URL/parts to pull and which sink to push into. The
 * engine still owns the decoder, state machine, running flag and error; threading
 * the rest through here lets one engine drive two independent pipelines — a
 * video-only primary plus an audio-only secondary — without either role being
 * hardcoded. State and error go through the sink (not the engine directly) so a
 * subordinate leg can suppress them; a third track would reuse the same shape. */
typedef struct {
    basis_media_engine_t* e;
    const char* url;
    basis_url_t* parts;
    basis_media_sink_t* sink;
    int allow_os_demux; /* Android OS-extractor fast path; primary leg only */
} demux_ctx_t;

/* Prefix-replay byte source: serves a small sniffed prefix first, then delegates to
 * the real read — lets run_http_like peek the leading bytes to detect the container
 * without consuming them from the demuxer. */
typedef struct {
    const uint8_t* prefix;
    int prefix_len;
    int prefix_pos;
    basis_read_fn inner_read;
    void* inner_ctx;
} prefix_src_t;

static int prefix_read(void* ctx, uint8_t* buf, int len) {
    prefix_src_t* p = (prefix_src_t*)ctx;
    if (p->prefix_pos < p->prefix_len) {
        int n = p->prefix_len - p->prefix_pos;
        if (n > len) n = len;
        memcpy(buf, p->prefix + p->prefix_pos, (size_t)n);
        p->prefix_pos += n;
        return n;
    }
    return p->inner_read(p->inner_ctx, buf, len);
}

/* True if the leading bytes are an ISO-BMFF/fragmented-MP4 box (type in bytes 4..8).
 * Lets us pick the demuxer by content, since CDN URLs like googlevideo's
 * .../videoplayback carry fMP4 with no .mp4 extension to switch on. */
static int looks_like_mp4(const uint8_t* b, int n) {
    if (n < 8) return 0;
    const char* t = (const char*)(b + 4);
    return memcmp(t, "ftyp", 4) == 0 || memcmp(t, "styp", 4) == 0 ||
           memcmp(t, "moof", 4) == 0 || memcmp(t, "sidx", 4) == 0 ||
           memcmp(t, "moov", 4) == 0 || memcmp(t, "free", 4) == 0 ||
           memcmp(t, "skip", 4) == 0 || memcmp(t, "mdat", 4) == 0;
}

/* ---- read-ahead buffer (paced / VOD sources) ----------------------------
 * Decouples the network read from the paced decode. A reader thread drains the
 * socket into this compressed byte ring as fast as the CDN delivers — banking
 * seconds ahead — while the demuxer consumes from the ring at the paced 1x rate.
 * Bursty CDN delivery (e.g. googlevideo's ~big-chunk-then-gap pattern) is absorbed
 * by the ring instead of starving the decoder. Compressed bytes are cheap (a few MB
 * holds many seconds), unlike decoded frames (the VRAM-bound decode ring). Used only
 * in paced mode; live sources read directly (no added latency). */
#define BASIS_READAHEAD_CAP (16 * 1024 * 1024)

typedef struct {
    uint8_t* buf;
    int cap, head, tail, count;   /* count/head/tail guarded by lock */
    int eof;                      /* producer done (reader hit EOF/error) */
    int closing;                  /* consumer done (tells the reader to stop) */
    volatile int reseek_park;     /* consumer repositioning: reader must park */
    volatile int reader_parked;   /* reader acknowledged the park */
    volatile int* running;        /* engine running flag, for prompt stop */
    basis_mutex_t lock;
} byte_ring_t;

static int ring_init(byte_ring_t* r, int cap) {
    memset(r, 0, sizeof(*r));
    r->buf = (uint8_t*)malloc((size_t)cap);
    if (!r->buf) return 0;
    r->cap = cap;
    mutex_init(&r->lock);
    return 1;
}
static void ring_free(byte_ring_t* r) {
    if (r->buf) { free(r->buf); r->buf = NULL; }
    mutex_destroy(&r->lock);
}

/* Producer: copy n bytes in, blocking while the ring is full. Bails if the engine
 * stops or the consumer is closing. */
static void ring_write(byte_ring_t* r, const uint8_t* data, int n, volatile int* running) {
    int off = 0;
    while (off < n) {
        mutex_lock(&r->lock);
        int space = r->cap - r->count;
        int chunk = n - off; if (chunk > space) chunk = space;
        if (chunk > 0) {
            int first = r->cap - r->head; if (first > chunk) first = chunk;
            memcpy(r->buf + r->head, data + off, (size_t)first);
            if (chunk > first) memcpy(r->buf, data + off + first, (size_t)(chunk - first));
            r->head = (r->head + chunk) % r->cap;
            r->count += chunk;
            off += chunk;
        }
        int closing = r->closing;
        mutex_unlock(&r->lock);
        if (off < n) {
            if (!*running || closing || r->reseek_park) return; /* parked writes drop pre-seek bytes */
            sleep_ms(2);   /* full: wait for the demuxer to drain */
        }
    }
}

/* Consumer (basis_read_fn): copy out up to len bytes, blocking while empty until the
 * producer signals EOF or the engine stops. Returns 0 only when fully drained. */
static int ring_read_fn(void* ctx, uint8_t* buf, int len) {
    byte_ring_t* r = (byte_ring_t*)ctx;
    for (;;) {
        mutex_lock(&r->lock);
        if (r->count > 0) {
            int chunk = r->count < len ? r->count : len;
            int first = r->cap - r->tail; if (first > chunk) first = chunk;
            memcpy(buf, r->buf + r->tail, (size_t)first);
            if (chunk > first) memcpy(buf + first, r->buf, (size_t)(chunk - first));
            r->tail = (r->tail + chunk) % r->cap;
            r->count -= chunk;
            mutex_unlock(&r->lock);
            return chunk;
        }
        int eof = r->eof;
        mutex_unlock(&r->lock);
        if (eof) return 0;
        if (r->running && !*r->running) return 0;   /* engine stopping */
        sleep_ms(2);   /* empty: wait for the reader */
    }
}

typedef struct {
    byte_ring_t* ring;
    basis_read_fn net_read;
    void* net_ctx;
    volatile int* running;
} reader_args_t;

static void reader_body(reader_args_t* a) {
    uint8_t tmp[65536];
    while (*a->running && !a->ring->closing) {
        if (a->ring->reseek_park) {
            /* The demuxer is repositioning the source underneath us: acknowledge
             * and idle until it finishes (http_reseek aborts a parked read, so a
             * blocked net_read also lands here via n <= 0). */
            a->ring->reader_parked = 1;
            sleep_ms(2);
            continue;
        }
        if (a->ring->eof) { sleep_ms(5); continue; } /* drained; stay alive for a reseek */
        int n = a->net_read(a->net_ctx, tmp, (int)sizeof(tmp));
        if (n <= 0) {
            if (a->ring->reseek_park) continue;  /* aborted for a reseek, not EOF */
            mutex_lock(&a->ring->lock);
            a->ring->eof = 1;
            mutex_unlock(&a->ring->lock);
            continue;
        }
        ring_write(a->ring, tmp, n, a->running);
    }
    mutex_lock(&a->ring->lock);
    a->ring->eof = 1;
    mutex_unlock(&a->ring->lock);
}

#if defined(_WIN32)
static DWORD WINAPI reader_entry(LPVOID p) { reader_body((reader_args_t*)p); return 0; }
#else
static void* reader_entry(void* p) { reader_body((reader_args_t*)p); return NULL; }
#endif

#if defined(_WIN32)
/* Byte-source reseek for the HTTP VOD path (handed to the MP4 demuxer). Parks
 * the read-ahead reader, swaps the response for a ranged one, flushes buffered
 * bytes and the replayed sniff prefix, and resumes. Runs on the demux thread. */
typedef struct {
    void* http;
    byte_ring_t* ring;      /* NULL when the demuxer reads the source directly */
    prefix_src_t* ps;
    volatile int* running;
} http_seek_src_t;

static int http_reseek(void* ctx, int64_t abs_offset) {
    http_seek_src_t* s = (http_seek_src_t*)ctx;
    if (s->ring) {
        s->ring->reseek_park = 1;
        basis_win_http_abort(s->http);   /* unblock a read the reader is parked in */
        while (!s->ring->reader_parked && *s->running) sleep_ms(1);
    } else {
        basis_win_http_abort(s->http);   /* demux thread is the only reader */
    }
    int rc = basis_win_http_reseek(s->http, abs_offset);
    s->ps->prefix_pos = s->ps->prefix_len;   /* sniffed offset-0 bytes must not replay */
    if (s->ring) {
        mutex_lock(&s->ring->lock);
        s->ring->head = s->ring->tail = s->ring->count = 0;
        s->ring->eof = (rc != 0);            /* failed reseek reads as end-of-stream */
        mutex_unlock(&s->ring->lock);
        s->ring->reader_parked = 0;
        s->ring->reseek_park = 0;
    }
    return rc;
}
#endif

/* HLS / LL-HLS: the URL is a playlist, not a continuous byte stream. The HLS
 * source fetches+parses the M3U8, stitches segments (and LL-HLS parts) into one
 * byte stream, and the existing TS/fMP4 demuxers consume it. Playlist and
 * segment fetches ride the platform HTTP byte source: WinHTTP on Windows, the
 * JNI HttpsURLConnection bridge on Android. */
#if defined(__ANDROID__)
/* Binds the provider's open(url) to basis_jni_https_open's (url, timeout).
 * 60s read timeout: LL-HLS blocking playlist reloads hold the response open
 * for up to a few target durations, well past a connect-scale timeout. */
static void* hls_jni_https_open(const char* url) { return basis_jni_https_open(url, 60000); }
#endif
static void run_hls(demux_ctx_t* c) {
#if defined(_WIN32) || defined(__ANDROID__)
#if defined(_WIN32)
    basis_http_provider_t provider = {
        basis_win_http_open, basis_win_http_read, basis_win_http_close
    };
#else
    basis_http_provider_t provider = {
        hls_jni_https_open, basis_jni_https_read, basis_jni_https_close
    };
#endif
    int is_fmp4 = 0;
    void* hls = basis_hls_open(c->url, &provider, c->sink->is_running, c->sink->user, &is_fmp4);
    if (!hls) {
        c->sink->on_error(c->sink->user, "failed to open HLS playlist");
        return;
    }
    /* Auto delivery (hint 0): a playlist carrying EXT-X-ENDLIST is a finished VOD
     * playlist (all segments available at once) and must be paced; a live playlist
     * has no endlist. A forced hint skips this. */
    if (c->e->paced_hint == 0 && basis_hls_is_vod(hls))
        c->e->paced = 1;
    /* Report the timeline only when seeks will actually work (a non-zero
     * duration is the managed layer's seekability signal): TS-segment VOD.
     * fMP4 VOD plays fine but stays timeline-less for now. */
    if (basis_hls_can_seek(hls)) {
        long total_ms = basis_hls_duration_ms(hls);
        if (total_ms > 0 && c->sink->on_duration)
            c->sink->on_duration(c->sink->user, (int64_t)total_ms * 1000);
    }
    /* HLS buffers segments and delivers faster than real time, so always pace delivery —
     * even for live (paced=0), which still presents at and converges to the live edge.
     * This replaces basis_hls.c's byte-rate token bucket (disabled there) with PTS-exact
     * AU pacing that tracks VBR and recovers from stalls. */
    c->e->pace_delivery = 1;
    c->sink->on_state(c->sink->user, BASIS_MEDIA_STATE_BUFFERING);
    /* Seeks reposition inside the HLS source (segment granularity) via
     * basis_media_seek_us -> basis_hls_seek_ms, so the demuxer gets no
     * byte-level reseek here. */
    mutex_lock(&c->e->lock);
    c->e->active_hls = hls;
    mutex_unlock(&c->e->lock);
    if (is_fmp4)
        basis_mp4_run(c->sink, basis_hls_read, hls, NULL, NULL);
    else
        basis_ts_run(c->sink, basis_hls_read, hls);
    mutex_lock(&c->e->lock);
    c->e->active_hls = NULL;
    mutex_unlock(&c->e->lock);
    basis_hls_close(hls);
#else
    c->sink->on_error(c->sink->user, "HLS playback requires the Windows or Android backend.");
#endif
}

static void run_http_like(demux_ctx_t* c) {
    /* HLS playlists are not a single continuous stream — hand off to the HLS
     * source before the OS-extractor attempt (which can't stitch segments) and
     * the plain TS/fMP4 byte-source path. (.m3u8 may carry a query.) */
    if (strstr(c->parts->path, ".m3u8")) {
        run_hls(c);
        return;
    }

    /* Android: the OS extractor can demux the URL itself (TLS included). Primary
     * leg only — an audio-only leg must feed the shared decoder's audio path, not
     * hand a whole muxed file to the OS extractor. m2ts is also kept away from
     * it: that container exists here to carry HDMV LPCM (stream_type 0x80),
     * which the extractor doesn't surface — it would play the video with the
     * audio silently missing, where the portable TS demuxer + LPCM bypass play
     * both. */
    int os_demux = c->allow_os_demux &&
                   !ends_with_ci(c->parts->path, ".m2ts") && !ends_with_ci(c->parts->path, ".mts");
    if (os_demux && basis_decoder_try_open_url(c->e->decoder, c->url)) {
        c->sink->on_state(c->sink->user, BASIS_MEDIA_STATE_BUFFERING);
        while (c->e->running) sleep_ms(20);
        return;
    }

    void* src = NULL;
    basis_read_fn rd = NULL;

#if defined(_WIN32)
    src = basis_win_http_open(c->url);   /* WinHTTP: handles http + https/TLS */
    rd = basis_win_http_read;
#elif defined(__ANDROID__)
    /* AMediaExtractor either took the URL (already returned above) or rejected
     * it (unsupported live container, etc). Fall back to a JNI-backed Java
     * HttpsURLConnection feeding the portable TS/MP4 demuxers — same path used
     * for RTSP/RTMP. Works for both http:// and https://.
     *
     * Read timeout is 60s, not 15s: live streams can have brief stalls (key-
     * frame intervals, network jitter, server buffering) that a short timeout
     * would mistake for a dead socket. Connect timeout stays implicitly short
     * (the open call). */
    src = basis_jni_https_open(c->url, 60000);
    rd = basis_jni_https_read;
#else
    if (c->parts->tls) {
        c->sink->on_error(c->sink->user, "https requires the platform TLS stack (WinHTTP/AMediaExtractor); not available on this build.");
        return;
    }
    src = basis_http_open(c->parts, 15000);
    rd = basis_http_read;
#endif

    if (!src) {
        c->sink->on_error(c->sink->user, "failed to open HTTP byte source");
        return;
    }

#if defined(_WIN32)
    /* Auto delivery (hint 0): a finite, byte-range-seekable HTTP body (known
     * Content-Length + Accept-Ranges) is on-demand and arrives faster than real time,
     * so pace it; an open-ended response is live. Set before the read-ahead gate and
     * the first AU, so pacing is in force from the start. A forced hint skips this. */
    if (c->e->paced_hint == 0 && basis_win_http_is_seekable(src))
        c->e->paced = 1;
    c->e->pace_delivery = c->e->paced; /* VOD over HTTP paces delivery; open-ended live doesn't */
#endif

    c->sink->on_state(c->sink->user, BASIS_MEDIA_STATE_BUFFERING);

    /* Pick the demuxer by sniffing the leading bytes, not the URL extension: CDN
     * URLs (e.g. googlevideo .../videoplayback) deliver fMP4 with no .mp4 in the
     * path, which would otherwise fall through to the MPEG-TS demuxer and stall.
     * The peeked bytes are replayed to the demuxer via prefix_read. Extension is the
     * fallback when the content sniff is inconclusive. */
    uint8_t head[16];
    int head_len = 0;
    while (head_len < (int)sizeof(head)) {
        int n = rd(src, head + head_len, (int)sizeof(head) - head_len);
        if (n <= 0) break;
        head_len += n;
    }
    prefix_src_t ps = { head, head_len, 0, rd, src };

    int is_mp4 = looks_like_mp4(head, head_len);
    int is_ts  = (head_len >= 1 && head[0] == 0x47);
    if (!is_mp4 && !is_ts)
        is_mp4 = ends_with_ci(c->parts->path, ".mp4") || ends_with_ci(c->parts->path, ".m4s");

    /* Paced (VOD): drain the network into a read-ahead ring on a reader thread and
     * demux from the ring at the paced rate, so bursty CDN delivery doesn't starve
     * playback. Live: demux straight off the network read (no added latency). */
    byte_ring_t ring;
    int use_readahead = c->e->paced && ring_init(&ring, BASIS_READAHEAD_CAP);
    basis_read_fn demux_read = prefix_read;
    void* demux_ctx = &ps;
    basis_thread_t reader;
    int reader_started = 0;
    reader_args_t ra;
    if (use_readahead) {
        ring.running = &c->e->running;
        ra.ring = &ring; ra.net_read = prefix_read; ra.net_ctx = &ps; ra.running = &c->e->running;
#if defined(_WIN32)
        reader = CreateThread(NULL, 0, reader_entry, &ra, 0, NULL);
        reader_started = (reader != NULL);
#else
        reader_started = (pthread_create(&reader, NULL, reader_entry, &ra) == 0);
#endif
        if (reader_started) { demux_read = ring_read_fn; demux_ctx = &ring; }
        else { ring_free(&ring); use_readahead = 0; }
    }

    /* A seekable VOD body gets a reseek hook so the MP4 demuxer can honour
     * absolute seeks with a ranged refetch; everything else demuxes as before. */
    basis_reseek_fn reseek = NULL;
    void* reseek_ctx = NULL;
#if defined(_WIN32)
    http_seek_src_t seek_src = { src, use_readahead ? &ring : NULL, &ps, &c->e->running };
    if (c->e->paced && basis_win_http_can_reseek(src)) {
        reseek = http_reseek;
        reseek_ctx = &seek_src;
    }
#endif

    if (is_mp4)
        basis_mp4_run(c->sink, demux_read, demux_ctx, reseek, reseek_ctx);
    else
        basis_ts_run(c->sink, demux_read, demux_ctx); /* default to MPEG-TS */

    if (use_readahead) {
        mutex_lock(&ring.lock); ring.closing = 1; mutex_unlock(&ring.lock); /* tell the reader to stop */
#if defined(_WIN32)
        /* The reader may be parked in WinHttpReadData; abort the request so the read returns
         * at once and the join can't stall on a stalled socket (src is the WinHTTP handle). */
        basis_win_http_abort(src);
        WaitForSingleObject(reader, INFINITE); CloseHandle(reader);
#else
        pthread_join(reader, NULL);
#endif
        ring_free(&ring);
    }

#if defined(_WIN32)
    basis_win_http_close(src);
#elif defined(__ANDROID__)
    basis_jni_https_close(src);
#else
    basis_http_close(src);
#endif
}

/* RIST: librist recovers an MPEG-TS byte stream over UDP (ARQ + optional PSK-AES);
 * once recovered it's the same MPEG-TS the player already demuxes, so we feed
 * basis_rist_read straight into basis_ts_run. The receiver is built only when the
 * plugin is compiled with BASIS_WITH_RIST; otherwise basis_rist_open reports a
 * clear error via the sink and returns NULL. */
static void run_rist(demux_ctx_t* c) {
    void* rist = basis_rist_open(c->parts, c->sink);
    if (!rist) return;  /* basis_rist_open set the error on the sink */
    c->sink->on_state(c->sink->user, BASIS_MEDIA_STATE_BUFFERING);
    basis_ts_run(c->sink, basis_rist_read, rist);
    basis_rist_close(rist);
}

/* Dispatch the URL to its protocol handler. Blocks until the stream drops, the
 * engine is stopped, or a hard error is set. */
static void run_protocol_once(demux_ctx_t* c) {
    if (basis_url_is_rtsp(c->parts)) {
        basis_rtsp_run(c->sink, c->parts);
    } else if (basis_url_is_rtmp(c->parts)) {
        if (c->parts->tls) {
            c->sink->on_error(c->sink->user, "rtmps (RTMP-over-TLS) is not supported; use rtmp:// or an https fMP4/TS URL.");
        } else {
            basis_rtmp_run(c->sink, c->parts);
        }
    } else if (basis_url_is_rist(c->parts)) {
        run_rist(c);
    } else { /* http / https */
        run_http_like(c);
    }
}

/* Sleep that wakes early when the engine is stopped, so teardown never blocks on
 * a reconnect backoff. */
static void sleep_interruptible(basis_media_engine_t* e, int ms) {
    while (ms > 0 && e->running) {
        int chunk = ms < 50 ? ms : 50;
        sleep_ms(chunk);
        ms -= chunk;
    }
}

static int engine_state_is_error(basis_media_engine_t* e) {
    int err;
    mutex_lock(&e->lock);
    err = (e->state == BASIS_MEDIA_STATE_ERROR);
    mutex_unlock(&e->lock);
    return err;
}

/* Demux thread: run the protocol, and on an unexpected drop reconnect with
 * exponential backoff (keeping the decoder + GPU resources alive — far cheaper
 * than a full teardown/reopen). Backoff resets whenever a run actually received
 * media, so brief blips recover instantly while a dead endpoint backs off. A hard
 * error (auth/unsupported) or repeated no-progress failures stop the loop; on
 * give-up we surface ENDED so the upper layer can do a full reopen if it loops. */
static void demux_body(basis_media_engine_t* e) {
    int backoff_ms = 500;
    int attempt = 0;
    const int MAX_ATTEMPTS = 6; /* ~500ms..8s capped: several retries before giving up */

    demux_ctx_t c = { e, e->url, &e->parts, &e->sink, 1 };

    while (e->running) {
        basis_engine_set_state(e, BASIS_MEDIA_STATE_CONNECTING);
        long au_before = e->video_au_count + e->audio_frame_count;

        run_protocol_once(&c);

        if (!e->running) break;              /* user stop */
        if (engine_state_is_error(e)) break; /* hard failure: retrying won't help */
        /* Paced (VOD) sources are finite and play once: a clean run end is EOF, not
         * a live drop to reconnect through. Looping would replay from PTS 0 while the
         * paced clock is at the old edge — every frame would read "behind" the clock
         * and flood in ungated (fast-forward). Stop instead. */
        if (e->paced) { basis_engine_set_state(e, BASIS_MEDIA_STATE_ENDED); break; }

        long au_after = e->video_au_count + e->audio_frame_count;
        long delta = au_after - au_before;
        BASIS_LOGI("demux run ended: delta_aus=%ld total_aus=%ld attempt=%d/%d",
                   delta, au_after, attempt + 1, MAX_ATTEMPTS);
        if (delta > 10) { attempt = 0; backoff_ms = 500; }
        else attempt++;

        if (attempt >= MAX_ATTEMPTS) {
            BASIS_LOGI("demux giving up after %d empty attempts; setting ENDED", MAX_ATTEMPTS);
            basis_engine_set_state(e, BASIS_MEDIA_STATE_ENDED);
            break;
        }

        basis_engine_set_state(e, BASIS_MEDIA_STATE_BUFFERING); /* reconnecting */
        sleep_interruptible(e, backoff_ms);
        backoff_ms *= 2;
        if (backoff_ms > 8000) backoff_ms = 8000;
    }
}

/* Audio leg of a split-stream source: pull the audio-only URL into the shared
 * decoder's audio path. The primary (video) leg owns the state machine and
 * end-of-stream; this reconnects on a drop with backoff and stops when the engine
 * stops or it can't make progress. A hard error surfaces through the audio sink's
 * on_error; and because the audio leg is required for a split source, if it gives up
 * having never produced a single frame we set an engine error rather than let the
 * video leg play on silently. */
static void audio_demux_body(basis_media_engine_t* e) {
    int backoff_ms = 500;
    int attempt = 0;
    const int MAX_ATTEMPTS = 6;

    demux_ctx_t c = { e, e->url_audio, &e->parts_audio, &e->audio_sink, 0 };

    while (e->running) {
        long aus_before = e->audio_frame_count;

        run_protocol_once(&c);

        if (!e->running) break;
        if (engine_state_is_error(e)) break;
        if (e->paced) break;   /* VOD: play once; the video leg drives ENDED */

        long delta = e->audio_frame_count - aus_before;
        if (delta > 10) { attempt = 0; backoff_ms = 500; }
        else attempt++;
        if (attempt >= MAX_ATTEMPTS) break; /* give up; the post-loop check errors if no audio ever arrived */

        sleep_interruptible(e, backoff_ms);
        backoff_ms *= 2;
        if (backoff_ms > 8000) backoff_ms = 8000;
    }

    /* The audio leg is required for a split source. If we stopped trying without it ever
     * producing a frame (a paced one-shot with no audio, or retries exhausted), surface a hard
     * error instead of silent video. Skip on a normal stop (e->running cleared) or an error
     * already raised via the audio sink's on_error. */
    if (e->running && !engine_state_is_error(e) && e->audio_frame_count == 0)
        basis_engine_set_error(e, "split-stream audio produced no frames");
}

#if defined(_WIN32)
static DWORD WINAPI thread_entry(LPVOID arg) { demux_body((basis_media_engine_t*)arg); return 0; }
static DWORD WINAPI audio_thread_entry(LPVOID arg) { audio_demux_body((basis_media_engine_t*)arg); return 0; }
#else
static void* thread_entry(void* arg) { demux_body((basis_media_engine_t*)arg); return NULL; }
static void* audio_thread_entry(void* arg) { audio_demux_body((basis_media_engine_t*)arg); return NULL; }
#endif

static int thread_start(basis_media_engine_t* e) {
#if defined(_WIN32)
    e->thread = CreateThread(NULL, 0, thread_entry, e, 0, NULL);
    return e->thread != NULL;
#else
    return pthread_create(&e->thread, NULL, thread_entry, e) == 0;
#endif
}
static void thread_join(basis_media_engine_t* e) {
    if (!e->thread_started) return;
#if defined(_WIN32)
    WaitForSingleObject(e->thread, INFINITE);
    CloseHandle(e->thread);
#else
    pthread_join(e->thread, NULL);
#endif
    e->thread_started = 0;
}

static int audio_thread_start(basis_media_engine_t* e) {
#if defined(_WIN32)
    e->audio_thread = CreateThread(NULL, 0, audio_thread_entry, e, 0, NULL);
    return e->audio_thread != NULL;
#else
    return pthread_create(&e->audio_thread, NULL, audio_thread_entry, e) == 0;
#endif
}
static void audio_thread_join(basis_media_engine_t* e) {
    if (!e->audio_thread_started) return;
#if defined(_WIN32)
    WaitForSingleObject(e->audio_thread, INFINITE);
    CloseHandle(e->audio_thread);
#else
    pthread_join(e->audio_thread, NULL);
#endif
    e->audio_thread_started = 0;
}

/* ---- public ABI --------------------------------------------------------- */

/* Shared open path. audio_url NULL/empty => single muxed stream (the only path
 * basis_media_open takes); non-empty => split-stream, with url as the video-only
 * primary and audio_url as the audio-only secondary feeding the same decoder. */
static basis_media_engine_t* open_impl(const char* url, const char* audio_url, int delivery_hint) {
    if (!url) return NULL;

    basis_media_engine_t* e = (basis_media_engine_t*)calloc(1, sizeof(*e));
    if (!e) return NULL;

    /* delivery_hint: 0=auto, 1=force live, 2=force on-demand. Auto starts live and
     * the protocol handler may flip it to paced once it has inspected the source. */
    e->paced_hint = delivery_hint;
    e->paced = (delivery_hint == 2) ? 1 : 0;
    e->pace_delivery = e->paced; /* VOD paces delivery; run_hls also enables it for live HLS */
    strncpy(e->url, url, sizeof(e->url) - 1);
    if (basis_url_parse(url, &e->parts) != 0) { free(e); return NULL; }

    int has_audio = (audio_url && audio_url[0]);
    if (has_audio) {
        strncpy(e->url_audio, audio_url, sizeof(e->url_audio) - 1);
        if (basis_url_parse(audio_url, &e->parts_audio) != 0) { free(e); return NULL; }
    }

    mutex_init(&e->lock);
    mutex_init(&e->submit_lock);
    e->state = BASIS_MEDIA_STATE_IDLE;

    /* Optional: a NULL context just means captions are unavailable (scan/poll no-op). */
    e->captions = basis_caption_create();

    basis_io_global_init();

    e->decoder = basis_decoder_create(e);
    if (!e->decoder) {
        basis_io_global_shutdown();
        basis_caption_destroy(e->captions);
        mutex_destroy(&e->submit_lock);
        mutex_destroy(&e->lock);
        free(e);
        return NULL;
    }

    install_sink(e);
    if (has_audio) install_audio_sink(e);

    e->running = 1;
    if (!thread_start(e)) {
        e->running = 0;
        basis_decoder_destroy(e->decoder);
        basis_io_global_shutdown();
        basis_caption_destroy(e->captions);
        mutex_destroy(&e->submit_lock);
        mutex_destroy(&e->lock);
        free(e);
        return NULL;
    }
    e->thread_started = 1;

    if (has_audio && !audio_thread_start(e)) {
        /* The caller asked for split-stream; failing to spawn the audio leg means
         * we can't honour that, so tear down rather than play silent video. */
        e->running = 0;
        thread_join(e);
        basis_decoder_destroy(e->decoder);
        basis_io_global_shutdown();
        basis_caption_destroy(e->captions);
        mutex_destroy(&e->submit_lock);
        mutex_destroy(&e->lock);
        free(e);
        return NULL;
    }
    if (has_audio) e->audio_thread_started = 1;

    return e;
}

BASIS_API basis_media_engine_t* BASIS_CALL basis_media_open(const char* url) {
    return open_impl(url, NULL, 0);
}

/* Split-stream / paced open. audio_url (when set) is the audio-only secondary played
 * in sync on one decoder/clock; delivery_hint (0=auto, 1=live, 2=on-demand) selects
 * the clock, auto-detected from the source when 0. A NULL/empty audio_url with
 * delivery_hint == 0 is exactly basis_media_open(video_url). */
BASIS_API basis_media_engine_t* BASIS_CALL basis_media_open_dual(const char* video_url, const char* audio_url, int delivery_hint) {
    return open_impl(video_url, audio_url, delivery_hint);
}

BASIS_API void BASIS_CALL basis_media_close(basis_media_engine_t* e) {
    if (!e) return;

    /* Stop the demux threads first so nothing submits while we tear down. Both
     * legs observe the same running flag; join both before freeing the decoder. */
    e->running = 0;
    thread_join(e);
    audio_thread_join(e);

    /* Free OS decode/audio + GPU resources. basis_decoder_destroy calls
     * render_release internally; with the threads joined nothing is mid-decode,
     * and D3D11/D3D12 COM Release is thread-safe. */
    if (e->decoder) {
        basis_decoder_destroy(e->decoder);
        e->decoder = NULL;
    }

    basis_caption_destroy(e->captions);
    basis_io_global_shutdown();
    mutex_destroy(&e->submit_lock);
    mutex_destroy(&e->lock);
    free(e);
}

BASIS_API void BASIS_CALL basis_media_play(basis_media_engine_t* e) {
    if (!e) return;
    e->paused = 0;
    basis_engine_set_state(e, BASIS_MEDIA_STATE_PLAYING);
}

BASIS_API void BASIS_CALL basis_media_pause(basis_media_engine_t* e) {
    if (!e) return;
    e->paused = 1;
    basis_engine_set_state(e, BASIS_MEDIA_STATE_PAUSED);
}

BASIS_API void BASIS_CALL basis_media_stop(basis_media_engine_t* e) {
    if (!e) return;
    e->paused = 1;
    basis_engine_set_state(e, BASIS_MEDIA_STATE_IDLE);
}

BASIS_API int BASIS_CALL basis_media_get_state(basis_media_engine_t* e) {
    if (!e) return BASIS_MEDIA_STATE_IDLE;
    mutex_lock(&e->lock);
    int s = (int)e->state;
    mutex_unlock(&e->lock);
    return s;
}

BASIS_API int BASIS_CALL basis_media_get_video_size(basis_media_engine_t* e, int* w, int* h) {
    if (!e || !e->decoder) return -1;
    return basis_decoder_get_video_size(e->decoder, w, h);
}

BASIS_API int BASIS_CALL basis_media_get_frame_origin(basis_media_engine_t* e) {
    if (!e || !e->decoder) return 0; /* upright until a backend says otherwise */
    return basis_decoder_get_frame_origin(e->decoder);
}

BASIS_API int64_t BASIS_CALL basis_media_get_position_us(basis_media_engine_t* e) {
    if (!e || !e->decoder) return -1;
    return basis_decoder_get_position_us(e->decoder);
}

BASIS_API int64_t BASIS_CALL basis_media_get_duration_us(basis_media_engine_t* e) {
    return e ? e->duration_us : 0; /* 0 = unknown / live */
}

BASIS_API int BASIS_CALL basis_media_seek_us(basis_media_engine_t* e, int64_t target_us) {
    if (!e || target_us < 0) return -1;
    int64_t dur = e->duration_us;
    if (dur <= 0) return -1;                 /* no seekable timeline (live / unindexed) */
    if (target_us > dur) target_us = dur;
    mutex_lock(&e->lock);
    e->seek_target_us = target_us;
    e->seek_seq++;
    void* hls = e->active_hls;
    int rc = hls ? basis_hls_request_seek(hls, target_us / 1000) : 0;
    /* HLS repositions inside the segment source — the TS demuxer never sees a
     * take_seek, so re-anchor pacing here. A stray pre-flush sample can win the
     * re-anchor, but it costs one more re-anchor when the flushed data lands,
     * not a stall. */
    if (hls && rc == 0) e->pace_started = 0;
    mutex_unlock(&e->lock);
    return rc;
}

BASIS_API int BASIS_CALL basis_media_poll_caption(basis_media_engine_t* e, char* buf, int buf_size,
                                                  int64_t* out_start_us, int64_t* out_end_us) {
    if (!e || !buf || buf_size <= 0) return -1;
    int64_t pos = e->decoder ? basis_decoder_get_position_us(e->decoder) : -1;
    return basis_caption_poll(e->captions, pos, buf, buf_size, out_start_us, out_end_us);
}

BASIS_API int BASIS_CALL basis_media_get_last_error(basis_media_engine_t* e, char* buf, int buf_size) {
    if (!e || !buf || buf_size <= 0) return 0;
    mutex_lock(&e->lock);
    int n = (int)strlen(e->error);
    if (n >= buf_size) n = buf_size - 1;
    memcpy(buf, e->error, (size_t)n);
    buf[n] = 0;
    mutex_unlock(&e->lock);
    return n;
}

BASIS_API int BASIS_CALL basis_media_get_debug(basis_media_engine_t* e, char* buf, int buf_size) {
    if (!e || !buf || buf_size <= 0) return 0;
    int n = snprintf(buf, (size_t)buf_size, "vau=%ld aau=%ld | ",
                     e->video_au_count, e->audio_frame_count);
    if (n < 0) n = 0;
    if (e->decoder && n < buf_size) n += basis_decoder_get_debug(e->decoder, buf + n, buf_size - n);
    return n;
}

BASIS_API void BASIS_CALL basis_media_set_buffer(basis_media_engine_t* e, int mode, int buffer_ms) {
    if (e && e->decoder) basis_decoder_set_buffer(e->decoder, mode, buffer_ms);
}

BASIS_API void BASIS_CALL basis_media_set_output_texture(basis_media_engine_t* e, void* native_texture, int w, int h) {
    if (e && e->decoder) basis_decoder_set_output_texture(e->decoder, native_texture, w, h);
}

BASIS_API void* BASIS_CALL basis_media_get_texture(basis_media_engine_t* e, int* w, int* h) {
    if (!e || !e->decoder) return NULL;
    return basis_decoder_get_texture(e->decoder, w, h);
}

BASIS_API uint64_t BASIS_CALL basis_media_get_frame_counter(basis_media_engine_t* e) {
    if (!e || !e->decoder) return 0;
    return basis_decoder_get_frame_counter(e->decoder);
}

BASIS_API int BASIS_CALL basis_media_get_audio_format(basis_media_engine_t* e, int* rate, int* ch) {
    if (!e || !e->decoder) return -1;
    return basis_decoder_get_audio_format(e->decoder, rate, ch);
}

BASIS_API int BASIS_CALL basis_media_read_audio(basis_media_engine_t* e, float* out, int max_floats) {
    if (!e || !e->decoder || !out || max_floats <= 0) return 0;
    if (e->paused) return 0; /* silence while paused */
    return basis_decoder_read_audio(e->decoder, out, max_floats);
}

/* The render-event function lives in the platform glue (basis_unity_plugin.cpp);
 * it dispatches BASIS_RENDER_UPDATE/RELEASE to basis_decoder_render_*. */
