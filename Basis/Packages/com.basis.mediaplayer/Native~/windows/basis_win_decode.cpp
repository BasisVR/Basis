/*
 * basis_win_decode.cpp — Windows OS-codec backend (implements basis_decoder_*).
 *
 * Pipeline:
 *   submit_video (demux thread): feed video AUs (Annex-B H.264/H.265, raw
 *     VP9/AV1 samples) to the Media Foundation decoder MFT running on a DXVA-enabled
 *     D3D11 device. Decoded NV12 is
 *     converted to BGRA by an ID3D11VideoProcessor into a keyed-mutex *shared*
 *     texture on the decode device.
 *   render_update (render thread): copy the shared BGRA into the Unity-visible
 *     output texture (created on Unity's device) under the keyed mutex.
 *   submit_audio: feed raw AAC to the AAC decoder MFT -> float PCM -> ring.
 *
 * Graphics targets:
 *   D3D11 — output texture created on Unity's ID3D11Device (BGRA). Primary path.
 *   D3D12 — present copies the due ring slot into a TYPELESS shared texture on the
 *           decode device; Unity opens that on its ID3D12Device and wraps it with
 *           CreateExternalTexture. Typeless is required so Unity can cast the sRGB
 *           SRV its (linear) colour space needs — a typed UNORM resource rejects
 *           sRGB SRV creation under D3D12. The render thread polls an event query until
 *           the copy retires on the GPU and only publishes once completion is confirmed
 *           and Unity holds the external texture, so Unity never samples a half-written
 *           or absent copy. A keyed mutex orders the decode-write against Unity's read.
 *
 * Notes / iterate-here:
 *   - Uses a synchronous (DXVA) decoder MFT via MFTEnumEx. Async hardware MFTs
 *     (event-driven) would lower latency further but need METransform* handling.
 *   - HEVC requires an installed HEVC decoder MFT (HEVC Video Extensions);
 *     VP9 and AV1 likewise (Store "VP9 Video Extensions" / "AV1 Video
 *     Extension", or a vendor MFT).
 */

#include "../basis_media_internal.h"

#include <windows.h>
#include <d3d11.h>
#include <d3d11_1.h>
#include <d3d11_4.h>
#include <d3d12.h>
#include <dxgi1_2.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mftransform.h>
#include <mferror.h>
#include <wmcodecdsp.h>
#include <mmreg.h>
#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <mutex>

#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mfuuid.lib")
#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")

#ifndef SAFE_RELEASE
#define SAFE_RELEASE(p) do { if (p) { (p)->Release(); (p) = nullptr; } } while (0)
#endif

/* ---- PCM ring ----------------------------------------------------------- */

/* Interleaved float FIFO with per-chunk PTS metadata. The producer (decode
 * thread) writes decoded chunks tagged with their media timestamps; the
 * consumer (Unity audio thread) reads gated against the presentation clock so
 * audio release is paced to the same timeline video presents on. All drops are
 * whole-frame multiples — a non-frame-sized drop would permanently rotate the
 * channel order for every consumer downstream. */
struct PcmRing {
    float* buf = nullptr;
    int cap = 0;     /* in floats */
    int head = 0, tail = 0;
    int frame = 1;   /* floats per interleaved frame (channel count) */
    int sr = 48000;  /* sample rate, for chunk durations */
    CRITICAL_SECTION cs;

    static const int CHUNKS = 1024;
    struct Chunk { int64_t pts; int floats; };
    Chunk chunks[CHUNKS] = {};
    int chead = 0, ccount = 0;
    long trims = 0;  /* clock-gated trims fired (diagnostics) */
    int64_t playedUs = INT64_MIN;  /* PTS served up to = the audio playback front;
                                    * the position for an audio-only stream that
                                    * never presents a video frame. */

    /* Serving is gated on media time: a sample is released when its PTS comes
     * due against the serve target (presentation clock + the consumer's output
     * latency, so alignment lands at the speaker). Surplus the mux delivered
     * early waits in the ring instead of becoming output latency, and a source
     * that delivers just-in-time banks a cushion behind the video hold instead
     * of running dry. early_hold_us is serve hysteresis: chunks up to that
     * far ahead of the target still release, so consumer pull batching is
     * absorbed without gaps and steady-state serve stays sequential. The
     * caller sizes it above the sink's pull depth — Unity's audio thread
     * pulls several DSP blocks back-to-back, and a hysteresis smaller than
     * that batch leaves the batch's last block with nothing due (a one-block
     * silent pop on an otherwise healthy queue). A head further than
     * TRIM_LATE overdue (connect burst, post-stall backlog, PTS jump) is
     * trimmed to the target — re-anchoring on the discontinuity rather than
     * discarding real-time delivery forever. */
    static const int64_t TRIM_LATE_US = 150000;

    void init(int floats) { cap = floats; buf = (float*)malloc(sizeof(float) * cap); InitializeCriticalSection(&cs); }
    void destroy() { free(buf); buf = nullptr; DeleteCriticalSection(&cs); }

    int fill() const { return (tail - head + cap) % cap; }

    /* PTS just past the newest queued sample — the audio delivery edge.
     * INT64_MIN when empty. */
    int64_t newest_pts() {
        EnterCriticalSection(&cs);
        int64_t r = INT64_MIN;
        if (ccount > 0) {
            Chunk& c = chunks[(chead + ccount - 1) % CHUNKS];
            r = c.pts + (int64_t)(c.floats / (frame > 0 ? frame : 1)) * 1000000LL / (sr > 0 ? sr : 48000);
        }
        LeaveCriticalSection(&cs);
        return r;
    }

    /* Drops the oldest `n` floats (rounded down to whole frames) from the float
     * ring and the chunk metadata together. Caller holds cs. */
    void drop_oldest(int n) {
        n -= n % frame;
        int avail = fill();
        if (n > avail) n = avail - (avail % frame);
        if (n <= 0) return;
        head = (head + n) % cap;
        while (n > 0 && ccount > 0) {
            Chunk& c = chunks[chead];
            if (c.floats <= n) { n -= c.floats; chead = (chead + 1) % CHUNKS; ccount--; }
            else {
                c.floats -= n;
                c.pts += (int64_t)(n / frame) * 1000000LL / (sr > 0 ? sr : 48000);
                n = 0;
            }
        }
    }

    void write(const float* s, int n, int64_t pts) {
        if (n <= 0) return;
        EnterCriticalSection(&cs);
        if (n > cap - 1) {
            /* Drop the oldest whole frames of an over-capacity write and carry
             * the timestamp forward, so the retained tail keeps a correct PTS
             * and the channel order isn't rotated by a sub-frame trim. */
            int keep = (cap - 1) - ((cap - 1) % frame);
            int drop = n - keep;
            s += drop;
            pts += (int64_t)(drop / frame) * 1000000LL / (sr > 0 ? sr : 48000);
            n = keep;
        }
        int space = cap - 1 - fill();
        if (n > space) {
            int need = (n - space) + frame - 1;
            drop_oldest(need - need % frame);
        }
        for (int i = 0; i < n; ++i) { buf[tail] = s[i]; tail = (tail + 1) % cap; }
        if (ccount == CHUNKS) {
            chunks[(chead + ccount - 1) % CHUNKS].floats += n;
        } else {
            Chunk& c = chunks[(chead + ccount) % CHUNKS];
            c.pts = pts; c.floats = n; ccount++;
        }
        LeaveCriticalSection(&cs);
    }

    /* target_us = INT64_MIN reads ungated (audio-only stream, no clock). */
    int read(float* out, int n, int64_t target_us, int64_t early_hold_us) {
        EnterCriticalSection(&cs);
        int64_t srr = sr > 0 ? sr : 48000;
        if (target_us != INT64_MIN && ccount > 0) {
            int64_t late = target_us - chunks[chead].pts;
            if (late > TRIM_LATE_US) {
                drop_oldest((int)(late * srr / 1000000LL) * frame);
                trims++;
            }
        }
        int got = 0;
        int64_t frontPts = (ccount > 0) ? chunks[chead].pts : INT64_MIN;
        while (got < n && ccount > 0) {
            Chunk& c = chunks[chead];
            if (target_us != INT64_MIN && c.pts > target_us + early_hold_us) break;
            int take = c.floats < n - got ? c.floats : n - got;
            for (int i = 0; i < take; ++i) { out[got + i] = buf[head]; head = (head + 1) % cap; }
            got += take;
            if (take == c.floats) { chead = (chead + 1) % CHUNKS; ccount--; }
            else {
                c.floats -= take;
                c.pts += (int64_t)take * 1000000LL / (frame * srr);
            }
        }
        /* Publish the playback front so an audio-only stream has a position.
         * Only when samples actually served: a gated read that breaks before
         * copying leaves the front unserved, so its PTS isn't yet "played". */
        if (frontPts != INT64_MIN && got > 0)
            playedUs = frontPts + (int64_t)(got / (frame > 0 ? frame : 1)) * 1000000LL / srr;
        LeaveCriticalSection(&cs);
        return got;
    }

    /* Drop everything buffered. Used on a seek so pre-seek chunks can neither
     * gate the ring (a backward seek leaves front chunks whose PTS is ahead of
     * the target, which block the newer post-seek audio queued behind them) nor
     * play out ahead of the post-seek audio that replaces them. */
    void flush() {
        EnterCriticalSection(&cs);
        head = 0; tail = 0;
        chead = 0; ccount = 0;
        playedUs = INT64_MIN;
        LeaveCriticalSection(&cs);
    }
};

/* ---- decoder ------------------------------------------------------------ */

struct basis_decoder {
    basis_media_engine_t* engine = nullptr;

    /* decode device (DXVA) */
    ID3D11Device* devDec = nullptr;
    ID3D11DeviceContext* ctxDec = nullptr;
    UINT resetToken = 0;
    IMFDXGIDeviceManager* devMgr = nullptr;

    /* video */
    IMFTransform* vdec = nullptr;
    basis_codec_t vcodec = BASIS_CODEC_NONE;
    int vwidth = 0, vheight = 0;         /* coded (decoder surface) size */
    int dispX = 0, dispY = 0;            /* clean-aperture offset within the coded surface */
    int dispW = 0, dispH = 0;            /* clean-aperture (visible) size; 0 = none, use coded */
    bool vconfigured = false;

    /* AV1 configOBUs, held until the first AU and prepended to it (a duplicated
     * sequence header is legal OBU syntax; a config-only input sample is of
     * unverified MFT tolerance). Cleared once consumed. */
    uint8_t vConfigObus[2048];
    int vConfigObusLen = 0;

    ID3D11VideoDevice* vdevice = nullptr;
    ID3D11VideoContext* vcontext = nullptr;
    ID3D11VideoProcessor* vproc = nullptr;
    ID3D11VideoProcessorEnumerator* vprocEnum = nullptr;

    /* Ring of BGRA shared buffers (decode device). The decode producer writes
     * round-robin; the render consumer presents frames on a PTS clock so bursty
     * decode delivery is smoothed into steady, framerate-accurate output. Each
     * buffer is a keyed-mutex shared resource (key 0 = free). */
    /* Frame ring. Sized so a normal jitter buffer fits in frames even at very high
     * source rates (32 frames = 533ms @60fps, 128ms @250fps). Present picks the
     * freshest due slot, so the ring only needs to span buffer + decode headroom. */
    static const int RING = 32;
    ID3D11Texture2D* ringTex[RING] = {};
    IDXGIKeyedMutex* ringMutexDec[RING] = {};
    ID3D11VideoProcessorOutputView* ringVpOut[RING] = {};
    HANDLE ringHandle[RING] = {};
    ID3D11Texture2D* ringOnUnity[RING] = {};
    IDXGIKeyedMutex* ringMutexUnity[RING] = {};
    int64_t ringPts[RING] = {};          /* PTS (us) of the frame in each slot; INT64_MIN = empty */
    int sharedW = 0, sharedH = 0;
    volatile LONGLONG writeSeq = 0;   /* total frames written by the producer */

    /* present clock (render thread) */
    LARGE_INTEGER qpcFreq = {};
    bool clockStarted = false;
    LONGLONG primeStartQpc = 0;          /* first render tick with a frame (VOD prime window) */
    LONGLONG wallStartQpc = 0;
    LONGLONG lastRenderQpc = 0;
    int64_t mediaStartUs = 0;
    int64_t renderTickUs = 16667;        /* EMA of the render callback period (display refresh when vsync'd) */
    int64_t lastPresentedPts = INT64_MIN;
    /* Stable presentation position for get_position_us: unlike lastPresentedPts
     * it survives the resync sentinel resets, and unlike lastPtsUs (decode-side)
     * it freezes with presentation — a paused/stopped player reads as holding
     * still even while the demuxer keeps feeding the ring. */
    volatile LONG64 presentedPosUs = -1;

    /* audio-master sync: pace video to audio Unity has actually consumed. */
    int64_t videoBasePts = INT64_MIN;        /* PTS of the first video frame (sync origin) */
    volatile LONGLONG audioSamplesRead = 0;  /* per-channel samples pulled by the audio thread */

    /* jitter buffer (how far behind live we present): selectable + dynamic. */
    volatile LONG bufferUs = 120000;         /* current buffer in microseconds */
    volatile LONG bufferMode = 1;            /* 0 = fixed (use bufferUs), 1 = dynamic (auto-tune) */

    /* Unity output texture (single; consumer copies the due ring buffer into it) */
    basis_gfx_api_t api = BASIS_GFX_NONE;
    ID3D11Device* devUnity = nullptr;       /* D3D11 path */
    ID3D11DeviceContext* ctxUnity = nullptr;
    ID3D11Texture2D* outTexD11 = nullptr;   /* CreateExternalTexture target (D3D11) */
    void* outTexD12 = nullptr;              /* ID3D12Resource* (D3D12 path) */
    ID3D11Texture2D* outSharedD12 = nullptr;       /* D3D12 path: typeless shared copy target (decode device) */
    IDXGIKeyedMutex* outSharedD12Mutex = nullptr;
    HANDLE outSharedD12Handle = nullptr;
    ID3D11Query* presentQuery = nullptr;           /* D3D12 path: event query — polled to copy completion */
    int d12OpenFail = 0;                           /* consecutive D3D12 OpenSharedHandle failures (render thread) */

    /* Vertical origin of the published frame: 0 = bottom-left (upright; Unity
     * samples it with no UV flip), 1 = top-left (consumer must flip V). Set once
     * when the video processor is created — 0 if its stream-mirror was actually
     * applied, 1 if this GPU's VP lacks mirror support so the frame stays
     * un-flipped and the consumer corrects it. Defaults to upright (no surprise
     * flip) before the first frame. */
    volatile LONG frameTopLeft = 0;

    volatile LONG frameCounter = 0;
    int64_t lastPtsUs = -1;
    int64_t prevWritePts = INT64_MIN;        /* last frame PTS written to the ring */
    int64_t frameIntervalUs = 0;             /* EMA of inter-frame PTS delta (source frame period) */
    LARGE_INTEGER createQpc = {};            /* engine open time (for time-to-first-frame) */
    volatile LONG ttffMs = -1;               /* ms from open to first presented frame */
    CRITICAL_SECTION presentLock;

    /* debug counters */
    volatile LONG dbg_in_ok = 0, dbg_in_rej = 0, dbg_out = 0, dbg_blit = 0, dbg_drop = 0;
    volatile LONG dbg_render = 0, dbg_copy = 0;
    volatile LONG dbg_acqfail = 0, dbg_nodue = 0, dbg_lagms = 0;

