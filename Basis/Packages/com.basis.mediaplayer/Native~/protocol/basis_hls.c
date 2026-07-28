/* basis_hls.c — HLS / Low-Latency HLS source. See basis_hls.h for the contract.
 *
 * Model: parse the M3U8, select one rendition, and present the live media as a
 * single continuous byte stream by stitching segments (and, for LL-HLS, parts)
 * through a basis_read_fn that basis_ts_run / basis_mp4_run consume unchanged.
 *
 * This pass: clear streams, single rendition, Windows fetch. Android/Quest
 * support is planned (it needs a non-Windows http provider). */

#include "basis_hls.h"
#include "../basis_media_internal.h"  /* BASIS_READ_REPOSITION */
#include "basis_io.h"                 /* basis_io_host_is_blocked (SSRF guard) */
#include "basis_url.h"                /* basis_url_parse */

#include <stdlib.h>
#include <string.h>
#include <stdio.h>

#if defined(_WIN32)
  #include <windows.h>
#else
  #include <time.h>
  #include <pthread.h>
#endif

static void hls_sleep_ms(int ms) {
#if defined(_WIN32)
    Sleep((DWORD)ms);
#else
    struct timespec ts;
    ts.tv_sec = ms / 1000;
    ts.tv_nsec = (long)(ms % 1000) * 1000000L;
    nanosleep(&ts, NULL);
#endif
}

/* ---- portable mutex / thread (read-ahead producer) ---------------------- */
#if defined(_WIN32)
typedef CRITICAL_SECTION hls_mutex_t;
typedef HANDLE           hls_thread_t;
static void hls_mutex_init(hls_mutex_t* m)    { InitializeCriticalSection(m); }
static void hls_mutex_destroy(hls_mutex_t* m) { DeleteCriticalSection(m); }
static void hls_mutex_lock(hls_mutex_t* m)    { EnterCriticalSection(m); }
static void hls_mutex_unlock(hls_mutex_t* m)  { LeaveCriticalSection(m); }
#else
typedef pthread_mutex_t hls_mutex_t;
typedef pthread_t       hls_thread_t;
static void hls_mutex_init(hls_mutex_t* m)    { pthread_mutex_init(m, NULL); }
static void hls_mutex_destroy(hls_mutex_t* m) { pthread_mutex_destroy(m); }
static void hls_mutex_lock(hls_mutex_t* m)    { pthread_mutex_lock(m); }
static void hls_mutex_unlock(hls_mutex_t* m)  { pthread_mutex_unlock(m); }
#endif

/* Signed CDN segment URIs carry the whole authorisation payload in the path and
 * run well past 1 KB (YouTube live signs ~1150 chars), so the cap has to clear
 * that with room to spare. It bounds the playlist struct, which is heap-allocated
 * at both parse sites for that reason. */
#define HLS_MAX_URI       2048
#define HLS_MAX_ITEMS      512   /* fetchable items retained per playlist parse   */
#define HLS_MAX_PLAYLIST   (8 << 20) /* 8 MiB playlist cap                         */
#define HLS_MAX_EMPTY_RELOADS 8  /* consecutive no-new-media reloads before giving up */
#define HLS_LIVE_MARGIN_SEGMENTS 3 /* playout buffer kept behind the live edge for plain (non-LL) HLS */
/* Read-ahead byte buffer (~5 s of 1080p HD). Kept small on purpose: a larger
 * read-ahead makes the VOD producer reach the end of the playlist further ahead
 * of playout, which widens the window where a seek is rejected outright (the
 * producer_done path). One heap allocation per HLS stream. */
#define HLS_RING_CAP (4 * 1024 * 1024)

/* (msn, part) media position. part == -1 means a whole segment. */
typedef struct {
    char uri[HLS_MAX_URI];
    long msn;
    int  part;   /* -1 = full segment, >=0 = partial segment index */
    long dur_ms; /* media duration of this segment/part (for real-time pacing) */
} hls_item_t;

typedef struct {
    long target_duration_ms;
    long media_seq_base;
    int  can_block_reload;   /* EXT-X-SERVER-CONTROL: CAN-BLOCK-RELOAD=YES   */
    long part_target_ms;     /* EXT-X-PART-INF: PART-TARGET                  */
    int  has_parts;          /* any EXT-X-PART seen                          */
    int  has_endlist;        /* EXT-X-ENDLIST (VOD / stream finished)        */
    int  is_fmp4;            /* EXT-X-MAP present or .m4s/.mp4 segment URIs  */
    char map_uri[HLS_MAX_URI]; /* EXT-X-MAP init segment (fMP4), resolved     */
    int  nfull;              /* count of full (#EXTINF) segments             */
    /* Retained items, oldest-first from item_start. A DVR live playlist lists far
     * more segments than the cap (a YouTube broadcast publishes its whole
     * multi-hour window every reload), and the ones worth playing are at the live
     * edge, so once full a live playlist rolls the oldest out. VOD keeps the head:
     * its playout starts at the first segment. Index via playlist_item(). */
    hls_item_t items[HLS_MAX_ITEMS];
    int  item_start;
    int  item_count;
} hls_playlist_t;

