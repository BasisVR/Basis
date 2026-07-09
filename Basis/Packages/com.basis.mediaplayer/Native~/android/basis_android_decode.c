/*
 * basis_android_decode.c — Android/Quest OS-codec backend (implements basis_decoder_*).
 *
 * Two entry paths:
 *   try_open_url  — https TS/MP4: AMediaExtractor (TLS+demux) -> AMediaCodec.
 *                   A worker thread pumps samples and renders to a Surface.
 *   submit_video  — rtsp/rtmp: the core demuxer feeds Annex-B AUs; we create the
 *                   codec from the SPS/PPS extradata and queue AUs directly.
 *
 * Video output goes to an AImageReader Surface in HardwareBuffer mode; each
 * decoded AHardwareBuffer is handed to the Vulkan present (basis_android_vk),
 * which imports it and resolves YCbCr -> an RGBA VkImage Unity samples.
 * Audio (AAC) is decoded to PCM and written to a ring the C# sink pulls.
 */

#include "../basis_media_internal.h"
#include "basis_android_vk.h"

#include <media/NdkMediaCodec.h>
#include <media/NdkMediaExtractor.h>
#include <media/NdkMediaFormat.h>
#include <media/NdkImageReader.h>
#include <media/NdkImage.h>
#include <android/native_window.h>
#include <android/hardware_buffer.h>
#include <android/log.h>

#include <pthread.h>
#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <unistd.h>

/* ---- PCM ring ----------------------------------------------------------- */

/* Interleaved float FIFO. Same alignment contract as the Windows ring: drops
 * are always whole-frame counts, so the surviving stream keeps its channel
 * phase; reads may return partial frames — the managed splitter carries
 * sub-frame remainders across pulls. Neither end anchors head/tail to an
 * absolute frame boundary. */
typedef struct {
    float* buf; int cap, head, tail;
    int frame; /* floats per interleaved frame (channel count) */
    pthread_mutex_t m;
} pcm_ring;

static void ring_init(pcm_ring* r, int floats) { r->buf = malloc(sizeof(float) * floats); r->cap = floats; r->head = r->tail = 0; r->frame = 2; pthread_mutex_init(&r->m, NULL); }
static void ring_free(pcm_ring* r) { free(r->buf); r->buf = NULL; pthread_mutex_destroy(&r->m); }
static void ring_set_frame(pcm_ring* r, int frame) {
    pthread_mutex_lock(&r->m);
    r->frame = frame > 0 ? frame : 1;
    r->head = r->tail = 0; /* buffered floats are in the old framing */
    pthread_mutex_unlock(&r->m);
}
static void ring_write(pcm_ring* r, const float* s, int n) {
    pthread_mutex_lock(&r->m);
    for (int i = 0; i < n; ++i) { int nt = (r->tail + 1) % r->cap; if (nt == r->head) r->head = (r->head + r->frame) % r->cap; r->buf[r->tail] = s[i]; r->tail = nt; }
    pthread_mutex_unlock(&r->m);
}
static int ring_read(pcm_ring* r, float* out, int n) {
    pthread_mutex_lock(&r->m);
    int got = 0; while (got < n && r->head != r->tail) { out[got++] = r->buf[r->head]; r->head = (r->head + 1) % r->cap; }
    pthread_mutex_unlock(&r->m);
    return got;
}

/* ---- decoder ------------------------------------------------------------ */

struct basis_decoder {
    basis_media_engine_t* engine;

    AImageReader* reader;
    ANativeWindow* window;

    AMediaCodec* vcodec;
    AMediaCodec* acodec;
    AMediaExtractor* extractor;
    int video_track, audio_track;

    basis_codec_t vc;
    int vw, vh;
    int vconfigured, aconfigured;

    int asr, ach;
    int apcm_float; /* decoder emits float PCM (pcm-encoding 4) instead of 16-bit */