    /* audio */
    IMFTransform* adec = nullptr;
    basis_codec_t acodec = BASIS_CODEC_NONE;
    int asr = 0, ach = 0, aobj = 2;
    int achSrc = 0;                 /* source-declared channel count; the repick
                                     * target (ach tracks the *chosen* output) */
    int aBits = 32;                 /* output sample bits: 32=float, 16=PCM int */
    bool aconfigured = false;

    /* LPCM bypass (no decoder): convert/reorder straight into the PCM ring. */
    int aLpcmAssign = 0;            /* Blu-ray channel_assignment */
    int aLpcmBits = 16;
    int aLpcmLE = 0;                /* 1 = little-endian samples (RIFF/WAV lane) */
    float* aLpcmBuf = nullptr;      /* reusable convert buffer */
    int aLpcmBufCap = 0;            /* in floats */

    /* Opus (libopus via the opussharp-shipped DLL, resolved at runtime — §4-b2).
     * Decode float straight into the ring like the LPCM bypass. */
    void* opusDec = nullptr;        /* OpusDecoder* or OpusMSDecoder* */
    int opusIsMS = 0;               /* 1 = multistream decoder (mapping family != 0) */
    int opusMappingFamily = 0;      /* OpusHead channel-mapping family */
    int opusPreSkip = 0;            /* encoder pre-skip frames still to drop */
    float* opusBuf = nullptr;       /* reusable decode buffer (interleaved float) */
    int opusBufCap = 0;             /* in floats */

    volatile LONG dbg_aout = 0;     /* AAC PCM outputs produced */
    PcmRing pcm;
    int64_t aPtsFallback = 0;       /* next chunk PTS when MF gives no sample time */

    /* Audio-gate clock: media-time offset from QPC, low-passed (~2s) so the
     * segment-cadence wobble of the live-edge lock (bursty transports advance
     * `newest` in jumps) averages out before the audio anchor reads it. The
     * audio thread reconstructs `now` as qpc_us + offset. INT64_MIN = clock
     * not started (audio holds for the synchronised start when the stream has
     * video; reads ungated on audio-only streams). */
    volatile LONGLONG audClockOffsetUs = INT64_MIN;

    /* Managed sink output latency (µs), reported via set_audio_latency: with
     * the per-DSP-block tap this is ~the DSP buffer. Biases the audio serve
     * target forward so samples released now come due exactly when they reach
     * the speaker. */
    volatile LONG audLatencyUs = 60000;

    /* Seek notification. basis_decoder_seek bumps seekGen (+ latches the target)
     * on the caller thread. Each consumer leg keeps its own last-seen copy and,
     * when it differs, flushes its stale buffers and re-anchors on ITS OWN thread:
     * the audio-submit (demux) thread flushes the PCM ring + the MF/Opus decoder;
     * the video-submit (demux) thread flushes the video MFT (drops its reorder
     * buffer so retained pre-seek frames can't repopulate the ring) and clears the
     * frame ring — it owns vdec and writes the ring; the render thread re-anchors
     * the present clock and also clears the ring so a stale frame can't present in
     * the window before the next video AU arrives. Nothing is touched across threads. */
    volatile LONG   seekGen = 0;
    volatile LONG64 seekTargetUs = 0;
    int64_t seekFromUs = 0;   /* pre-seek audio front, for the audio-only settle (main thread only) */
    LONG audioSeekGen = 0;    /* audio-submit (demux) thread only */
    LONG videoSeekGen = 0;    /* video-submit (demux) thread only */
    LONG renderSeekGen = 0;   /* render thread only */
    int  audioSettling = 0;   /* audio-only position: hold get_position at the seek target
                               * until post-seek audio serves near it (main thread only) */
    volatile LONG videoSeekAck = 0; /* demux publishes seekGen here once it has flushed
                                     * vdec + dropped pre-seek frames; the render leg
                                     * holds until it matches so it neither anchors to a
                                     * stale frame nor races the producer's ring clear */
    int64_t vPrerollCutUs = INT64_MIN; /* video-submit thread only: after a seek, decoded
                                        * frames short of this are the keyframe run-up to
                                        * the target — reference-only, never banked. Set
                                        * from seekTargetUs at the seek flush, cleared by
                                        * the first frame at or past it. */
    int vAwaitKey = 0;                 /* video-submit thread only: set at the seek flush,
                                        * cleared by the first OUTPUT of the first keyframe
                                        * submitted after it (matched by PTS via
                                        * vAwaitKeyPts). Output produced before that is
                                        * post-flush mid-GOP input decoded against stale
                                        * references (the HLS path can still emit a pre-seek
                                        * tail AU the engine's seek_taken gate doesn't
                                        * cover) — never banked, and it must not end the
                                        * preroll run-up either. Clearing at the keyframe's
                                        * SUBMISSION is not enough: the MFT's reorder
                                        * pipeline can emit a tail AU's garbage frame after
                                        * it, with a pre-seek PTS past the target. */
    int64_t vAwaitKeyPts = INT64_MIN;  /* video-submit thread only: PTS of that keyframe;
                                        * INT64_MIN until it is submitted */
    int vAwaitDrained = 0;             /* video-submit thread only: outputs drained since
                                        * the seek flush; bounds the wait so a dropped or
                                        * re-stamped keyframe output — or a run whose
                                        * post-seek AUs are never flagged as keyframes,
                                        * which would otherwise never latch vAwaitKeyPts —
                                        * can't hold the gate (and video) shut until the
                                        * next seek */
};

/* ---- D3D / MF helpers --------------------------------------------------- */

static bool create_decode_device(basis_decoder* d) {
    UINT flags = D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_BGRA_SUPPORT;
    D3D_FEATURE_LEVEL fl[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
    HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags,
                                   fl, 2, D3D11_SDK_VERSION, &d->devDec, nullptr, &d->ctxDec);
    if (FAILED(hr)) return false;

    ID3D11Multithread* mt = nullptr;
    if (SUCCEEDED(d->devDec->QueryInterface(__uuidof(ID3D11Multithread), (void**)&mt))) {
        mt->SetMultithreadProtected(TRUE);
        mt->Release();
    }

    /* D3D12 present-copy completion query (decode device). The render thread polls it
     * to GPU completion so the copied frame is fully written before it is published to
     * Unity's D3D12 device, which has no other handoff sync — Flush alone does not wait.
     * End() re-arms it each present, so a timed-out wait leaves no stale state. It is the
     * only completion primitive on this path, so a creation failure is fatal for D3D12. */
    if (d->api == BASIS_GFX_D3D12) {
        D3D11_QUERY_DESC qd = {}; qd.Query = D3D11_QUERY_EVENT;
        if (FAILED(d->devDec->CreateQuery(&qd, &d->presentQuery)) || !d->presentQuery) {
            basis_engine_set_error(d->engine, "failed to create D3D12 present completion query");
            return false;
        }
    }

    hr = MFCreateDXGIDeviceManager(&d->resetToken, &d->devMgr);
    if (FAILED(hr)) return false;
    hr = d->devMgr->ResetDevice(d->devDec, d->resetToken);
    if (FAILED(hr)) return false;

    d->devDec->QueryInterface(__uuidof(ID3D11VideoDevice), (void**)&d->vdevice);
    d->ctxDec->QueryInterface(__uuidof(ID3D11VideoContext), (void**)&d->vcontext);
    return d->vdevice && d->vcontext;
}

/* FCC('AV01') media subtype, defined locally so header vintage doesn't gate the
 * build (MFVideoFormat_AV1 only exists in recent SDK headers). */
static const GUID kMFVideoFormatAV1 = {0x31305641,0x0000,0x0010,{0x80,0x00,0x00,0xAA,0x00,0x38,0x9B,0x71}};

static const GUID* video_subtype(basis_codec_t c) {
    if (c == BASIS_CODEC_H265) return &MFVideoFormat_HEVC;
    if (c == BASIS_CODEC_VP9)  return &MFVideoFormat_VP90;
    if (c == BASIS_CODEC_AV1)  return &kMFVideoFormatAV1;
    return &MFVideoFormat_H264;
}

/* Finds a synchronous (DXVA-capable) decoder MFT for the codec. */
static IMFTransform* create_video_mft(basis_codec_t codec) {
    MFT_REGISTER_TYPE_INFO inType = { MFMediaType_Video, *video_subtype(codec) };
    IMFActivate** acts = nullptr;
    UINT32 count = 0;
    UINT32 flags = MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_LOCALMFT | MFT_ENUM_FLAG_SORTANDFILTER;
    if (FAILED(MFTEnumEx(MFT_CATEGORY_VIDEO_DECODER, flags, &inType, nullptr, &acts, &count)) || count == 0)
        return nullptr;
    IMFTransform* mft = nullptr;
    for (UINT32 i = 0; i < count; ++i) {
        if (!mft && SUCCEEDED(acts[i]->ActivateObject(IID_PPV_ARGS(&mft)))) { /* keep first */ }
        acts[i]->Release();
    }
    CoTaskMemFree(acts);
    return mft;
}

/* ---- capability probe ---------------------------------------------------- */

/* D3D11 decoder-profile GUIDs, defined locally so header vintage doesn't gate
 * the build (the values are the documented DXVA profile GUIDs). */
static const GUID kProfileH264VldNoFgt = {0x1b81be68,0xa0c7,0x11d3,{0xb9,0x84,0x00,0xc0,0x4f,0x2e,0x73,0xc5}};
static const GUID kProfileHevcVldMain  = {0x5b11d51b,0x2f4c,0x4452,{0xbc,0xc3,0x09,0xf2,0xa1,0x16,0x0c,0xc0}};
static const GUID kProfileVp9Profile0  = {0x463707f8,0xa1d0,0x4585,{0x87,0x6d,0x83,0xaa,0x6d,0x60,0xb8,0x9e}};
static const GUID kProfileAv1Profile0  = {0xb8be4ccb,0xcf53,0x46ba,{0x8d,0x59,0xd6,0xb8,0xa6,0xda,0x5d,0x2a}};

/* Leg 1: is there a decoder MFT for the subtype? Enumerated with exactly
 * create_video_mft's flags so a pass here means configure_video_mft will find
 * the same MFT (nothing is activated). */
static int probe_mft_present(const GUID* subtype) {
    MFT_REGISTER_TYPE_INFO inType = { MFMediaType_Video, *subtype };
    IMFActivate** acts = nullptr;
    UINT32 count = 0;
    UINT32 flags = MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_LOCALMFT | MFT_ENUM_FLAG_SORTANDFILTER;
    if (FAILED(MFTEnumEx(MFT_CATEGORY_VIDEO_DECODER, flags, &inType, nullptr, &acts, &count)))
        return 0;
    for (UINT32 i = 0; i < count; ++i) acts[i]->Release();
    CoTaskMemFree(acts);
    return count > 0;
}

/* Leg 2: does the GPU hardware-decode the profile? An MFT can pass leg 1 and
 * still decode on CPU via its internal software fallback (the Store VP9/AV1
 * extensions do this on GPUs without hardware decode) — those samples arrive
 * without DXGI backing and the drain rejects them, so an MFT-only probe would
 * be a false positive. Beyond the profile GUID, the check confirms NV12
 * output and a decoder configuration at the resolution ceiling this codec is
 * offered at — a listed profile alone promises neither. Uses a transient
 * device: the probe can run before any player exists. */
static int probe_gpu_profile(const GUID* profile, UINT width, UINT height) {
    ID3D11Device* dev = nullptr;
    ID3D11DeviceContext* ctx = nullptr;
    /* same flags + feature levels as create_decode_device, so a probe pass
     * means the real decode device will also create */
    UINT flags = D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_BGRA_SUPPORT;
    D3D_FEATURE_LEVEL fl[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
    if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags,
                                 fl, 2, D3D11_SDK_VERSION, &dev, nullptr, &ctx)))
        return 0;
    int found = 0;
    ID3D11VideoDevice* vd = nullptr;
    if (SUCCEEDED(dev->QueryInterface(__uuidof(ID3D11VideoDevice), (void**)&vd))) {
        UINT n = vd->GetVideoDecoderProfileCount();
        for (UINT i = 0; i < n && !found; ++i) {
            GUID g;
            if (SUCCEEDED(vd->GetVideoDecoderProfile(i, &g)) && g == *profile) found = 1;
        }
        if (found) {
            BOOL nv12 = FALSE;
            if (FAILED(vd->CheckVideoDecoderFormat(profile, DXGI_FORMAT_NV12, &nv12)) || !nv12)
                found = 0;
        }
        if (found) {
            D3D11_VIDEO_DECODER_DESC desc = {};
            desc.Guid = *profile;
            desc.SampleWidth = width;
            desc.SampleHeight = height;
            desc.OutputFormat = DXGI_FORMAT_NV12;
            UINT configs = 0;
            if (FAILED(vd->GetVideoDecoderConfigCount(&desc, &configs)) || configs == 0)
                found = 0;
        }
        vd->Release();
    }
    SAFE_RELEASE(ctx);
    SAFE_RELEASE(dev);
    return found;
}

extern "C" int basis_decoder_probe_video_codec(int codec) {
    /* Cached for process lifetime (0 unprobed / 1 no / 2 yes). Resolves run
     * concurrently on worker threads; reads and writes go through the
     * interlocked API so every access has defined synchronisation, and a
     * racing recompute is harmless — both writers store the same verdict. */
    static volatile LONG cache[BASIS_CODEC_AV1 + 1];
    if (codec < BASIS_CODEC_H264 || codec > BASIS_CODEC_AV1) return 0;
    LONG c = InterlockedCompareExchange(&cache[codec], 0, 0);
    if (c) return c == 2;

    /* the probe may run before any decoder exists — start MF here too
     * (CoInitializeEx is per-thread and may report an existing STA, which
     * MF doesn't mind; MFStartup refcounts; neither is ever shut down,
     * matching basis_decoder_create). A failed MFStartup returns 0
     * without caching, so it doesn't become a permanent verdict. */
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(MFStartup(MF_VERSION))) return 0;
    const GUID* prof = codec == BASIS_CODEC_H265 ? &kProfileHevcVldMain
                     : codec == BASIS_CODEC_VP9  ? &kProfileVp9Profile0
                     : codec == BASIS_CODEC_AV1  ? &kProfileAv1Profile0
                     : &kProfileH264VldNoFgt;
    /* 8-bit profile 0 only for VP9/AV1: 10-bit is deliberately unprobed — the
     * resolver filters to SDR/8-bit and the drain's software-fallback guard
     * catches direct 10-bit files that fall back. The GPU-profile leg matters
     * most for AV1: the Store AV1 extension falls back to its internal dav1d
     * on GPUs without hardware AV1 decode (most of today's VR desktops), so
     * an MFT-only probe would be a false positive there. The config check
     * runs at each codec's offer ceiling (the resolver caps avc1 at 1080p;
     * H.265/VP9/AV1 are offered up to 2160p), so a codec isn't failed for
     * missing headroom it will never be asked for. */
    UINT pw = codec == BASIS_CODEC_H264 ? 1920 : 3840;
    UINT ph = codec == BASIS_CODEC_H264 ? 1088 : 2160;
    int ok = probe_mft_present(video_subtype((basis_codec_t)codec)) &&
             probe_gpu_profile(prof, pw, ph);
    InterlockedExchange((volatile LONG*)&cache[codec], ok ? 2 : 1);
    return ok;
}

