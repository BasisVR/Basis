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
#include <stdatomic.h>
#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <unistd.h>
#include <time.h>

/* ---- monotonic clock ---------------------------------------------------- */

static int64_t now_monotonic_us(void) {
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (int64_t)ts.tv_sec * 1000000LL + ts.tv_nsec / 1000;
}

/* ---- PCM ring ----------------------------------------------------------- */

/* Interleaved float FIFO with per-chunk PTS metadata, mirroring the Windows
 * PcmRing (basis_win_decode.cpp): the decode thread writes chunks tagged with
 * their media timestamps; the audio thread reads gated against the presentation
 * clock, so a connect burst or post-stall backlog is trimmed rather than served
 * out forever behind the video. Drops are always whole-frame counts, so the
 * surviving stream keeps its channel phase; reads may return partial frames —
 * the managed splitter carries sub-frame remainders across pulls. */
#define PCM_CHUNKS 1024
typedef struct { int64_t pts; int floats; } pcm_chunk;
typedef struct {
    float* buf; int cap, head, tail;
    int frame; /* floats per interleaved frame (channel count) */
    int sr;    /* sample rate, for chunk durations */
    pcm_chunk chunks[PCM_CHUNKS];
    int chead, ccount;
    long trims;          /* diagnostics: clock-gated trims fired */
    int lastTrimFloats;  /* diagnostics: floats dropped by the last trim */
    int64_t playedUs;    /* PTS served up to = playback front (audio-only position) */
    pthread_mutex_t m;
} pcm_ring;

/* Serving is gated on media time (mirroring the Windows PcmRing): a sample is
 * released when its PTS comes due against the serve target (presentation clock
 * + the sink's output latency, so alignment lands at the speaker). Surplus the
 * mux delivered early waits in the ring instead of becoming output latency,
 * and just-in-time delivery banks a cushion behind the video hold instead of
 * running dry. The caller's early-hold is serve hysteresis sized above the
 * sink's pull-batch depth. A head further than TRIM_LATE overdue (connect
 * burst, post-stall backlog, PTS jump) is trimmed to the target — re-anchoring
 * on the discontinuity rather than discarding real-time delivery forever. */
#define PCM_TRIM_LATE_US     150000LL

static void ring_init(pcm_ring* r, int floats) { r->buf = malloc(sizeof(float) * floats); r->cap = floats; r->head = r->tail = 0; r->frame = 2; r->sr = 48000; r->chead = r->ccount = 0; r->trims = 0; r->lastTrimFloats = 0; r->playedUs = INT64_MIN; pthread_mutex_init(&r->m, NULL); }
static void ring_free(pcm_ring* r) { free(r->buf); r->buf = NULL; pthread_mutex_destroy(&r->m); }
static void ring_set_frame(pcm_ring* r, int frame, int sr) {
    pthread_mutex_lock(&r->m);
    r->frame = frame > 0 ? frame : 1;
    if (sr > 0) r->sr = sr;
    r->head = r->tail = 0;   /* buffered floats are in the old framing */
    r->chead = r->ccount = 0;
    pthread_mutex_unlock(&r->m);
}

static int ring_fill(const pcm_ring* r) { return (r->tail - r->head + r->cap) % r->cap; }

/* Drops the oldest `n` floats (rounded down to whole frames) from the float ring
 * and the chunk metadata together. Caller holds r->m. */
static void ring_drop_oldest(pcm_ring* r, int n) {
    n -= n % r->frame;
    int avail = ring_fill(r);
    if (n > avail) n = avail - (avail % r->frame);
    if (n <= 0) return;
    r->head = (r->head + n) % r->cap;
    int srr = r->sr > 0 ? r->sr : 48000;
    while (n > 0 && r->ccount > 0) {
        pcm_chunk* c = &r->chunks[r->chead];
        if (c->floats <= n) { n -= c->floats; r->chead = (r->chead + 1) % PCM_CHUNKS; r->ccount--; }
        else { c->floats -= n; c->pts += (int64_t)(n / r->frame) * 1000000LL / srr; n = 0; }
    }
}

static void ring_write(pcm_ring* r, const float* s, int n, int64_t pts) {
    if (n <= 0) return;
    pthread_mutex_lock(&r->m);
    int srr = r->sr > 0 ? r->sr : 48000;
    if (n > r->cap - 1) {
        /* Over-capacity write: drop the oldest whole frames and carry the PTS
         * forward so the retained tail keeps a correct timestamp. */
        int keep = (r->cap - 1) - ((r->cap - 1) % r->frame);
        int drop = n - keep;
        s += drop;
        pts += (int64_t)(drop / r->frame) * 1000000LL / srr;
        n = keep;
    }
    int space = r->cap - 1 - ring_fill(r);
    if (n > space) {
        int need = (n - space) + r->frame - 1;
        ring_drop_oldest(r, need - need % r->frame);
    }
    for (int i = 0; i < n; ++i) { r->buf[r->tail] = s[i]; r->tail = (r->tail + 1) % r->cap; }
    if (r->ccount == PCM_CHUNKS) {
        r->chunks[(r->chead + r->ccount - 1) % PCM_CHUNKS].floats += n;   /* metadata full: coalesce into the tail chunk */
    } else {
        pcm_chunk* c = &r->chunks[(r->chead + r->ccount) % PCM_CHUNKS];
        c->pts = pts; c->floats = n; r->ccount++;
    }
    pthread_mutex_unlock(&r->m);
}

/* target_us = INT64_MIN reads ungated (audio-only stream, no clock). */
static int ring_read(pcm_ring* r, float* out, int n, int64_t target_us, int64_t early_hold_us) {
    pthread_mutex_lock(&r->m);
    int srr = r->sr > 0 ? r->sr : 48000;
    if (target_us != INT64_MIN && r->ccount > 0) {
        int64_t late = target_us - r->chunks[r->chead].pts;
        if (late > PCM_TRIM_LATE_US) {
            int drop = (int)(late * srr / 1000000LL) * r->frame;
            ring_drop_oldest(r, drop);
            r->trims++; r->lastTrimFloats = drop;
        }
    }
    int got = 0;
    int64_t frontPts = (r->ccount > 0) ? r->chunks[r->chead].pts : INT64_MIN;
    while (got < n && r->ccount > 0) {
        pcm_chunk* c = &r->chunks[r->chead];
        if (target_us != INT64_MIN && c->pts > target_us + early_hold_us) break;
        int take = c->floats < n - got ? c->floats : n - got;
        for (int i = 0; i < take; ++i) { out[got + i] = r->buf[r->head]; r->head = (r->head + 1) % r->cap; }
        got += take;
        if (take == c->floats) { r->chead = (r->chead + 1) % PCM_CHUNKS; r->ccount--; }
        else { c->floats -= take; c->pts += (int64_t)take * 1000000LL / ((int64_t)r->frame * srr); }
    }
    /* Publish the playback front so an audio-only stream has a position. Only
     * when samples actually served: a gated read that breaks before copying
     * leaves the front unserved, so its PTS isn't yet "played". */
    if (frontPts != INT64_MIN && got > 0)
        r->playedUs = frontPts + (int64_t)(got / (r->frame > 0 ? r->frame : 1)) * 1000000LL / srr;
    pthread_mutex_unlock(&r->m);
    return got;
}

/* PTS just past the newest queued sample — the audio delivery edge.
 * INT64_MIN when empty. */
static int64_t ring_newest_pts(pcm_ring* r) {
    pthread_mutex_lock(&r->m);
    int64_t v = INT64_MIN;
    if (r->ccount > 0) {
        pcm_chunk* c = &r->chunks[(r->chead + r->ccount - 1) % PCM_CHUNKS];
        v = c->pts + (int64_t)(c->floats / (r->frame > 0 ? r->frame : 1)) * 1000000LL / (r->sr > 0 ? r->sr : 48000);
    }
    pthread_mutex_unlock(&r->m);
    return v;
}

/* Diagnostics: currently-queued audio, in milliseconds. */
static int ring_fill_ms(pcm_ring* r) {
    pthread_mutex_lock(&r->m);
    int frames = r->frame > 0 ? ring_fill(r) / r->frame : 0;
    int srr = r->sr > 0 ? r->sr : 48000;
    pthread_mutex_unlock(&r->m);
    return (int)((int64_t)frames * 1000 / srr);
}

/* Drop everything buffered — used on a seek so pre-seek chunks can neither gate
 * the ring (front chunks ahead of the target block the post-seek audio behind
 * them) nor play out ahead of the post-seek audio. Mirrors PcmRing::flush. */
static void ring_flush(pcm_ring* r) {
    pthread_mutex_lock(&r->m);
    r->head = 0; r->tail = 0;
    r->chead = 0; r->ccount = 0;
    r->playedUs = INT64_MIN;
    pthread_mutex_unlock(&r->m);
}

/* ---- video frame ring --------------------------------------------------- */

/* Decoded frames are held as acquired AImages (each owning an AHardwareBuffer),
 * tagged with their presentation PTS, so render_update can present the frame due
 * on the presentation clock instead of always the newest — the Android mirror of
 * the Windows frame ring. Sized to span the jitter buffer plus a few slots of
 * decode headroom (the codec needs free buffers to render into); each slot is a
 * full-resolution hardware buffer, so it's kept modest — but large enough that
 * maxBuf ((VRING-6) * frame-period) comfortably exceeds the sync hold at 24-30fps
 * (26 usable frames = ~1.08s @24fps, ~867ms @30fps). */