    basis_codec_t ac;       /* audio lane: AAC (MediaCodec) or LPCM (direct convert) */
    int aLpcmAssign;        /* Blu-ray channel_assignment (from the format's config blob) */
    int aLpcmBits;          /* 16 or 24 */
    float* lpcmBuf;         /* conversion scratch, grown to the largest frame batch */
    int lpcmBufCap;

    basis_vk_present* vk;

    pthread_t worker;
    int worker_started;

    int64_t lastPtsUs;
    pcm_ring pcm;
};

/* ---- AImageReader callback: capture the decoded AHardwareBuffer ---------- */

static void on_image(void* ctx, AImageReader* reader) {
    basis_decoder_t* d = (basis_decoder_t*)ctx;
    AImage* img = NULL;
    if (AImageReader_acquireLatestImage(reader, &img) != AMEDIA_OK || !img) return;

    AHardwareBuffer* ahb = NULL;
    if (AImage_getHardwareBuffer(img, &ahb) == AMEDIA_OK && ahb) {
        int w = d->vw, h = d->vh;
        int32_t aw = 0, ah = 0;
        AImage_getWidth(img, &aw); AImage_getHeight(img, &ah);
        if (aw > 0) w = aw; if (ah > 0) h = ah;
        if (d->vk) basis_vk_set_hardware_buffer(d->vk, ahb, w, h); /* present acquires its own ref */
    }
    AImage_delete(img);
}

static int ensure_reader(basis_decoder_t* d, int w, int h) {
    if (d->reader) return 0;
    media_status_t st = AImageReader_newWithUsage(
        w, h, AIMAGE_FORMAT_PRIVATE,
        AHARDWAREBUFFER_USAGE_GPU_SAMPLED_IMAGE, 4, &d->reader);
    if (st != AMEDIA_OK || !d->reader) { basis_engine_set_error(d->engine, "AImageReader_newWithUsage failed"); return -1; }

    AImageReader_ImageListener listener = { d, on_image };
    AImageReader_setImageListener(d->reader, &listener);
    AImageReader_getWindow(d->reader, &d->window);
    d->vw = w; d->vh = h;
    return 0;
}

/* ---- output draining (push decoded frames to the Surface) --------------- */

static void drain_video_output(basis_decoder_t* d) {
    if (!d->vcodec) return;
    for (;;) {
        AMediaCodecBufferInfo info;
        ssize_t oi = AMediaCodec_dequeueOutputBuffer(d->vcodec, &info, 0);
        if (oi >= 0) {
            d->lastPtsUs = info.presentationTimeUs;
            /* render=true pushes the frame onto the AImageReader Surface */
            AMediaCodec_releaseOutputBuffer(d->vcodec, oi, info.size != 0);
        } else if (oi == AMEDIACODEC_INFO_OUTPUT_FORMAT_CHANGED) {
            AMediaFormat* f = AMediaCodec_getOutputFormat(d->vcodec);
            int32_t w = 0, h = 0;
            AMediaFormat_getInt32(f, AMEDIAFORMAT_KEY_WIDTH, &w);
            AMediaFormat_getInt32(f, AMEDIAFORMAT_KEY_HEIGHT, &h);
            if (w > 0 && h > 0) { d->vw = w; d->vh = h; }
            AMediaFormat_delete(f);
        } else {
            break; /* try again later / no buffer */
        }
    }
}