/* Read the clean-aperture (visible) region from the MFT's current output type.
 * H.264/H.265 round the coded surface up to a macroblock multiple (e.g. 1080 -> 1088),
 * and MF_MT_MINIMUM_DISPLAY_APERTURE carries the visible sub-rect. Stored so the video
 * processor can crop the coded pad instead of blitting it 1:1 (the pad would otherwise
 * land at the top of the frame after the decode-time vertical mirror). Left zeroed when
 * the type carries no aperture, and the caller falls back to the coded size. Many
 * decoders only populate it after the first-frame stream change, so this is read there
 * too, not just at configure. */
static void read_display_aperture(basis_decoder* d) {
    d->dispX = d->dispY = d->dispW = d->dispH = 0;
    if (!d->vdec) return;
    IMFMediaType* cur = nullptr;
    if (FAILED(d->vdec->GetOutputCurrentType(0, &cur)) || !cur) return;
    MFVideoArea area = {};
    if (SUCCEEDED(cur->GetBlob(MF_MT_MINIMUM_DISPLAY_APERTURE, (UINT8*)&area, sizeof(area), nullptr)) &&
        area.Area.cx > 0 && area.Area.cy > 0) {
        d->dispX = area.OffsetX.value;
        d->dispY = area.OffsetY.value;
        d->dispW = area.Area.cx;
        d->dispH = area.Area.cy;
    }
    cur->Release();
}

static bool configure_video_mft(basis_decoder* d) {
    d->vdec = create_video_mft(d->vcodec);
    if (!d->vdec) {
        basis_engine_set_error(d->engine,
            d->vcodec == BASIS_CODEC_VP9
            ? "no Media Foundation VP9 decoder (install 'VP9 Video Extensions' from the Microsoft Store)"
            : d->vcodec == BASIS_CODEC_AV1
            ? "no Media Foundation AV1 decoder (install 'AV1 Video Extension' from the Microsoft Store)"
            : "no Media Foundation decoder MFT for this codec (HEVC needs the HEVC Video Extension)");
        return false;
    }

    /* bind DXVA device manager */
    d->vdec->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER, (ULONG_PTR)d->devMgr);

    /* Without a frame size the Store HEVC MFT accepts the type, then reads a null
     * pointer on its own worker thread once data arrives and crashes the process.
     * Refuse here: only H.265 elementary streams reach this point sizeless (no SPS
     * parser for TS/RTSP/RTMP), and the size can't be recovered once it crashes. */
    if (d->vwidth <= 0 || d->vheight <= 0) {
        const char* codec_name =
            d->vcodec == BASIS_CODEC_H265 ? "H.265" :
            d->vcodec == BASIS_CODEC_H264 ? "H.264" :
            d->vcodec == BASIS_CODEC_VP9  ? "VP9"   :
            d->vcodec == BASIS_CODEC_AV1  ? "AV1"   : "this video codec";
        char msg[176];
        snprintf(msg, sizeof(msg),
            "video track (%s) announced no frame size, so the decoder cannot be configured",
            codec_name);
        basis_engine_set_error(d->engine, msg);
        SAFE_RELEASE(d->vdec);
        return false;
    }

    /* input type */
    IMFMediaType* in = nullptr;
    MFCreateMediaType(&in);
    in->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    in->SetGUID(MF_MT_SUBTYPE, *video_subtype(d->vcodec));
    in->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    MFSetAttributeSize(in, MF_MT_FRAME_SIZE, d->vwidth, d->vheight);
    HRESULT hr = d->vdec->SetInputType(0, in, 0);
    in->Release();
    if (FAILED(hr)) {
        basis_engine_set_error(d->engine, "MFT SetInputType failed");
        SAFE_RELEASE(d->vdec);
        return false;
    }

    /* pick an NV12 output type */
    IMFMediaType* out = nullptr;
    for (DWORD i = 0; ; ++i) {
        IMFMediaType* t = nullptr;
        if (FAILED(d->vdec->GetOutputAvailableType(0, i, &t))) break;
        GUID sub; t->GetGUID(MF_MT_SUBTYPE, &sub);
        if (sub == MFVideoFormat_NV12) { out = t; break; }
        t->Release();
    }
    if (!out) {
        MFCreateMediaType(&out);
        out->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
        out->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
    }
    hr = d->vdec->SetOutputType(0, out, 0);
    out->Release();
    if (FAILED(hr)) {
        basis_engine_set_error(d->engine, "MFT SetOutputType(NV12) failed");
        SAFE_RELEASE(d->vdec);
        return false;
    }
    read_display_aperture(d);

    d->vdec->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
    d->vdec->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
    return true;
}

static void release_shared_locked(basis_decoder* d) {
    for (int i = 0; i < basis_decoder::RING; ++i) {
        SAFE_RELEASE(d->ringVpOut[i]);
        SAFE_RELEASE(d->ringMutexDec[i]);
        SAFE_RELEASE(d->ringTex[i]);
        SAFE_RELEASE(d->ringMutexUnity[i]);
        SAFE_RELEASE(d->ringOnUnity[i]);
        if (d->ringHandle[i]) { CloseHandle(d->ringHandle[i]); d->ringHandle[i] = nullptr; }
        d->ringPts[i] = INT64_MIN;
    }
    SAFE_RELEASE(d->outTexD11);
    if (d->outTexD12) { ((ID3D12Resource*)d->outTexD12)->Release(); d->outTexD12 = nullptr; }
    SAFE_RELEASE(d->outSharedD12Mutex);
    SAFE_RELEASE(d->outSharedD12);
    if (d->outSharedD12Handle) { CloseHandle(d->outSharedD12Handle); d->outSharedD12Handle = nullptr; }
    d->d12OpenFail = 0;   /* new handle on the next build — don't carry the old retry count */
    d->writeSeq = 0;
    d->clockStarted = false;
    d->primeStartQpc = 0;
    d->lastPresentedPts = INT64_MIN;
}

/* Allocate the ring of keyed-mutex BGRA buffers on the decode device, open each
 * on Unity's device, and create the single Unity-visible output texture. */
static bool ensure_shared_textures(basis_decoder* d, int w, int h) {
    if (d->ringTex[0] && d->sharedW == w && d->sharedH == h) return true;

    EnterCriticalSection(&d->presentLock);
    release_shared_locked(d);
    d->sharedW = d->sharedH = 0;   /* only re-set on full success below, so a failed build retries */

    D3D11_TEXTURE2D_DESC desc = {};
    desc.Width = w; desc.Height = h; desc.MipLevels = 1; desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
    desc.MiscFlags = D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX | D3D11_RESOURCE_MISC_SHARED_NTHANDLE;

    bool ok = true;
    for (int i = 0; i < basis_decoder::RING && ok; ++i) {
        if (FAILED(d->devDec->CreateTexture2D(&desc, nullptr, &d->ringTex[i]))) { ok = false; break; }
        d->ringTex[i]->QueryInterface(__uuidof(IDXGIKeyedMutex), (void**)&d->ringMutexDec[i]);

        IDXGIResource1* res1 = nullptr;
        if (SUCCEEDED(d->ringTex[i]->QueryInterface(__uuidof(IDXGIResource1), (void**)&res1))) {
            res1->CreateSharedHandle(nullptr, DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE, nullptr, &d->ringHandle[i]);
            res1->Release();
        }
        if (d->api == BASIS_GFX_D3D11 && d->devUnity && d->ringHandle[i]) {
            ID3D11Device1* dev1 = nullptr;
            if (SUCCEEDED(d->devUnity->QueryInterface(__uuidof(ID3D11Device1), (void**)&dev1))) {
                dev1->OpenSharedResource1(d->ringHandle[i], __uuidof(ID3D11Texture2D), (void**)&d->ringOnUnity[i]);
                dev1->Release();
            }
            if (d->ringOnUnity[i])
                d->ringOnUnity[i]->QueryInterface(__uuidof(IDXGIKeyedMutex), (void**)&d->ringMutexUnity[i]);
        }
        /* D3D11 presents by copying ringOnUnity (the slot opened on Unity's device);
         * without it this slot can never present, so fail the build and retry rather
         * than caching a half-set ring. D3D12 copies ringTex directly, so it doesn't
         * need the shared mirror. */
        if (d->api == BASIS_GFX_D3D11 && !d->ringOnUnity[i]) { ok = false; break; }
        d->ringPts[i] = INT64_MIN;
    }

    /* Unity-visible output texture (TYPELESS so Unity makes a UNORM or sRGB SRV as
     * its colour space needs; a typed UNORM fails sRGB SRV creation with 0x80070057). */
    if (ok && d->api == BASIS_GFX_D3D11 && d->devUnity) {
        D3D11_TEXTURE2D_DESC od = desc;
        od.MiscFlags = 0;
        od.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        od.Format = DXGI_FORMAT_B8G8R8A8_TYPELESS;
        if (FAILED(d->devUnity->CreateTexture2D(&od, nullptr, &d->outTexD11))) {
            basis_engine_set_error(d->engine, "failed to create Unity output texture");
            ok = false;
        }
    }
    /* D3D12: there is no Unity D3D11 device to copy into, so present copies the due
     * ring slot into this TYPELESS shared texture on the decode device; Unity opens
     * it on its ID3D12Device (typeless so it can cast the UNORM or sRGB SRV its
     * colour space needs — a typed UNORM rejects sRGB SRV creation under D3D12).
     * NTHANDLE sharing requires a keyed mutex, so it carries one like the ring. */
    if (ok && d->api == BASIS_GFX_D3D12) {
        D3D11_TEXTURE2D_DESC od = desc;
        od.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        od.Format = DXGI_FORMAT_B8G8R8A8_TYPELESS;
        od.MiscFlags = D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX | D3D11_RESOURCE_MISC_SHARED_NTHANDLE;
        if (FAILED(d->devDec->CreateTexture2D(&od, nullptr, &d->outSharedD12))) {
            basis_engine_set_error(d->engine, "failed to create D3D12 shared output texture");
            ok = false;
        } else {
            d->outSharedD12->QueryInterface(__uuidof(IDXGIKeyedMutex), (void**)&d->outSharedD12Mutex);
            if (!d->outSharedD12Mutex) {
                /* The present path depends on this mutex for cross-device (decode->Unity
                 * D3D12) ordering; without it the copy would publish unsynchronised. */
                basis_engine_set_error(d->engine, "failed to acquire D3D12 shared output keyed mutex");
                ok = false;
            }
            IDXGIResource1* res1 = nullptr;
            if (ok && SUCCEEDED(d->outSharedD12->QueryInterface(__uuidof(IDXGIResource1), (void**)&res1))) {
                res1->CreateSharedHandle(nullptr, DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE, nullptr, &d->outSharedD12Handle);
                res1->Release();
            }
            if (ok && !d->outSharedD12Handle) {
                basis_engine_set_error(d->engine, "failed to share D3D12 output texture handle");
                ok = false;
            }
        }
    }

    /* Cache the size only once the full path (ring + output texture) succeeded, so a
     * failed output-texture/share create isn't masked by the early-return guard and
     * is retried on the next call instead of leaving no usable shared output. */
    if (ok) { d->sharedW = w; d->sharedH = h; }

    LeaveCriticalSection(&d->presentLock);
    return ok;
}

/* NV12 (decode device) -> next ring BGRA buffer via the video processor. */
static void video_process_to_shared(basis_decoder* d, ID3D11Texture2D* nv12, UINT arrayIndex, int64_t pts_us) {
    D3D11_TEXTURE2D_DESC td; nv12->GetDesc(&td);
    int w = (int)td.Width, h = (int)td.Height;
    if (d->vwidth != w || d->vheight != h) { d->vwidth = w; d->vheight = h; }

    /* Crop the coded surface to its clean aperture so the macroblock pad (e.g. the
     * 8 rows from 1080 -> 1088) never reaches Unity; blitted 1:1 it copies the pad and
     * the decode-time vertical mirror moves it to the top of the displayed frame. The
     * output texture is the visible size, so Unity also samples the true aspect. */
    int cw = w, ch = h, sx = 0, sy = 0;
    if (d->dispW > 0 && d->dispH > 0 &&
        d->dispX >= 0 && d->dispY >= 0 &&
        d->dispX + d->dispW <= w && d->dispY + d->dispH <= h) {
        cw = d->dispW; ch = d->dispH; sx = d->dispX; sy = d->dispY;
    }
    if (!ensure_shared_textures(d, cw, ch)) return;
    if (d->videoBasePts == INT64_MIN) d->videoBasePts = pts_us; /* sync origin */

    if (!d->vproc) {
        D3D11_VIDEO_PROCESSOR_CONTENT_DESC cd = {};
        cd.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
        cd.InputWidth = w; cd.InputHeight = h; cd.OutputWidth = cw; cd.OutputHeight = ch;
        cd.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
        if (FAILED(d->vdevice->CreateVideoProcessorEnumerator(&cd, &d->vprocEnum))) return;
        if (FAILED(d->vdevice->CreateVideoProcessor(d->vprocEnum, 0, &d->vproc))) return;
        /* Try to make the video processor emit a bottom-left origin frame so Unity
         * samples it right-way-up with no UV flip. VideoProcessorSetStreamMirror is
         * an OPTIONAL feature: a GPU's VP advertises it via the MIRROR caps bit, and
         * drivers that lack it (some Intel iGPU / WARP / virtualized adapters)
         * silently ignore the call — that is the "video is upside-down only on some
         * machines" bug, since the method returns void so the no-op is invisible. So
         * gate on the cap: when the mirror actually runs, mark the frame upright;
         * otherwise leave it top-left and report that origin so the consumer applies
         * a free, deterministic UV flip instead. VideoProcessorSetStreamMirror lives
         * on ID3D11VideoContext1 (D3D11.1+), so query it from the base context. */
        bool mirrored = false;
        D3D11_VIDEO_PROCESSOR_CAPS vpcaps = {};
        bool canMirror = SUCCEEDED(d->vprocEnum->GetVideoProcessorCaps(&vpcaps)) &&
                         (vpcaps.FeatureCaps & D3D11_VIDEO_PROCESSOR_FEATURE_CAPS_MIRROR);
        if (canMirror) {
            ID3D11VideoContext1* vctx1 = nullptr;
            if (SUCCEEDED(d->vcontext->QueryInterface(__uuidof(ID3D11VideoContext1), (void**)&vctx1)) && vctx1) {
                vctx1->VideoProcessorSetStreamMirror(d->vproc, 0, TRUE, FALSE, TRUE);
                vctx1->Release();
                mirrored = true;
            }
        }
        d->frameTopLeft = mirrored ? 0 : 1;

        /* Sample only the clean aperture (see the crop note above); persists on the
         * stream for every blt. A full-frame rect when no aperture is a no-op. */
        RECT srcRect = { sx, sy, sx + cw, sy + ch };
        d->vcontext->VideoProcessorSetStreamSourceRect(d->vproc, 0, TRUE, &srcRect);
        RECT dstRect = { 0, 0, cw, ch };
        d->vcontext->VideoProcessorSetStreamDestRect(d->vproc, 0, TRUE, &dstRect);
    }

    int slot = (int)(d->writeSeq % basis_decoder::RING);

    if (!d->ringVpOut[slot]) {
        D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC ovd = {};
        ovd.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
        if (FAILED(d->vdevice->CreateVideoProcessorOutputView(d->ringTex[slot], d->vprocEnum, &ovd, &d->ringVpOut[slot]))) return;
    }

    ID3D11VideoProcessorInputView* inView = nullptr;
    D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC ivd = {};
    ivd.FourCC = 0;
    ivd.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
    ivd.Texture2D.ArraySlice = arrayIndex;
    if (FAILED(d->vdevice->CreateVideoProcessorInputView(nv12, d->vprocEnum, &ivd, &inView))) return;

    D3D11_VIDEO_PROCESSOR_STREAM stream = {};
    stream.Enable = TRUE;
    stream.pInputSurface = inView;

    /* key 0 = free. Short wait then drop — only contends if the consumer is still
     * reading THIS slot (i.e. it lagged a whole ring), which the PTS pacing avoids.
     * Never stalls the demux/network thread. */
    if (d->ringMutexDec[slot]) {
        if (d->ringMutexDec[slot]->AcquireSync(0, 4) != S_OK) { InterlockedIncrement(&d->dbg_drop); inView->Release(); return; }
    }
    d->vcontext->VideoProcessorBlt(d->vproc, d->ringVpOut[slot], 0, 1, &stream);
    d->ctxDec->Flush();
    if (d->ringMutexDec[slot]) d->ringMutexDec[slot]->ReleaseSync(0);

    /* track the source frame period (EMA), ignoring discontinuities, so the pacer
     * can size the buffer in time while keeping it within the ring's frame span. */
    if (d->prevWritePts != INT64_MIN) {
        int64_t dlt = pts_us - d->prevWritePts;
        if (dlt > 0 && dlt < 1000000)
            d->frameIntervalUs = d->frameIntervalUs ? (d->frameIntervalUs * 7 + dlt) / 8 : dlt;
    }
    d->prevWritePts = pts_us;

    /* publish: stamp PTS (aligned int64 write is atomic on x64), then bump seq. */
    d->ringPts[slot] = pts_us;
    InterlockedIncrement64(&d->writeSeq);
    InterlockedIncrement(&d->frameCounter);
    InterlockedIncrement(&d->dbg_blit);
    inView->Release();
}

