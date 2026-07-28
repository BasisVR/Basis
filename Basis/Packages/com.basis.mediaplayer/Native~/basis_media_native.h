/*
 * basis_media_native — flat C ABI for OS-codec live media playback.
 *
 * Replaces the previous libvpx VP9 wrapper. There is no embedded codec library:
 * video/audio are decoded by the operating system (Media Foundation on Windows,
 * MediaCodec on Android) and presented zero-copy into a GPU texture that Unity
 * wraps with Texture2D.CreateExternalTexture.
 *
 * The engine owns its own threads: a protocol/demux thread pulls the live stream
 * (RTSP-over-TCP / RTMP / MPEG-TS / fragmented-MP4), splits it into elementary
 * H.264/H.265 + AAC, and feeds the OS decoders. Decoded video lands in a
 * platform texture; decoded audio lands in a lock-free PCM ring that the C# audio
 * sink pulls on the Unity audio thread.
 *
 * Threading contract:
 *   - basis_media_open/close/play/pause/stop and all getters are safe to call
 *     from the Unity main thread.
 *   - basis_media_read_audio is called from the Unity audio thread only.
 *   - The function returned by basis_media_get_render_event_func runs on the
 *     Unity render thread only (issued via CommandBuffer.IssuePluginEventAndData).
 *   - basis_media_get_texture returns a handle that is only valid to bind after
 *     at least one BASIS_RENDER_UPDATE has run and get_frame_counter() > 0.
 *
 * License: MIT (this wrapper). No third-party code is statically linked; all
 * decoding uses OS frameworks, so there are no extra license obligations.
 */

#ifndef BASIS_MEDIA_NATIVE_H
#define BASIS_MEDIA_NATIVE_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32) || defined(_WIN64)
  #define BASIS_API __declspec(dllexport)
  #define BASIS_CALL __stdcall
#else
  #define BASIS_API __attribute__((visibility("default")))
  #define BASIS_CALL
#endif

typedef struct basis_media_engine basis_media_engine_t;

typedef enum basis_media_state {
    BASIS_MEDIA_STATE_IDLE       = 0,
    BASIS_MEDIA_STATE_CONNECTING = 1,
    BASIS_MEDIA_STATE_BUFFERING  = 2,
    BASIS_MEDIA_STATE_PLAYING    = 3,
    BASIS_MEDIA_STATE_PAUSED     = 4,
    BASIS_MEDIA_STATE_ENDED      = 5,
    BASIS_MEDIA_STATE_ERROR      = 6
} basis_media_state_t;

/* Render-event opcodes passed as the eventId to the render-event function.
 * The engine pointer is passed as the event's data argument. */
typedef enum basis_render_op {
    BASIS_RENDER_UPDATE  = 1,  /* publish the newest decoded frame into the Unity texture */
    BASIS_RENDER_RELEASE = 2   /* release GPU resources for the engine in `data` (render thread) */
} basis_render_op_t;

/* ---- Lifecycle ---------------------------------------------------------- */

/* Parse `url`, spin up protocol + decode threads, and begin connecting.
 * Returns NULL only on allocation failure or an unrecognised scheme; transport
 * failures surface asynchronously via basis_media_get_state / get_last_error.
 * Supported schemes: rtsp://, rtspt://, rtmp://, rtmps://, http://, https://
 * (the last two pick a demuxer by content sniff — MPEG-TS, fMP4/progressive MP4
 * or RIFF/WAV — with the URL extension as fallback). */
BASIS_API basis_media_engine_t* BASIS_CALL basis_media_open(const char* url);

/* Split-stream / paced open. video_url carries video (e.g. an H.264-only fMP4);
 * audio_url, when non-NULL, is a separate audio-only stream fed by a second demux
 * thread into the same decoder so both present against one clock (adaptive YouTube
 * above ~360p).
 *
 * delivery_hint selects the live-vs-on-demand clock: 0 = auto-detect at open,
 * 1 = force live, 2 = force on-demand. On-demand throttles delivery to ~1x and
 * presents on a fixed 1x-from-first-PTS clock, for VOD that arrives faster than real
 * time (which would otherwise fast-forward on the live-edge clock). Auto picks
 * on-demand when the source looks finite — an HTTP body with a known Content-Length
 * and byte-range support, or an HLS playlist carrying EXT-X-ENDLIST — and live
 * otherwise (non-HTTP transports and open-ended HTTP responses). A NULL/empty
 * audio_url with delivery_hint == 0 behaves exactly like basis_media_open(video_url).
 * Same return and async-error contract. */
BASIS_API basis_media_engine_t* BASIS_CALL basis_media_open_dual(const char* video_url, const char* audio_url, int delivery_hint);