typedef struct basis_hls {
    basis_http_provider_t http;
    int  (*is_running)(void* user);
    void* user;

    char media_url[HLS_MAX_URI]; /* resolved media-playlist URL (reload target) */
    int  is_fmp4;

    /* media position cursor: the next thing we want to fetch */
    long want_msn;
    int  want_part;

    int  can_block_reload;
    long part_target_ms;
    long target_duration_ms;
    int  endlist_seen;
    long total_ms;               /* VOD: summed EXTINF durations (0 when live/unknown) */

    /* VOD seek index: every full segment in playlist order, retained at open so
     * a seek can rebuild the fetch queue from any point. TS segments only. */
    char (*vod_uri)[HLS_MAX_URI];
    long* vod_dur_ms;
    int   vod_count;
    volatile int  seek_pending;  /* producer repositions at its next iteration */
    volatile long seek_target_ms;
    /* Seek-boundary handshake with the consumer. request_seek bumps seek_gen
     * (single writer: the seek caller); the producer sets flush_gen to match
     * once it has flushed the ring and requeued at the target (single writer:
     * the producer). While seek_gen != flush_gen the ring holds pre-seek bytes,
     * so basis_hls_read withholds them; when they match it raises
     * BASIS_READ_REPOSITION once (tracked by read_signaled_gen, consumer-only)
     * so the demuxer flushes and re-anchors at the exact boundary. */
    volatile long seek_gen;
    volatile long flush_gen;
    long          read_signaled_gen;

    int  map_served;             /* fMP4: init segment already streamed once */
    char map_uri[HLS_MAX_URI];

    /* pending fetch queue (resolved absolute URLs, in play order) */
    char pending[HLS_MAX_ITEMS][HLS_MAX_URI];
    long pending_dur[HLS_MAX_ITEMS];   /* media duration (ms) per queued item */
    int  pending_head;
    int  pending_count;

    void* seg_ctx;               /* current open segment byte source (producer only) */
    int   empty_reloads;

    /* read-ahead: the producer thread fetches segments into `ring`; basis_hls_read
     * drains it. Only `ring`/head/tail/count are shared (under `lock`); the queue,
     * seg_ctx and playlist cursor are touched by the producer thread only. */
    uint8_t*     ring;
    int          ring_cap;
    int          ring_head;      /* write position */
    int          ring_tail;      /* read position  */
    int          ring_count;     /* bytes currently buffered */
    hls_mutex_t  lock;
    hls_thread_t thread;
    int          thread_started;
    volatile int stop;
    volatile int producer_done; /* the producer thread has actually exited (stop,
                                 * policy block, reload exhaustion) — the only
                                 * state that rejects a seek */
    volatile int vod_idle;      /* VOD fully fetched and the producer is parked,
                                 * alive, waiting for a seek to revive it; with a
                                 * drained ring and no seek in flight the reader
                                 * reports end-of-stream. Set in the endlist
                                 * branch and cleared in the seek flush, both
                                 * under `lock`, so the reader can never see an
                                 * idle mark alongside a fresh flush generation */

    /* Delivery is paced by the engine (pace_gate, by AU timestamp), not here. */
} basis_hls_t;

/* ---- small string / URL helpers ----------------------------------------- */

static int ci_eq_n(const char* a, const char* b, size_t n) {
    for (size_t i = 0; i < n; ++i) {
        char x = a[i], y = b[i];
        if (x >= 'A' && x <= 'Z') x += 32;
        if (y >= 'A' && y <= 'Z') y += 32;
        if (x != y) return 0;
        if (!x) return 1;
    }
    return 1;
}

static int starts_with(const char* s, const char* p) {
    return strncmp(s, p, strlen(p)) == 0;
}

static int ends_with_ci(const char* s, const char* suffix) {
    size_t ls = strlen(s), lf = strlen(suffix);
    if (lf > ls) return 0;
    return ci_eq_n(s + (ls - lf), suffix, lf);
}

/* Extract a tag attribute value: KEY=VALUE or KEY="VALUE" from a tag line. */
static int attr_str(const char* line, const char* key, char* out, int outsz) {
    size_t klen = strlen(key);
    const char* p = line;
    while ((p = strstr(p, key)) != NULL) {
        /* require the char before key to be ':' or ',' to avoid substring hits */
        if (p != line && p[-1] != ':' && p[-1] != ',') { p += klen; continue; }
        if (p[klen] != '=') { p += klen; continue; }
        const char* v = p + klen + 1;
        int i = 0;
        if (*v == '"') {
            v++;
            while (*v && *v != '"' && i < outsz - 1) out[i++] = *v++;
        } else {
            while (*v && *v != ',' && *v != '\r' && *v != '\n' && i < outsz - 1) out[i++] = *v++;
        }
        out[i] = 0;
        return 1;
    }
    return 0;
}

static long attr_long(const char* line, const char* key, long def) {
    char buf[64];
    if (attr_str(line, key, buf, sizeof(buf))) return atol(buf);
    return def;
}

/* Seconds (possibly fractional, e.g. "0.33334") to milliseconds. */
static long attr_ms(const char* line, const char* key, long def) {
    char buf[64];
    if (!attr_str(line, key, buf, sizeof(buf))) return def;
    double s = atof(buf);
    return (long)(s * 1000.0 + 0.5);
}

/* Resolve `ref` against the absolute base URL `base` into `out`.
 * Handles absolute (http[s]://), root-relative (/path) and same-directory
 * relative refs. "../" is not normalised (rare in HLS); acceptable first pass.
 *
 * Returns 0 when the resolved URL doesn't fit. A relative ref is concatenated
 * onto the base, so the result can overrun where neither part alone would, and
 * a clipped absolute URL is still well-formed enough to be fetched — callers
 * drop the reference rather than store one the origin will refuse. */
static int resolve_url(const char* base, const char* ref, char* out, int outsz) {
    int n;
    if (starts_with(ref, "http://") || starts_with(ref, "https://")) {
        n = snprintf(out, outsz, "%s", ref);
    } else {
        /* find scheme://host end (first '/' after "scheme://") */
        const char* host = strstr(base, "://");
        host = host ? host + 3 : base;
        const char* host_end = strchr(host, '/');

        if (ref[0] == '/') {
            if (host_end) {
                int hlen = (int)(host - base) + (int)(host_end - host);
                n = snprintf(out, outsz, "%.*s%s", hlen, base, ref);
            } else {
                n = snprintf(out, outsz, "%s%s", base, ref);
            }
        } else {
            /* same-directory relative: base up to and including last '/' (before any '?') */
            const char* q = strchr(base, '?');
            const char* end = q ? q : base + strlen(base);
            const char* slash = end;
            while (slash > host && *slash != '/') slash--;
            int dirlen = (slash >= host && *slash == '/') ? (int)(slash - base) + 1 : (int)(end - base);
            n = snprintf(out, outsz, "%.*s%s", dirlen, base, ref);
        }
    }
    return n >= 0 && n < outsz;
}

