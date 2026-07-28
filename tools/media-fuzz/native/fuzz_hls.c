/*
 * fuzz_hls - libFuzzer target for the HLS playlist source (basis_hls_*).
 *
 * basis_hls.c parses attacker-controlled M3U8 text (master + media playlists,
 * EXT-X tags, segment/variant/map URIs) and stitches the referenced segments
 * into one byte stream. It runs on a peer-broadcast URL, so a playlist that
 * miscounts an attribute, over-copies a URI, or mishandles the seek/reposition
 * read path must fault here under ASan/UBSan, not on a client.
 *
 * The transport is injected: an in-memory provider serves the fuzzer's bytes as
 * the body of every fetched URL (the playlist and every segment), so no network
 * is touched. The SSRF host check is stubbed out below so parsing is actually
 * reached — that guard resolves DNS and is exercised at runtime, not here; the
 * URL-parsing half of it is covered by fuzz_url.
 *
 * Build: see ../build.sh (clang -fsanitize=fuzzer,address,undefined).
 */
#include <stdint.h>
#include <stddef.h>
#include <string.h>
#include <stdlib.h>

#include "basis_media_internal.h"   /* BASIS_READ_REPOSITION */
#include "protocol/basis_hls.h"

/* Stub the SSRF resolver so the HLS parser is isolated from DNS/sockets (and the
 * fuzz build needn't link basis_io.c). Always "allowed" -> parsing proceeds. */
int basis_io_host_is_blocked(const char* host) { (void)host; return 0; }

/* One in-memory response = the whole fuzz buffer, replayed per open(). */
static const uint8_t* g_data;
static size_t g_size;

typedef struct { size_t pos; } mem_ctx;

static void* mem_open(const char* url) { (void)url; return calloc(1, sizeof(mem_ctx)); }
static void  mem_close(void* ctx) { free(ctx); }
static int   mem_read(void* ctx, uint8_t* buf, int len) {
    mem_ctx* c = (mem_ctx*)ctx;
    if (!c || len <= 0) return 0;
    size_t avail = g_size - c->pos;
    size_t take = (size_t)len < avail ? (size_t)len : avail;
    if (take) { memcpy(buf, g_data + c->pos, take); c->pos += take; }
    return (int)take;
}

/* Bound the producer's blocking reloads/retries so a crafted "live" playlist
 * can't spin and drag down fuzz throughput; libFuzzer -timeout is the outer
 * backstop. Generous enough to stitch a multi-segment playlist, far below the
 * old 100k that let a non-advancing playlist burn ~100k callbacks per input. */
enum { HLS_MAX_POLL_CALLBACKS = 4096 };
static int g_poll;
static int keep_running(void* user) { (void)user; return (++g_poll) <= HLS_MAX_POLL_CALLBACKS; }

int LLVMFuzzerTestOneInput(const uint8_t* data, size_t size) {
    g_data = data; g_size = size; g_poll = 0;

    basis_http_provider_t provider = { mem_open, mem_read, mem_close };
    int is_fmp4 = 0;
    void* hls = basis_hls_open("http://fuzz.invalid/index.m3u8", &provider,
                               keep_running, NULL, &is_fmp4);
    if (!hls) return 0;

    /* Touch the metadata getters and drain the stitched stream so segment
     * stitching and the demuxer-facing read path are exercised. */
    (void)basis_hls_is_vod(hls);
    (void)basis_hls_duration_ms(hls);
    if (basis_hls_can_seek(hls) && size) basis_hls_request_seek(hls, (long long)(data[0]) * 100);

    uint8_t buf[4096];
    volatile uint8_t sink = 0;
    for (int i = 0; i < 2048; ++i) {
        int n = basis_hls_read(hls, buf, (int)sizeof(buf));
        if (n == BASIS_READ_REPOSITION) continue;
        if (n <= 0) break;
        for (int k = 0; k < n; ++k) sink ^= buf[k];   /* force ASan to see the bytes */
    }
    (void)sink;

    basis_hls_close(hls);
    return 0;
}