#define VRING 32

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
    int vw, vh;               /* coded frame dims (buffer size, macroblock-padded) */
    uint64_t dispWH;          /* display dims (w<<32)|h, published as one atomic; 0 until first frame */
    int fcValid, fcL, fcT, fcR, fcB; /* MediaFormat display-crop rect (authoritative when present) */
    int vconfigured, aconfigured;

    int asr, ach;
    int apcm_float; /* decoder emits float PCM (pcm-encoding 4) instead of 16-bit */

    basis_codec_t ac;       /* audio lane: AAC (MediaCodec) or LPCM (direct convert) */
    int aLpcmAssign;        /* Blu-ray channel_assignment (from the format's config blob) */
    int aLpcmBits;          /* 16 or 24 */
    int aLpcmLE;            /* 1 = little-endian samples (RIFF/WAV lane) */
    float* lpcmBuf;         /* conversion scratch, grown to the largest frame batch */
    int lpcmBufCap;

    basis_vk_present* vk;

    pthread_t worker;
    int worker_started;

    int64_t lastPtsUs;      /* video decode edge: PTS of the newest decoded video frame
                             * (set only on the video path; stays -1 for audio-only) */
    pcm_ring pcm;

    /* video frame ring (parallel arrays; img==NULL marks an empty slot) */
    AImage* vimg[VRING];
    int64_t vpts[VRING];
    int vfw[VRING], vfh[VRING];
    float vuv[VRING][4];      /* per-frame crop UV transform (scale.xy, offset.zw) */
    pthread_mutex_t vm;

    /* presentation clock (render thread), mirroring basis_win_decode.cpp */
    int clockStarted;
    int64_t wallStartUs;      /* monotonic-us origin of the current clock lock */
    int64_t lastRenderUs;
    int64_t renderTickUs;     /* EMA of the render-callback period (due-check lookahead) */
    int64_t primeStartUs;     /* first render tick with a frame (VOD prime window) */
    int64_t mediaStartUs;
    int64_t lastPresentedPts; /* PTS of the frame currently shown; INT64_MIN = none */
    int64_t presentedPosUs;   /* stable position for get_position_us; -1 until first present */
    int  audioSettling;       /* audio-only position: hold get_position at the seek target
                               * until post-seek audio serves near it (main thread only) */
    int64_t frameIntervalUs;  /* EMA of inter-frame PTS delta (source frame period) */
    int64_t prevWritePts;     /* last frame PTS enqueued (for the interval EMA) */
    int64_t audClockOffsetUs; /* published media-time offset from the monotonic clock; INT64_MIN = not started */
    int bufferUs;             /* jitter buffer: how far behind live we present */
    int bufferMode;           /* 0 = fixed, 1 = dynamic */
    int audioLatencyUs;       /* managed sink's reported output latency; drives the video hold + audio lead */

    /* Seek notification (mirrors basis_win_decode.cpp). basis_decoder_seek bumps
     * seekGen (+ latches the target) on the caller thread. Each leg keeps its own
     * last-seen copy and flushes on ITS OWN thread: the audio submit (demux) thread
     * flushes the PCM ring + AAC codec; the video submit (demux) thread flushes the
     * video codec + frame ring (it owns vcodec and writes vimg); the render thread
     * re-anchors the present clock. seekGen/seekTargetUs are cross-thread (atomics). */
    int     seekGen;          /* atomic */
    int64_t seekTargetUs;     /* atomic */
    int64_t seekFromUs;       /* pre-seek audio front, for the audio-only settle (main thread only) */
    int audioSeekGen;         /* audio-submit (demux) thread only */
    int64_t aOutPtsBiasUs;    /* audio-submit thread only: correction added to codec
                               * output PTS before it banks into the ring. The MP3
                               * software decoder stamps outputs from an internal
                               * first-input anchor plus a sample accumulator and
                               * ignores later input-PTS jumps — the anchor survives
                               * a flush — so post-seek outputs keep pre-seek time
                               * (frozen position bar; backward seeks overrun the
                               * duration). Measured per seek as first post-flush
                               * input PTS minus first post-flush output PTS; ~0 for
                               * decoders that pass input PTS through. */
    int64_t aResyncInPts;     /* audio-submit thread only: first post-flush input
                               * PTS, awaiting its output to measure the bias;
                               * INT64_MIN = no measurement pending */
    int     aResyncPending;   /* audio-submit thread only: seek flush done, latch
                               * aResyncInPts from the next queued input */
    int videoSeekGen;         /* video-submit (demux) thread only */
    int renderSeekGen;        /* render thread only */
    int videoSeekAck;         /* atomic: demux publishes seekGen once it has flushed the
                               * codec + released pre-seek frames; the render leg holds
                               * until it matches so it neither anchors to nor deletes a
                               * post-seek frame the producer has already enqueued */
    int64_t vPrerollCutUs;    /* video-submit thread only: after a seek, decoded frames
                               * short of this are the keyframe run-up to the target —
                               * reference-only, released unrendered. Set from
                               * seekTargetUs at the seek flush, cleared by the first
                               * frame at or past it. */
    int vAwaitKey;            /* video-submit thread only: set at the seek flush, cleared
                               * by the first OUTPUT of the first keyframe submitted after
                               * it (matched by PTS via vAwaitKeyPts). Output before that
                               * is post-flush mid-GOP input decoded against stale
                               * reference memory (Adreno emits it as a visible pre-seek
                               * flash; the HLS path can still hand over a pre-seek tail
                               * AU the engine's seek_taken gate doesn't cover) — released
                               * unrendered, and it must not end the preroll run-up.
                               * Clearing at the keyframe's SUBMISSION is not enough: a
                               * tail AU queued just before it can still emit its garbage
                               * frame afterwards, and with a pre-seek PTS past the target
                               * that frame would both show and end the run-up. */
    int64_t vAwaitKeyPts;     /* video-submit thread only: PTS of that keyframe;
                               * INT64_MIN until it is submitted */
    int vAwaitDrained;        /* video-submit thread only: outputs drained since the
                               * seek flush; bounds the wait so a dropped or
                               * re-stamped keyframe output — or a run whose post-seek
                               * AUs are never flagged as keyframes, which would
                               * otherwise never latch vAwaitKeyPts — can't hold the
                               * gate (and video) shut until the next seek */

    /* debug counters */
    long dbg_render, dbg_nodue, dbg_acqfail, dbg_drop, dbg_lagms;
};

/* ---- AImageReader callback: enqueue decoded frames into the video ring --- */

/* Deletes the oldest frame in the ring (freeing its reader slot). Caller holds
 * d->vm. */
static void vring_drop_oldest_locked(basis_decoder_t* d) {
    int oldest = -1; int64_t best = INT64_MAX;
    for (int i = 0; i < VRING; ++i) if (d->vimg[i] && d->vpts[i] < best) { best = d->vpts[i]; oldest = i; }
    if (oldest >= 0) { AImage_delete(d->vimg[oldest]); d->vimg[oldest] = NULL; d->dbg_drop++; }
}