/* Upper bound on a single decoded output frame — 8K RGB is ~100 MB, so this is
 * past any real frame while stopping a malformed cbSize from driving a huge alloc. */
#define BASIS_MAX_OUTPUT_BUFFER (256u * 1024u * 1024u)

/* Pull all currently-available output samples from the video MFT.
 * CRITICAL: in DXVA mode the MFT hands us its own IMFSample in outBuf.pSample,
 * backed by a small pool of D3D11 surfaces. That sample MUST be released every
 * iteration or the pool drains and ProcessOutput returns NEED_MORE_INPUT forever
 * (the "one frame then stall" bug). We release outBuf.pSample on every path. */
static void drain_video(basis_decoder* d) {
    for (;;) {
        MFT_OUTPUT_STREAM_INFO si = {};
        d->vdec->GetOutputStreamInfo(0, &si);
        bool providesSamples = (si.dwFlags & (MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) != 0;

        MFT_OUTPUT_DATA_BUFFER outBuf = {};
        outBuf.dwStreamID = 0;
        if (!providesSamples) {
            IMFSample* s = nullptr; IMFMediaBuffer* mb = nullptr;
            DWORD cb = si.cbSize;
            if (!cb) {
                /* Dims are attacker-announced; bound each side BEFORE multiplying so
                 * the product can't overflow (16384 is past any real frame — the SPS
                 * parser already caps decode dimensions well below this). */
                if (d->vwidth > 0 && d->vwidth <= 16384 && d->vheight > 0 && d->vheight <= 16384)
                    cb = (DWORD)((uint64_t)d->vwidth * (uint64_t)d->vheight * 3u);
                else
                    cb = 0;
            }
            /* Cap both the MFT-declared cbSize and the fallback estimate so a
             * malformed output size can't exhaust memory. */
            if (cb == 0 || cb > BASIS_MAX_OUTPUT_BUFFER ||
                FAILED(MFCreateSample(&s)) || FAILED(MFCreateMemoryBuffer(cb, &mb))) {
                SAFE_RELEASE(s); SAFE_RELEASE(mb);
                break;
            }
            if (FAILED(s->AddBuffer(mb))) { mb->Release(); s->Release(); break; }
            mb->Release();
            outBuf.pSample = s;
        }

        DWORD status = 0;
        HRESULT hr = d->vdec->ProcessOutput(0, 1, &outBuf, &status);

        if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) {
            SAFE_RELEASE(outBuf.pSample);
            if (outBuf.pEvents) outBuf.pEvents->Release();
            break;
        }
        if (hr == MF_E_TRANSFORM_STREAM_CHANGE) {
            IMFMediaType* t = nullptr;
            for (DWORD i = 0; ; ++i) {
                IMFMediaType* c = nullptr;
                if (FAILED(d->vdec->GetOutputAvailableType(0, i, &c))) break;
                GUID sub; c->GetGUID(MF_MT_SUBTYPE, &sub);
                if (sub == MFVideoFormat_NV12) { t = c; break; }
                c->Release();
            }
            if (t) { d->vdec->SetOutputType(0, t, 0); t->Release(); read_display_aperture(d); }
            SAFE_RELEASE(outBuf.pSample);
            if (outBuf.pEvents) outBuf.pEvents->Release();
            continue;
        }
        if (FAILED(hr)) {
            SAFE_RELEASE(outBuf.pSample);
            if (outBuf.pEvents) outBuf.pEvents->Release();
            break;
        }

        IMFSample* outSample = outBuf.pSample;
        if (outSample) {
            InterlockedIncrement(&d->dbg_out);
            LONGLONG ts = 0;
            if (SUCCEEDED(outSample->GetSampleTime(&ts))) d->lastPtsUs = ts / 10; /* 100ns -> us */

            IMFMediaBuffer* mb = nullptr;
            if (SUCCEEDED(outSample->GetBufferByIndex(0, &mb))) {
                IMFDXGIBuffer* dxgi = nullptr;
                if (SUCCEEDED(mb->QueryInterface(__uuidof(IMFDXGIBuffer), (void**)&dxgi))) {
                    ID3D11Texture2D* tex = nullptr;
                    UINT subIndex = 0;
                    dxgi->GetResource(__uuidof(ID3D11Texture2D), (void**)&tex);
                    dxgi->GetSubresourceIndex(&subIndex);
                    if (tex) {
                        /* Post-seek preroll (keyframe run-up short of the target):
                         * decoded so later frames have their references, never shown.
                         * Output is display-order, so the first frame at or past the
                         * target ends the run-up for good — but only once the
                         * post-flush keyframe's own output has emerged: output before
                         * that is post-flush mid-GOP garbage whose PTS may sit past
                         * the target, and it must neither show nor end the run-up.
                         * The PTS match is the designed clear; the drain bound is a
                         * backstop, set well past any plausible garbage-tail length,
                         * so a dropped or re-stamped keyframe output degrades to at
                         * worst one stale frame instead of wedging video. */
                        if (d->vAwaitKey &&
                            ((d->vAwaitKeyPts != INT64_MIN && d->lastPtsUs == d->vAwaitKeyPts) ||
                             ++d->vAwaitDrained > 16))
                            d->vAwaitKey = 0;
                        if (d->vAwaitKey ||
                            (d->vPrerollCutUs != INT64_MIN && d->lastPtsUs < d->vPrerollCutUs)) {
                            /* skip banking */
                        } else {
                            d->vPrerollCutUs = INT64_MIN;
                            video_process_to_shared(d, tex, subIndex, d->lastPtsUs);
                        }
                        tex->Release();
                    }
                    dxgi->Release();
                } else {
                    /* Only DXGI-backed samples reach the video processor. A
                     * system-memory sample means the MFT fell back to CPU decode
                     * (e.g. the Store VP9 extension's internal libvpx on a GPU
                     * without hardware decode for the profile) — every frame
                     * would be discarded and the screen would stay black, so
                     * fail loudly instead. */
                    basis_engine_set_error(d->engine,
                        "video decoder produced software frames (no GPU decode path for this codec/profile)");
                }
                mb->Release();
            }
        }

        SAFE_RELEASE(outBuf.pSample);   /* releases MFT-provided OR locally-allocated sample */
        if (outBuf.pEvents) outBuf.pEvents->Release();
    }
}

/* ---- audio MFT (AAC -> float PCM) -------------------------------------- */

/* Pick the output type the decoder offers, set it, and refresh the derived
 * format state (asr/ach/aBits + the PCM ring's frame width and rate). Prefer
 * a channel count matching the input, then the stereo fold-down, then IEEE
 * float. For >2-channel AAC the decoder also offers a stereo fold-down, so
 * matching the input channel count is what keeps the discrete surround
 * channels (e.g. 5.1); when nothing matches the input (unexpected layout) the
 * fold-down is the predictable fallback every consumer handles. Types wider
 * than 8 channels never rank — the splitter downstream maps at most 8 lanes.
 * Float vs 16-bit PCM only changes the conversion in drain_audio. Shared by
 * the initial configure and the drain's stream-change renegotiation (HE-AAC
 * raises one when the SBR-doubled rate replaces the core rate). */
/* ---- libopus runtime loader (§4-b2) -------------------------------------- */
/* The plugin does not link libopus; it resolves the decode entry points at
 * runtime from the opus.dll com.avionblock.opussharp ships. C# passes the path
 * (the in-Editor path differs from a flattened build); all decoders in the
 * process share one resolved table. A missing library or symbol degrades to
 * muted audio (the format is rejected), never a crash. */
#define OPUS_SET_GAIN_REQUEST 4034
#define OPUS_RESET_STATE 4028
typedef struct OpusDecoder OpusDecoder;
typedef struct OpusMSDecoder OpusMSDecoder;
struct opus_api {
    OpusDecoder*   (*dec_create)(int32_t Fs, int channels, int* error);
    int            (*decode_float)(OpusDecoder*, const unsigned char*, int32_t, float*, int, int);
    void           (*dec_destroy)(OpusDecoder*);
    OpusMSDecoder* (*ms_create)(int32_t Fs, int channels, int streams, int coupled,
                                const unsigned char* mapping, int* error);
    int            (*ms_decode_float)(OpusMSDecoder*, const unsigned char*, int32_t, float*, int, int);
    void           (*ms_destroy)(OpusMSDecoder*);
    int            (*dec_ctl)(OpusDecoder*, int request, ...);
    int            (*ms_ctl)(OpusMSDecoder*, int request, ...);
    const char*    (*version)(void);
};
static opus_api g_opus = {};
static bool g_opus_ok = false, g_opus_tried = false;
/* Wide path so a project under a non-ANSI directory (e.g. C:\媒体\Basis) still
 * loads: LoadLibraryA would fail there and the miss is cached for the session. */
static wchar_t g_opus_path[32768] = {0};
/* The loader and path setter touch process-wide state; concurrent decoder opens
 * (multiple players) would otherwise race g_opus_tried/g_opus_ok/g_opus_path. */
static std::mutex g_opus_mtx;

/* Resolve opus.dll next to this plugin (the standalone-build flattened Plugins
 * dir) and load it by absolute path — never a bare name, which would search the
 * cwd/PATH and let a planted opus.dll be loaded. */
static HMODULE opus_load_from_plugin_dir() {
    HMODULE self = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                            (LPCWSTR)&opus_load_from_plugin_dir, &self))
        return nullptr;
    wchar_t path[2048];
    DWORD n = GetModuleFileNameW(self, path, (DWORD)(sizeof(path) / sizeof(path[0])));
    if (n == 0 || n >= sizeof(path) / sizeof(path[0])) return nullptr;
    wchar_t* slash = wcsrchr(path, L'\\');
    if (!slash) return nullptr;
    slash[1] = 0;                                /* keep the trailing backslash */
    if (wcslen(path) + wcslen(L"opus.dll") >= sizeof(path) / sizeof(path[0])) return nullptr;
    wcscat_s(path, sizeof(path) / sizeof(path[0]), L"opus.dll");
    /* LOAD_WITH_ALTERED_SEARCH_PATH: resolve opus.dll's own dependencies from its
     * directory rather than the process search path. */
    return LoadLibraryExW(path, nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
}

static bool opus_load() {
    std::lock_guard<std::mutex> lock(g_opus_mtx);
    if (g_opus_tried) return g_opus_ok;
    g_opus_tried = true;
    /* Absolute, trusted paths only. The C# side supplies the opussharp path in
     * the Editor; standalone builds resolve opus.dll next to the plugin. */
    HMODULE lib = g_opus_path[0] ? LoadLibraryExW(g_opus_path, nullptr, LOAD_WITH_ALTERED_SEARCH_PATH) : nullptr;
    if (!lib) lib = opus_load_from_plugin_dir();
    if (!lib) return false;
    g_opus.dec_create      = (decltype(g_opus.dec_create))      GetProcAddress(lib, "opus_decoder_create");
    g_opus.decode_float    = (decltype(g_opus.decode_float))    GetProcAddress(lib, "opus_decode_float");
    g_opus.dec_destroy     = (decltype(g_opus.dec_destroy))     GetProcAddress(lib, "opus_decoder_destroy");
    g_opus.ms_create       = (decltype(g_opus.ms_create))       GetProcAddress(lib, "opus_multistream_decoder_create");
    g_opus.ms_decode_float = (decltype(g_opus.ms_decode_float)) GetProcAddress(lib, "opus_multistream_decode_float");
    g_opus.ms_destroy      = (decltype(g_opus.ms_destroy))      GetProcAddress(lib, "opus_multistream_decoder_destroy");
    g_opus.dec_ctl         = (decltype(g_opus.dec_ctl))         GetProcAddress(lib, "opus_decoder_ctl");
    g_opus.ms_ctl          = (decltype(g_opus.ms_ctl))          GetProcAddress(lib, "opus_multistream_decoder_ctl");
    g_opus.version         = (decltype(g_opus.version))         GetProcAddress(lib, "opus_get_version_string");
    g_opus_ok = g_opus.dec_create && g_opus.decode_float && g_opus.dec_destroy &&
                g_opus.ms_create && g_opus.ms_decode_float && g_opus.ms_destroy;
    return g_opus_ok;
}

/* C# resolves the opussharp library path and passes it before the first Opus
 * decode (the Editor Packages path vs the flattened Plugins dir). C#-facing, so
 * exported and __stdcall to match the P/Invoke (unlike the internal decoder
 * entry points the native core calls). */
extern "C" __declspec(dllexport) void __stdcall basis_decoder_set_opus_library_path(const wchar_t* path) {
    if (!path) return;
    std::lock_guard<std::mutex> lock(g_opus_mtx);
    wcsncpy_s(g_opus_path, _countof(g_opus_path), path, _TRUNCATE);
}

static bool pick_audio_output(basis_decoder* d) {
    IMFMediaType* chosen = nullptr; int bits = 0; int chosenRank = -1;
    int target = d->achSrc ? d->achSrc : (d->ach ? d->ach : 2);
    for (DWORD i = 0; ; ++i) {
        IMFMediaType* t = nullptr;
        if (FAILED(d->adec->GetOutputAvailableType(0, i, &t))) break;
        GUID sub; t->GetGUID(MF_MT_SUBTYPE, &sub);
        UINT32 b = 0, tch = 0;
        t->GetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, &b);
        t->GetUINT32(MF_MT_AUDIO_NUM_CHANNELS, &tch);
        bool isFloat = (sub == MFAudioFormat_Float);
        bool isPcm = (sub == MFAudioFormat_PCM);
        if (!isFloat && !isPcm) { t->Release(); continue; }
        if (tch > 8) { t->Release(); continue; }
        int rank = ((int)tch == target ? 10000 : 0) + ((int)tch == 2 ? 1000 : 0) + (isFloat ? 100 : 0) + (int)tch;
        if (rank > chosenRank) {
            if (chosen) chosen->Release();
            chosen = t; chosenRank = rank;
            bits = isFloat ? 32 : (int)(b ? b : 16);
        } else {
            t->Release();
        }
    }
    if (!chosen) return false;

    UINT32 sr = 0, ch = 0;
    chosen->GetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, &sr);
    chosen->GetUINT32(MF_MT_AUDIO_NUM_CHANNELS, &ch);
    HRESULT hr = d->adec->SetOutputType(0, chosen, 0);
    chosen->Release();
    if (FAILED(hr)) return false;

    if (sr) d->asr = (int)sr;
    if (ch) d->ach = (int)ch;
    d->aBits = (bits == 16) ? 16 : 32;
    EnterCriticalSection(&d->pcm.cs);
    d->pcm.frame = d->ach > 0 ? d->ach : 1;
    d->pcm.sr = d->asr > 0 ? d->asr : 48000;
    LeaveCriticalSection(&d->pcm.cs);
    return true;
}