/* ---- playlist fetch ------------------------------------------------------ */

/* SSRF gate for every URL a playlist steers us to. The managed layer validates
 * only the entry URL; variant/segment/map URIs come from the (attacker-controlled)
 * playlist body and are followed here, so re-check each one: it must stay on
 * http(s) and its host must not resolve to a non-global-unicast address. The
 * platform HTTP stacks (WinHTTP / JNI) don't apply this guard themselves.
 *
 * This is a pre-check: it blocks literal internal addresses and hosts that resolve
 * private. It does NOT close two provider-side bypasses — active DNS rebinding (the
 * platform stack re-resolves the name when it connects) and an allowed URL that
 * redirects to an internal host (WinHTTP/JNI follow redirects). Fully closing those
 * needs connect-by-pinned-IP plus per-redirect re-validation and connected-peer
 * verification at the HTTP-provider boundary — tracked as a follow-up. */
/* out_blocked (nullable) distinguishes a deterministic policy rejection (bad
 * scheme/host — retrying can never succeed) from a transient provider open
 * failure, so a caller can terminate on the former instead of busy-looping. */
static void* hls_guarded_open(basis_hls_t* h, const char* url, int* out_blocked) {
    if (out_blocked) *out_blocked = 1;   /* set for the policy-reject early returns */
    basis_url_t u;
    if (basis_url_parse(url, &u) != 0) return NULL;
    if (strcmp(u.scheme, "http") != 0 && strcmp(u.scheme, "https") != 0) return NULL;
    if (basis_io_host_is_blocked(u.host)) return NULL;
    if (out_blocked) *out_blocked = 0;   /* passed policy; any NULL below is transient */
    return h->http.open(url);
}

/* GET `url` fully into a NUL-terminated buffer (caller frees). Returns length,
 * or <0 on error / stop. */
static int fetch_text(basis_hls_t* h, const char* url, char** out, int* out_blocked) {
    *out = NULL;
    void* ctx = hls_guarded_open(h, url, out_blocked);
    if (!ctx) return -1;

    int cap = 16384, len = 0;
    char* buf = (char*)malloc(cap);
    if (!buf) { h->http.close(ctx); return -1; }

    for (;;) {
        if (h->is_running && !h->is_running(h->user)) { free(buf); h->http.close(ctx); return -1; }
        if (len + 4096 > cap) {
            /* Over the cap: fail rather than return what fitted. A clipped body
             * ends mid-URI, and the truncated URI parses as a valid-looking
             * segment the CDN then rejects — a silent playback failure that
             * looks nothing like the size limit that caused it. */
            if (cap >= HLS_MAX_PLAYLIST) { free(buf); h->http.close(ctx); return -1; }
            cap *= 2;
            char* nb = (char*)realloc(buf, cap);
            if (!nb) { free(buf); h->http.close(ctx); return -1; }
            buf = nb;
        }
        int n = h->http.read(ctx, (uint8_t*)buf + len, cap - len - 1);
        if (n <= 0) break;
        len += n;
    }
    h->http.close(ctx);
    buf[len] = 0;
    *out = buf;
    return len;
}

/* ---- M3U8 parsing -------------------------------------------------------- */

/* Returns 1 if the playlist is a master (has EXT-X-STREAM-INF). */
static int playlist_is_master(const char* text) {
    return strstr(text, "#EXT-X-STREAM-INF") != NULL;
}

/* Copy one playlist line (CR stripped) into out. Returns 0 if it doesn't fit —
 * callers drop the line rather than store a prefix, since a clipped URI is a
 * URL the origin answers 403 to and nothing downstream can tell it apart from a
 * genuine authorisation failure. */
static int copy_line(char* out, int outsz, const char* src, int len) {
    if (len >= outsz) return 0;
    memcpy(out, src, (size_t)len);
    out[len] = 0;
    if (len && out[len - 1] == '\r') out[len - 1] = 0;
    return 1;
}

/* Pick a single rendition from a master playlist: highest BANDWIDTH variant.
 * Writes its resolved absolute URL into out. Returns 1 on success. */
static int master_pick_variant(basis_hls_t* h, const char* base, const char* text,
                               char* out, int outsz) {
    const char* p = text;
    long best_bw = -1;
    char best_uri[HLS_MAX_URI] = {0};
    char line[2048];

    while (*p) {
        const char* nl = strchr(p, '\n');
        int llen = nl ? (int)(nl - p) : (int)strlen(p);
        if (llen >= (int)sizeof(line)) llen = (int)sizeof(line) - 1;
        memcpy(line, p, llen); line[llen] = 0;
        if (llen && line[llen - 1] == '\r') line[llen - 1] = 0;

        if (starts_with(line, "#EXT-X-STREAM-INF")) {
            long bw = attr_long(line, "BANDWIDTH", 0);
            /* the variant URI is the next non-comment, non-empty line */
            const char* q = nl ? nl + 1 : p + llen;
            while (*q) {
                const char* qnl = strchr(q, '\n');
                int qlen = qnl ? (int)(qnl - q) : (int)strlen(q);
                if (qlen && q[0] != '#' && q[0] != '\r') {
                    /* A variant URI that doesn't fit is skipped, leaving a
                     * shorter-URI rendition to win rather than selecting one
                     * that can't be fetched. */
                    char uline[HLS_MAX_URI], cand[HLS_MAX_URI];
                    if (copy_line(uline, (int)sizeof(uline), q, qlen) && uline[0] && bw >= best_bw &&
                        resolve_url(base, uline, cand, sizeof(cand))) {
                        best_bw = bw;
                        snprintf(best_uri, sizeof(best_uri), "%s", cand);
                    }
                    break;
                }
                if (!qnl) break;
                q = qnl + 1;
            }
        }
        if (!nl) break;
        p = nl + 1;
    }

    if (!best_uri[0]) return 0;
    snprintf(out, outsz, "%s", best_uri);
    return 1;
}