static void on_image(void* ctx, AImageReader* reader) {
    basis_decoder_t* d = (basis_decoder_t*)ctx;
    for (;;) {
        /* Can't hold more than the reader's maxImages at once, so free the oldest
         * slot before acquiring when the ring is full (drops the least useful
         * frame rather than stalling the decoder). */
        pthread_mutex_lock(&d->vm);
        int held = 0; for (int i = 0; i < VRING; ++i) if (d->vimg[i]) held++;
        if (held >= VRING) vring_drop_oldest_locked(d);
        pthread_mutex_unlock(&d->vm);

        AImage* img = NULL;
        if (AImageReader_acquireNextImage(reader, &img) != AMEDIA_OK || !img) break;

        int64_t ts_ns = 0;
        AImage_getTimestamp(img, &ts_ns);       /* MediaCodec propagates the input PTS (ns) */
        /* Seek-generation tag (sub-microsecond digits, written at release):
         * a mismatch means this frame crossed a seek flush in flight — showing
         * it would anchor the present clock at the pre-seek position. */
        {
            int tag = (int)(((ts_ns % 1000) + 1000) % 1000);
            int gen = ((__atomic_load_n(&d->seekGen, __ATOMIC_ACQUIRE) % 1000) + 1000) % 1000;
            if (tag != gen) { AImage_delete(img); continue; }
        }
        int64_t pts = ts_ns / 1000;
        int32_t aw = 0, ah = 0;
        AImage_getWidth(img, &aw); AImage_getHeight(img, &ah);

        /* Crop the coded buffer to the display rectangle. The coded height is
         * padded up to a macroblock multiple (360->368, 1080->1088); sampling
         * the whole buffer draws the pad rows as an edge strip. The denominator
         * must be the true buffer geometry Vulkan imports — AImage width/height
         * report the display crop on some devices (Quest gives 360 for a 368-row
         * buffer), so the pad-crop is taken from the hardware buffer, not the
         * image. The visible region comes from the codec display-crop, then the
         * AImage crop (exclusive), then the AImage size when it's already the
         * display region, else the whole buffer. A full texel is trimmed off
         * each cropped edge so bilinear taps can't pull the subsampled chroma of
         * a pad texel into the last valid row — matching the platform
         * GLConsumer's YUV420 inset that the SurfaceTexture path bakes in. */
        int bufW = 0, bufH = 0;
        AHardwareBuffer* dahb = NULL;
        if (AImage_getHardwareBuffer(img, &dahb) == AMEDIA_OK && dahb) {
            AHardwareBuffer_Desc dsc; AHardwareBuffer_describe(dahb, &dsc);
            bufW = (int)dsc.width; bufH = (int)dsc.height;
        }
        /* Snapshot the coded dims and the MediaFormat crop rect as a unit under
         * d->vm: both are written on the decode thread, and a format change must
         * not leave us mixing old and new values here. */
        pthread_mutex_lock(&d->vm);
        int vw = d->vw, vh = d->vh;
        int fcv = d->fcValid, fl = d->fcL, ft = d->fcT, fr = d->fcR, fb = d->fcB;
        pthread_mutex_unlock(&d->vm);
        if (bufW <= 0) bufW = vw > 0 ? vw : (aw > 0 ? aw : 1);
        if (bufH <= 0) bufH = vh > 0 ? vh : (ah > 0 ? ah : 1);

        int cw = bufW, ch = bufH, cl = 0, ct = 0;
        AImageCropRect cr; int haveImgCrop = (AImage_getCropRect(img, &cr) == AMEDIA_OK);
        if (fcv) {
            /* MediaFormat crop right/bottom are inclusive (w = right-left+1);
             * fall back to exclusive if the inclusive read overshoots the buffer. */
            int rw = fr - fl + 1, rh = fb - ft + 1;
            if (rw <= 0 || rw > bufW) rw = fr - fl;
            if (rh <= 0 || rh > bufH) rh = fb - ft;
            /* Require the whole rectangle inside the buffer: a non-zero offset
             * plus the extent must not run past the edge. */
            if (rw > 0 && rh > 0 && fl >= 0 && ft >= 0 && fl <= bufW - rw && ft <= bufH - rh) {
                cw = rw; ch = rh; cl = fl; ct = ft;
            }
        } else if (haveImgCrop && (cr.right - cr.left) > 0 && (cr.bottom - cr.top) > 0 &&
                   cr.left >= 0 && cr.top >= 0 &&
                   cr.right <= bufW && cr.bottom <= bufH) {
            cw = cr.right - cr.left; ch = cr.bottom - cr.top; cl = cr.left; ct = cr.top;
        } else if (aw > 0 && ah > 0 && aw <= bufW && ah <= bufH && (aw < bufW || ah < bufH)) {
            cw = aw; ch = ah;   /* AImage reports the display size directly */
        }
        float uvsx = (float)cw / bufW, uvsy = (float)ch / bufH;
        float uvox = (float)cl / bufW, uvoy = (float)ct / bufH;
        if (cw < bufW) { float tx = 1.0f / bufW; uvsx -= 2.0f * tx; uvox += tx; }
        if (ch < bufH) { float ty = 1.0f / bufH; uvsy -= 2.0f * ty; uvoy += ty; }
        /* Publish w and h as one value so a reader can't latch a mixed pair
         * (new width, old height) across a format change and size the RT wrong. */
        __atomic_store_n(&d->dispWH, ((uint64_t)(uint32_t)cw << 32) | (uint32_t)ch, __ATOMIC_RELAXED);

        pthread_mutex_lock(&d->vm);
        int slot = -1; for (int i = 0; i < VRING; ++i) if (!d->vimg[i]) { slot = i; break; }
        if (slot >= 0) {
            d->vimg[slot] = img;
            d->vpts[slot] = pts;
            d->vfw[slot] = bufW;   /* import extent must match the AHB */
            d->vfh[slot] = bufH;
            d->vuv[slot][0] = uvsx; d->vuv[slot][1] = uvsy;
            d->vuv[slot][2] = uvox; d->vuv[slot][3] = uvoy;
            /* frame-period EMA, for the jitter-buffer ceiling */
            if (d->prevWritePts != INT64_MIN) {
                int64_t dpts = pts - d->prevWritePts;
                if (dpts > 0 && dpts < 1000000)
                    d->frameIntervalUs = d->frameIntervalUs > 0 ? (d->frameIntervalUs * 7 + dpts) / 8 : dpts;
            }
            d->prevWritePts = pts;
            img = NULL;
        }
        pthread_mutex_unlock(&d->vm);

        if (img) AImage_delete(img); /* ring still full after a drop: shouldn't happen */
    }
}

static int ensure_reader(basis_decoder_t* d, int w, int h) {
    if (d->reader) return 0;
    media_status_t st = AImageReader_newWithUsage(
        w, h, AIMAGE_FORMAT_PRIVATE,
        AHARDWAREBUFFER_USAGE_GPU_SAMPLED_IMAGE, VRING, &d->reader);
    if (st != AMEDIA_OK || !d->reader) { basis_engine_set_error(d->engine, "AImageReader_newWithUsage failed"); return -1; }

    AImageReader_ImageListener listener = { d, on_image };
    AImageReader_setImageListener(d->reader, &listener);
    AImageReader_getWindow(d->reader, &d->window);
    d->vw = w; d->vh = h;
    return 0;
}

/* ---- output draining (push decoded frames to the Surface) --------------- */

/* Returns 1 once the codec has emitted its end-of-stream output (only after
 * basis_decoder_notify_end_of_stream queued the EOS input); 0 otherwise. */
static int drain_video_output(basis_decoder_t* d) {
    if (!d->vcodec) return 0;
    int eos = 0;
    for (;;) {
        AMediaCodecBufferInfo info;
        ssize_t oi = AMediaCodec_dequeueOutputBuffer(d->vcodec, &info, 0);
        if (oi >= 0) {
            if (info.flags & AMEDIACODEC_BUFFER_FLAG_END_OF_STREAM) eos = 1;
            d->lastPtsUs = info.presentationTimeUs;
            /* render=true pushes the frame onto the AImageReader Surface. Post-seek
             * preroll (keyframe run-up short of the target) is decoded so later
             * frames have their references but released unrendered; output is
             * display-order, so the first frame at or past the target ends the
             * run-up for good — but only once the post-flush keyframe's own
             * output has emerged: output before that is post-flush mid-GOP
             * garbage whose PTS may sit past the target, and it must neither
             * show nor end the run-up. */
            int render = info.size != 0;
            /* The PTS match is the designed clear (the keyframe's own output; the
             * cut below gates it). The drain bound is a backstop, set well past any
             * plausible garbage-tail length: past it the cut still suppresses the
             * run-up, so the worst case is one stale frame — degraded, not wedged. */
            if (d->vAwaitKey &&
                ((d->vAwaitKeyPts != INT64_MIN && info.presentationTimeUs == d->vAwaitKeyPts) ||
                 ++d->vAwaitDrained > 16))
                d->vAwaitKey = 0;
            if (d->vAwaitKey) {
                render = 0;
            } else if (d->vPrerollCutUs != INT64_MIN) {
                if (info.presentationTimeUs < d->vPrerollCutUs) render = 0;
                else d->vPrerollCutUs = INT64_MIN;
            }
            if (render) {
                /* Tag the frame with the seek generation in the sub-microsecond
                 * digits of the surface timestamp (the PTS rides in whole
                 * microseconds, so on_image's ts/1000 is untouched). A frame
                 * still in flight through the AImageReader listener when a seek
                 * flushes carries the old tag and dies at on_image instead of
                 * presenting its pre-seek PTS and mis-anchoring the clock.
                 * The tag is this thread's videoSeekGen, which advances only in
                 * the seek-flush block: a pre-seek frame drained after the seek
                 * posts but before the flush runs still carries the old tag,
                 * where the live seekGen would stamp it as post-seek content. */
                int64_t tag = (int64_t)(((d->videoSeekGen % 1000) + 1000) % 1000);
                AMediaCodec_releaseOutputBufferAtTime(d->vcodec, oi,
                                                      info.presentationTimeUs * 1000 + tag);
            } else {
                AMediaCodec_releaseOutputBuffer(d->vcodec, oi, 0);
            }
        } else if (oi == AMEDIACODEC_INFO_OUTPUT_FORMAT_CHANGED) {
            AMediaFormat* f = AMediaCodec_getOutputFormat(d->vcodec);
            int32_t w = 0, h = 0;
            AMediaFormat_getInt32(f, AMEDIAFORMAT_KEY_WIDTH, &w);
            AMediaFormat_getInt32(f, AMEDIAFORMAT_KEY_HEIGHT, &h);
            /* Display crop: the coded buffer pads to a macroblock multiple; this
             * rect is the visible region. Authoritative when the codec sets it
             * (AImage_getCropRect is unreliable on some devices). Publish the
             * coded dims and crop rect together under d->vm (on_image reads them
             * on the listener thread), clearing the crop on every format change
             * so a format that drops it doesn't leave a stale one active. */
            int32_t cl = 0, ct = 0, crr = 0, cb = 0;
            int haveCrop = AMediaFormat_getRect(f, AMEDIAFORMAT_KEY_DISPLAY_CROP, &cl, &ct, &crr, &cb);
            pthread_mutex_lock(&d->vm);
            if (w > 0 && h > 0) { d->vw = w; d->vh = h; }
            d->fcValid = haveCrop ? 1 : 0;
            if (haveCrop) { d->fcL = cl; d->fcT = ct; d->fcR = crr; d->fcB = cb; }
            pthread_mutex_unlock(&d->vm);
            AMediaFormat_delete(f);
        } else {
            break; /* try again later / no buffer */
        }
    }
    return eos;
}

