/* HLS / Low-Latency HLS source.
 *
 * Not a demuxer: this fetches and parses the M3U8 playlist, selects a single
 * rendition, and exposes the live media as ONE continuous byte stream
 * (basis_read_fn-compatible) by stitching segments — and, for LL-HLS, partial
 * segments — back to back. The existing MPEG-TS (basis_ts_run) and fMP4
 * (basis_mp4_run) demuxers then consume it unchanged.
 *
 * Transport is injected as a basis_http_provider so the protocol code stays
 * portable: on Windows the engine passes the basis_win_http_* trio, on Android
 * the basis_jni_https_* trio (TLS, redirects and chunked handled by each).
 * Playlists and segments are fetched through it.
 *
 * Low latency: when the origin advertises EXT-X-SERVER-CONTROL:CAN-BLOCK-RELOAD
 * with EXT-X-PART parts, the client starts near the live edge, reloads the media
 * playlist with blocking _HLS_msn/_HLS_part queries, and feeds parts as they are
 * produced — targeting ~PART-HOLD-BACK latency. Against a plain (non-LL) origin it
 * falls back to live-edge, segment-by-segment playback (segment-bound latency).
 *
 * Scope: clear streams, single rendition, Windows and Android fetch.
 */
#ifndef BASIS_HLS_H
#define BASIS_HLS_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Pluggable HTTP(S) byte-source. Matches basis_win_http_open/read/close exactly:
 * open(url) returns a context that streams the response body; read is
 * basis_read_fn-compatible (bytes read, 0 on EOF, <0 on error); close frees it. */
typedef struct basis_http_provider {
    void* (*open)(const char* url);
    int   (*read)(void* ctx, uint8_t* buf, int len);
    void  (*close)(void* ctx);
} basis_http_provider_t;

/* Open an HLS source. Fetches the (master and/or media) playlist via `http`,
 * resolves a single rendition, and prepares the stitched live byte stream.
 * `is_running(user)` is polled during blocking reloads/retries so a stop unwinds
 * promptly. On success returns a context and sets *out_is_fmp4 to 1 when segments
 * are fragmented-MP4 (feed basis_mp4_run) or 0 for MPEG-TS (feed basis_ts_run).
 * Returns NULL on failure. */
void* basis_hls_open(const char* url, const basis_http_provider_t* http,
                     int (*is_running)(void* user), void* user, int* out_is_fmp4);

/* basis_read_fn-compatible. Serves the stitched segment/part bytes, advancing to
 * the next segment and reloading the playlist (blocking for LL-HLS) as needed.
 * Returns bytes read, 0 when the stream ends or the engine stops, <0 on error.
 * After a basis_hls_request_seek it withholds the pre-seek ring and, once the
 * producer has requeued at the target, returns BASIS_READ_REPOSITION once at the
 * boundary so the demuxer can drop its pre-seek state and re-anchor pacing. */
int basis_hls_read(void* ctx, uint8_t* buf, int len);

/* 1 if the opened playlist is VOD (EXT-X-ENDLIST seen), 0 if live. Reflects the
 * playlist parsed at open, so the caller can pick live-vs-on-demand pacing. */
int basis_hls_is_vod(void* ctx);

/* Total VOD duration in milliseconds (summed segment EXTINF values), 0 for live
 * or unknown. Playlists beyond the internal segment cap report the truncated
 * total, matching what actually plays. */
long basis_hls_duration_ms(void* ctx);

/* 1 when the source can honour a seek: a TS-segment VOD playlist whose segment
 * producer is still alive. fMP4 VOD reports 0 — repositioning the stitched
 * byte stream mid-box can't be resynchronised the way TS can. */
int basis_hls_can_seek(void* ctx);

/* Requests a reposition to target_ms. Asynchronous: the segment producer
 * rebuilds its fetch queue from the segment containing the target and flushes
 * buffered bytes; playback resumes from that segment boundary. Returns 0 when
 * accepted, -1 when the source can't seek. */
int basis_hls_request_seek(void* ctx, long long target_ms);

void basis_hls_close(void* ctx);

#ifdef __cplusplus
}
#endif
#endif
