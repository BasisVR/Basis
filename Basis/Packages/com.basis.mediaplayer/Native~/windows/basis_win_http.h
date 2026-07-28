/* WinHTTP byte source for http:// and https:// live streams on Windows.
 * Handles TLS, redirects and chunked transfer transparently; the demuxers just
 * pull bytes via basis_win_http_read (basis_read_fn-compatible). */
#ifndef BASIS_WIN_HTTP_H
#define BASIS_WIN_HTTP_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

void* basis_win_http_open(const char* url);
int   basis_win_http_read(void* ctx, uint8_t* buf, int len);
void  basis_win_http_close(void* ctx);

/* Unblock a thread parked in basis_win_http_read by closing the underlying request, so a
 * pending read returns immediately. Call before joining a reader thread on shutdown so the
 * join can't stall on a stalled socket read; a following basis_win_http_close stays safe. */
void  basis_win_http_abort(void* ctx);

/* 1 if the response is a finite, byte-range-seekable body (known Content-Length and
 * Accept-Ranges: bytes) — i.e. on-demand/VOD rather than an open-ended live stream.
 * 0 otherwise. Reflects the headers captured when the source was opened. */
int   basis_win_http_is_seekable(void* ctx);

/* 1 when a ranged re-request will actually be honoured: the initial GET carries a
 * bytes=0- probe, and only a 206 answer proves the server implements ranges —
 * Accept-Ranges alone is advertisement some servers don't back up. Stricter than
 * is_seekable (which only drives live-vs-VOD pacing). */
int   basis_win_http_can_reseek(void* ctx);

/* Body size in bytes, or -1 when unknown (chunked / open-ended / live). Reflects
 * the Content-Length captured at open; used by the Ogg demuxer for granule seek. */
long long basis_win_http_content_length(void* ctx);

/* Continues the stream from an absolute byte offset by issuing a ranged GET on the
 * same connection (requires a 206 response; a server that ignores Range fails the
 * call rather than silently restarting at 0). Only valid on a seekable body, with
 * no basis_win_http_read concurrently in flight — park or abort the reading thread
 * first (a prior basis_win_http_abort is fine; this opens a fresh request).
 * Returns 0 on success; on failure reads report EOF. */
int   basis_win_http_reseek(void* ctx, long long offset);

#ifdef __cplusplus
}
#endif
#endif