void basis_decoder_notify_end_of_stream(basis_decoder_t* d) {
    if (!d || !d->vcodec) return;
    /* Caller is the video-submit (demux) thread, which owns vcodec — same
     * ownership as submit_video. MediaCodec flushes asynchronously after the
     * EOS input, so pump the output side (bounded) until the EOS-flagged
     * buffer emerges rather than trusting a single drain pass.
     * TRY_AGAIN_LATER from dequeueInputBuffer is routine while the codec is
     * busy, so retry to a short deadline (draining outputs frees input slots).
     * If the EOS input still can't be queued, the retained tail stays in the
     * codec, presentation_pending never sees it, and the core's drain-wait
     * ends on its idle cap — degraded, not wedged. */
    ssize_t ii = -1;
    for (int i = 0; i < 50 && ii < 0; ++i) {   /* ~500ms deadline */
        ii = AMediaCodec_dequeueInputBuffer(d->vcodec, 10000);
        if (ii < 0) drain_video_output(d);
    }
    if (ii < 0) return;
    if (AMediaCodec_queueInputBuffer(d->vcodec, ii, 0, 0, 0,
                                     AMEDIACODEC_BUFFER_FLAG_END_OF_STREAM) != AMEDIA_OK) return;
    for (int i = 0; i < 100; ++i) {   /* ~1s cap */
        if (drain_video_output(d)) return;
        usleep(10000);
    }
}