static void drain_audio_output(basis_decoder_t* d) {
    if (!d->acodec) return;
    for (;;) {
        AMediaCodecBufferInfo info;
        ssize_t oi = AMediaCodec_dequeueOutputBuffer(d->acodec, &info, 0);
        if (oi >= 0) {
            size_t cap = 0;
            uint8_t* buf = AMediaCodec_getOutputBuffer(d->acodec, oi, &cap);
            if (buf && info.size >= 2) {
                if (d->apcm_float) {
                    ring_write(&d->pcm, (const float*)(buf + info.offset), (int)(info.size / 4));
                } else {
                    int n = info.size / 2; /* 16-bit PCM */
                    float tmp[4096];
                    const int16_t* s16 = (const int16_t*)(buf + info.offset);
                    int off = 0;
                    while (off < n) {
                        int chunk = n - off; if (chunk > 4096) chunk = 4096;
                        for (int i = 0; i < chunk; ++i) tmp[i] = s16[off + i] / 32768.0f;
                        ring_write(&d->pcm, tmp, chunk);
                        off += chunk;
                    }
                }
            }
            AMediaCodec_releaseOutputBuffer(d->acodec, oi, false);
        } else if (oi == AMEDIACODEC_INFO_OUTPUT_FORMAT_CHANGED) {
            AMediaFormat* f = AMediaCodec_getOutputFormat(d->acodec);
            AMediaFormat_getInt32(f, AMEDIAFORMAT_KEY_SAMPLE_RATE, &d->asr);
            AMediaFormat_getInt32(f, AMEDIAFORMAT_KEY_CHANNEL_COUNT, &d->ach);
            int32_t enc = 2; /* android.media.AudioFormat.ENCODING_PCM_*: 2 = 16-bit (the default when the key is absent), 4 = float */
            AMediaFormat_getInt32(f, "pcm-encoding", &enc);
            d->apcm_float = (enc == 4);
            if (d->ach > 0 && d->ach != d->pcm.frame) ring_set_frame(&d->pcm, d->ach);
            __android_log_print(ANDROID_LOG_INFO, "basis_media",
                "audio output format: %d Hz, %d ch, pcm-encoding %d", d->asr, d->ach, (int)enc);
            AMediaFormat_delete(f);
        } else break;
    }
}

/* Ask the audio decoder for the stream's full channel layout: AAC decoders on
 * some devices fold multichannel down to stereo unless configured with an
 * output-channel ceiling. Both the generic (API 32+) and the legacy AAC key
 * are set — unknown keys are ignored, and values above the stream's channel
 * count clamp to it. */
static void request_full_channel_output(AMediaFormat* fmt) {
    AMediaFormat_setInt32(fmt, "max-output-channel-count", 99);
    AMediaFormat_setInt32(fmt, "aac-max-output-channel_count", 99);
}

/* ---- URL path: extractor + worker thread -------------------------------- */

static AMediaCodec* create_codec_for_track(basis_decoder_t* d, AMediaFormat* fmt, int video) {
    const char* mime = NULL;
    if (!AMediaFormat_getString(fmt, AMEDIAFORMAT_KEY_MIME, &mime) || !mime) return NULL;
    AMediaCodec* c = AMediaCodec_createDecoderByType(mime);
    if (!c) return NULL;
    ANativeWindow* surface = NULL;
    if (!video) request_full_channel_output(fmt);
    if (video) {
        int32_t w = 1280, h = 720;
        AMediaFormat_getInt32(fmt, AMEDIAFORMAT_KEY_WIDTH, &w);
        AMediaFormat_getInt32(fmt, AMEDIAFORMAT_KEY_HEIGHT, &h);
        if (ensure_reader(d, w, h) != 0) { AMediaCodec_delete(c); return NULL; }
        surface = d->window;
    }
    if (AMediaCodec_configure(c, fmt, surface, NULL, 0) != AMEDIA_OK) { AMediaCodec_delete(c); return NULL; }
    if (AMediaCodec_start(c) != AMEDIA_OK) { AMediaCodec_delete(c); return NULL; }
    return c;
}

static void feed_extractor_sample(basis_decoder_t* d, AMediaCodec* codec, int track) {
    ssize_t ii = AMediaCodec_dequeueInputBuffer(codec, 0);
    if (ii < 0) return;
    size_t cap = 0;
    uint8_t* buf = AMediaCodec_getInputBuffer(codec, ii, &cap);
    ssize_t sz = AMediaExtractor_readSampleData(d->extractor, buf, cap);
    if (sz < 0) {
        AMediaCodec_queueInputBuffer(codec, ii, 0, 0, 0, AMEDIACODEC_BUFFER_FLAG_END_OF_STREAM);
    } else {
        int64_t pts = AMediaExtractor_getSampleTime(d->extractor);
        AMediaCodec_queueInputBuffer(codec, ii, 0, sz, pts, 0);
        AMediaExtractor_advance(d->extractor);
    }
}

