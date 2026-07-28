/*
 * basis_jni_https.h — Android HTTPS byte source backed by java.net.HttpsURLConnection.
 *
 * AMediaExtractor handles the easy case (seekable container files over HTTPS).
 * Live MPEG-TS streams are rejected by AMediaExtractor with UNSUPPORTED, so the
 * core falls back to the portable basis_ts demuxer — and needs an HTTPS byte
 * source the demuxer can read from. NDK has no TLS API of its own; the smallest
 * dependency-free option is to bridge to Java via JNI.
 *
 * Same contract as protocol/basis_http: open returns an opaque ctx, read fills a
 * caller buffer (bytes read; 0 = EOF; <0 = error), close frees it. Read is called
 * on the demux thread; the JNI layer attaches that thread to the JVM as needed.
 */

#ifndef BASIS_JNI_HTTPS_H
#define BASIS_JNI_HTTPS_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

void* basis_jni_https_open(const char* url, int timeout_ms);
int   basis_jni_https_read(void* ctx, uint8_t* buf, int len);
void  basis_jni_https_close(void* ctx);

/* Non-zero when the response proved a finite, byte-range-fetchable body
 * (Range probe answered 206, or Accept-Ranges: bytes with a known
 * Content-Length) — the live-vs-VOD delivery auto-detect signal. */
int   basis_jni_https_is_seekable(void* ctx);

/* Non-zero when a ranged re-request will actually be honoured (the probe
 * answered 206, not just an Accept-Ranges advertisement) — the gate for wiring
 * the demuxer's reseek hook. Stricter than is_seekable. */
int   basis_jni_https_can_reseek(void* ctx);

/* Body size in bytes, or -1 when unknown (chunked / open-ended / live). From the
 * Content-Length captured at open; used by the Ogg demuxer for granule seek. */
long long basis_jni_https_content_length(void* ctx);

/* Interrupts a read parked in InputStream.read() on another thread (disconnects
 * the connection so the blocked read throws and returns). The caller uses this to
 * unblock a read-ahead reader before reseeking; a racing read reports error. */
void  basis_jni_https_abort(void* ctx);

/* Replaces the response with a ranged GET from `offset` so the stream continues
 * there. Valid only on a can_reseek body; the caller must guarantee no concurrent
 * read is in flight (abort/park the reader first — a prior abort is fine, this
 * re-opens). Returns 0 on success; on failure the source is left stream-less and
 * reads report EOF. */
int   basis_jni_https_reseek(void* ctx, long long offset);

#ifdef __cplusplus
}
#endif

#endif /* BASIS_JNI_HTTPS_H */