/* The i-th retained item in playlist order. */
static const hls_item_t* playlist_item(const hls_playlist_t* pl, int i) {
    return &pl->items[(pl->item_start + i) % HLS_MAX_ITEMS];
}

/* Claim a slot for the next item. Once the array is full a live playlist rolls
 * the oldest item out (the live edge is what plays); a VOD keeps what it has.
 * Returns NULL when the item was dropped. */
static hls_item_t* playlist_append(hls_playlist_t* pl, int live) {
    if (pl->item_count < HLS_MAX_ITEMS) {
        int idx = (pl->item_start + pl->item_count) % HLS_MAX_ITEMS;
        pl->item_count++;
        return &pl->items[idx];
    }
    if (!live) return NULL;
    int idx = pl->item_start;
    pl->item_start = (pl->item_start + 1) % HLS_MAX_ITEMS;
    return &pl->items[idx];
}

/* Parse a media playlist into pl. base = media playlist URL (for resolving). */
static void parse_media_playlist(const char* base, const char* text, hls_playlist_t* pl) {
    memset(pl, 0, sizeof(*pl));
    pl->media_seq_base = 0;

    /* Retention policy is decided up front: EXT-X-ENDLIST closes a VOD, and its
     * absence means the list is a live window that will be reloaded. */
    int live = strstr(text, "#EXT-X-ENDLIST") == NULL;

    const char* p = text;
    char line[HLS_MAX_URI + 512];  /* holds a tag line carrying a full-length URI */
    int seg_index = 0;   /* full segments seen so far */
    int part_index = 0;  /* parts of the in-progress segment */

    while (*p) {
        const char* nl = strchr(p, '\n');
        int llen = nl ? (int)(nl - p) : (int)strlen(p);
        if (llen >= (int)sizeof(line)) llen = (int)sizeof(line) - 1;
        memcpy(line, p, llen); line[llen] = 0;
        if (llen && line[llen - 1] == '\r') line[llen - 1] = 0;

        if (starts_with(line, "#EXT-X-TARGETDURATION")) {
            const char* c = strchr(line, ':');
            if (c) pl->target_duration_ms = atol(c + 1) * 1000;
        } else if (starts_with(line, "#EXT-X-MEDIA-SEQUENCE")) {
            const char* c = strchr(line, ':');
            if (c) pl->media_seq_base = atol(c + 1);
        } else if (starts_with(line, "#EXT-X-SERVER-CONTROL")) {
            char v[16];
            if (attr_str(line, "CAN-BLOCK-RELOAD", v, sizeof(v)) && ci_eq_n(v, "yes", 3))
                pl->can_block_reload = 1;
        } else if (starts_with(line, "#EXT-X-PART-INF")) {
            pl->part_target_ms = attr_ms(line, "PART-TARGET", pl->part_target_ms);
        } else if (starts_with(line, "#EXT-X-MAP")) {
            char uri[HLS_MAX_URI];
            if (attr_str(line, "URI", uri, sizeof(uri))) {
                /* An init segment that doesn't fit leaves map_uri empty — the
                 * same state as a playlist carrying no EXT-X-MAP, and better
                 * than fetching a clipped URL as though it were the real one. */
                if (!resolve_url(base, uri, pl->map_uri, sizeof(pl->map_uri))) pl->map_uri[0] = 0;
                pl->is_fmp4 = 1;
            }
        } else if (starts_with(line, "#EXT-X-PART:")) {
            char uri[HLS_MAX_URI];
            if (attr_str(line, "URI", uri, sizeof(uri))) {
                /* Resolve before claiming a slot: a slot taken and then
                 * abandoned would sit in the array with no URI. */
                char resolved[HLS_MAX_URI];
                hls_item_t* it = resolve_url(base, uri, resolved, sizeof(resolved))
                                 ? playlist_append(pl, live) : NULL;
                if (it) {
                    snprintf(it->uri, sizeof(it->uri), "%s", resolved);
                    it->msn = pl->media_seq_base + seg_index;
                    it->part = part_index;
                    it->dur_ms = attr_ms(line, "DURATION", 0);
                    pl->has_parts = 1;
                    if (ends_with_ci(it->uri, ".m4s") || ends_with_ci(it->uri, ".mp4")) pl->is_fmp4 = 1;
                }
                /* Counted whether or not it was retained: a dropped part still
                 * occupies its slot in the source numbering, and the cursor in
                 * enqueue_new_media compares against it. */
                part_index++;
            }
        } else if (starts_with(line, "#EXTINF")) {
            /* #EXTINF:<seconds>,  — capture the duration for real-time pacing */
            long extinf_ms = 0;
            { const char* c = strchr(line, ':'); if (c) extinf_ms = (long)(atof(c + 1) * 1000.0 + 0.5); }
            /* the segment URI is the next non-comment line */
            const char* q = nl ? nl + 1 : p + llen;
            while (*q) {
                const char* qnl = strchr(q, '\n');
                int qlen = qnl ? (int)(qnl - q) : (int)strlen(q);
                if (qlen && q[0] != '#' && q[0] != '\r') {
                    char uline[HLS_MAX_URI], resolved[HLS_MAX_URI];
                    /* A URI that is over-long or won't resolve leaves the segment
                     * unfetchable, but it is still a segment: seg_index has to
                     * count it either way or every later media sequence number
                     * shifts. */
                    if (copy_line(uline, (int)sizeof(uline), q, qlen) && uline[0] &&
                        resolve_url(base, uline, resolved, sizeof(resolved))) {
                        hls_item_t* it = playlist_append(pl, live);
                        if (it) {
                            snprintf(it->uri, sizeof(it->uri), "%s", resolved);
                            it->msn = pl->media_seq_base + seg_index;
                            it->part = -1;
                            it->dur_ms = extinf_ms;
                            if (ends_with_ci(it->uri, ".m4s") || ends_with_ci(it->uri, ".mp4")) pl->is_fmp4 = 1;
                        }
                    }
                    seg_index++;
                    part_index = 0; /* parts now belong to the next in-progress segment */
                    p = qnl ? qnl : q + qlen;
                    goto next_line;
                }
                if (!qnl) { p = q + qlen; goto next_line; }
                q = qnl + 1;
            }
        } else if (starts_with(line, "#EXT-X-ENDLIST")) {
            pl->has_endlist = 1;
        }

        if (!nl) break;
        p = nl + 1;
        continue;
    next_line:
        if (!*p) break;
        if (*p == '\n') p++;
        continue;
    }

    pl->nfull = seg_index;
}