int basis_decoder_presentation_pending(basis_decoder_t* d) {
    if (!d) return 0;
    int pending = 0;
    pthread_mutex_lock(&d->vm);
    for (int i = 0; i < VRING; ++i) if (d->vimg[i]) { pending = 1; break; }
    pthread_mutex_unlock(&d->vm);
    if (!pending) pending = ring_fill_ms(&d->pcm) > 0;
    return pending;
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
                /* First output after a seek flush: its input PTS is known, so any
                 * difference is the decoder's own timeline drifting from the demux
                 * timeline — cancel it for the rest of this seek generation. */
                if (d->aResyncInPts != INT64_MIN) {
                    d->aOutPtsBiasUs = d->aResyncInPts - info.presentationTimeUs;
                    d->aResyncInPts = INT64_MIN;
                }
                int64_t pts = info.presentationTimeUs + d->aOutPtsBiasUs;
                int frame = d->ach > 0 ? d->ach : (d->pcm.frame > 0 ? d->pcm.frame : 2);
                int srr = d->asr > 0 ? d->asr : 48000;
                if (d->apcm_float) {
                    int n = (int)(info.size / 4);
                    /* Priming is dropped by starting past it; the time below is
                     * derived from the offset, so it stays right for the rest. */
                    int skip = basis_frames_before_origin(pts, n / frame, srr) * frame;
                    if (skip < n)
                        ring_write(&d->pcm, (const float*)(buf + info.offset) + skip, n - skip,
                                   pts + (int64_t)(skip / frame) * 1000000LL / srr);
                } else {
                    int n = info.size / 2; /* 16-bit PCM */
                    float tmp[4096];
                    const int16_t* s16 = (const int16_t*)(buf + info.offset);
                    int off = basis_frames_before_origin(pts, n / frame, srr) * frame;
                    while (off < n) {
                        int chunk = n - off; if (chunk > 4096) chunk = 4096;
                        for (int i = 0; i < chunk; ++i) tmp[i] = s16[off + i] / 32768.0f;
                        ring_write(&d->pcm, tmp, chunk, pts + (int64_t)(off / frame) * 1000000LL / srr);
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
            if (d->ach > 0 && d->ach != d->pcm.frame) ring_set_frame(&d->pcm, d->ach, d->asr);
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
    if (!buf) { AMediaCodec_queueInputBuffer(codec, ii, 0, 0, 0, 0); return; }
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
        /* No seek support on this path (no seekTo, no flush), but the drain's
         * frame tag reads videoSeekGen — keep it tracking seekGen so a seek
         * request can't leave every subsequent frame tagged stale. */
        d->videoSeekGen = __atomic_load_n(&d->seekGen, __ATOMIC_ACQUIRE);

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

int basis_decoder_probe_video_codec(int codec) {
    /* Cached per process (0 unprobed / 1 no / 2 yes); atomics keep the
     * concurrent worker-thread accesses defined, and a racing recompute
     * stores the same verdict. createDecoderByType is the platform answer —
     * every Quest hardware-decodes VP9, and AV1 tracks the silicon (Quest 3 /
     * XR2 Gen 2 has hardware AV1, Quest 2 has no AV1 decoder at all), so
     * decoder presence is the right verdict there. (On general Android this
     * can return the software c2.android decoder and probe optimistic; the
     * NDK exposes no MediaCodecList to tell them apart.) */
    static _Atomic int cache[BASIS_CODEC_AV1 + 1];
    if (codec < BASIS_CODEC_H264 || codec > BASIS_CODEC_AV1) return 0;
    int c = atomic_load(&cache[codec]);
    if (c) return c == 2;
    const char* mime = codec == BASIS_CODEC_H265 ? "video/hevc"
                     : codec == BASIS_CODEC_VP9  ? "video/x-vnd.on2.vp9"
                     : codec == BASIS_CODEC_AV1  ? "video/av01"
                     : "video/avc";
    int ok = 0;
    AMediaCodec* dec = AMediaCodec_createDecoderByType(mime);
    if (dec) { ok = 1; AMediaCodec_delete(dec); }
    atomic_store(&cache[codec], ok ? 2 : 1);
    return ok;
}

basis_decoder_t* basis_decoder_create(basis_media_engine_t* engine) {
    basis_decoder_t* d = (basis_decoder_t*)calloc(1, sizeof(*d));
    if (!d) return NULL;
    d->engine = engine;
    d->video_track = d->audio_track = -1;
    d->lastPtsUs = -1;
    d->vk = basis_vk_create();
    ring_init(&d->pcm, 48000 * 8 * 4); /* ~4s at 8ch — the PTS-gated serve banks
                                        * mux lead + the jitter cushion in the
                                        * ring, so capacity holds both at full
                                        * width */

    pthread_mutex_init(&d->vm, NULL);
    d->lastPresentedPts = INT64_MIN;
    d->presentedPosUs = -1;
    d->vPrerollCutUs = INT64_MIN;
    d->vAwaitKey = 0;
    d->vAwaitKeyPts = INT64_MIN;
    d->vAwaitDrained = 0;
    d->prevWritePts = INT64_MIN;
    d->aResyncInPts = INT64_MIN;
    d->audClockOffsetUs = INT64_MIN;
    d->bufferUs = 120000;
    d->bufferMode = 1;
    d->audioLatencyUs = 60000; /* ~the tap's DSP-buffer figure until the sink reports */
    return d;
}

void basis_decoder_destroy(basis_decoder_t* d) {
    if (!d) return;
    if (d->worker_started) pthread_join(d->worker, NULL);
    basis_decoder_render_release(d);
    if (d->vcodec) { AMediaCodec_stop(d->vcodec); AMediaCodec_delete(d->vcodec); }
    if (d->acodec) { AMediaCodec_stop(d->acodec); AMediaCodec_delete(d->acodec); }
    if (d->extractor) AMediaExtractor_delete(d->extractor);
    /* Release any frames still held in the video ring before the reader they
     * belong to (the worker is already joined, so nothing enqueues concurrently). */
    for (int i = 0; i < VRING; ++i) if (d->vimg[i]) { AImage_delete(d->vimg[i]); d->vimg[i] = NULL; }
    if (d->reader) AImageReader_delete(d->reader);
    if (d->vk) basis_vk_destroy(d->vk);
    pthread_mutex_destroy(&d->vm);
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
            if (d->ach > 0) ring_set_frame(&d->pcm, d->ach, d->asr);
            d->acodec = create_codec_for_track(d, f, 0);
            d->aconfigured = 1;
        }
        AMediaFormat_delete(f);
    }
    if (!d->vcodec) {
        /* No decodable video: audio-only containers (WAV, audio-only MP4) go to
         * the portable demuxers instead, which share the audio-only state
         * handling (PLAYING / unsupported-format error) with the other
         * platforms. Undo whatever the track scan configured and decline the
         * URL so the core falls back. */
        __android_log_print(ANDROID_LOG_INFO, "basis_media",
            "AMediaExtractor found no decodable video track; falling back to JNI HTTPS + portable demuxers");
        if (d->acodec) { AMediaCodec_delete(d->acodec); d->acodec = NULL; }
        d->aconfigured = 0;
        d->asr = 0; d->ach = 0;
        d->video_track = d->audio_track = -1;
        AMediaExtractor_delete(d->extractor); d->extractor = NULL;
        return 0;
    }
    d->vconfigured = 1;

    pthread_create(&d->worker, NULL, url_worker, d);
    d->worker_started = 1;
    return 1; /* took ownership of the URL */
}

int basis_decoder_set_video_format(basis_decoder_t* d, basis_codec_t codec,
                                   const uint8_t* extradata, int extradata_len, int w, int h) {
    if (!d || d->vconfigured) return 0;
    d->vc = codec; if (w > 0) d->vw = w; if (h > 0) d->vh = h;
    const char* mime = (codec == BASIS_CODEC_H265) ? "video/hevc"
                     : (codec == BASIS_CODEC_VP9)  ? "video/x-vnd.on2.vp9"
                     : (codec == BASIS_CODEC_AV1)  ? "video/av01"
                     : "video/avc";

    if (ensure_reader(d, d->vw ? d->vw : 1280, d->vh ? d->vh : 720) != 0) return -1;

    AMediaFormat* fmt = AMediaFormat_new();
    AMediaFormat_setString(fmt, AMEDIAFORMAT_KEY_MIME, mime);
    AMediaFormat_setInt32(fmt, AMEDIAFORMAT_KEY_WIDTH, d->vw ? d->vw : 1280);
    AMediaFormat_setInt32(fmt, AMEDIAFORMAT_KEY_HEIGHT, d->vh ? d->vh : 720);
    if (extradata && extradata_len > 0)
        AMediaFormat_setBuffer(fmt, "csd-0", (void*)extradata, extradata_len); /* Annex-B SPS/PPS(/VPS); AV1 configOBUs */

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

/* Big-endian bit cursor over an AudioSpecificConfig; -1 past the end. */
static int asc_bits(const uint8_t* a, int len, int* pos, int n) {
    int v = 0;
    for (int i = 0; i < n; ++i) {
        int b = (*pos)++;
        if (b >= len * 8) return -1;
        v = (v << 1) | ((a[b >> 3] >> (7 - (b & 7))) & 1);
    }
    return v;
}

/* An AudioSpecificConfig from an MP4 esds can carry a backward-compatible SBR
 * sync extension (0x2b7) with sbrPresentFlag=0 — SBR advertised but absent.
 * C2SoftAacDec rejects a multichannel LC config with that inert tail
 * (aacDecoder_DecodeFrame 0x1001, "Invalid AAC stream" -> substituted silence),
 * while the same audio over TS decodes fine: ADTS framing can't express the
 * extension, so the decoder never sees it. Return the config length to hand the
 * decoder — the 2-byte core for AAC-LC with an inert SBR/PS tail, otherwise the
 * config unchanged so real HE-AAC keeps its SBR signalling and PCE /
 * explicit-rate configs are left alone. */
static int aac_core_asc_len(const uint8_t* asc, int asc_len) {
    if (!asc || asc_len < 2) return asc_len;
    int p = 0;
    if (asc_bits(asc, asc_len, &p, 5) != 2) return asc_len;    /* AAC-LC only */
    if (asc_bits(asc, asc_len, &p, 4) == 15) return asc_len;   /* explicit rate: leave */
    if (asc_bits(asc, asc_len, &p, 4) < 1) return asc_len;     /* channelConfig 0 = PCE: leave */
    /* GASpecificConfig (AAC-LC) */
    asc_bits(asc, asc_len, &p, 1);                             /* frameLengthFlag */
    if (asc_bits(asc, asc_len, &p, 1) == 1)                    /* dependsOnCoreCoder */
        asc_bits(asc, asc_len, &p, 14);                        /* coreCoderDelay */
    asc_bits(asc, asc_len, &p, 1);                             /* extensionFlag (0 for LC) */
    /* Only the byte-aligned two-byte AAC-LC core is trimmed: a set
     * dependsOnCoreCoder pushes p to 30 bits, where (p+7)/8 would keep two bits
     * of the sync extension and hand the decoder a malformed csd-0. */
    if (p != 16) return asc_len;
    int core_len = 2;
    if (asc_bits(asc, asc_len, &p, 11) == 0x2b7 &&             /* SBR sync extension */
        asc_bits(asc, asc_len, &p, 5) == 5 &&                  /* extensionAudioObjectType = SBR */
        asc_bits(asc, asc_len, &p, 1) == 0)                    /* sbrPresentFlag = 0: inert */
        return core_len;
    return asc_len;
}

int basis_decoder_set_audio_format(basis_decoder_t* d, basis_codec_t codec,
                                   int sample_rate, int channels, const uint8_t* asc, int asc_len) {
    if (!d || d->aconfigured) return 0;

    if (codec == BASIS_CODEC_LPCM) {
        /* Decoder bypass, mirroring the Windows lane: no MediaCodec involved —
         * submit_audio converts straight into the ring. The config blob carries
         * the channel-assignment + bits codes, plus an optional flags byte:
         * bit0 = little-endian WAVE-order samples (the RIFF/WAV lane, played at
         * the file rate — the splitter resamples). Blu-ray TS (2-byte config,
         * big-endian) stays 48 kHz only; the TS demuxer pre-filters, this is
         * the matching backstop. 16- or 24-bit only either way. */
        if (channels < 1 || channels > 8 || !asc || asc_len < 2) return 0;
        int le = asc_len >= 3 && (asc[2] & 1);
        if (le ? (sample_rate < 8000 || sample_rate > 96000) : (sample_rate != 48000)) return 0;
        int bits = asc[1] == 1 ? 16 : asc[1] == 3 ? 24 : 0;
        if (!bits) return 0; /* 20-bit unsupported */
        d->ac = BASIS_CODEC_LPCM;
        d->asr = sample_rate; d->ach = channels;
        d->aLpcmAssign = asc[0];
        d->aLpcmBits = bits;
        d->aLpcmLE = le;
        ring_set_frame(&d->pcm, channels, sample_rate);
        d->aconfigured = 1;
        return 0;
    }

    if (codec == BASIS_CODEC_OPUS) {
        /* Native MediaCodec Opus (audio/opus, a required core codec). csd-0 is
         * the OpusHead; C2 Opus decoders also want csd-1 (codec delay / encoder
         * pre-skip in ns) and csd-2 (seek pre-roll, 80 ms in ns) as 8-byte
         * native-endian (LE on arm64) int64 buffers — ExoPlayer's exact shape;
         * older decoders reject a csd-0-only config. The decoder honours the
         * pre-skip itself, so no hand-trim here (unlike the Windows libopus lane).
         * Channel count comes from OpusHead byte 9; Opus always decodes 48 kHz. */
        if (!asc || asc_len < 19 || memcmp(asc, "OpusHead", 8) != 0) return 0;
        int ch = asc[9];
        int preskip = asc[10] | (asc[11] << 8);
        if (ch < 1 || ch > 8) return 0;
        d->ac = BASIS_CODEC_OPUS;
        d->asr = 48000; d->ach = ch;
        ring_set_frame(&d->pcm, ch, 48000);
        AMediaFormat* fmt = AMediaFormat_new();
        AMediaFormat_setString(fmt, AMEDIAFORMAT_KEY_MIME, "audio/opus");
        AMediaFormat_setInt32(fmt, AMEDIAFORMAT_KEY_SAMPLE_RATE, 48000);
        AMediaFormat_setInt32(fmt, AMEDIAFORMAT_KEY_CHANNEL_COUNT, ch);
        AMediaFormat_setBuffer(fmt, "csd-0", (void*)asc, asc_len);
        int64_t csd1 = (int64_t)preskip * 1000000000LL / 48000; /* codec delay ns */
        int64_t csd2 = 80000000LL;                              /* seek pre-roll 80 ms ns */
        AMediaFormat_setBuffer(fmt, "csd-1", &csd1, sizeof(csd1));
        AMediaFormat_setBuffer(fmt, "csd-2", &csd2, sizeof(csd2));
        AMediaCodec* c = AMediaCodec_createDecoderByType("audio/opus");
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

    if (codec == BASIS_CODEC_MP3) {
        /* MediaCodec's audio/mpeg decoder parses the frame headers itself, so no
         * csd is supplied — unlike AAC. Same MediaCodec submit/drain path. */
        d->ac = BASIS_CODEC_MP3;
        d->asr = sample_rate; d->ach = channels;
        ring_set_frame(&d->pcm, channels ? channels : 2, sample_rate);
        AMediaFormat* fmt = AMediaFormat_new();
        AMediaFormat_setString(fmt, AMEDIAFORMAT_KEY_MIME, "audio/mpeg");
        AMediaFormat_setInt32(fmt, AMEDIAFORMAT_KEY_SAMPLE_RATE, sample_rate ? sample_rate : 48000);
        AMediaFormat_setInt32(fmt, AMEDIAFORMAT_KEY_CHANNEL_COUNT, channels ? channels : 2);
        request_full_channel_output(fmt);
        AMediaFormat_setInt32(fmt, "max-input-size", 32768);
        AMediaCodec* c = AMediaCodec_createDecoderByType("audio/mpeg");
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

    if (codec != BASIS_CODEC_AAC) return 0;
    d->ac = BASIS_CODEC_AAC;
    d->asr = sample_rate; d->ach = channels;
    ring_set_frame(&d->pcm, channels ? channels : 2, sample_rate);
    AMediaFormat* fmt = AMediaFormat_new();
    AMediaFormat_setString(fmt, AMEDIAFORMAT_KEY_MIME, "audio/mp4a-latm");
    AMediaFormat_setInt32(fmt, AMEDIAFORMAT_KEY_SAMPLE_RATE, sample_rate ? sample_rate : 48000);
    AMediaFormat_setInt32(fmt, AMEDIAFORMAT_KEY_CHANNEL_COUNT, channels ? channels : 2);
    request_full_channel_output(fmt);
    /* AAC frames reach ~8 KB (13-bit ADTS length); the default input buffer was
     * smaller, so large 5.1 frames were fed truncated and the decoder rejected
     * them (0x4004 -> silence). Give it headroom so whole multichannel frames fit. */
    AMediaFormat_setInt32(fmt, "max-input-size", 32768);
    int csd_len = aac_core_asc_len(asc, asc_len);
    if (csd_len != asc_len)
        __android_log_print(ANDROID_LOG_INFO, "basis_media",
            "AAC config: dropped inert SBR sync extension (%d -> %d bytes)", asc_len, csd_len);
    if (asc && csd_len > 0) AMediaFormat_setBuffer(fmt, "csd-0", (void*)asc, csd_len);
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
    if (!d || !d->vcodec || !annexb || len <= 0) return -1;
    /* First video AU after a seek: flush the codec and release the pre-seek frames
     * in the ring so they can't present ahead of the post-seek content. Demux thread
     * owns vcodec and writes vimg (drain_video_output below), so both are safe here;
     * the frame release mirrors the shutdown path (AImage_delete under vm). */
    int svg = __atomic_load_n(&d->seekGen, __ATOMIC_ACQUIRE);
    if (svg != d->videoSeekGen) {
        d->videoSeekGen = svg;
        AMediaCodec_flush(d->vcodec);
        d->vPrerollCutUs = __atomic_load_n(&d->seekTargetUs, __ATOMIC_ACQUIRE);
        d->vAwaitKey = 1;
        d->vAwaitKeyPts = INT64_MIN;
        d->vAwaitDrained = 0;
        pthread_mutex_lock(&d->vm);
        for (int i = 0; i < VRING; ++i) if (d->vimg[i]) { AImage_delete(d->vimg[i]); d->vimg[i] = NULL; }
        pthread_mutex_unlock(&d->vm);
        /* Frames released to the Surface before the flush can still be in flight
         * through the AImageReader listener and would land after the clear
         * above; on_image drops them by their seek-generation timestamp tag,
         * so no quiescence wait is needed here. */
        /* Publish the generation so the render leg knows the pre-seek frames are gone
         * and it can re-anchor. Releasing frames on this (owning) thread only stops
         * the render leg from deleting post-seek frames drain_video_output re-enqueues. */
        __atomic_store_n(&d->videoSeekAck, svg, __ATOMIC_RELEASE);
    }
    if (key && d->vAwaitKey && d->vAwaitKeyPts == INT64_MIN) d->vAwaitKeyPts = pts_us;
    int rc = -1;
    ssize_t ii = AMediaCodec_dequeueInputBuffer(d->vcodec, 2000);
    if (ii >= 0) {
        size_t cap = 0;
        uint8_t* buf = AMediaCodec_getInputBuffer(d->vcodec, ii, &cap); /* size_t: no sign-cast */
        if (buf && (size_t)len <= cap) {
            memcpy(buf, annexb, (size_t)len);
            rc = (AMediaCodec_queueInputBuffer(d->vcodec, ii, 0, (size_t)len, pts_us, 0) == AMEDIA_OK) ? 0 : -1;
        } else {
            /* NULL buffer or the AU doesn't fit: never queue a partial frame — it
             * decodes to corruption. Release the slot empty and report the drop. */
            AMediaCodec_queueInputBuffer(d->vcodec, ii, 0, 0, pts_us, 0);
        }
    }
    drain_video_output(d);
    return rc;
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
static void submit_lpcm(basis_decoder_t* d, const uint8_t* p, int len, int64_t pts_us) {
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
                int v = d->aLpcmLE ? (int16_t)(s[c * 2] | (s[c * 2 + 1] << 8))
                                   : (int16_t)((s[c * 2] << 8) | s[c * 2 + 1]);
                o[oc] = v / 32768.0f;
            } else {
                int v = d->aLpcmLE ? ((s[c * 3 + 2] << 16) | (s[c * 3 + 1] << 8) | s[c * 3])
                                   : ((s[c * 3] << 16) | (s[c * 3 + 1] << 8) | s[c * 3 + 2]);
                if (v & 0x800000) v -= 0x1000000;
                o[oc] = v / 8388608.0f;
            }
        }
    }
    ring_write(&d->pcm, d->lpcmBuf, floats, pts_us);
}

int basis_decoder_submit_audio(basis_decoder_t* d, const uint8_t* data, int len, int64_t pts_us) {
    if (!d || !data || len <= 0) return -1;
    /* First audio AU after a seek: drop the stale pre-seek ring so this post-seek
     * audio serves immediately, and flush the AAC codec so it doesn't overlap-add
     * across the discontinuity. Demux thread, the only thread that touches acodec. */
    int sg = __atomic_load_n(&d->seekGen, __ATOMIC_ACQUIRE);
    if (sg != d->audioSeekGen) {
        d->audioSeekGen = sg;
        ring_flush(&d->pcm);
        if (d->acodec) AMediaCodec_flush(d->acodec);
        d->aResyncPending = 1;
        d->aResyncInPts = INT64_MIN;
    }
    if (d->ac == BASIS_CODEC_LPCM) { submit_lpcm(d, data, len, pts_us); return 0; }
    if (!d->acodec) return -1;
    int rc = -1;
    ssize_t ii = AMediaCodec_dequeueInputBuffer(d->acodec, 2000);
    if (ii >= 0) {
        size_t cap = 0;
        uint8_t* buf = AMediaCodec_getInputBuffer(d->acodec, ii, &cap);
        if (buf && (size_t)len <= cap) {
            memcpy(buf, data, (size_t)len);
            rc = (AMediaCodec_queueInputBuffer(d->acodec, ii, 0, len, pts_us, 0) == AMEDIA_OK) ? 0 : -1;
            if (rc == 0 && d->aResyncPending) { d->aResyncPending = 0; d->aResyncInPts = pts_us; }
        } else {
            /* Never feed a partial frame — it decodes to an error + silence.
             * max-input-size should prevent this; return the buffer empty if not. */
            AMediaCodec_queueInputBuffer(d->acodec, ii, 0, 0, pts_us, 0);
        }
    }
    drain_audio_output(d);
    return rc;
}

/* ---- render thread + accessors ----------------------------------------- */

/* Runs the presentation clock and hands the due frame's hardware buffer to the
 * Vulkan present. Mirrors the render-thread clock in basis_win_decode.cpp: a
 * wall-rate clock slewed toward the decode edge with a capped correction rate,
 * clamped at edge + buffer so a stall can't run it ahead, a jitter buffer
 * behind the edge that doubles as the audio bank (the PCM serve is gated to
 * this same clock), and the audio-gate offset published for the ring. Render
 * thread. */
static void present_select(basis_decoder_t* d) {
    pthread_mutex_lock(&d->vm);

    int64_t newest = INT64_MIN;
    for (int i = 0; i < VRING; ++i) if (d->vimg[i] && d->vpts[i] > newest) newest = d->vpts[i];

    int paced = basis_engine_is_paced(d->engine) != 0;

    /* Audio-first start (live): with no decodable video yet — a mid-GOP join
     * waits for the next IDR, up to a full GOP — run the presentation clock
     * from the audio delivery edge instead, so audio plays immediately and
     * video joins the already-running clock when its first frame decodes
     * (both tracks share a timeline, so joining needs no re-anchor). The
     * audio edge stands in for `newest` below; the present loop no-ops on an
     * empty frame ring. VOD keeps the primed, synchronised start, and an
     * audio-only stream (video never configured) keeps its ungated serve. */
    int noVideoYet = (newest == INT64_MIN);
    if (noVideoYet) {
        if (!d->vconfigured || !d->aconfigured || paced) { pthread_mutex_unlock(&d->vm); return; }
        newest = ring_newest_pts(&d->pcm);
        if (newest == INT64_MIN) { pthread_mutex_unlock(&d->vm); return; }
    }

    int64_t nowq = now_monotonic_us();
    int64_t interval = d->frameIntervalUs > 0 ? d->frameIntervalUs : 16666;

    /* Hold sizes: with audio, the jitter cushion both streams play behind —
     * the audio serve is gated to the same clock, so the video hold is also
     * the audio bank that absorbs delivery burst/starve cycles. Capped to the
     * ring's frame span so the decoder can't lap the presenter. */
    int64_t pacedBuf = d->aconfigured ? 460000 : 250000;
    {
        int64_t ringSpanCap = (int64_t)(VRING - 6) * interval;
        if (pacedBuf > ringSpanCap) pacedBuf = ringSpanCap;
    }

    /* VOD prime: hold presentation until the ring has banked a hold's worth
     * of frames (3s fallback), so a start against struggling delivery buffers
     * first instead of presenting the first frame, starving, and churning
     * through resyncs. */
    if (paced && !d->clockStarted) {
        if (!d->primeStartUs) d->primeStartUs = nowq;
        int held = 0;
        for (int i = 0; i < VRING; ++i) if (d->vimg[i]) held++;
        if ((int64_t)held * interval < pacedBuf + 2 * interval && nowq - d->primeStartUs < 3000000) {
            pthread_mutex_unlock(&d->vm);
            return;
        }
    }

    if (!d->clockStarted) {
        d->clockStarted = 1;
        d->wallStartUs = nowq;
        d->lastRenderUs = nowq;
        d->mediaStartUs = newest;
        d->lastPresentedPts = INT64_MIN;
    }
    int64_t dtUs = nowq - d->lastRenderUs;
    d->lastRenderUs = nowq;
    if (dtUs < 0) dtUs = 0; else if (dtUs > 1000000) dtUs = 1000000;
    if (dtUs > 1000 && dtUs < 100000) d->renderTickUs += (dtUs - d->renderTickUs) / 8;
    int64_t wallElapsed = nowq - d->wallStartUs;

    /* Presentation clock: wall-rate advance, slewed toward the decode edge
     * with a capped correction rate — 50% during the first ~1.2s after an
     * anchor, ~2% after — so burst error is absorbed by the jitter buffer
     * instead of being chased in slow/fast swings. Clamped at edge + buffer
     * so a delivery stall can't run it ahead (resume would dump the backlog
     * in a skip burst). Large gaps hard-resync; the paced forward threshold
     * scales with the ring span so startup fill is slewed away, not chased. */
    int64_t nowMedia;
    int64_t clk = d->mediaStartUs + wallElapsed;
    int64_t err = newest - clk;
    if (paced) {
        int64_t posLimit = (int64_t)(VRING - 4) * interval;
        if (posLimit < 1000000) posLimit = 1000000;
        if (err > posLimit || err < -1000000) {
            d->wallStartUs = nowq; d->mediaStartUs = newest; d->lastPresentedPts = INT64_MIN;
            clk = newest; wallElapsed = 0;
        } else {
            int64_t corr = err * dtUs / 250000;
            int64_t cap = dtUs / 50;
            if (corr > cap) corr = cap; else if (corr < -cap) corr = -cap;
            d->mediaStartUs += corr; clk += corr;
        }
        int64_t edgeMax = newest + pacedBuf;
        if (clk > edgeMax) { d->mediaStartUs -= clk - edgeMax; clk = edgeMax; }
        nowMedia = clk - pacedBuf;
    } else {
        if (err > 700000 || err < -700000) {
            d->wallStartUs = nowq; d->mediaStartUs = newest; d->lastPresentedPts = INT64_MIN;
            clk = newest; wallElapsed = 0;
        } else {
            int64_t corr = err * dtUs / 250000;
            int64_t cap = (wallElapsed < 1200000) ? dtUs / 2 : dtUs / 50;
            if (corr > cap) corr = cap; else if (corr < -cap) corr = -cap;
            d->mediaStartUs += corr; clk += corr;
        }
        int64_t edgeMax = newest + d->bufferUs;
        if (clk > edgeMax) { d->mediaStartUs -= clk - edgeMax; clk = edgeMax; }

        /* Jitter buffer: capped to the ring span; dynamic mode grows on
         * underrun risk and shrinks when over-buffered. With audio the floor
         * is the shared cushion that banks audio in the ring. */
        int64_t maxBuf = (int64_t)(VRING - 6) * interval; if (maxBuf < 60000) maxBuf = 60000;
        int64_t buf = d->bufferUs;
        int64_t fillUs = newest - (clk - buf);
        if (d->bufferMode == 1) {
            if (fillUs < 2 * interval) buf += interval;
            else if (fillUs > buf + 200000) buf -= 10000;
        }
        int64_t minBuf = d->aconfigured ? 460000 : 40000;
        if (buf < minBuf) buf = minBuf;
        if (buf > maxBuf) buf = maxBuf;
        d->bufferUs = (int)buf;

        /* Fast start (video-only): ramp the cushion so the first frame shows
         * almost immediately. With audio the start is synchronised on the
         * full buffer instead — a sub-1x clock during the ramp would force
         * the PTS-gated audio serve to under-fill every block. */
        int64_t effBuf = (!d->aconfigured && wallElapsed < 1200000) ? (buf * wallElapsed / 1200000) : buf;
        nowMedia = clk - effBuf;
    }
    d->dbg_lagms = (long)((newest - nowMedia) / 1000);

    /* Publish the audio-gate clock as an offset from the monotonic clock. Live
     * low-passes (~2s) to absorb the segment-cadence wobble of the edge lock;
     * paced publishes directly. Large jumps snap so the gate follows resyncs. */
    {
        int64_t off = nowMedia - nowq;
        int64_t prev = __atomic_load_n(&d->audClockOffsetUs, __ATOMIC_RELAXED);
        if (paced || prev == INT64_MIN || off - prev > 700000 || off - prev < -700000)
            __atomic_store_n(&d->audClockOffsetUs, off, __ATOMIC_RELAXED);
        else
            __atomic_store_n(&d->audClockOffsetUs, prev + (off - prev) * dtUs / 2000000, __ATOMIC_RELAXED);
    }

    /* recover from a non-monotonic/bogus PTS leaving lastPresentedPts stuck */
    if (d->lastPresentedPts != INT64_MIN && d->lastPresentedPts > newest) d->lastPresentedPts = INT64_MIN;

    /* Present the latest frame that is due and newer than the last shown; then
     * delete every frame at or before it (consumed), keeping the future ones
     * queued. The due check looks ahead half a render tick so a frame lands on
     * the tick nearest its due time, not the tick after it (due times drift
     * through the tick phase whenever the source rate doesn't divide the
     * refresh rate); capped at half the source frame period so a high-rate
     * source can't be shown a whole frame early. The edge clamp above makes a
     * stalled clock impossible, so no forced-present guard is needed: the ring
     * drains through normal due presents as the clock reaches them. */
    int64_t lookahead = d->renderTickUs / 2;
    if (lookahead > interval / 2) lookahead = interval / 2;
    int64_t dueBy = nowMedia + lookahead;

    int best = -1; int64_t bestPts = d->lastPresentedPts;
    for (int i = 0; i < VRING; ++i)
        if (d->vimg[i] && d->vpts[i] <= dueBy && d->vpts[i] > bestPts) { bestPts = d->vpts[i]; best = i; }

    if (best < 0) {
        d->dbg_nodue++;
        pthread_mutex_unlock(&d->vm);
        return;
    }

    AHardwareBuffer* ahb = NULL;
    int fw = d->vfw[best], fh = d->vfh[best];
    if (AImage_getHardwareBuffer(d->vimg[best], &ahb) == AMEDIA_OK && ahb && d->vk)
        basis_vk_set_hardware_buffer(d->vk, ahb, fw, fh, d->vuv[best]); /* present acquires its own ref */

    d->lastPresentedPts = bestPts;
    __atomic_store_n(&d->presentedPosUs, bestPts, __ATOMIC_RELAXED);
    d->dbg_render++;

    for (int i = 0; i < VRING; ++i)
        if (d->vimg[i] && d->vpts[i] <= bestPts) { AImage_delete(d->vimg[i]); d->vimg[i] = NULL; }

    pthread_mutex_unlock(&d->vm);
}

int basis_decoder_render_update(basis_decoder_t* d) {
    if (!d || !d->vk) return -1;
    if (basis_engine_is_paused(d->engine)) return 0;
    /* First render after a seek: reset the present clock so present_select re-locks
     * it to the first post-seek frame instead of staying clamped to the stale decode
     * edge (a video/clock freeze on a cold forward seek). Render-thread-owned clock;
     * the frame ring is cleared by the demux thread that owns it (submit_video), and
     * this leg waits on that clear (videoSeekAck) before selecting a frame. */
    int rsg = __atomic_load_n(&d->seekGen, __ATOMIC_ACQUIRE);
    if (rsg != d->renderSeekGen) {
        d->renderSeekGen = rsg;
        d->clockStarted = 0;
        d->primeStartUs = 0;
        d->lastPresentedPts = INT64_MIN;
        d->mediaStartUs = 0;
        /* Only with video: report the target so get_position_us tracks before the
         * first post-seek frame presents (present_select overwrites it once one does).
         * Audio-only has no present to advance a pinned value, so leaving it set would
         * freeze the position at the target — leave it unset and let the audio-front
         * settle in get_position_us own the position (matches basis_decoder_seek). */
        if (d->vcodec)
            __atomic_store_n(&d->presentedPosUs,
                             __atomic_load_n(&d->seekTargetUs, __ATOMIC_ACQUIRE), __ATOMIC_RELAXED);
    }
    /* Hold frame selection until the demux thread has flushed the codec and released
     * the pre-seek frames. Selecting before then would show a stale frame, and
     * releasing them here would race the producer and could delete post-seek frames
     * it has already enqueued (notably when seeking while paused). Keep rendering so
     * the last frame stays up during the hold. */
    if (__atomic_load_n(&d->videoSeekAck, __ATOMIC_ACQUIRE) == rsg) {
        present_select(d);
    }
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
    if (!d) return -1;
    /* Report the display (crop) size, not the coded buffer, so the Unity RT is
     * sized to the visible region — no pad rows, exact aspect. The crop is only
     * known once the first frame decodes; until then decline so C# keeps polling
     * and latches the RT on the display size rather than the coded one. */
    uint64_t wh = __atomic_load_n(&d->dispWH, __ATOMIC_RELAXED);
    int dw = (int)(uint32_t)(wh >> 32), dh = (int)(uint32_t)wh;
    if (dw <= 0 || dh <= 0) return -1;
    if (w) *w = dw; if (h) *h = dh; return 0;
}
/* The Vulkan resolve always flips to upright via a negative-height viewport, so
 * the published frame is bottom-left origin on every Android GPU. */
int basis_decoder_get_frame_origin(basis_decoder_t* d) { (void)d; return 0; }
/* Presentation position once a frame has shown; decode edge before that
 * (start-up, audio-only) so early consumers still see the clock move. */
void basis_decoder_seek(basis_decoder_t* d, int64_t target_us) {
    if (!d) return;
    /* Record the pre-seek audio front before the flush clears it, so the audio-only
     * settle can tell post-seek audio (near the target) from a stale pre-seek frame
     * that slipped the drop (near this origin) — see get_position_us. A rapid re-seek
     * with the ring already empty falls back to the prior target (where we were). */
    pthread_mutex_lock(&d->pcm.m);
    int64_t from = d->pcm.playedUs;
    pthread_mutex_unlock(&d->pcm.m);
    d->seekFromUs = from != INT64_MIN ? from : __atomic_load_n(&d->seekTargetUs, __ATOMIC_ACQUIRE);
    /* Drop any pre-seek PCM still queued so the audio callback stops serving it
     * immediately rather than up to the next audio AU. ring_flush takes the pcm
     * mutex, safe from this (caller) thread; the codec reset stays on the submit
     * thread where the decoder is owned. */
    ring_flush(&d->pcm);
    /* Invalidate the audio serve clock: it re-derives from presents, and until the
     * first post-seek frame presents it still describes the pre-seek timeline. On a
     * backward seek that stale (higher) clock reads freshly banked post-target audio
     * as long-stale and the serve trims it away — eating the first second of audio
     * after video resumes. INT64_MIN is the serve's hold state (a stream with video
     * holds audio until the clock exists), so post-seek audio banks through the
     * settle and releases in sync with the first presented frame. Audio-only stays
     * ungated: its offset never leaves INT64_MIN in the first place. */
    __atomic_store_n(&d->audClockOffsetUs, INT64_MIN, __ATOMIC_RELAXED);
    /* Latch the target before bumping the generation so any leg that sees the new
     * generation reads the matching target. */
    __atomic_store_n(&d->seekTargetUs, target_us, __ATOMIC_RELEASE);
    if (d->vcodec) {
        /* Video present: snap the presentation clock to the target so the seek bar
         * shows the target immediately, before the first post-seek frame presents. */
        __atomic_store_n(&d->presentedPosUs, target_us, __ATOMIC_RELAXED);
    } else {
        /* Audio-only: no frame ever presents, so nothing would advance a pinned
         * presentedPosUs and get_position_us (which returns it whenever >= 0) would
         * freeze at the target. Leave it unset so get_position_us reports the audio
         * front (playedUs), and mark the position settling: the ring was just
         * flushed, but a pre-seek AU decoded in the window before the demuxer
         * repositions can still drain a stale chunk into it, which would bounce the
         * reported position (and the seek bar) to the old spot. get_position_us holds
         * at the target through the settle until post-seek audio serves near it — the
         * audio mirror of the video present clock re-anchoring to the target. */
        __atomic_store_n(&d->presentedPosUs, -1, __ATOMIC_RELAXED);
        d->audioSettling = 1;
    }
    __atomic_add_fetch(&d->seekGen, 1, __ATOMIC_RELEASE);
}

int64_t basis_decoder_get_position_us(basis_decoder_t* d) {
    if (!d) return -1;
    int64_t presented = __atomic_load_n(&d->presentedPosUs, __ATOMIC_RELAXED);
    if (presented >= 0) return presented;
    if (d->lastPtsUs >= 0) return d->lastPtsUs;
    /* Audio-only: no video ever presents, so report the audio playback front.
     * Through a post-seek settle, hold at the target until served audio lands nearer
     * the target than the pre-seek origin. The core drops pre-seek audio before the
     * ring, but that drop is not airtight (a frame can slip the seek-generation
     * visibility window); latching on the first served sample would then report ~the
     * pre-seek position. A slipped frame sits near the origin and post-seek audio near
     * the target, so "nearer target than origin" rejects the former at any seek size —
     * unlike a fixed proximity window, which a slip within it (a short seek) defeats. */
    pthread_mutex_lock(&d->pcm.m);
    int64_t played = d->pcm.playedUs;
    pthread_mutex_unlock(&d->pcm.m);
    if (d->audioSettling) {
        int64_t target = __atomic_load_n(&d->seekTargetUs, __ATOMIC_ACQUIRE);
        if (played != INT64_MIN) {
            int64_t from = d->seekFromUs;
            uint64_t dTarget = played >= target ? (uint64_t)played - (uint64_t)target : (uint64_t)target - (uint64_t)played;
            uint64_t dFrom   = played >= from   ? (uint64_t)played - (uint64_t)from   : (uint64_t)from   - (uint64_t)played;
            if (dTarget <= dFrom) { d->audioSettling = 0; return played; }
        }
        return target;
    }
    return played != INT64_MIN ? played : -1;
}
int basis_decoder_get_audio_format(basis_decoder_t* d, int* r, int* c) {
    if (!d || !d->aconfigured) return -1; if (r) *r = d->asr ? d->asr : 48000; if (c) *c = d->ach ? d->ach : 2; return 0;
}
int basis_decoder_read_audio(basis_decoder_t* d, float* out, int max) {
    if (!d) return 0;
    if (basis_engine_is_paused(d->engine)) return 0;
    /* Reconstruct the presentation clock from the published offset and serve
     * against it, biased forward by the sink's output latency so release-now
     * lands on the clock at the speaker. Before the clock exists, a stream
     * with video holds audio — on live that is only until the next render
     * tick bootstraps the clock from the audio edge (audio-first start; on
     * VOD, until the prime releases), so playout can never free-run on a
     * timeline the clock won't match. Audio-only streams read ungated. */
    int64_t target = INT64_MIN;
    int64_t off = __atomic_load_n(&d->audClockOffsetUs, __ATOMIC_RELAXED);
    if (off != INT64_MIN) {
        target = now_monotonic_us() + off + d->audioLatencyUs;
    } else if (d->vconfigured) {
        return 0;
    }
    /* Hysteresis must exceed the sink's pull depth (it drains several DSP
     * blocks back-to-back); the reported output latency is that depth plus
     * headroom, so size the hold from it. */
    int64_t hold = 60000 + d->audioLatencyUs;
    return ring_read(&d->pcm, out, max, target, hold);
}
int basis_decoder_get_debug(basis_decoder_t* d, char* buf, int size) {
    if (!d || !buf || size <= 0) return 0;
    int vq = 0;
    pthread_mutex_lock(&d->vm);
    for (int i = 0; i < VRING; ++i) if (d->vimg[i]) vq++;
    pthread_mutex_unlock(&d->vm);
    int aq = ring_fill_ms(&d->pcm);
    int srr = d->pcm.sr > 0 ? d->pcm.sr : 48000;
    int frm = d->pcm.frame > 0 ? d->pcm.frame : 2;
    int atrimms = (int)((int64_t)(d->pcm.lastTrimFloats / frm) * 1000 / srr);
    /* vq = video frames held; aq = audio queued (ms); atrim = clock-gated trims
     * fired; lag = live edge minus present clock. Video keys (render/nodue/lag/
     * buf/mode/acq) are also parsed into the diagnostics CSV. */
    return snprintf(buf, (size_t)size,
                    "render=%ld nodue=%ld acq=%ld drop=%ld lag=%ldms buf=%dms mode=%d vq=%d aq=%dms atrim=%ld atrimms=%d alat=%dms | acfg=%d asr=%d ach=%d vw=%d vh=%d",
                    d->dbg_render, d->dbg_nodue, d->dbg_acqfail, d->dbg_drop, d->dbg_lagms,
                    d->bufferUs / 1000, d->bufferMode, vq, aq, d->pcm.trims, atrimms, d->audioLatencyUs / 1000,
                    d->aconfigured, d->asr, d->ach, d->vw, d->vh);
}
void basis_decoder_set_buffer(basis_decoder_t* d, int mode, int ms) {
    if (!d) return;
    d->bufferMode = (mode != 0) ? 1 : 0;
    if (ms > 0) d->bufferUs = ms * 1000;
}

/* The managed audio sink reports its measured output latency; it biases the
 * audio serve target forward so samples released now come due exactly when
 * they reach the speaker. Clamped to a sane range. */
void basis_decoder_set_audio_latency(basis_decoder_t* d, int latency_us) {
    if (!d) return;
    if (latency_us < 0) latency_us = 0;
    else if (latency_us > 500000) latency_us = 500000;
    d->audioLatencyUs = latency_us;
}

void basis_decoder_set_output_texture(basis_decoder_t* d, void* native_texture, int w, int h) {
    if (d && d->vk) basis_vk_set_output_texture(d->vk, native_texture, w, h);
}