/* Stop all threads (joining them) and free everything, including GPU textures.
 * D3D11/D3D12 resources are freed via thread-safe COM Release; the joined decode
 * threads guarantee nothing is mid-decode. The caller should drop its external-
 * texture wrapper before calling this. Safe to pass NULL. (BASIS_RENDER_RELEASE
 * remains available for hosts that prefer render-thread teardown, but the C#
 * binding does not use it — issuing a deferred event then freeing here would be a
 * use-after-free.) */
BASIS_API void BASIS_CALL basis_media_close(basis_media_engine_t* engine);

/* ---- Control ------------------------------------------------------------ */

BASIS_API void BASIS_CALL basis_media_play(basis_media_engine_t* engine);
BASIS_API void BASIS_CALL basis_media_pause(basis_media_engine_t* engine);
BASIS_API void BASIS_CALL basis_media_stop(basis_media_engine_t* engine);

/* ---- State -------------------------------------------------------------- */

BASIS_API int BASIS_CALL basis_media_get_state(basis_media_engine_t* engine); /* basis_media_state_t */

/* 0 and fills w/h once the first frame's dimensions are known; -1 otherwise. */
BASIS_API int BASIS_CALL basis_media_get_video_size(basis_media_engine_t* engine, int* out_w, int* out_h);

/* Vertical origin of the output texture's rows, so the consumer can apply a free
 * UV flip on backends that can't normalize orientation themselves:
 *   0 = bottom-left origin — the frame is upright; sample with no flip.
 *   1 = top-left origin — the frame is upside-down; the consumer must flip V.
 * Windows returns 1 when the GPU's D3D11 video processor lacks mirror support
 * (driver-dependent — the root cause of "flip only works on some machines"); the
 * Vulkan path always returns 0. Returns 0 before the first frame (safe default). */
BASIS_API int BASIS_CALL basis_media_get_frame_origin(basis_media_engine_t* engine);

/* Presentation position of the most recently published video frame, in
 * microseconds from stream start. -1 if unknown. */
BASIS_API int64_t BASIS_CALL basis_media_get_position_us(basis_media_engine_t* engine);

/* Total media duration in microseconds for on-demand sources whose container or
 * playlist reveals one (progressive MP4 sample tables, HLS VOD segment totals).
 * 0 while unknown and for live sources — a non-zero value is also the signal
 * that the source has a seekable timeline. May become available only after the
 * container index has been parsed, so poll rather than reading once at open. */
BASIS_API int64_t BASIS_CALL basis_media_get_duration_us(basis_media_engine_t* engine);

/* Requests an absolute seek to target_us on a source with a seekable timeline
 * (basis_media_get_duration_us > 0; targets past the end clamp to it). Seeking
 * is asynchronous: the demuxer repositions at the next sample boundary and
 * playback resumes from the preceding keyframe, so the landing position is at
 * or shortly before the target — observe basis_media_get_position_us. Returns
 * 0 when the request was accepted, -1 when the source cannot seek. */
BASIS_API int BASIS_CALL basis_media_seek_us(basis_media_engine_t* engine, int64_t target_us);

/* Copies the in-band caption cue (CEA-608 CC1) active at the current presentation
 * position into buf (UTF-8, NUL-terminated). Returns bytes written (0 = no active
 * cue), or -1 on bad args. out_start_us/out_end_us receive the active cue's time
 * range in microseconds (may be NULL). Poll once per frame from the main thread;
 * the text changes only when the displayed caption does. */
BASIS_API int BASIS_CALL basis_media_poll_caption(basis_media_engine_t* engine, char* buf, int buf_size,
                                                  int64_t* out_start_us, int64_t* out_end_us);

/* Copies the latest error message (UTF-8, NUL-terminated) into buf. Returns the
 * number of bytes written (excluding NUL), or 0 if there is no error. */
BASIS_API int BASIS_CALL basis_media_get_last_error(basis_media_engine_t* engine, char* buf, int buf_size);

/* ---- Capability ---------------------------------------------------------- */

/* Video codec ids accepted by basis_media_probe_video_codec. */
#define BASIS_VIDEO_CODEC_H264 1
#define BASIS_VIDEO_CODEC_H265 2
#define BASIS_VIDEO_CODEC_VP9  3
#define BASIS_VIDEO_CODEC_AV1  4

/* 1 if this platform can decode the codec end to end (decoder present AND the
 * GPU hardware-decodes it — a decoder that would silently fall back to CPU
 * frames the present path can't publish reports 0). Engine-less: callable
 * before any player exists, from any thread; the result is computed once and
 * cached for the process lifetime. Meant for stream/format selection (e.g.
 * offering VP9 ladders only where they will actually play). */