/* ---- queue helpers ------------------------------------------------------- */

static void queue_push(basis_hls_t* h, const char* url, long dur_ms) {
    if (h->pending_count >= HLS_MAX_ITEMS) return;
    int idx = (h->pending_head + h->pending_count) % HLS_MAX_ITEMS;
    snprintf(h->pending[idx], HLS_MAX_URI, "%s", url);
    h->pending_dur[idx] = dur_ms;
    h->pending_count++;
}

static const char* queue_pop(basis_hls_t* h, long* out_dur_ms) {
    if (h->pending_count == 0) return NULL;
    int idx = h->pending_head;
    const char* s = h->pending[idx];
    if (out_dur_ms) *out_dur_ms = h->pending_dur[idx];
    h->pending_head = (h->pending_head + 1) % HLS_MAX_ITEMS;
    h->pending_count--;
    return s;
}

/* Enqueue everything in the freshly parsed playlist that is at or beyond our
 * (want_msn, want_part) cursor, advancing the cursor past what we enqueue. A full
 * segment for msn M supersedes its parts: if we already consumed parts of M we
 * skip the full segment, otherwise we take it. */
static void enqueue_new_media(basis_hls_t* h, const hls_playlist_t* pl) {
    for (int i = 0; i < pl->item_count; ++i) {
        const hls_item_t* it = playlist_item(pl, i);
        if (it->part >= 0) {
            /* part P of segment M */
            if (it->msn > h->want_msn || (it->msn == h->want_msn && it->part >= h->want_part)) {
                queue_push(h, it->uri, it->dur_ms);
                h->want_msn = it->msn;
                h->want_part = it->part + 1;
            }
        } else {
            /* full segment M completes that segment */
            if (h->want_msn < it->msn) {
                queue_push(h, it->uri, it->dur_ms);
                h->want_msn = it->msn + 1;
                h->want_part = 0;
            } else if (h->want_msn == it->msn && h->want_part == 0) {
                queue_push(h, it->uri, it->dur_ms);
                h->want_msn = it->msn + 1;
                h->want_part = 0;
            } else if (h->want_msn == it->msn && h->want_part > 0) {
                /* already rode this segment's parts; skip the redundant full segment */
                h->want_msn = it->msn + 1;
                h->want_part = 0;
            }
        }
    }
}

/* Build the (optionally blocking) reload URL and fetch+parse the media playlist,
 * enqueuing any new media. Returns 1 if new media was enqueued, 0 if none, <0 on
 * error/stop. */
static int reload_and_enqueue(basis_hls_t* h) {
    char url[HLS_MAX_URI + 96];
    if (h->can_block_reload && h->want_part >= 0) {
        char sep = strchr(h->media_url, '?') ? '&' : '?';
        snprintf(url, sizeof(url), "%s%c_HLS_msn=%ld&_HLS_part=%d",
                 h->media_url, sep, h->want_msn, h->want_part);
    } else {
        snprintf(url, sizeof(url), "%s", h->media_url);
    }

    char* text = NULL;
    int blocked = 0;
    int n = fetch_text(h, url, &text, &blocked);
    if (n < 0) { free(text); return blocked ? -2 : -1; } /* -2 = policy-blocked (deterministic) */

    /* Off the stack: the playlist is ~1 MiB and this runs on the producer thread. */
    hls_playlist_t* pl = (hls_playlist_t*)malloc(sizeof(*pl));
    if (!pl) { free(text); return -1; }
    parse_media_playlist(h->media_url, text, pl);
    free(text);

    if (pl->target_duration_ms) h->target_duration_ms = pl->target_duration_ms;
    if (pl->has_endlist) h->endlist_seen = 1;

    int before = h->pending_count;
    enqueue_new_media(h, pl);
    free(pl);
    return (h->pending_count > before) ? 1 : 0;
}

/* ---- read-ahead producer ------------------------------------------------- */

static int hls_should_run(basis_hls_t* h) {
    return !h->stop && (h->is_running == NULL || h->is_running(h->user));
}