/* Configures the in-box AAC decoder MFT. Fails silently (audio stays muted, video
 * unaffected) — never errors the engine. aconfigured/aout in the debug string say
 * whether it worked. */
static bool configure_audio_mft(basis_decoder* d, const uint8_t* asc, int asc_len) {
    if (FAILED(CoCreateInstance(CLSID_CMSAACDecMFT, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&d->adec))))
        return false;

    IMFMediaType* in = nullptr;
    MFCreateMediaType(&in);
    in->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
    in->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_AAC);
    in->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, d->asr ? d->asr : 48000);
    in->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, d->ach ? d->ach : 2);
    in->SetUINT32(MF_MT_AAC_PAYLOAD_TYPE, 0); /* raw AAC frames */
    {
        /* MF_MT_USER_DATA = HEAACWAVEINFO bytes after WAVEFORMATEX (12) + ASC. */
        uint8_t blob[64] = {0};
        int n = 12;
        if (asc && asc_len > 0 && 12 + asc_len <= (int)sizeof(blob)) { memcpy(blob + 12, asc, asc_len); n = 12 + asc_len; }
        in->SetBlob(MF_MT_USER_DATA, blob, n);
    }
    HRESULT hr = d->adec->SetInputType(0, in, 0);
    in->Release();
    if (FAILED(hr)) { SAFE_RELEASE(d->adec); return false; }

    if (!pick_audio_output(d)) { SAFE_RELEASE(d->adec); return false; }

    d->adec->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
    d->adec->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
    return true;
}

/* Configures the in-box MP3 decoder. Unlike AAC (a fixed CLSID) the MP3 decoder
 * is found by enumeration, so activate the first MFT that takes MFAudioFormat_MP3.
 * The input type is built from an MPEGLAYER3WAVEFORMAT so MF fills in the subtype
 * and codec-private bytes; the decoder parses each frame header itself. Fails
 * silently like configure_audio_mft — audio stays muted, video unaffected. */
static bool configure_mp3_mft(basis_decoder* d, int sample_rate, int channels) {
    MFT_REGISTER_TYPE_INFO inInfo = { MFMediaType_Audio, MFAudioFormat_MP3 };
    IMFActivate** acts = nullptr;
    UINT32 count = 0;
    UINT32 flags = MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_LOCALMFT | MFT_ENUM_FLAG_SORTANDFILTER;
    if (FAILED(MFTEnumEx(MFT_CATEGORY_AUDIO_DECODER, flags, &inInfo, nullptr, &acts, &count)) || count == 0)
        return false;
    for (UINT32 i = 0; i < count; ++i) {
        if (!d->adec && SUCCEEDED(acts[i]->ActivateObject(IID_PPV_ARGS(&d->adec)))) { /* keep first */ }
        acts[i]->Release();
    }
    CoTaskMemFree(acts);
    if (!d->adec) return false;

    MPEGLAYER3WAVEFORMAT wf = {};
    wf.wfx.wFormatTag = WAVE_FORMAT_MPEGLAYER3;
    wf.wfx.nChannels = (WORD)(channels > 0 ? channels : 2);
    wf.wfx.nSamplesPerSec = (DWORD)(sample_rate > 0 ? sample_rate : 48000);
    wf.wfx.nBlockAlign = 1;
    wf.wfx.cbSize = MPEGLAYER3_WFX_EXTRA_BYTES;
    wf.wID = MPEGLAYER3_ID_MPEG;
    wf.nBlockSize = 1;
    wf.nFramesPerBlock = 1;

    IMFMediaType* in = nullptr;
    MFCreateMediaType(&in);
    HRESULT hr = MFInitMediaTypeFromWaveFormatEx(in, (const WAVEFORMATEX*)&wf, sizeof(wf));
    if (SUCCEEDED(hr)) hr = d->adec->SetInputType(0, in, 0);
    in->Release();
    if (FAILED(hr)) { SAFE_RELEASE(d->adec); return false; }

    if (!pick_audio_output(d)) { SAFE_RELEASE(d->adec); return false; }

    d->adec->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
    d->adec->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
    return true;
}

static void drain_audio(basis_decoder* d) {
    for (;;) {
        MFT_OUTPUT_STREAM_INFO si = {};
        d->adec->GetOutputStreamInfo(0, &si);
        IMFSample* sample = nullptr; IMFMediaBuffer* mb = nullptr;
        MFCreateSample(&sample);
        MFCreateMemoryBuffer(si.cbSize ? si.cbSize : 65536, &mb);
        sample->AddBuffer(mb);

        MFT_OUTPUT_DATA_BUFFER ob = {}; ob.pSample = sample; DWORD status = 0;
        HRESULT hr = d->adec->ProcessOutput(0, 1, &ob, &status);
        if (hr == MF_E_TRANSFORM_STREAM_CHANGE) {
            /* The decoder renegotiates its output mid-stream — HE-AAC does this
             * when in-band SBR doubles the rate past what configure saw. Repick
             * and keep draining; giving up here mutes audio for good. */
            mb->Release(); sample->Release();
            if (ob.pEvents) ob.pEvents->Release();
            if (!pick_audio_output(d)) break;
            continue;
        }
        if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT || FAILED(hr)) { mb->Release(); sample->Release(); break; }

        /* The decoder propagates input sample times to its outputs; fall back
         * to a sample-counted timeline if one comes back without a time. */
        LONGLONG st100 = 0;
        int64_t pts = SUCCEEDED(sample->GetSampleTime(&st100)) ? st100 / 10 : d->aPtsFallback;

        BYTE* p = nullptr; DWORD cur = 0;
        if (SUCCEEDED(mb->Lock(&p, nullptr, &cur)) && cur > 0) {
            int srr = d->asr > 0 ? d->asr : 48000;
            int ch = d->ach > 0 ? d->ach : 1;
            if (d->aBits == 16) {
                int n = (int)(cur / 2);
                const int16_t* s16 = (const int16_t*)p;
                float tmp[4096];
                int maxFrames = 4096 / ch; if (maxFrames < 1) maxFrames = 1;
                /* Priming is dropped by starting past it; the per-chunk time below
                 * is derived from off, so it stays correct for what remains. */
                int off = basis_frames_before_origin(pts, n / ch, srr) * ch;
                /* Write whole interleaved frames only: a sub-frame chunk would
                 * give the ring's per-chunk PTS a fractional sample count. */
                while (off + ch <= n) {
                    int framesLeft = (n - off) / ch;
                    int c = (framesLeft > maxFrames ? maxFrames : framesLeft) * ch;
                    for (int i = 0; i < c; ++i) tmp[i] = s16[off + i] / 32768.0f;
                    d->pcm.write(tmp, c, pts + (int64_t)(off / ch) * 1000000LL / srr);
                    off += c;
                }
                d->aPtsFallback = pts + (int64_t)(n / ch) * 1000000LL / srr;
            } else {
                int n = (int)(cur / sizeof(float));
                int skip = basis_frames_before_origin(pts, n / ch, srr) * ch;
                if (skip < n)
                    d->pcm.write((const float*)p + skip, n - skip,
                                 pts + (int64_t)(skip / ch) * 1000000LL / srr);
                d->aPtsFallback = pts + (int64_t)(n / ch) * 1000000LL / srr;
            }
            mb->Unlock();
            InterlockedIncrement(&d->dbg_aout);
        }
        mb->Release(); sample->Release();
        if (ob.pEvents) ob.pEvents->Release();
    }
}

/* ---- internal API impl -------------------------------------------------- */

extern "C" basis_decoder_t* basis_decoder_create(basis_media_engine_t* engine) {
    static bool mfStarted = false;
    if (!mfStarted) { CoInitializeEx(nullptr, COINIT_MULTITHREADED); MFStartup(MF_VERSION); mfStarted = true; }

    basis_decoder* d = new basis_decoder();
    d->engine = engine;
    d->api = basis_gfx_get_api();
    d->devUnity = (ID3D11Device*)basis_gfx_get_d3d11_device();
    if (d->devUnity) d->devUnity->GetImmediateContext(&d->ctxUnity);
    InitializeCriticalSection(&d->presentLock);
    QueryPerformanceFrequency(&d->qpcFreq);
    QueryPerformanceCounter(&d->createQpc);
    for (int i = 0; i < basis_decoder::RING; ++i) d->ringPts[i] = INT64_MIN;
    d->pcm.init(48000 * 8 * 4); /* ~4s at 8ch — the PTS-gated serve banks mux
                                 * lead + the jitter cushion in the ring, so
                                 * capacity must hold both at full width */

    if (!create_decode_device(d)) {
        basis_engine_set_error(engine, "failed to create DXVA D3D11 decode device");
        /* keep the object; audio/video setup will fail gracefully */
    }
    return d;
}

extern "C" void basis_decoder_destroy(basis_decoder_t* d) {
    if (!d) return;
    basis_decoder_render_release(d); /* idempotent GPU teardown */

    SAFE_RELEASE(d->vproc);
    SAFE_RELEASE(d->vprocEnum);
    SAFE_RELEASE(d->vcontext);
    SAFE_RELEASE(d->vdevice);
    SAFE_RELEASE(d->vdec);
    SAFE_RELEASE(d->adec);
    SAFE_RELEASE(d->devMgr);
    SAFE_RELEASE(d->presentQuery);
    SAFE_RELEASE(d->ctxDec);
    SAFE_RELEASE(d->devDec);
    SAFE_RELEASE(d->ctxUnity);
    /* shared textures + handles already freed by basis_decoder_render_release above */
    DeleteCriticalSection(&d->presentLock);
    d->pcm.destroy();
    free(d->aLpcmBuf);
    if (d->opusDec) {
        if (d->opusIsMS) { if (g_opus.ms_destroy) g_opus.ms_destroy((OpusMSDecoder*)d->opusDec); }
        else             { if (g_opus.dec_destroy) g_opus.dec_destroy((OpusDecoder*)d->opusDec); }
    }
    free(d->opusBuf);
    delete d;
}

extern "C" int basis_decoder_set_video_format(basis_decoder_t* d, basis_codec_t codec,
                                              const uint8_t* extradata, int extradata_len, int w, int h) {
    if (!d || d->vconfigured) return 0;
    d->vcodec = codec; d->vwidth = w; d->vheight = h;
    if (!d->devDec) return -1;
    if (!configure_video_mft(d)) return -1;
    if (extradata && extradata_len > 0) {
        if (codec == BASIS_CODEC_AV1) {
            /* configOBUs ride the first real AU (see vConfigObus) rather than
             * being fed as their own sample. */
            if (extradata_len <= (int)sizeof(d->vConfigObus)) {
                memcpy(d->vConfigObus, extradata, extradata_len);
                d->vConfigObusLen = extradata_len;
            }
        } else {
            /* Feed SPS/PPS (Annex B extradata) as the first input so the MFT has config. */
            basis_decoder_submit_video(d, extradata, extradata_len, 0, 0);
        }
    }
    d->vconfigured = true;
    return 0;
}

extern "C" int basis_decoder_set_audio_format(basis_decoder_t* d, basis_codec_t codec,
                                              int sample_rate, int channels, const uint8_t* asc, int asc_len) {
    if (!d || d->aconfigured) return 0;

    if (codec == BASIS_CODEC_LPCM) {
        /* No decoder involved — submit_audio converts straight into the ring.
         * The config blob carries the channel-assignment + bits codes, plus an
         * optional flags byte: bit0 = little-endian WAVE-order samples (the
         * RIFF/WAV lane). Blu-ray TS (2-byte config, big-endian) stays 48 kHz
         * only — the TS demuxer pre-filters, this is the matching backstop.
         * The WAV lane plays at the file rate: the splitter downstream
         * resamples source rate to DSP rate. 16- or 24-bit only either way. */
        if (channels < 1 || channels > 8 || !asc || asc_len < 2) return 0;
        int le = asc_len >= 3 && (asc[2] & 1);
        if (le ? (sample_rate < 8000 || sample_rate > 96000) : (sample_rate != 48000)) return 0;
        int bits = asc[1] == 1 ? 16 : asc[1] == 3 ? 24 : 0;
        if (!bits) return 0; /* 20-bit unsupported */
        d->acodec = BASIS_CODEC_LPCM;
        d->asr = sample_rate; d->ach = channels;
        d->aLpcmAssign = asc[0];
        d->aLpcmBits = bits;
        d->aLpcmLE = le;
        d->aconfigured = true;
        d->pcm.frame = channels;
        d->pcm.sr = sample_rate;
        return 0;
    }

    if (codec == BASIS_CODEC_OPUS) {
        /* OpusHead (the extradata): [8]=version [9]=channels [10..11]=pre_skip LE
         * [16..17]=output gain Q7.8 LE [18]=mapping family; family 1 adds
         * [19]=streams [20]=coupled [21..]=channel-mapping table. Decode is
         * native-side via the runtime-loaded libopus (§4-b2); a missing library
         * or a decoder-create failure rejects the format = muted, video intact. */
        if (!asc || asc_len < 19 || memcmp(asc, "OpusHead", 8) != 0) return 0;
        if (!opus_load()) return 0;
        int ch = asc[9];
        int preskip = asc[10] | (asc[11] << 8);
        int16_t gain = (int16_t)(asc[16] | (asc[17] << 8));
        int family = asc[18];
        if (ch < 1 || ch > 8) return 0;
        if (family == 0 && ch > 2) return 0;             /* family 0 is mono/stereo only */
        if (family != 0 && family != 1 && family != 255) return 0; /* reserved family */
        d->opusMappingFamily = family;
        int err = 0;
        if (family == 0) {
            OpusDecoder* dec = g_opus.dec_create(48000, ch, &err);
            if (!dec || err != 0) return 0;
            if (gain != 0 && g_opus.dec_ctl) g_opus.dec_ctl(dec, OPUS_SET_GAIN_REQUEST, (int)gain);
            d->opusDec = dec; d->opusIsMS = 0;
        } else {
            if (asc_len < 21 + ch) return 0;
            int streams = asc[19], coupled = asc[20];
            OpusMSDecoder* ms = g_opus.ms_create(48000, ch, streams, coupled, asc + 21, &err);
            if (!ms || err != 0) return 0;
            /* Output gain applies independently of channel mapping (RFC 7845), and
             * the multistream decoder has its own CTL entry point. */
            if (gain != 0 && g_opus.ms_ctl) g_opus.ms_ctl(ms, OPUS_SET_GAIN_REQUEST, (int)gain);
            d->opusDec = ms; d->opusIsMS = 1;
        }
        d->acodec = BASIS_CODEC_OPUS;
        d->asr = 48000; d->ach = ch;
        d->opusPreSkip = preskip;
        d->aconfigured = true;
        d->pcm.frame = ch;
        d->pcm.sr = 48000;
        return 0;
    }

    if (codec == BASIS_CODEC_MP3) {
        d->asr = sample_rate; d->ach = channels; d->achSrc = channels;
        if (configure_mp3_mft(d, sample_rate, channels)) {
            d->acodec = BASIS_CODEC_MP3;
            d->aconfigured = true;
            d->pcm.frame = d->ach > 0 ? d->ach : 1;
            d->pcm.sr = d->asr > 0 ? d->asr : 48000;
        }
        return 0;
    }

    if (codec != BASIS_CODEC_AAC) return 0;

    /* The in-box AAC decoder (CLSID_CMSAACDecMFT) handles at most 6 channels
     * (5.1) and only explicitly-signalled layouts. Fed anything wider it
     * accepts the input type and then AVs inside CAACDec::CheckModeChange
     * decoding the first frame (rather than erroring), so screen the layout
     * before configuring — the ASC channelConfiguration where present, since
     * containers misreport, plus the container channel count as backstop.
     * channelConfiguration 0 (layout defined by an in-band PCE) and reserved
     * values leave the real width unknown; treat those as unsupported too.
     * Rejected audio follows the configure_audio_mft failure path: muted
     * (acfg=0 in the debug string), video unaffected. */
    int eff = channels;
    if (asc && asc_len >= 2 && (asc[0] >> 3) != 31 /* AOT escape */) {
        int freqIdx = ((asc[0] & 7) << 1) | (asc[1] >> 7);
        if (freqIdx != 15 /* explicit-rate escape shifts the field */) {
            int cc = (asc[1] >> 3) & 0xF;
            if (cc < 1 || cc > 6) return 0;
            if (cc > eff) eff = cc; /* containers under-report; the ASC is what
                                     * the decoder parses, so target its width */
        }
    }
    if (eff > 6) return 0;

    d->asr = sample_rate; d->ach = eff; d->achSrc = eff;
    if (configure_audio_mft(d, asc, asc_len)) {
        d->acodec = BASIS_CODEC_AAC;
        d->aconfigured = true;
        d->pcm.frame = d->ach > 0 ? d->ach : 1;
        d->pcm.sr = d->asr > 0 ? d->asr : 48000;
    }
    return 0;
}