BASIS_API int BASIS_CALL basis_media_probe_video_codec(int codec);

/* Copies a one-line diagnostic counter string (demux AU counts + decoder
 * in/out/blit/drop tallies) into buf. Returns bytes written. For tooling/logs. */
BASIS_API int BASIS_CALL basis_media_get_debug(basis_media_engine_t* engine, char* buf, int buf_size);

/* Copies a human-readable transport description into buf and returns bytes
 * written. Protocols that negotiate a transport report the settled choice
 * (RTSP: "RTSP over UDP", "RTSP over TCP", "RTSP over TCP (UDP unavailable)");
 * everything else reports its URL scheme. Valid from open; refined when the
 * protocol settles, so read it once playback has started. */
BASIS_API int BASIS_CALL basis_media_get_transport(basis_media_engine_t* engine, char* buf, int buf_size);

/* Jitter-buffer control. mode: 0 = fixed (use buffer_ms), 1 = dynamic (auto-tune;
 * buffer_ms is the starting value). buffer_ms is how far behind live video is
 * presented (latency vs smoothness). Safe to call any time after open. */
BASIS_API void BASIS_CALL basis_media_set_buffer(basis_media_engine_t* engine, int mode, int buffer_ms);

/* Reports the managed audio sink's measured output latency (microseconds). The
 * backend paces video presentation this far behind live so audio and video land
 * together; smaller values mean lower end-to-end latency. Backends that time
 * audio internally (desktop) ignore it. Safe to call any time after open. */
BASIS_API void BASIS_CALL basis_media_set_audio_latency(basis_media_engine_t* engine, int latency_us);

/* ---- Zero-copy video ---------------------------------------------------- */

/* Native handle for the Unity-visible output texture, to wrap with
 * Texture2D.CreateExternalTexture. Only valid once get_frame_counter() > 0.
 *   Windows D3D11 : ID3D11Texture2D*           -> TextureFormat.BGRA32
 *   Windows D3D12 : ID3D12Resource*            -> TextureFormat.BGRA32
 *   Android Vulkan: VkImage (as uintptr_t)     -> TextureFormat.RGBA32
 * out_w/out_h receive the texture dimensions. Returns NULL before the first
 * frame or if the size changed and the texture is being reallocated. */
BASIS_API void* BASIS_CALL basis_media_get_texture(basis_media_engine_t* engine, int* out_w, int* out_h);

/* Monotonic counter bumped each time BASIS_RENDER_UPDATE publishes a new frame.
 * C# polls this to know when to (re)bind the external texture and to detect size
 * changes. Starts at 0. */
BASIS_API uint64_t BASIS_CALL basis_media_get_frame_counter(basis_media_engine_t* engine);

/* Register a Unity-allocated destination texture for the engine to render into.
 * Used on the Android/Vulkan path INSTEAD of basis_media_get_texture +
 * Texture2D.CreateExternalTexture: Mali drivers (Pixel 7/8/9 Tensor SoC) crash
 * inside vkCreateImageView when Unity wraps a plugin-owned VkImage, so we flip
 * the handoff direction — C# allocates a RenderTexture, hands its native
 * pointer here, and the plugin uses IUnityGraphicsVulkan::AccessTexture each
 * render to render into Unity's existing image. On Windows this is a no-op.
 * Safe to call once the video size is known (TryGetVideoSize succeeds);
 * passing NULL clears the registration. */
BASIS_API void BASIS_CALL basis_media_set_output_texture(basis_media_engine_t* engine, void* native_texture, int w, int h);

/* ---- Audio (pulled from the Unity audio thread) ------------------------- */

/* 0 and fills sample_rate/channels once known; -1 otherwise. */
BASIS_API int BASIS_CALL basis_media_get_audio_format(basis_media_engine_t* engine, int* out_sample_rate, int* out_channels);

/* Pull up to max_floats interleaved float samples [-1,1] into `out`. Returns the
 * number of floats actually written; the caller zero-fills the remainder. Never
 * blocks. */
BASIS_API int BASIS_CALL basis_media_read_audio(basis_media_engine_t* engine, float* out, int max_floats);

/* ---- Unity render-thread entry ------------------------------------------ */

typedef void (BASIS_CALL *basis_render_event_func)(int event_id, void* data);

/* Returns the function to hand to CommandBuffer.IssuePluginEventAndData
 * (executed via Graphics.ExecuteCommandBuffer). Stable for the module lifetime. */
BASIS_API basis_render_event_func BASIS_CALL basis_media_get_render_event_func(void);

#ifdef __cplusplus
}
#endif

#endif /* BASIS_MEDIA_NATIVE_H */