/* Copy n bytes into the ring, sleeping while it's full. Stops early on shutdown. */
static void ring_write(basis_hls_t* h, const uint8_t* data, int n) {
    int written = 0;
    while (written < n && hls_should_run(h)) {
        hls_mutex_lock(&h->lock);
        /* Stop filling the moment a seek is requested: the bytes queued here are
         * pre-seek and about to be flushed, and the consumer withholds the ring
         * while the seek is in flight, so a full ring here would otherwise wedge
         * the producer against a consumer that has stopped draining. The producer
         * picks the seek up at the top of its next loop. */
        if (h->seek_pending) { hls_mutex_unlock(&h->lock); break; }
        int space = h->ring_cap - h->ring_count;
        if (space > 0) {
            int chunk = n - written;
            if (chunk > space) chunk = space;
            int first = h->ring_cap - h->ring_head;
            if (first > chunk) first = chunk;
            memcpy(h->ring + h->ring_head, data + written, (size_t)first);
            if (chunk > first) memcpy(h->ring, data + written + first, (size_t)(chunk - first));
            h->ring_head = (h->ring_head + chunk) % h->ring_cap;
            h->ring_count += chunk;
            hls_mutex_unlock(&h->lock);
            written += chunk;
        } else {
            hls_mutex_unlock(&h->lock);
            hls_sleep_ms(2); /* ring full — wait for the consumer to drain */
        }
    }
}

/* Producer loop: fetch segments/parts (and reload the live playlist) ahead of
 * playout into the ring, so the decoder sees a continuous byte stream with no
 * per-segment connection gaps. */
static void hls_producer(basis_hls_t* h) {
    uint8_t tmp[16384];
    while (hls_should_run(h)) {
        /* Take the seek request under the lock so the pending flag, target and
         * generation read as one consistent snapshot. Only accept it when the VOD
         * segment list is present (the only seekable case) — clearing the flag
         * without flushing would strand the reader withholding the ring. */
        hls_mutex_lock(&h->lock);
        int do_seek = h->seek_pending && h->vod_count > 0;
        long seek_target = h->seek_target_ms;
        long seek_g = h->seek_gen;
        if (do_seek) h->seek_pending = 0;
        hls_mutex_unlock(&h->lock);
        if (do_seek) {
            /* Reposition: drop the current segment, rebuild the queue from the
             * segment containing the target, flush buffered bytes. The consumer
             * may hold a partial TS packet from before the jump; the TS demuxer
             * resynchronises on the 0x47 sync byte. */
            if (h->seg_ctx) { h->http.close(h->seg_ctx); h->seg_ctx = NULL; }
            long acc = 0;
            int idx = 0;
            for (; idx < h->vod_count - 1; ++idx) {
                if (acc + h->vod_dur_ms[idx] > seek_target) break;
                acc += h->vod_dur_ms[idx];
            }
            h->pending_head = 0;
            h->pending_count = 0;
            for (int i = idx; i < h->vod_count; ++i)
                queue_push(h, h->vod_uri[i], h->vod_dur_ms[i]);
            hls_mutex_lock(&h->lock);
            h->ring_head = h->ring_tail = h->ring_count = 0;
            /* Publish the flush for the snapshot generation under the same lock
             * as the ring clear, so a consumer that acquires it sees an emptied
             * ring and the matched generation together; its next serve is
             * guaranteed post-seek. Leaving idle in the same critical section
             * keeps the reader from ever pairing an emptied ring with a stale
             * end-of-stream verdict while the target segment is still fetching. */
            h->flush_gen = seek_g;
            h->vod_idle = 0;
            hls_mutex_unlock(&h->lock);
        }
        if (!h->seg_ctx) {
            if (h->is_fmp4 && !h->map_served && h->map_uri[0]) {
                int blocked = 0;
                h->seg_ctx = hls_guarded_open(h, h->map_uri, &blocked); /* fMP4 init segment first */
                if (!h->seg_ctx) {
                    /* A policy-blocked map can never load and its fragments are
                     * useless without it, so stop instead of spinning; a transient
                     * open failure backs off and retries. */
                    if (blocked) break;
                    if (!hls_should_run(h)) break;
                    hls_sleep_ms(50);
                    continue;
                }
                h->map_served = 1;
            } else {
                const char* next = queue_pop(h, NULL);
                if (next) {
                    int blocked = 0;
                    h->seg_ctx = hls_guarded_open(h, next, &blocked);
                    if (!h->seg_ctx) {
                        if (blocked) break;    /* policy-blocked (SSRF): fail playback deterministically */
                        continue;              /* transient: skip; the next pop advances */
                    }
                    h->empty_reloads = 0;
                } else if (h->endlist_seen) {
                    /* Unseekable VOD (fMP4: no TS segment list, request_seek
                     * rejects it) has nothing to park for — exit normally. */
                    if (h->vod_count == 0) break;
                    /* VOD exhausted — park, don't exit. A backward seek into the
                     * tail must still work for as long as the source is open, so
                     * the thread idles here and the top-of-loop seek take revives
                     * it. Arbitrate against a pending request under the lock so
                     * the two can't interleave: either loop back to honour it, or
                     * publish idle for the reader's end-of-stream verdict. */
                    hls_mutex_lock(&h->lock);
                    if (h->seek_pending) { hls_mutex_unlock(&h->lock); continue; }
                    h->vod_idle = 1;
                    hls_mutex_unlock(&h->lock);
                    hls_sleep_ms(10);
                    continue;
                } else {
                    int r = reload_and_enqueue(h);
                    if (r > 0) { h->empty_reloads = 0; }
                    else if (r == -2) break; /* playlist policy-blocked: retrying can't recover */
                    else if (r < 0) {
                        if (!hls_should_run(h)) break;
                        hls_sleep_ms(50); /* transient fetch error — back off and retry */
                    } else { /* r == 0: nothing new yet */
                        if (++h->empty_reloads >= HLS_MAX_EMPTY_RELOADS) break;
                        if (!h->can_block_reload) {
                            long wait = h->target_duration_ms > 0 ? h->target_duration_ms / 2 : 1000, w = 0;
                            while (w < wait && hls_should_run(h)) { hls_sleep_ms(50); w += 50; }
                        }
                    }
                    continue;
                }
            }
        }

        int n = h->http.read(h->seg_ctx, tmp, (int)sizeof(tmp));
        if (n > 0) {
            ring_write(h, tmp, n);
        } else {
            h->http.close(h->seg_ctx);
            h->seg_ctx = NULL;
            /* top up the queue in the background so the next segment is ready */
            if (!h->endlist_seen && h->pending_count <= HLS_LIVE_MARGIN_SEGMENTS)
                reload_and_enqueue(h);
        }
    }
    /* Publish under the lock so basis_hls_read and basis_hls_request_seek see a
     * consistent producer_done (the endlist path already set it under the lock
     * to arbitrate against a late seek; this covers the stop/exhaustion exits). */
    hls_mutex_lock(&h->lock);
    h->producer_done = 1;
    hls_mutex_unlock(&h->lock);
}