static void* url_worker(void* arg) {
    basis_decoder_t* d = (basis_decoder_t*)arg;
    basis_engine_set_state(d->engine, BASIS_MEDIA_STATE_PLAYING);
    while (basis_engine_is_running(d->engine)) {
        if (basis_engine_is_paused(d->engine)) { usleep(10000); continue; }

        int track = AMediaExtractor_getSampleTrackIndex(d->extractor);
        if (track == d->video_track && d->vcodec) feed_extractor_sample(d, d->vcodec, track);
        else if (track == d->audio_track && d->acodec) feed_extractor_sample(d, d->acodec, track);
        else if (track < 0) { usleep(5000); } /* EOS or none ready */
        else AMediaExtractor_advance(d->extractor);

        drain_video_output(d);
        drain_audio_output(d);
    }
    return NULL;
}

/* ---- internal API ------------------------------------------------------- */

basis_decoder_t* basis_decoder_create(basis_media_engine_t* engine) {
    basis_decoder_t* d = (basis_decoder_t*)calloc(1, sizeof(*d));
    if (!d) return NULL;
    d->engine = engine;
    d->video_track = d->audio_track = -1;
    d->lastPtsUs = -1;
    d->vk = basis_vk_create();
    ring_init(&d->pcm, 48000 * 2 * 4);
    return d;
}

void basis_decoder_destroy(basis_decoder_t* d) {
    if (!d) return;
    if (d->worker_started) pthread_join(d->worker, NULL);
    basis_decoder_render_release(d);
    if (d->vcodec) { AMediaCodec_stop(d->vcodec); AMediaCodec_delete(d->vcodec); }
    if (d->acodec) { AMediaCodec_stop(d->acodec); AMediaCodec_delete(d->acodec); }
    if (d->extractor) AMediaExtractor_delete(d->extractor);
    if (d->reader) AImageReader_delete(d->reader);
    if (d->vk) basis_vk_destroy(d->vk);
    ring_free(&d->pcm);
    free(d->lpcmBuf);
    free(d);
}

int basis_decoder_try_open_url(basis_decoder_t* d, const char* url) {
    /* Android: try AMediaExtractor first (HW demux of seekable containers).
     * On failure (live TS, HLS/DASH manifest, etc.) return 0 so the core falls
     * back to basis_jni_https + the portable basis_ts/basis_mp4 demuxer. */
    d->extractor = AMediaExtractor_new();
    media_status_t st = AMediaExtractor_setDataSource(d->extractor, url);
    if (st != AMEDIA_OK) {
        __android_log_print(ANDROID_LOG_INFO, "basis_media",
            "AMediaExtractor rejected url (code %d); falling back to JNI HTTPS + TS/MP4 demuxer", (int)st);
        AMediaExtractor_delete(d->extractor); d->extractor = NULL;
        return 0;
    }
    size_t n = AMediaExtractor_getTrackCount(d->extractor);
    for (size_t i = 0; i < n; ++i) {
        AMediaFormat* f = AMediaExtractor_getTrackFormat(d->extractor, i);
        const char* mime = NULL;
        AMediaFormat_getString(f, AMEDIAFORMAT_KEY_MIME, &mime);
        if (mime && strncmp(mime, "video/", 6) == 0 && d->video_track < 0) {
            d->video_track = (int)i;
            AMediaExtractor_selectTrack(d->extractor, i);
            d->vcodec = create_codec_for_track(d, f, 1);
        } else if (mime && strncmp(mime, "audio/", 6) == 0 && d->audio_track < 0) {
            d->audio_track = (int)i;
            AMediaExtractor_selectTrack(d->extractor, i);
            AMediaFormat_getInt32(f, AMEDIAFORMAT_KEY_SAMPLE_RATE, &d->asr);
            AMediaFormat_getInt32(f, AMEDIAFORMAT_KEY_CHANNEL_COUNT, &d->ach);
            if (d->ach > 0) ring_set_frame(&d->pcm, d->ach);
            d->acodec = create_codec_for_track(d, f, 0);
            d->aconfigured = 1;
        }
        AMediaFormat_delete(f);
    }
    if (!d->vcodec) { basis_engine_set_error(d->engine, "Android: no decodable video track"); return 1; }
    d->vconfigured = 1;

    pthread_create(&d->worker, NULL, url_worker, d);
    d->worker_started = 1;
    return 1; /* took ownership of the URL */
}

