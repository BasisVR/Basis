/*
 * basis_media_internal.h — shared types between the engine core, the protocol
 * demuxers, and the platform decode/present backends.
 *
 * Layering:
 *   basis_media_core.c          engine lifecycle, demux thread, state, dispatch
 *   protocol/ sources           URL/IO/bitstream + RTSP/RTMP/TS/MP4 demuxers
 *                               (push elementary streams into a basis_media_sink)
 *   windows/ + android/         basis_decoder: OS decode -> GPU texture + PCM ring
 *
 * Demuxers never touch the platform decoder directly; they push into a
 * basis_media_sink whose callbacks the engine implements (forwarding to the
 * active basis_decoder). That keeps the protocol code portable and unit-testable.
 */

#ifndef BASIS_MEDIA_INTERNAL_H
#define BASIS_MEDIA_INTERNAL_H

#include <stdint.h>
#include <stddef.h>
#include "basis_media_native.h"

#ifdef __cplusplus
extern "C" {
#endif

/* Elementary codec identifiers used across the sink boundary. The video ids
 * are also the public probe ids (basis_media_probe_video_codec). */
typedef enum basis_codec {
    BASIS_CODEC_NONE = 0,
    BASIS_CODEC_H264 = 1,
    BASIS_CODEC_H265 = 2,
    BASIS_CODEC_VP9  = 3,
    BASIS_CODEC_AV1  = 4,
    BASIS_CODEC_AAC  = 10,
    BASIS_CODEC_LPCM = 11,  /* raw integer PCM: Blu-ray HDMV LPCM (TS stream_type
                             * 0x80, big-endian) or RIFF/WAV (little-endian —
                             * flags byte 3 of the announce config blob) */
    BASIS_CODEC_OPUS = 12,  /* Opus in WebM (A_OPUS); extradata is the OpusHead */
    BASIS_CODEC_MP3  = 13   /* MPEG-1/2 Layer III; decoded in-box, no init data */
} basis_codec_t;

/* Sink the demuxers push into. All callbacks are invoked from the demux thread.
 * `user` is the owning basis_media_engine. Implementations must be fast and must
 * not block the demux thread for long. */
typedef struct basis_media_sink {
    void* user;

    /* Called once per elementary video track when the codec/config is first known.
     * `extradata` is codec config: for H.264 the SPS/PPS (Annex B or avcC — the
     * decoder accepts either), for H.265 the VPS/SPS/PPS, for AV1 the configOBUs
     * from the av1C record (the record's 4 header bytes stripped, so the blob is
     * valid low-overhead OBU syntax). May be NULL/0 when the config is inline in
     * the access units instead. */
    void (*on_video_format)(void* user, basis_codec_t codec,
                            const uint8_t* extradata, int extradata_len,
                            int width, int height);

    /* One coded video access unit: Annex B form (start-code separated NALUs)
     * for H.264/H.265; for VP9/AV1 one raw sample exactly as stored (a possible
     * VP9 superframe or AV1 temporal unit of low-overhead OBUs — fed to the
     * decoder whole, never split). pts_us is the
     * presentation timestamp; dts_us the decode timestamp, used for delivery
     * pacing (composition offsets can put pts_us further ahead than the pacing
     * lead — a demuxer without decode timestamps passes pts_us for both).
     * key != 0 marks an IDR/keyframe. */
    void (*on_video_au)(void* user, const uint8_t* annexb, int len,
                        int64_t pts_us, int64_t dts_us, int key);

    /* Called once when the audio codec/config is first known. For AAC, `asc` is
     * the AudioSpecificConfig (2+ bytes) when available. */
    void (*on_audio_format)(void* user, basis_codec_t codec,
                            int sample_rate, int channels,
                            const uint8_t* asc, int asc_len);

    /* One coded audio frame. For AAC this is a raw AAC frame (no ADTS header);
     * demuxers that produce ADTS strip it first. */
    void (*on_audio_frame)(void* user, const uint8_t* data, int len, int64_t pts_us);

    /* Lifecycle signals from the demuxer. */
    void (*on_state)(void* user, basis_media_state_t state);
    void (*on_error)(void* user, const char* message);
    void (*on_end_of_stream)(void* user);

    /* Total media duration once the container/playlist reveals one (VOD).
     * Live sources never call it. May be NULL (standalone harnesses); may fire
     * again on a reconnect re-parsing the same index. */
    void (*on_duration)(void* user, int64_t duration_us);

    /* Human-readable transport description once a protocol settles on one
     * (e.g. "RTSP over UDP", "RTSP over TCP (UDP unavailable)"). Only
     * protocols that negotiate call it; may be NULL. Exposed to the host via
     * basis_media_get_transport. */
    void (*on_transport)(void* user, const char* transport);

    /* Absolute-seek handshake for on-demand sources. A demuxer that can seek
     * polls it between samples: returns 1 and writes the target when a request
     * is pending, 0 otherwise. Each sink hands out a request once. May be NULL
     * (standalone harnesses, sources that never seek). */
    int (*take_seek)(void* user, int64_t* out_target_us);

    /* Demuxers poll this in their read loops; return 0 to unwind and exit. */
    int (*is_running)(void* user);
} basis_media_sink_t;

/* Generic blocking byte source for demuxers that read a continuous stream
 * (MPEG-TS / fMP4 over TCP or HTTP). Returns bytes read, 0 on EOF, <0 on error. */
typedef int (*basis_read_fn)(void* ctx, uint8_t* buf, int len);

/* A read_fn may return this (instead of bytes) at the exact boundary where the
 * source has repositioned for a seek; the next read delivers post-seek data.
 * The demuxer must drop any buffered/partial state and re-anchor the pace clock
 * (via sink->take_seek) before consuming further, so a stale pre-seek sample
 * can't survive the jump. This exact value is the reposition signal; any other
 * negative return is a read error. Only sources that reposition mid-stream (the
 * HLS segment source) ever return it. */
#define BASIS_READ_REPOSITION (-2)

/* Repositions a byte source to an absolute offset (a ranged HTTP refetch).
 * Returns 0 on success — subsequent reads deliver from `abs_offset`. Called by
 * a demuxer between its own reads, on its own thread; sources that can't
 * reposition simply aren't given one (demuxers receive NULL). */
typedef int (*basis_reseek_fn)(void* ctx, int64_t abs_offset);

/* ---- Platform decode/present backend (windows/ + android/) --------------- */

typedef struct basis_decoder basis_decoder_t;

/* Create/destroy the OS decoder bound to `engine` (used for logging/state). */
basis_decoder_t* basis_decoder_create(basis_media_engine_t* engine);
void             basis_decoder_destroy(basis_decoder_t* dec);

/* Engine-less capability probe behind basis_media_probe_video_codec: 1 if this
 * platform decodes the codec (basis_codec_t video id). Answers for as much of
 * the decode path as the platform can verify up front — decoder presence at
 * minimum, hardware decode where the platform exposes it (a decoder whose
 * internal software fallback produces frames the present path rejects should
 * be a 0). Cached for process lifetime; safe to call concurrently from worker
 * threads. */
int basis_decoder_probe_video_codec(int codec);

/* Configure tracks (called from the demux thread before the first submit). */
int basis_decoder_set_video_format(basis_decoder_t* dec, basis_codec_t codec,
                                   const uint8_t* extradata, int extradata_len,
                                   int width, int height);
int basis_decoder_set_audio_format(basis_decoder_t* dec, basis_codec_t codec,
                                   int sample_rate, int channels,
                                   const uint8_t* asc, int asc_len);

/* Submit coded data (demux thread). Annex B for video; raw AAC for audio. */
int basis_decoder_submit_video(basis_decoder_t* dec, const uint8_t* annexb, int len,
                               int64_t pts_us, int key);
int basis_decoder_submit_audio(basis_decoder_t* dec, const uint8_t* data, int len,
                               int64_t pts_us);

/* Optional: platforms whose OS layer can demux a URL itself (Android's
 * AMediaExtractor handles https TS/MP4 incl. TLS) take ownership here. Returns 1
 * if the decoder now owns the URL (the core must NOT run a demuxer and must not
 * call submit_*), 0 if the core should demux and feed submit_*(). The Windows
 * backend always returns 0. */
int basis_decoder_try_open_url(basis_decoder_t* dec, const char* url);

/* Render thread: publish the newest decoded frame into the Unity-visible texture,
 * and release GPU resources. */
int  basis_decoder_render_update(basis_decoder_t* dec);
void basis_decoder_render_release(basis_decoder_t* dec);

/* Seek notification (any thread; typically the caller of basis_media_seek_us).
 * Sets a target + bumps a generation the decoder's consumer legs observe; each
 * leg flushes its own stale buffers and re-anchors the clock ON ITS OWN THREAD,
 * so nothing is flushed across threads. Idempotent per generation. */
void basis_decoder_seek(basis_decoder_t* dec, int64_t target_us);

/* Accessors mirrored by the public ABI (any thread unless noted). */
void*    basis_decoder_get_texture(basis_decoder_t* dec, int* out_w, int* out_h);
uint64_t basis_decoder_get_frame_counter(basis_decoder_t* dec);
int      basis_decoder_get_video_size(basis_decoder_t* dec, int* out_w, int* out_h);
/* 0 = bottom-left origin (frame upright; sample as-is). 1 = top-left origin
 * (frame upside-down; consumer must flip V). Windows reports 1 when the D3D11
 * video processor can't mirror on this GPU; Vulkan always normalizes to 0. */
int      basis_decoder_get_frame_origin(basis_decoder_t* dec);
int64_t  basis_decoder_get_position_us(basis_decoder_t* dec);
/* Delivery has ended: flush the video decoder's reorder tail into the frame
 * ring (nothing else ever tells it the stream is over, and the retained
 * frames are the last seconds of the file — large at low frame rates).
 * Video only: the audio decoder's latency is a single frame and a split
 * source's audio leg may still be submitting. Call on the video-submit
 * (demux) thread under the engine's submit lock. */
void     basis_decoder_notify_end_of_stream(basis_decoder_t* dec);
/* Non-zero while decoded frames are still banked for presentation or decoded
 * audio is still unserved — the end-of-stream drain waits on this rather than
 * inferring quiescence from a position stall (which a variable-frame-rate
 * tail can legitimately hold flat for seconds). */
int      basis_decoder_presentation_pending(basis_decoder_t* dec);
int      basis_decoder_get_audio_format(basis_decoder_t* dec, int* out_rate, int* out_channels);
int      basis_decoder_read_audio(basis_decoder_t* dec, float* out, int max_floats); /* audio thread */
int      basis_decoder_get_debug(basis_decoder_t* dec, char* buf, int size); /* diagnostics */
void     basis_decoder_set_buffer(basis_decoder_t* dec, int mode, int buffer_ms); /* 0=fixed,1=dynamic */
void     basis_decoder_set_audio_latency(basis_decoder_t* dec, int latency_us);   /* managed sink output latency, for A/V pacing */
void     basis_decoder_set_output_texture(basis_decoder_t* dec, void* native_texture, int w, int h); /* Android: Unity-owned dst */

/* ---- Engine internals shared with the platform backend ------------------ */

/* Set by the platform backend during UnityPluginLoad so decoders can create
 * resources on Unity's graphics device. Defined in basis_unity_plugin.cpp.
 * Targets: PC/VR = D3D11 (and D3D12 when the project runs DX12); Quest = Vulkan. */
typedef enum basis_gfx_api {
    BASIS_GFX_NONE   = 0,
    BASIS_GFX_D3D11  = 1,
    BASIS_GFX_D3D12  = 2,
    BASIS_GFX_VULKAN = 3,
    BASIS_GFX_GLES   = 4
} basis_gfx_api_t;

basis_gfx_api_t basis_gfx_get_api(void);

/* D3D: Unity's device. D3D12 also exposes the command queue for resource state. */
void* basis_gfx_get_d3d11_device(void);   /* ID3D11Device*  or NULL */
void* basis_gfx_get_d3d12_device(void);   /* ID3D12Device*  or NULL */
void* basis_gfx_get_d3d12_queue(void);    /* ID3D12CommandQueue* or NULL */

/* Vulkan: opaque pointer to the IUnityGraphicsVulkan instance, plus the raw
 * instance/device/physical-device/queue handles the AHB importer needs. The
 * returned values are VkInstance/VkDevice/VkPhysicalDevice/VkQueue as uintptr_t. */
void*    basis_gfx_get_unity_vulkan(void);
uint64_t basis_gfx_vk_instance(void);
uint64_t basis_gfx_vk_device(void);
uint64_t basis_gfx_vk_physical_device(void);
uint64_t basis_gfx_vk_graphics_queue(void);
uint32_t basis_gfx_vk_graphics_queue_family(void);

/* Vulkan: query the VkImage backing a C#-side Texture/RenderTexture (its
 * GetNativeTexturePtr()). Observe-only — nothing is recorded into Unity's
 * command buffer and no layout transition is requested; the caller owns all
 * synchronisation against the image. Returns 1 on success
 * (out_image/out_format/out_w/out_h filled), 0 if unavailable. Render thread
 * only. */
int basis_gfx_vk_access_texture(void* native_texture,
                                uint64_t* out_image,
                                int* out_format,
                                int* out_w,
                                int* out_h);

/* Thread-safe state/error helpers implemented in basis_media_core.c. */
void        basis_engine_set_state(basis_media_engine_t* engine, basis_media_state_t state);
void        basis_engine_set_error(basis_media_engine_t* engine, const char* message);
basis_decoder_t* basis_engine_get_decoder(basis_media_engine_t* engine);

/* Render-thread entry point (the Unity plugin's OnRenderEvent forwards here). The
 * engine pointer comes from Unity and can arrive after basis_media_close has freed
 * it; this checks a liveness registry under a lock and no-ops on a stale engine,
 * so a late render event can't use-after-free. event_id is a BASIS_RENDER_* value. */
void        basis_engine_render_event(basis_media_engine_t* engine, int event_id);

/* Consulted by the platform backend: paused freezes video publishing and mutes
 * audio reads; running going to 0 tells decode/demux loops to unwind. */
int basis_engine_is_paused(basis_media_engine_t* engine);
int basis_engine_is_running(basis_media_engine_t* engine);

/* Non-zero when the source opened in paced (VOD) mode: the platform backend
 * presents on a fixed 1x-from-first-PTS clock instead of the live-edge clock. */
int basis_engine_is_paced(basis_media_engine_t* engine);

/* Leading frames of a decoded audio block that sit ahead of the media-time
 * origin, and so must not reach the PCM ring.
 *
 * Encoder delay is signalled by starting the presentation after it — an MP4 edit
 * list's media_time, Opus's pre-skip — and the demuxers carry that through as a
 * negative presentation time rather than dropping the samples, because the
 * decoder still has to be fed them to prime. Their *output* is not content: for
 * AAC that is one 1024-sample frame, so letting it through delays everything
 * after it by 21 ms.
 *
 * `pts` is the block's presentation time in microseconds, `frames` its length,
 * `rate` the sample rate. Returns 0 when the block starts at or after the origin,
 * and `frames` when all of it precedes the origin. Rounds up, so a partially
 * primed frame is dropped rather than half-played. */
static inline int basis_frames_before_origin(int64_t pts, int frames, int rate) {
    if (pts >= 0 || frames <= 0 || rate <= 0) return 0;
    /* -pts is UB at INT64_MIN, and (-pts) * rate can overflow before the cap;
     * a drop that large already covers the whole block. */
    if (pts == INT64_MIN || -pts > (INT64_MAX - 999999) / (int64_t)rate)
        return frames;
    int64_t drop = ((-pts) * (int64_t)rate + 999999) / 1000000;
    return drop >= (int64_t)frames ? frames : (int)drop;
}

#ifdef __cplusplus
}
#endif

#endif /* BASIS_MEDIA_INTERNAL_H */