#if defined(_WIN32)
static DWORD WINAPI hls_thread_entry(LPVOID arg) { hls_producer((basis_hls_t*)arg); return 0; }
#else
static void* hls_thread_entry(void* arg) { hls_producer((basis_hls_t*)arg); return NULL; }
#endif

static int hls_thread_start(basis_hls_t* h) {
#if defined(_WIN32)
    h->thread = CreateThread(NULL, 0, hls_thread_entry, h, 0, NULL);
    return h->thread != NULL;
#else
    return pthread_create(&h->thread, NULL, hls_thread_entry, h) == 0;
#endif
}

static void hls_thread_join(basis_hls_t* h) {
    if (!h->thread_started) return;
#if defined(_WIN32)
    WaitForSingleObject(h->thread, INFINITE);
    CloseHandle(h->thread);
#else
    pthread_join(h->thread, NULL);
#endif
    h->thread_started = 0;
}

/* ---- public API ---------------------------------------------------------- */

void* basis_hls_open(const char* url, const basis_http_provider_t* http,
                     int (*is_running)(void* user), void* user, int* out_is_fmp4) {
    if (!url || !http || !http->open || !http->read || !http->close) return NULL;

    basis_hls_t* h = (basis_hls_t*)calloc(1, sizeof(*h));
    if (!h) return NULL;
    h->http = *http;
    h->is_running = is_running;
    h->user = user;

    /* Fetch the entry playlist; follow one master->media indirection. */
    char* text = NULL;
    if (fetch_text(h, url, &text, NULL) < 0 || !text) { free(text); free(h); return NULL; }

    if (playlist_is_master(text)) {
        char media[HLS_MAX_URI];
        if (!master_pick_variant(h, url, text, media, sizeof(media))) { free(text); free(h); return NULL; }
        snprintf(h->media_url, sizeof(h->media_url), "%s", media);
        free(text);
        if (fetch_text(h, h->media_url, &text, NULL) < 0 || !text) { free(text); free(h); return NULL; }
    } else {
        snprintf(h->media_url, sizeof(h->media_url), "%s", url);
    }

    /* Off the stack: the playlist struct is ~1 MiB. */
    hls_playlist_t* pl = (hls_playlist_t*)malloc(sizeof(*pl));
    if (!pl) { free(text); free(h); return NULL; }
    parse_media_playlist(h->media_url, text, pl);
    free(text);

    /* Nothing fetchable. Either the playlist carries no media at all, or every
     * URI in it was over the length cap — both are terminal, and failing here
     * beats handing the producer a list it can only reload fruitlessly. */
    if (pl->item_count == 0) { free(pl); free(h); return NULL; }

    h->is_fmp4 = pl->is_fmp4;
    h->can_block_reload = pl->can_block_reload && pl->has_parts;
    h->part_target_ms = pl->part_target_ms;
    h->target_duration_ms = pl->target_duration_ms ? pl->target_duration_ms : 6000;
    h->endlist_seen = pl->has_endlist;
    if (pl->has_endlist) {
        /* Sum whole segments only — parts subdivide the same media time. A VOD
         * beyond HLS_MAX_ITEMS is truncated at parse, so this under-reports in
         * lockstep with what actually plays. */
        for (int i = 0; i < pl->item_count; ++i)
            if (playlist_item(pl, i)->part < 0) h->total_ms += playlist_item(pl, i)->dur_ms;
        /* Retain the segment list so a seek can rebuild the queue from any
         * index. fMP4 VOD is excluded: a mid-stream ring flush would land the
         * demuxer inside a box, and it can't resynchronise the way TS does. */
        if (!pl->is_fmp4) {
            h->vod_uri = (char (*)[HLS_MAX_URI])malloc((size_t)pl->item_count * HLS_MAX_URI);
            h->vod_dur_ms = (long*)malloc((size_t)pl->item_count * sizeof(long));
            if (h->vod_uri && h->vod_dur_ms) {
                for (int i = 0; i < pl->item_count; ++i) {
                    const hls_item_t* it = playlist_item(pl, i);
                    if (it->part >= 0) continue;
                    memcpy(h->vod_uri[h->vod_count], it->uri, HLS_MAX_URI);
                    h->vod_dur_ms[h->vod_count] = it->dur_ms;
                    h->vod_count++;
                }
            } else {
                free(h->vod_uri); h->vod_uri = NULL;
                free(h->vod_dur_ms); h->vod_dur_ms = NULL;
            }
        }
    }
    if (pl->map_uri[0]) snprintf(h->map_uri, sizeof(h->map_uri), "%s", pl->map_uri);

    /* VOD (EXT-X-ENDLIST): start at the first segment so the whole recording
     * plays start-to-finish. Live: start at (or just behind) the live edge.
     * LL live: the in-progress segment's first part (lowest latency; first part
     * of a segment is normally an independent keyframe). Non-LL live: the last
     * complete segment (guaranteed keyframe). */
    if (h->endlist_seen) {
        h->want_msn = pl->media_seq_base;
        h->want_part = 0;
    } else if (h->can_block_reload) {
        h->want_msn = pl->media_seq_base + pl->nfull;
        h->want_part = 0;
    } else {
        /* Start a few segments behind the live edge so playout always has a buffer and
         * segment fetches never wait on the encoder (plain HLS has no parts to ride). Each
         * segment starts on a keyframe, so any of these is a valid decode start. Clamp to
         * the oldest retained item, not to media_seq_base: a DVR window lists more
         * segments than are kept, and the ones before item[0] were rolled out. */
        long edge = pl->media_seq_base + (pl->nfull > 0 ? pl->nfull - 1 : 0);
        long start = edge - HLS_LIVE_MARGIN_SEGMENTS;
        long oldest = playlist_item(pl, 0)->msn;
        if (start < oldest) start = oldest;
        h->want_msn = start;
        h->want_part = 0;
    }

    enqueue_new_media(h, pl);
    free(pl);

    /* Start the read-ahead producer so segments buffer ahead of playout. */
    h->ring_cap = HLS_RING_CAP;
    h->ring = (uint8_t*)malloc((size_t)h->ring_cap);
    if (!h->ring) { free(h->vod_uri); free(h->vod_dur_ms); free(h); return NULL; }
    hls_mutex_init(&h->lock);
    if (!hls_thread_start(h)) {
        hls_mutex_destroy(&h->lock);
        free(h->ring);
        free(h->vod_uri);
        free(h->vod_dur_ms);
        free(h);
        return NULL;
    }
    h->thread_started = 1;

    if (out_is_fmp4) *out_is_fmp4 = h->is_fmp4;
    return h;
}