int basis_decoder_set_video_format(basis_decoder_t* d, basis_codec_t codec,
                                   const uint8_t* extradata, int extradata_len, int w, int h) {
    if (!d || d->vconfigured) return 0;
    d->vc = codec; if (w > 0) d->vw = w; if (h > 0) d->vh = h;
    const char* mime = (codec == BASIS_CODEC_H265) ? "video/hevc" : "video/avc";

    if (ensure_reader(d, d->vw ? d->vw : 1280, d->vh ? d->vh : 720) != 0) return -1;

    AMediaFormat* fmt = AMediaFormat_new();
    AMediaFormat_setString(fmt, AMEDIAFORMAT_KEY_MIME, mime);
    AMediaFormat_setInt32(fmt, AMEDIAFORMAT_KEY_WIDTH, d->vw ? d->vw : 1280);
    AMediaFormat_setInt32(fmt, AMEDIAFORMAT_KEY_HEIGHT, d->vh ? d->vh : 720);
    if (extradata && extradata_len > 0)
        AMediaFormat_setBuffer(fmt, "csd-0", (void*)extradata, extradata_len); /* Annex-B SPS/PPS(/VPS) */

    AMediaCodec* c = AMediaCodec_createDecoderByType(mime);
    if (!c || AMediaCodec_configure(c, fmt, d->window, NULL, 0) != AMEDIA_OK ||
        AMediaCodec_start(c) != AMEDIA_OK) {
        if (c) AMediaCodec_delete(c);
        AMediaFormat_delete(fmt);
        basis_engine_set_error(d->engine, "Android: video AMediaCodec configure/start failed");
        return -1;
    }
    d->vcodec = c;
    AMediaFormat_delete(fmt);
    d->vconfigured = 1;
    basis_engine_set_state(d->engine, BASIS_MEDIA_STATE_PLAYING);
    return 0;
}

int basis_decoder_set_audio_format(basis_decoder_t* d, basis_codec_t codec,
                                   int sample_rate, int channels, const uint8_t* asc, int asc_len) {
    if (!d || d->aconfigured) return 0;

    if (codec == BASIS_CODEC_LPCM) {
        /* Decoder bypass, mirroring the Windows lane: no MediaCodec involved —
         * submit_audio converts straight into the ring. 48 kHz / 16- or 24-bit
         * only; the TS demuxer filters to these before announcing, this is the
         * matching backstop. The config blob carries the Blu-ray
         * channel_assignment + bits code. */
        if (sample_rate != 48000 || channels < 1 || channels > 8 || !asc || asc_len < 2) return 0;
        int bits = asc[1] == 1 ? 16 : asc[1] == 3 ? 24 : 0;
        if (!bits) return 0; /* 20-bit unsupported */
        d->ac = BASIS_CODEC_LPCM;
        d->asr = sample_rate; d->ach = channels;
        d->aLpcmAssign = asc[0];
        d->aLpcmBits = bits;
        ring_set_frame(&d->pcm, channels);
        d->aconfigured = 1;
        return 0;
    }

    if (codec != BASIS_CODEC_AAC) return 0;
    d->ac = BASIS_CODEC_AAC;
    d->asr = sample_rate; d->ach = channels;
    ring_set_frame(&d->pcm, channels ? channels : 2);
    AMediaFormat* fmt = AMediaFormat_new();
    AMediaFormat_setString(fmt, AMEDIAFORMAT_KEY_MIME, "audio/mp4a-latm");
    AMediaFormat_setInt32(fmt, AMEDIAFORMAT_KEY_SAMPLE_RATE, sample_rate ? sample_rate : 48000);
    AMediaFormat_setInt32(fmt, AMEDIAFORMAT_KEY_CHANNEL_COUNT, channels ? channels : 2);
    request_full_channel_output(fmt);
    if (asc && asc_len > 0) AMediaFormat_setBuffer(fmt, "csd-0", (void*)asc, asc_len);
    AMediaCodec* c = AMediaCodec_createDecoderByType("audio/mp4a-latm");
    if (!c || AMediaCodec_configure(c, fmt, NULL, NULL, 0) != AMEDIA_OK ||
        AMediaCodec_start(c) != AMEDIA_OK) {
        if (c) AMediaCodec_delete(c);
        AMediaFormat_delete(fmt);
        d->aconfigured = 1;
        return -1;
    }
    d->acodec = c;
    AMediaFormat_delete(fmt);
    d->aconfigured = 1;
    return 0;
}