/* Upper bound on a single compressed access unit — far above any real one (an 8K
 * HEVC keyframe is a few MB), so a demuxer that ever declared a wild size can't
 * drive a huge MFCreateMemoryBuffer allocation. */
#define BASIS_MAX_INPUT_SAMPLE (64 * 1024 * 1024)

static IMFSample* make_input_sample(const uint8_t* data, int len, int64_t pts_us) {
    /* len/data come from the demuxer (attacker-controlled). Reject a wild size and
     * return NULL cleanly on a failed allocation rather than dereference a null buffer. */
    if (!data || len <= 0 || len > BASIS_MAX_INPUT_SAMPLE) return nullptr;
    IMFSample* s = nullptr; IMFMediaBuffer* b = nullptr;
    if (FAILED(MFCreateSample(&s))) return nullptr;
    if (FAILED(MFCreateMemoryBuffer((DWORD)len, &b))) { s->Release(); return nullptr; }
    BYTE* p = nullptr; DWORD maxlen = 0;
    HRESULT lhr = b->Lock(&p, &maxlen, nullptr);
    if (FAILED(lhr) || !p || maxlen < (DWORD)len) {
        if (SUCCEEDED(lhr)) b->Unlock();   /* locked but unusable: unlock before releasing */
        b->Release(); s->Release(); return nullptr;
    }
    memcpy(p, data, (size_t)len);
    if (FAILED(b->Unlock()) || FAILED(b->SetCurrentLength((DWORD)len)) ||
        FAILED(s->AddBuffer(b))) {
        b->Release(); s->Release(); return nullptr;
    }
    s->SetSampleTime((LONGLONG)pts_us * 10); /* us -> 100ns */
    b->Release();
    return s;
}

/* Split-stream thread-safety: submit_video (video demux thread) and submit_audio (audio
 * demux thread) can run concurrently. They are safe by separation — distinct MFTs (vdec vs
 * adec) feeding distinct outputs (the video frame path vs the PCM ring), with no shared
 * mutable state between them and atomic (Interlocked) debug counters. The render thread reads
 * each output under its own lock. Keep video-path and audio-path state disjoint to preserve
 * this; if that ever changes, serialise submission through a decoder mutex. */
extern "C" int basis_decoder_submit_video(basis_decoder_t* d, const uint8_t* annexb, int len, int64_t pts_us, int key) {
    /* Bound len here, before the AV1 configOBU concatenation below adds to it — so
     * the total can't overflow int or drive an oversized allocation. */
    if (!d || !d->vdec || !annexb || len <= 0 || len > BASIS_MAX_INPUT_SAMPLE) return -1;
    /* First video AU after a seek: flush the MFT so its reorder buffer can't emit
     * retained pre-seek frames into the ring, and drop the frames already in the
     * ring. Demux thread owns vdec and writes the ring (drain_video below), so both
     * are safe here; ring slots are aligned int64, cleared the same lock-free way
     * they're written. */
    LONG svg = InterlockedCompareExchange(&d->seekGen, 0, 0);
    if (svg != d->videoSeekGen) {
        d->videoSeekGen = svg;
        d->vdec->ProcessMessage(MFT_MESSAGE_COMMAND_FLUSH, 0);
        for (int i = 0; i < basis_decoder::RING; ++i) d->ringPts[i] = INT64_MIN;
        d->vPrerollCutUs = InterlockedCompareExchange64(&d->seekTargetUs, 0, 0);
        d->vAwaitKey = 1;
        d->vAwaitKeyPts = INT64_MIN;
        d->vAwaitDrained = 0;
        /* The ring is this (demux) thread's to clear — do it here only, then publish
         * the generation so the render leg knows the pre-seek frames are gone. That
         * keeps a single writer of the ring on seek and stops the render leg from
         * clearing frames this thread may already have repopulated. */
        InterlockedExchange(&d->videoSeekAck, svg);
    }
    if (key && d->vAwaitKey && d->vAwaitKeyPts == INT64_MIN) d->vAwaitKeyPts = pts_us;
    IMFSample* s;
    bool carried_config = false;
    if (d->vConfigObusLen > 0) {
        /* first AV1 AU: prepend the held configOBUs so the decoder sees the
         * sequence header before any frame data */
        if (d->vConfigObusLen > BASIS_MAX_INPUT_SAMPLE - len) return -1; /* concat would overflow the cap */
        int total = d->vConfigObusLen + len;
        uint8_t* tmp = (uint8_t*)malloc((size_t)total);
        if (tmp) {
            memcpy(tmp, d->vConfigObus, d->vConfigObusLen);
            memcpy(tmp + d->vConfigObusLen, annexb, len);
            s = make_input_sample(tmp, total, pts_us);
            free(tmp);
            carried_config = true;
        } else {
            s = make_input_sample(annexb, len, pts_us);
        }
    } else {
        s = make_input_sample(annexb, len, pts_us);
    }
    if (!s) return -1;   /* sample allocation failed; skip this AU rather than crash */

    /* Feed the AU, draining output to make room rather than dropping it. The
     * decoder must accept every frame or playback decimates to the rate at which
     * the input queue happens to have room. */
    bool consumed = false;
    for (int attempt = 0; attempt < 16 && !consumed; ++attempt) {
        HRESULT hr = d->vdec->ProcessInput(0, s, 0);
        if (hr == MF_E_NOTACCEPTING) {
            InterlockedIncrement(&d->dbg_in_rej);
            drain_video(d); /* pull outputs to free input slots, then retry */
        } else {
            if (SUCCEEDED(hr)) InterlockedIncrement(&d->dbg_in_ok);
            consumed = true;
        }
    }
    s->Release();
    /* Only drop the held configOBUs once the sample carrying them was accepted;
     * otherwise the next AU must re-prepend them or AV1 never sees its sequence
     * header. If the sample was never consumed, report it so the caller knows. */
    if (consumed && carried_config) d->vConfigObusLen = 0;
    drain_video(d);
    return consumed ? 0 : -1;
}

/* Source-order -> WAVE-order channel map for the Blu-ray HDMV LPCM
 * channel_assignment values whose stream order differs from WAVE (Blu-ray
 * places the LFE last and the side pair before the rears). The index tables
 * match ffmpeg's pcm_bluray decoder remap for assignments 9 (5.1), 10 (7.0)
 * and 11 (7.1), and were verified by ear against a 7.1 channel-marker stream.
 * NULL = identity (mono/stereo/3.0/4.0/5.0 already arrive in WAVE order). */
static const int* lpcm_remap(int assign) {
    static const int k51[6] = { 0, 1, 2, 4, 5, 3 };
    static const int k70[7] = { 0, 1, 2, 5, 3, 4, 6 };
    static const int k71[8] = { 0, 1, 2, 6, 4, 5, 7, 3 };
    if (assign == 9) return k51;
    if (assign == 10) return k70;
    if (assign == 11) return k71;
    return nullptr;
}