int basis_hls_read(void* ctx, uint8_t* buf, int len) {
    basis_hls_t* h = (basis_hls_t*)ctx;
    if (!h || len <= 0) return 0;

    for (;;) {
        if (h->stop || (h->is_running && !h->is_running(h->user))) return 0;

        /* No metering here: the engine paces delivery by AU timestamp (pace_gate),
         * holding the demux thread on submit, so serving the ring as fast as the
         * demuxer pulls can't flood the decoder. The seek generations are read
         * under the same lock that guards the ring, so the boundary stays
         * consistent with the flushed ring state (volatile orders nothing across
         * threads on its own). */
        hls_mutex_lock(&h->lock);
        /* Seek in flight: the ring still holds pre-seek bytes until the producer
         * flushes and requeues at the target. Withhold them, since handing them
         * to the demuxer would let a stale AU re-anchor pacing to the old timeline.
         * producer_done here means the thread actually exited (stop / policy
         * block / reload exhaustion) — a parked VOD producer still honours the
         * request — so only then stop waiting for a flush that will never come. */
        if (h->seek_gen != h->flush_gen) {
            int done = h->producer_done;
            hls_mutex_unlock(&h->lock);
            if (done) return 0;
            hls_sleep_ms(2);
            continue;
        }
        /* First read after the flush settled: raise the reposition boundary once
         * so the demuxer drops its pre-seek state and re-anchors before the
         * target segment's bytes flow. */
        if (h->flush_gen != h->read_signaled_gen) {
            h->read_signaled_gen = h->flush_gen;
            hls_mutex_unlock(&h->lock);
            return BASIS_READ_REPOSITION;
        }
        if (h->ring_count > 0) {
            int take = h->ring_count < len ? h->ring_count : len;
            int first = h->ring_cap - h->ring_tail;
            if (first > take) first = take;
            memcpy(buf, h->ring + h->ring_tail, (size_t)first);
            if (take > first) memcpy(buf + first, h->ring, (size_t)(take - first));
            h->ring_tail = (h->ring_tail + take) % h->ring_cap;
            h->ring_count -= take;
            hls_mutex_unlock(&h->lock);
            return take;
        }
        /* End of stream: the ring is drained and either the producer exited or a
         * fully-fetched VOD is parked with no seek in flight (this branch is only
         * reachable with the generations settled, so a pending seek can't race
         * the verdict — request_seek bumps the generation under this lock). */
        int done = h->producer_done || h->vod_idle;
        hls_mutex_unlock(&h->lock);
        if (done) return 0;

        hls_sleep_ms(2); /* ring empty: wait for the producer to buffer more */
    }
}

int basis_hls_is_vod(void* ctx) {
    basis_hls_t* h = (basis_hls_t*)ctx;
    return h ? h->endlist_seen : 0;
}

long basis_hls_duration_ms(void* ctx) {
    basis_hls_t* h = (basis_hls_t*)ctx;
    return h ? h->total_ms : 0;
}

int basis_hls_can_seek(void* ctx) {
    basis_hls_t* h = (basis_hls_t*)ctx;
    return h && h->vod_count > 0 && !h->producer_done;
}

int basis_hls_request_seek(void* ctx, long long target_ms) {
    basis_hls_t* h = (basis_hls_t*)ctx;
    if (!h || h->vod_count <= 0 || target_ms < 0) return -1;   /* vod_count is fixed at open */
    /* Reject only when the producer thread has actually exited — a fully-fetched
     * VOD parks its producer instead, precisely so a seek into the tail (or after
     * playout drained the ring) still repositions. Accept atomically with that
     * check so a request can't be lost against a concurrent exit; publish
     * {target, generation, pending} together, and the generation runs ahead of
     * flush_gen until the producer finishes flushing, during which the consumer
     * withholds the pre-seek ring. */
    hls_mutex_lock(&h->lock);
    if (h->producer_done) { hls_mutex_unlock(&h->lock); return -1; }
    h->seek_target_ms = (long)target_ms;
    h->seek_gen++;
    h->seek_pending = 1;
    hls_mutex_unlock(&h->lock);
    return 0;
}

void basis_hls_close(void* ctx) {
    basis_hls_t* h = (basis_hls_t*)ctx;
    if (!h) return;
    h->stop = 1;
    hls_thread_join(h);
    if (h->seg_ctx) { h->http.close(h->seg_ctx); h->seg_ctx = NULL; }
    hls_mutex_destroy(&h->lock);
    free(h->vod_uri);
    free(h->vod_dur_ms);
    free(h->ring);
    free(h);
}