int basis_decoder_submit_video(basis_decoder_t* d, const uint8_t* annexb, int len, int64_t pts_us, int key) {
    (void)key;
    if (!d || !d->vcodec) return -1;
    ssize_t ii = AMediaCodec_dequeueInputBuffer(d->vcodec, 2000);
    if (ii >= 0) {
        size_t cap = 0;
        uint8_t* buf = AMediaCodec_getInputBuffer(d->vcodec, ii, &cap);
        int n = len < (int)cap ? len : (int)cap;
        memcpy(buf, annexb, n);
        AMediaCodec_queueInputBuffer(d->vcodec, ii, 0, n, pts_us, 0);
    }
    drain_video_output(d);
    return 0;
}

/* Source-order -> WAVE-order channel map for the Blu-ray HDMV LPCM
 * channel_assignment values whose stream order differs from WAVE (Blu-ray
 * places the LFE last and the side pair before the rears). Same tables as the
 * Windows lane, which match ffmpeg's pcm_bluray remap for assignments 9 (5.1),
 * 10 (7.0) and 11 (7.1) and were verified by ear against a 7.1 channel-marker
 * stream. NULL = identity (mono/stereo/3.0/4.0/5.0 already arrive in WAVE
 * order). */
static const int* lpcm_remap(int assign) {
    static const int k51[6] = { 0, 1, 2, 4, 5, 3 };
    static const int k70[7] = { 0, 1, 2, 5, 3, 4, 6 };
    static const int k71[8] = { 0, 1, 2, 6, 4, 5, 7, 3 };
    if (assign == 9) return k51;
    if (assign == 10) return k70;
    if (assign == 11) return k71;
    return NULL;
}

/* LPCM bypass: big-endian 16/24-bit Blu-ray-order PCM -> interleaved WAVE-order
 * float, straight into the ring. Whole frames only, so the ring keeps its
 * channel phase (the alignment contract in the ring comment above). */
static void submit_lpcm(basis_decoder_t* d, const uint8_t* p, int len) {
    int ch = d->ach;
    int bytes = d->aLpcmBits / 8;
    int frame_bytes = ch * bytes;
    int frames = frame_bytes > 0 ? len / frame_bytes : 0;
    if (frames <= 0) return;
    int floats = frames * ch;
    if (floats > d->lpcmBufCap) {
        float* nb = (float*)realloc(d->lpcmBuf, sizeof(float) * (size_t)floats);
        if (!nb) return;
        d->lpcmBuf = nb; d->lpcmBufCap = floats;
    }
    const int* map = lpcm_remap(d->aLpcmAssign);
    for (int f = 0; f < frames; ++f) {
        const uint8_t* s = p + f * frame_bytes;
        float* o = d->lpcmBuf + f * ch;
        for (int c = 0; c < ch; ++c) {
            int oc = map ? map[c] : c;
            if (bytes == 2) {
                int v = (int16_t)((s[c * 2] << 8) | s[c * 2 + 1]);
                o[oc] = v / 32768.0f;
            } else {
                int v = (s[c * 3] << 16) | (s[c * 3 + 1] << 8) | s[c * 3 + 2];
                if (v & 0x800000) v -= 0x1000000;
                o[oc] = v / 8388608.0f;
            }
        }
    }
    ring_write(&d->pcm, d->lpcmBuf, floats);
}