static void submit_lpcm(basis_decoder* d, const uint8_t* p, int len, int64_t pts_us) {
    int ch = d->ach;
    int bytes = d->aLpcmBits / 8;
    int frame_bytes = ch * bytes;
    int frames = len / frame_bytes;
    if (frames <= 0) return;
    int floats = frames * ch;
    if (floats > d->aLpcmBufCap) {
        float* nb = (float*)realloc(d->aLpcmBuf, sizeof(float) * floats);
        if (!nb) return;
        d->aLpcmBuf = nb; d->aLpcmBufCap = floats;
    }
    const int* map = lpcm_remap(d->aLpcmAssign);
    for (int f = 0; f < frames; ++f) {
        const uint8_t* s = p + f * frame_bytes;
        float* o = d->aLpcmBuf + f * ch;
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
    d->pcm.write(d->aLpcmBuf, floats, pts_us);
    InterlockedIncrement(&d->dbg_aout);
}

/* One Opus packet -> float PCM straight into the ring (§4-b2). Like the LPCM
 * bypass, no OS decoder is involved. */
/* Opus mapping family 1 delivers Vorbis channel order; the ring (and Unity) want
 * WAVE/SMPTE order. Table maps each WAVE output channel to its Vorbis source
 * index; NULL = identity (mono, stereo, and quad already coincide). Per RFC 7845
 * §5.1.1 (Vorbis order) and WAVEFORMATEXTENSIBLE (WAVE order). */
static const int* opus_vorbis_to_wave(int ch) {
    static const int m3[3] = { 0, 2, 1 };                      /* L C R      -> L R C */
    static const int m5[5] = { 0, 2, 1, 3, 4 };                /* FL C FR RL RR -> FL FR C RL RR */
    static const int m6[6] = { 0, 2, 1, 5, 3, 4 };             /* +LFE last -> LFE at index 3 (5.1) */
    static const int m7[7] = { 0, 2, 1, 6, 5, 3, 4 };          /* 6.1 */
    static const int m8[8] = { 0, 2, 1, 7, 5, 6, 3, 4 };       /* 7.1 */
    switch (ch) {
        case 3: return m3; case 5: return m5; case 6: return m6;
        case 7: return m7; case 8: return m8; default: return nullptr;
    }
}

static void submit_opus(basis_decoder* d, const uint8_t* data, int len, int64_t pts_us) {
    if (!d->opusDec || !g_opus_ok) return;
    int ch = d->ach > 0 ? d->ach : 2;
    int need = 5760 * ch;             /* max Opus frame (120 ms @ 48k) * channels */
    if (d->opusBufCap < need) {
        float* nb = (float*)realloc(d->opusBuf, sizeof(float) * (size_t)need);
        if (!nb) return;
        d->opusBuf = nb; d->opusBufCap = need;
    }
    int n = d->opusIsMS
        ? g_opus.ms_decode_float((OpusMSDecoder*)d->opusDec, data, (int32_t)len, d->opusBuf, 5760, 0)
        : g_opus.decode_float((OpusDecoder*)d->opusDec, data, (int32_t)len, d->opusBuf, 5760, 0);
    if (n <= 0) return;               /* <0 = decode error, 0 = nothing produced */

    /* Drop the encoder pre-skip from the head of the stream (once); libopus
     * won't, and the priming samples aren't real audio. Advance the pts by what
     * we drop so the remainder stays on the block timeline. */
    int drop = d->opusPreSkip < n ? d->opusPreSkip : n;
    d->opusPreSkip -= drop;
    int remain = n - drop;
    if (remain <= 0) return;
    int64_t out_pts = pts_us + (int64_t)drop * 1000000LL / 48000;
    /* Honour the media-time origin too (shared with #959; usually 0 for WebM). */
    int origin = basis_frames_before_origin(out_pts, remain, 48000);
    if (origin >= remain) return;
    float* out = d->opusBuf + (int64_t)(drop + origin) * ch;
    int outframes = remain - origin;
    /* Family 1 is Vorbis channel order; reorder to WAVE before the ring. Family 0
     * is mono/stereo and family 255 has no defined layout — neither is remapped. */
    if (d->opusMappingFamily == 1) {
        const int* map = opus_vorbis_to_wave(ch);
        if (map) {
            for (int f = 0; f < outframes; ++f) {
                float* fr = out + (int64_t)f * ch;
                float tmp[8];
                for (int c = 0; c < ch; ++c) tmp[c] = fr[map[c]];
                memcpy(fr, tmp, sizeof(float) * (size_t)ch);
            }
        }
    }
    d->pcm.write(out, outframes * ch, out_pts + (int64_t)origin * 1000000LL / 48000);
    InterlockedIncrement(&d->dbg_aout);
}

extern "C" int basis_decoder_submit_audio(basis_decoder_t* d, const uint8_t* data, int len, int64_t pts_us) {
    if (!d || !data || len <= 0) return -1;
    /* First audio AU after a seek: drop the stale pre-seek ring so this post-seek
     * audio serves immediately (BUG: multi-second post-seek silence), and flush
     * the MF decoder so it doesn't overlap-add across the discontinuity. Runs on
     * the demux thread, which is the only thread that touches `adec`. */
    LONG sg = InterlockedCompareExchange(&d->seekGen, 0, 0);
    if (sg != d->audioSeekGen) {
        d->audioSeekGen = sg;
        d->pcm.flush();
        if (d->adec) d->adec->ProcessMessage(MFT_MESSAGE_COMMAND_FLUSH, 0);
        /* Opus bypasses the MFT; reset its predictive/history state so the first
         * post-seek packet doesn't decode against the pre-seek timeline. */
        if (d->opusDec) {
            if (d->opusIsMS) { if (g_opus.ms_ctl)  g_opus.ms_ctl((OpusMSDecoder*)d->opusDec, OPUS_RESET_STATE); }
            else             { if (g_opus.dec_ctl) g_opus.dec_ctl((OpusDecoder*)d->opusDec, OPUS_RESET_STATE); }
        }
        /* Seed the no-timestamp fallback from this AU so post-seek chunks land on
         * the target timeline; 0 would put them at the start and the serve gate
         * would trim or mis-time them. */
        d->aPtsFallback = pts_us;
    }
    if (d->acodec == BASIS_CODEC_LPCM) { submit_lpcm(d, data, len, pts_us); return 0; }
    if (d->acodec == BASIS_CODEC_OPUS) { submit_opus(d, data, len, pts_us); return 0; }
    if (!d->adec) return -1;
    IMFSample* s = make_input_sample(data, len, pts_us);
    if (!s) return -1;   /* sample allocation failed; skip this frame rather than crash */
    HRESULT hr = d->adec->ProcessInput(0, s, 0);
    s->Release();
    if (hr == MF_E_NOTACCEPTING) { drain_audio(d); }
    drain_audio(d);
    return 0;
}

extern "C" int basis_decoder_try_open_url(basis_decoder_t* d, const char* url) {
    (void)d; (void)url;
    return 0; /* Windows always uses the core demuxers + WinHTTP */
}

/* ---- render thread ------------------------------------------------------ */

/* Block until the decode-device copy into outSharedD12 has retired on the GPU, so the
 * caller can release the shared surface and publish only a fully-written frame — Flush
 * alone submits but does not wait, and the surface is single-buffered, so returning while
 * the copy is in flight would let Unity sample a torn write. Polls an event query; End()
 * re-arms it each present, so no stale completion carries over. Returns false only when
 * completion can never come — a genuine GetData error (device lost) or a multi-second
 * stall (GPU wedged) — both of which it surfaces as an engine error; the caller then
 * neither releases the surface nor advances. A normal copy retires in well under a ms. */
static bool present_copy_complete(basis_decoder* d, int64_t freq) {
    if (!d->presentQuery) return false; /* no primitive to confirm completion */
    d->ctxDec->End(d->presentQuery);
    d->ctxDec->Flush();
    LARGE_INTEGER t0; QueryPerformanceCounter(&t0);
    for (;;) {
        BOOL gpuDone = FALSE;
        HRESULT hr = d->ctxDec->GetData(d->presentQuery, &gpuDone, sizeof(gpuDone), 0);
        if (hr == S_OK) return true;        /* GPU reached End — copy retired */
        if (hr != S_FALSE) {                /* genuine error, e.g. device removed */
            basis_engine_set_error(d->engine, "D3D12 present copy completion query failed");
            return false;
        }
        LARGE_INTEGER tn; QueryPerformanceCounter(&tn);
        if (tn.QuadPart - t0.QuadPart >= freq) {   /* ~1s: a copy this slow means a wedged GPU */
            basis_engine_set_error(d->engine, "D3D12 present copy did not complete (GPU stalled)");
            return false;
        }
        Sleep(0);
    }
}

extern "C" int basis_decoder_render_update(basis_decoder_t* d) {
    if (!d) return -1;
    InterlockedIncrement(&d->dbg_render);
    if (basis_engine_is_paused(d->engine)) return 0;
    if (d->writeSeq == 0) return 0;

    LARGE_INTEGER nowq; QueryPerformanceCounter(&nowq);
    EnterCriticalSection(&d->presentLock);

    /* First render after a seek: re-anchor the present clock to the first post-seek
     * frame instead of the stale `newest` — without a re-anchor the clock stays
     * clamped and freezes until a post-seek frame arrives (a ~18s video hang on a
     * cold forward seek). The ring is cleared by the demux thread that owns it (see
     * submit_video); this leg only re-anchors, then waits on that clear before
     * proceeding so it never anchors to a stale frame or races the producer. */
    {
        LONG sg = InterlockedCompareExchange(&d->seekGen, 0, 0);
        if (sg != d->renderSeekGen) {
            d->renderSeekGen = sg;
            d->clockStarted = false;
            d->primeStartQpc = 0;
            d->lastPresentedPts = INT64_MIN;
            d->videoBasePts = INT64_MIN;
            /* Report the target now so get_position_us tracks before the first
             * post-seek frame presents; render overwrites it once it does. */
            InterlockedExchange64(&d->presentedPosUs, InterlockedCompareExchange64(&d->seekTargetUs, 0, 0));
        }
        /* Hold until the demux thread has flushed vdec and dropped the pre-seek
         * frames. The prime/anchor path below then re-locks to the first post-seek
         * frame the producer writes. */
        if (InterlockedCompareExchange(&d->videoSeekAck, 0, 0) != sg) {
            LeaveCriticalSection(&d->presentLock);
            return 0;
        }
    }

    /* newest available PTS in the ring */
    int64_t newest = INT64_MIN;
    for (int i = 0; i < basis_decoder::RING; ++i) if (d->ringPts[i] > newest) newest = d->ringPts[i];
    /* Audio-first start (live): with no decodable video yet — a mid-GOP join
     * waits for the next IDR, up to a full GOP — run the presentation clock
     * from the audio delivery edge instead, so audio plays immediately and
     * video joins the already-running clock when its first frame decodes
     * (both tracks share a timeline, so joining needs no re-anchor). The
     * audio edge stands in for `newest` below; the present loop no-ops on an
     * empty frame ring. VOD keeps the primed, synchronised start, and an
     * audio-only stream (video never configured) keeps its ungated serve —
     * seeding a clock for it would silently convert that documented path
     * into gated playback. */
    int noVideoYet = (newest == INT64_MIN);
    if (noVideoYet) {
        if (!d->vconfigured || !d->aconfigured || basis_engine_is_paced(d->engine)) { LeaveCriticalSection(&d->presentLock); return 0; }
        newest = d->pcm.newest_pts();
        if (newest == INT64_MIN) { LeaveCriticalSection(&d->presentLock); return 0; }
    }

    /* Presentation clock: wall-rate (QPC) advance, slewed toward the live decode
     * edge with a capped correction rate — 50% during the first ~1.2s after an
     * anchor (startup pipeline-fill converges quickly), ~2% after. The cap keeps
     * the present cadence steady when the decode edge moves in bursts (muxed
     * demux clumps, network jitter): burst error is absorbed by the jitter
     * buffer instead of being chased, so frames keep crossing the present point
     * at 1x rather than in slow/fast swings that hold a frame long and then
     * skip one to catch up. The clock is also clamped at edge + buffer so a
     * delivery stall can't run it ahead — presents freeze at the buffer edge
     * and resume without a skip burst. Large gaps (startup, rebuffer,
     * discontinuity) hard-resync. */
    int64_t freq = d->qpcFreq.QuadPart ? d->qpcFreq.QuadPart : 1;
    bool paced = basis_engine_is_paced(d->engine) != 0;
    int64_t nowMedia;
    int64_t interval = d->frameIntervalUs > 0 ? d->frameIntervalUs : 16666;

    /* Paced hold: with audio, the jitter cushion both streams play behind —
     * the audio serve is gated to the same clock, so the video hold is also
     * the audio bank that absorbs delivery burst/starve cycles. Capped to the
     * ring's frame span so the decoder can't lap the presenter. */
    int64_t pacedBuf = d->aconfigured ? 460000 : 250000;
    {
        int64_t ringSpanCap = (int64_t)(basis_decoder::RING - 6) * interval;
        if (pacedBuf > ringSpanCap) pacedBuf = ringSpanCap;
    }

    /* VOD prime: hold presentation until the ring has banked a hold's worth
     * of frames (3s fallback for sources that can't fill it), so a start
     * against struggling delivery buffers first instead of presenting the
     * first frame, starving, and churning through resyncs. Live starts at
     * the edge immediately — its fast-start ramp owns that experience. */
    if (paced && !d->clockStarted) {
        if (!d->primeStartQpc) d->primeStartQpc = nowq.QuadPart;
        int held = 0;
        for (int i = 0; i < basis_decoder::RING; ++i) if (d->ringPts[i] != INT64_MIN) held++;
        int64_t waitedUs = (nowq.QuadPart - d->primeStartQpc) * 1000000LL / freq;
        if ((int64_t)held * interval < pacedBuf + 2 * interval && waitedUs < 3000000) {
            LeaveCriticalSection(&d->presentLock);
            return 0;
        }
    }

    if (!d->clockStarted) {
        d->clockStarted = true;
        d->wallStartQpc = nowq.QuadPart;
        d->lastRenderQpc = nowq.QuadPart;
        d->mediaStartUs = newest;
        d->lastPresentedPts = INT64_MIN;
    }
    int64_t dtUs = (nowq.QuadPart - d->lastRenderQpc) * 1000000LL / freq;
    d->lastRenderQpc = nowq.QuadPart;
    if (dtUs < 0) dtUs = 0; else if (dtUs > 1000000) dtUs = 1000000;
    if (dtUs > 1000 && dtUs < 100000) d->renderTickUs += (dtUs - d->renderTickUs) / 8;
    int64_t wallElapsed = (int64_t)((nowq.QuadPart - d->wallStartQpc) * 1000000LL / freq);

    if (paced) {
        /* Paced (VOD) clock: same wall-rate slew, tuned for VOD — a fixed
         * buffer (no dynamic sizing) and the audio gate published directly
         * (no 2s EMA). Delivery is throttled to ~1x upstream, so the edge
         * never leaps and the hard-resync below only fires on a real
         * discontinuity (loop/seek), never the per-segment wobble that
         * destabilises live. */
        int64_t clk = d->mediaStartUs + wallElapsed;
        int64_t err = newest - clk;
        /* Positive error up to the ring span is normal here — startup pipeline
         * fill and post-stall delivery catch-up both push the edge ahead of
         * the clock in bulk — and VOD has nowhere it needs to hurry back to,
         * so it is slewed away at the capped rate rather than snapped or
         * chased (a snap skips seconds of content; a fast chase plays visibly
         * sped-up). Resync only when the writer is about to lap the ring
         * (a stall so long that holding 1x would present overwritten slots)
         * or on a backward jump (loop seam). */
        int64_t posLimit = (int64_t)(basis_decoder::RING - 4) * interval;
        if (posLimit < 1000000) posLimit = 1000000;
        if (err > posLimit || err < -1000000) {
            d->wallStartQpc = nowq.QuadPart;
            d->mediaStartUs = newest;
            d->lastPresentedPts = INT64_MIN;
            clk = newest;
            wallElapsed = 0;
        } else {
            int64_t corr = err * dtUs / 250000;        /* ~0.25s lock toward the edge */
            int64_t cap = dtUs / 50;
            if (corr > cap) corr = cap; else if (corr < -cap) corr = -cap;
            d->mediaStartUs += corr;
            clk += corr;
        }
        /* Stall guard: clamp at edge + buffer, the point past which nothing is
         * due anyway, so a delivery stall can't run the clock ahead of the
         * frames (resume would then dump the backlog in a skip burst). */
        int64_t edgeMax = newest + pacedBuf;
        if (clk > edgeMax) { d->mediaStartUs -= clk - edgeMax; clk = edgeMax; }
        nowMedia = clk - pacedBuf;
        d->dbg_lagms = (LONG)((newest - nowMedia) / 1000);
        int64_t qpcUs = (nowq.QuadPart - d->createQpc.QuadPart) * 1000000LL / freq;
        InterlockedExchange64(&d->audClockOffsetUs, nowMedia - qpcUs);
    } else {
    int64_t liveClock = d->mediaStartUs + wallElapsed;
    int64_t err = newest - liveClock;            /* >0: clock behind the live edge */
    if (err > 700000 || err < -700000) {
        d->wallStartQpc = nowq.QuadPart;
        d->mediaStartUs = newest;
        d->lastPresentedPts = INT64_MIN;
        liveClock = newest;
        wallElapsed = 0;
    } else {
        int64_t corr = err * dtUs / 250000;      /* TAU ~0.25s lock toward live */
        int64_t cap = (wallElapsed < 1200000) ? dtUs / 2 : dtUs / 50;
        if (corr > cap) corr = cap; else if (corr < -cap) corr = -cap;
        d->mediaStartUs += corr;
        liveClock += corr;
    }
    /* Stall guard: clamp at edge + buffer, the point past which nothing is due
     * anyway, so a delivery stall can't run the clock ahead of the frames
     * (resume would then dump the backlog in a skip burst). */
    int64_t edgeMax = newest + d->bufferUs;
    if (liveClock > edgeMax) { d->mediaStartUs -= liveClock - edgeMax; liveClock = edgeMax; }

    /* Jitter buffer: present this far behind the live edge. Capped to the ring's
     * frame span so the decoder can't lap the presenter — a fixed-ms buffer would
     * overrun the ring at high source rates, so the ceiling scales with the source
     * frame period (e.g. 120ms is fine at 60fps but clamps near 100ms at 250fps).
     * Dynamic mode grows fast on underrun risk and shrinks symmetrically when
     * over-buffered, with a 200ms hysteresis to avoid grow/shrink fighting. */
    int64_t maxBuf = (int64_t)(basis_decoder::RING - 6) * interval;
    if (maxBuf < 60000) maxBuf = 60000;
    int64_t buf = d->bufferUs;
    int64_t fill = newest - (liveClock - buf);
    if (d->bufferMode == 1) {
        if (fill < 2 * interval) buf += interval;
        else if (fill > buf + 200000) buf -= 10000;
    }
    /* With audio configured, the buffer is the shared jitter cushion: the
     * audio serve is gated to this same clock, so presenting this far behind
     * the live edge is what banks enough audio in the ring to ride out
     * delivery burst/starve cycles (audio cannot be released from ahead of
     * the decode edge). */
    int64_t minBuf = d->aconfigured ? 460000 : 40000;
    if (buf < minBuf) buf = minBuf;
    if (buf > maxBuf) buf = maxBuf;
    d->bufferUs = (LONG)buf;

    /* Fast start (video-only): ramp the effective cushion from ~0 up to the
     * target over the first ~1.2s, so the first decoded frame is presented
     * almost immediately, then settle into the full buffer. With audio the
     * start is synchronised on the full buffer instead — the ramp advances
     * the clock at less than 1x, which would force the PTS-gated audio serve
     * to under-fill every block (a crackly first second); holding both
     * streams to the same fixed timeline costs ~half a second of start-up
     * and buys a clean, in-sync first frame. wallElapsed resets on a hard
     * resync, so a rebuffer re-primes the video-only ramp too. */
    int64_t effBuf = (!d->aconfigured && wallElapsed < 1200000) ? (buf * wallElapsed / 1200000) : buf;
    nowMedia = liveClock - effBuf;
    d->dbg_lagms = (LONG)((newest - nowMedia) / 1000);

    /* Publish the audio-gate clock as a low-passed offset from QPC (~2s EMA);
     * large jumps (startup, hard resync, discontinuity) snap instead of
     * filtering so the gate follows resyncs immediately. */
    {
        int64_t qpcUs = (nowq.QuadPart - d->createQpc.QuadPart) * 1000000LL / freq;
        int64_t off = nowMedia - qpcUs;
        LONGLONG prev = InterlockedCompareExchange64(&d->audClockOffsetUs, 0, 0);
        if (prev == INT64_MIN || off - prev > 700000 || off - prev < -700000) {
            InterlockedExchange64(&d->audClockOffsetUs, off);
        } else {
            InterlockedExchange64(&d->audClockOffsetUs, prev + (off - prev) * dtUs / 2000000);
        }
    }
    }

    /* recover from non-monotonic/bogus PTS (lastPresentedPts stuck above the ring) */
    if (d->lastPresentedPts != INT64_MIN && d->lastPresentedPts > newest) d->lastPresentedPts = INT64_MIN;

    /* Present the latest frame that is due and newer than the last shown. The
     * due check looks ahead half a render tick so a frame lands on the tick
     * nearest its due time, not the tick after it: due times drift through the
     * tick phase whenever the source rate doesn't divide the refresh rate
     * (23.976fps against 60Hz), and always latching a full tick late turns
     * that drift into an extra-tick hold followed by a visible skip. Capped at
     * half the source frame period so a high-rate source can't be shown a
     * whole frame early. */
    int64_t lookahead = d->renderTickUs / 2;
    if (lookahead > interval / 2) lookahead = interval / 2;
    int64_t dueBy = nowMedia + lookahead;
    int best = -1; int64_t bestPts = d->lastPresentedPts;
    for (int i = 0; i < basis_decoder::RING; ++i) {
        int64_t p = d->ringPts[i];
        if (p == INT64_MIN) continue;
        if (p > bestPts && p <= dueBy) { best = i; bestPts = p; }
    }
    if (best < 0) { InterlockedIncrement(&d->dbg_nodue); LeaveCriticalSection(&d->presentLock); return 0; }

    if (d->api == BASIS_GFX_D3D11 && d->outTexD11 && d->ringOnUnity[best] && d->ctxUnity) {
        HRESULT a = d->ringMutexUnity[best] ? d->ringMutexUnity[best]->AcquireSync(0, 8) : S_OK;
        if (a == S_OK) {
            d->ctxUnity->CopyResource(d->outTexD11, d->ringOnUnity[best]);
            if (d->ringMutexUnity[best]) d->ringMutexUnity[best]->ReleaseSync(0);
            d->lastPresentedPts = bestPts;
            InterlockedExchange64(&d->presentedPosUs, bestPts);
            InterlockedIncrement(&d->dbg_copy);
            if (d->ttffMs < 0) {
                LARGE_INTEGER tnow; QueryPerformanceCounter(&tnow);
                d->ttffMs = (LONG)((tnow.QuadPart - d->createQpc.QuadPart) * 1000 / freq);
            }
        } else {
            InterlockedIncrement(&d->dbg_acqfail);
        }
    } else if (d->api == BASIS_GFX_D3D12 && d->outSharedD12 && d->outSharedD12Mutex && d->ctxDec) {
        /* Make sure Unity has opened the shared output on its D3D12 device first —
         * without that external texture there is nothing for it to sample, so
         * presentation state must not advance until the open succeeds. */
        if (!d->outTexD12 && d->outSharedD12Handle) {
            ID3D12Device* dev12 = (ID3D12Device*)basis_gfx_get_d3d12_device();
            if (dev12) {
                ID3D12Resource* res = nullptr;
                HRESULT hr = dev12->OpenSharedHandle(d->outSharedD12Handle, IID_PPV_ARGS(&res));
                if (SUCCEEDED(hr)) {
                    d->outTexD12 = res;
                    d->d12OpenFail = 0;
                } else if (++d->d12OpenFail == 120) {
                    /* Persisted ~2s: the shared frame never reached Unity. Surface the
                     * HRESULT once (further frames keep retrying) so this integration
                     * failure is diagnosable instead of a silent black screen. */
                    char m[96];
                    snprintf(m, sizeof(m), "D3D12 OpenSharedHandle failed (hr=0x%08lX)", (unsigned long)hr);
                    basis_engine_set_error(d->engine, m);
                }
            }
        }
        /* Copy the due ring slot into the typeless shared output on the decode device
         * (D3D11). Unity's D3D12 device has no handoff sync with this copy, so wait for
         * GPU completion before publishing. Publish only when completion is confirmed
         * AND Unity holds the external texture, so it never shows a half-written or
         * not-yet-available frame; otherwise leave the slot for the next present. D3D11
         * copies on Unity's own (serialized) context instead — there is no Unity D3D11
         * context here, so the copy runs decode-side. */
        if (d->outTexD12) {
            HRESULT a = d->ringMutexDec[best] ? d->ringMutexDec[best]->AcquireSync(0, 8) : S_OK;
            if (a == S_OK) {
                HRESULT ad = d->outSharedD12Mutex->AcquireSync(0, 8);
                if (ad == S_OK) {
                    d->ctxDec->CopyResource(d->outSharedD12, d->ringTex[best]);
                    if (present_copy_complete(d, freq)) {
                        /* Release only after the copy retired, so the shared surface is
                         * never handed on (or sampled) mid-write. */
                        d->outSharedD12Mutex->ReleaseSync(0);
                        d->lastPresentedPts = bestPts;
                        InterlockedExchange64(&d->presentedPosUs, bestPts);
                        InterlockedIncrement(&d->dbg_copy);
                        if (d->ttffMs < 0) {
                            LARGE_INTEGER tnow; QueryPerformanceCounter(&tnow);
                            d->ttffMs = (LONG)((tnow.QuadPart - d->createQpc.QuadPart) * 1000 / freq);
                        }
                    } else {
                        /* Completion can't be confirmed — present_copy_complete only
                         * returns false on a device-lost / wedged GPU it already flagged
                         * as an engine error. Keep the mutex held rather than expose an
                         * in-flight write; teardown frees it as playback stops. */
                        InterlockedIncrement(&d->dbg_drop);
                    }
                }
                if (d->ringMutexDec[best]) d->ringMutexDec[best]->ReleaseSync(0);
            } else {
                InterlockedIncrement(&d->dbg_acqfail);
            }
        }
    }
    LeaveCriticalSection(&d->presentLock);
    return 0;
}

extern "C" void basis_decoder_render_release(basis_decoder_t* d) {
    if (!d) return;
    EnterCriticalSection(&d->presentLock);
    release_shared_locked(d);
    d->sharedW = d->sharedH = 0;
    LeaveCriticalSection(&d->presentLock);
}

extern "C" void* basis_decoder_get_texture(basis_decoder_t* d, int* w, int* h) {
    if (!d) return nullptr;
    if (w) *w = d->sharedW;
    if (h) *h = d->sharedH;
    if (d->api == BASIS_GFX_D3D12) return d->outTexD12;
    return d->outTexD11;
}

extern "C" uint64_t basis_decoder_get_frame_counter(basis_decoder_t* d) {
    return d ? (uint64_t)d->frameCounter : 0;
}
extern "C" int basis_decoder_get_video_size(basis_decoder_t* d, int* w, int* h) {
    if (!d || d->sharedW <= 0) return -1;
    if (w) *w = d->sharedW; if (h) *h = d->sharedH; return 0;
}
extern "C" int basis_decoder_get_frame_origin(basis_decoder_t* d) { return d ? (int)d->frameTopLeft : 0; }

extern "C" void basis_decoder_notify_end_of_stream(basis_decoder_t* d) {
    if (!d || !d->vdec) return;
    /* Caller is the video-submit (demux) thread, which owns vdec and the ring —
     * same ownership as submit_video, so drain_video is safe here. The MFT is
     * synchronous: after DRAIN, ProcessOutput hands over every retained frame,
     * and drain_video's until-NEED_MORE_INPUT loop is that documented pattern.
     * Best-effort by design: if either message fails, the tail stays inside
     * the MFT, presentation_pending never sees it, and the core's drain-wait
     * ends on its idle cap — the frames are unrecoverable either way, so
     * there is nothing a propagated HRESULT could change. */
    d->vdec->ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
    d->vdec->ProcessMessage(MFT_MESSAGE_COMMAND_DRAIN, 0);
    drain_video(d);
}

extern "C" int basis_decoder_presentation_pending(basis_decoder_t* d) {
    if (!d) return 0;
    int pending = 0;
    EnterCriticalSection(&d->presentLock);
    /* Compare against lastPresentedPts, not presentedPosUs: a seek snaps the
     * latter to the target before anything presents, so a banked frame whose
     * PTS lands exactly on the target would read as already shown and the
     * EOS drain could raise ENDED without it. lastPresentedPts stays
     * INT64_MIN until a frame genuinely presents, and the final frame's
     * lingering ring slot equals it (not >), so a finished play-out still
     * reads as drained. */
    int64_t presented = d->lastPresentedPts;
    for (int i = 0; i < basis_decoder::RING; ++i)
        if (d->ringPts[i] != INT64_MIN && d->ringPts[i] > presented) { pending = 1; break; }
    LeaveCriticalSection(&d->presentLock);
    if (!pending) {
        EnterCriticalSection(&d->pcm.cs);
        pending = d->pcm.fill() > 0;
        LeaveCriticalSection(&d->pcm.cs);
    }
    return pending;
}

extern "C" void basis_decoder_seek(basis_decoder_t* d, int64_t target_us) {
    if (!d) return;
    /* Record the pre-seek audio front before the flush clears it, so the audio-only
     * settle can tell post-seek audio (near the target) from a stale pre-seek frame
     * that slipped the drop (near this origin) — see get_position_us. A rapid re-seek
     * with the ring already empty falls back to the prior target (where we were). */
    EnterCriticalSection(&d->pcm.cs);
    int64_t from = d->pcm.playedUs;
    LeaveCriticalSection(&d->pcm.cs);
    d->seekFromUs = from != INT64_MIN ? from : InterlockedCompareExchange64(&d->seekTargetUs, 0, 0);
    /* Drop any pre-seek PCM still queued so the audio callback stops serving it
     * immediately rather than up to the next audio AU. pcm.flush() is cs-guarded,
     * safe from this (caller) thread; the codec-state reset stays on the submit
     * thread where the MFT/Opus decoder is owned. */
    d->pcm.flush();
    /* Invalidate the audio serve clock: it re-derives from presents, and until the
     * first post-seek frame presents it still describes the pre-seek timeline. On a
     * backward seek that stale (higher) clock reads freshly banked post-target audio
     * as long-stale and the serve trims it away — eating the first second of audio
     * after video resumes. INT64_MIN is the serve's hold state (a stream with video
     * holds audio until the clock exists), so post-seek audio banks through the
     * settle and releases in sync with the first presented frame. Audio-only stays
     * ungated: its offset never leaves INT64_MIN in the first place. */
    InterlockedExchange64(&d->audClockOffsetUs, INT64_MIN);
    /* Latch target before bumping the generation so any leg that observes the new
     * generation reads the matching target. */
    InterlockedExchange64(&d->seekTargetUs, target_us);
    if (d->vdec) {
        /* Video present: snap the presentation clock to the target so the seek bar
         * shows the target immediately, before the first post-seek frame presents. */
        InterlockedExchange64(&d->presentedPosUs, target_us);
    } else {
        /* Audio-only: no frame ever presents, so nothing would advance a pinned
         * presentedPosUs and get_position_us (which returns it whenever >= 0) would
         * freeze at the target. Leave it unset so get_position_us reports the audio
         * front (playedUs), and mark the position settling: the ring was just
         * flushed, but a pre-seek AU decoded in the window before the demuxer
         * repositions can still drain a stale chunk into it, which would bounce the
         * reported position (and the seek bar) to the old spot. get_position_us
         * holds at the target through the settle until post-seek audio serves near
         * it — the audio mirror of the video render leg re-anchoring to the target. */
        InterlockedExchange64(&d->presentedPosUs, -1);
        d->audioSettling = 1;
    }
    InterlockedIncrement(&d->seekGen);
}

extern "C" int64_t basis_decoder_get_position_us(basis_decoder_t* d) {
    if (!d) return -1;
    /* Presentation position once a frame has shown; decode-side before that
     * (start-up, audio-only) so early consumers still see the clock move. */
    int64_t presented = InterlockedCompareExchange64((volatile LONG64*)&d->presentedPosUs, 0, 0);
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
    EnterCriticalSection(&d->pcm.cs);
    int64_t played = d->pcm.playedUs;
    LeaveCriticalSection(&d->pcm.cs);
    if (d->audioSettling) {
        int64_t target = InterlockedCompareExchange64((volatile LONG64*)&d->seekTargetUs, 0, 0);
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
extern "C" int basis_decoder_get_audio_format(basis_decoder_t* d, int* r, int* c) {
    if (!d || !d->aconfigured) return -1;
    if (r) *r = d->asr ? d->asr : 48000;
    if (c) *c = d->ach ? d->ach : 2;
    return 0;
}
extern "C" int basis_decoder_read_audio(basis_decoder_t* d, float* out, int max_floats) {
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
    LONGLONG off = InterlockedCompareExchange64(&d->audClockOffsetUs, 0, 0);
    if (off != INT64_MIN) {
        LARGE_INTEGER q; QueryPerformanceCounter(&q);
        int64_t freq = d->qpcFreq.QuadPart ? d->qpcFreq.QuadPart : 1;
        target = (q.QuadPart - d->createQpc.QuadPart) * 1000000LL / freq + off + d->audLatencyUs;
    } else if (d->vconfigured) {
        return 0;
    }
    /* Hysteresis must exceed the sink's pull depth (it drains several DSP
     * blocks back-to-back); the reported output latency is that depth plus
     * headroom, so size the hold from it. */
    int64_t hold = 60000 + (int64_t)d->audLatencyUs;
    int n = d->pcm.read(out, max_floats, target, hold);
    if (n > 0 && d->ach > 0) InterlockedAdd64(&d->audioSamplesRead, (LONGLONG)(n / d->ach));
    return n;
}

extern "C" void basis_decoder_set_buffer(basis_decoder_t* d, int mode, int buffer_ms) {
    if (!d) return;
    d->bufferMode = (mode != 0) ? 1 : 0;
    if (buffer_ms > 0) d->bufferUs = (LONG)(buffer_ms * 1000);
}

extern "C" void basis_decoder_set_audio_latency(basis_decoder_t* d, int latency_us) {
    if (!d) return;
    if (latency_us < 0) latency_us = 0; else if (latency_us > 500000) latency_us = 500000;
    InterlockedExchange(&d->audLatencyUs, (LONG)latency_us);
}

extern "C" void basis_decoder_set_output_texture(basis_decoder_t* d, void* native_texture, int w, int h) {
    /* Windows uses D3D11/12 CreateExternalTexture (no Mali crash there), so the
     * AccessTexture path is not needed. Accept the call for ABI uniformity. */
    (void)d; (void)native_texture; (void)w; (void)h;
}

extern "C" int basis_decoder_get_debug(basis_decoder_t* d, char* buf, int size) {
    if (!d || !buf || size <= 0) return 0;
    /* vq = ring frames newer than the presented one; aq = audio queued (ms);
     * atrim = clock-gated trims fired; alat = the sink output latency the
     * serve target is biased by. Same keys as the Android backend so the
     * diagnostics CSV columns line up across platforms. */
    int vq = 0;
    int64_t presented = InterlockedCompareExchange64(&d->presentedPosUs, 0, 0);
    EnterCriticalSection(&d->presentLock);
    for (int i = 0; i < basis_decoder::RING; ++i)
        if (d->ringPts[i] != INT64_MIN && d->ringPts[i] > presented) vq++;
    LeaveCriticalSection(&d->presentLock);
    EnterCriticalSection(&d->pcm.cs);
    int aFill = d->pcm.fill();
    int aFrame = d->pcm.frame > 0 ? d->pcm.frame : 2;
    int aSr = d->pcm.sr > 0 ? d->pcm.sr : 48000;
    long aTrims = d->pcm.trims;
    LeaveCriticalSection(&d->pcm.cs);
    int aq = (int)((int64_t)(aFill / aFrame) * 1000 / aSr);
    return snprintf(buf, (size_t)size,
                    "blit=%ld copy=%ld render=%ld nodue=%ld acq=%ld lag=%ldms buf=%ldms mode=%d vq=%d aq=%dms atrim=%ld alat=%ldms ttff=%ldms | acfg=%d aout=%ld asr=%d",
                    (long)d->dbg_blit, (long)d->dbg_copy, (long)d->dbg_render, (long)d->dbg_nodue, (long)d->dbg_acqfail,
                    (long)d->dbg_lagms, (long)(d->bufferUs / 1000), (int)d->bufferMode, vq, aq, aTrims,
                    (long)(d->audLatencyUs / 1000), (long)d->ttffMs,
                    d->aconfigured ? 1 : 0, (long)d->dbg_aout, d->asr);
}