int basis_decoder_submit_audio(basis_decoder_t* d, const uint8_t* data, int len, int64_t pts_us) {
    if (!d || !data || len <= 0) return -1;
    if (d->ac == BASIS_CODEC_LPCM) { submit_lpcm(d, data, len); return 0; }
    if (!d->acodec) return -1;
    ssize_t ii = AMediaCodec_dequeueInputBuffer(d->acodec, 2000);
    if (ii >= 0) {
        size_t cap = 0;
        uint8_t* buf = AMediaCodec_getInputBuffer(d->acodec, ii, &cap);
        int n = len < (int)cap ? len : (int)cap;
        memcpy(buf, data, n);
        AMediaCodec_queueInputBuffer(d->acodec, ii, 0, n, pts_us, 0);
    }
    drain_audio_output(d);
    return 0;
}

/* ---- render thread + accessors ----------------------------------------- */

int basis_decoder_render_update(basis_decoder_t* d) {
    if (!d || !d->vk) return -1;
    if (basis_engine_is_paused(d->engine)) return 0;
    return basis_vk_render_update(d->vk);
}
void basis_decoder_render_release(basis_decoder_t* d) { if (d && d->vk) basis_vk_release(d->vk); }

void* basis_decoder_get_texture(basis_decoder_t* d, int* w, int* h) {
    if (!d || !d->vk) { if (w) *w = 0; if (h) *h = 0; return NULL; }
    uint64_t img = basis_vk_get_image(d->vk, w, h);
    return (void*)(uintptr_t)img;
}
uint64_t basis_decoder_get_frame_counter(basis_decoder_t* d) { return d && d->vk ? basis_vk_frame_counter(d->vk) : 0; }
int basis_decoder_get_video_size(basis_decoder_t* d, int* w, int* h) {
    if (!d || d->vw <= 0) return -1; if (w) *w = d->vw; if (h) *h = d->vh; return 0;
}
/* The Vulkan resolve always flips to upright via a negative-height viewport, so
 * the published frame is bottom-left origin on every Android GPU. */
int basis_decoder_get_frame_origin(basis_decoder_t* d) { (void)d; return 0; }
int64_t basis_decoder_get_position_us(basis_decoder_t* d) { return d ? d->lastPtsUs : -1; }
int basis_decoder_get_audio_format(basis_decoder_t* d, int* r, int* c) {
    if (!d || !d->aconfigured) return -1; if (r) *r = d->asr ? d->asr : 48000; if (c) *c = d->ach ? d->ach : 2; return 0;
}
int basis_decoder_read_audio(basis_decoder_t* d, float* out, int max) { return d ? ring_read(&d->pcm, out, max) : 0; }
int basis_decoder_get_debug(basis_decoder_t* d, char* buf, int size) {
    if (!d || !buf || size <= 0) return 0;
    return snprintf(buf, (size_t)size, "vw=%d vh=%d", d->vw, d->vh);
}
void basis_decoder_set_buffer(basis_decoder_t* d, int mode, int ms) {
    /* Present pacing on the Android/Vulkan path is TODO (see basis_android_vk.c);
     * accept the call so the ABI is uniform. */
    (void)d; (void)mode; (void)ms;
}

void basis_decoder_set_output_texture(basis_decoder_t* d, void* native_texture, int w, int h) {
    if (d && d->vk) basis_vk_set_output_texture(d->vk, native_texture, w, h);
}
