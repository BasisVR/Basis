/*
 * basis_webm.c — WebM/Matroska demuxer -> VP9/AV1 video + Opus audio into a
 * basis_media_sink.
 *
 * Scope (deliberately narrow): one video track (V_VP9 / V_AV1) and one audio
 * track (A_OPUS), either or both. Other CodecIDs (subtitles, A_VORBIS, …) are
 * skipped; a WebM with no supported track at all raises a clear error instead of
 * playing silently. An audio-only Opus WebM (YouTube's audio legs) is valid input.
 * Blocks route to video vs audio by their TrackNumber varint.
 *
 * Walk rules (matching what real muxers emit and tolerant parsers accept):
 *   - Segment children arrive in any order; multiple SeekHeads are legal.
 *     A Cluster before Tracks is treated as malformed (playing blocks with no
 *     announced track is meaningless).
 *   - Only Segment and Cluster may be unknown-size (streamed WebM). An
 *     unknown-size Cluster ends at the next Segment-child ID. Any other
 *     unknown-size element is malformed.
 *   - Clusters are streamed child-by-child, never whole-buffered: a cluster is
 *     seconds of media (>10 MiB at 4K) and demuxer memory is per-player-instance
 *     memory. Only individual block payloads are buffered (WEBM_MAX_BLOCK);
 *     whole-buffered header elements (Info/Tracks/SeekHead/Cues) are capped at
 *     WEBM_MAX_HEADER.
 *   - Block-relative timestamps are SIGNED s16; TimestampScale is not always
 *     the 1 ms default — all timestamp math saturates in int64.
 *
 * Seek (Cues index, cluster granularity): Cues before the clusters parse
 * inline; a trailing Cues (default ffmpeg mux) is ranged-fetched at open via
 * the start-of-Segment SeekHead when the source can reposition. Duration is
 * only reported once the index and a repositionable source are both in hand,
 * so a reported duration always means the seek bar works; cueless or
 * non-repositionable streams play forward-only with duration 0.
 */

#include "basis_webm.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>

#define WEBM_MAX_HEADER  (16LL * 1024 * 1024)  /* whole-buffered header elements
                                                 * (Info/Tracks/SeekHead/Cues): real ones
                                                 * are KBs — a 64k-entry Cues is ~1.5 MiB —
                                                 * so a declared size beyond this is hostile,
                                                 * not big */
#define WEBM_MAX_BLOCK   (64LL * 1024 * 1024)  /* single block payload cap */
#define WEBM_MAX_LACES   256                    /* flag byte holds count-1, so 256 is the format max */
#define WEBM_MAX_CUES    65536                  /* ~1 MiB of index; hostile-input guard (a 10 h
                                                 * video cued every 5 s is ~7k entries) */

/* Element IDs (marker bit kept, as read). */
#define ID_EBML          0x1A45DFA3
#define ID_SEGMENT       0x18538067
#define ID_SEEKHEAD      0x114D9B74
#define ID_INFO          0x1549A966
#define ID_TRACKS        0x1654AE6B
#define ID_CUES          0x1C53BB6B
#define ID_CLUSTER       0x1F43B675
#define ID_CHAPTERS      0x1043A770
#define ID_TAGS          0x1254C367
#define ID_ATTACHMENTS   0x1941A469
#define ID_VOID          0xEC
#define ID_CRC32         0xBF
/* EBML header children */
#define ID_DOCTYPE       0x4282
#define ID_MAXIDLENGTH   0x42F2
#define ID_MAXSIZELENGTH 0x42F3
/* Info children */
#define ID_TIMESTAMPSCALE 0x2AD7B1
#define ID_DURATION      0x4489
/* SeekHead children */
#define ID_SEEK          0x4DBB
#define ID_SEEKID        0x53AB
#define ID_SEEKPOSITION  0x53AC
/* Tracks children */
#define ID_TRACKENTRY    0xAE
#define ID_TRACKNUMBER   0xD7
#define ID_TRACKTYPE     0x83
#define ID_CODECID       0x86
#define ID_CODECPRIVATE  0x63A2
#define ID_CODECDELAY    0x56AA
#define ID_VIDEO         0xE0
#define ID_PIXELWIDTH    0xB0
#define ID_PIXELHEIGHT   0xBA
#define ID_AUDIO         0xE1
#define ID_CHANNELS      0x9F
#define ID_SAMPLINGFREQ  0xB5
/* Cluster children */
#define ID_TIMESTAMP     0xE7
#define ID_SIMPLEBLOCK   0xA3
#define ID_BLOCKGROUP    0xA0
#define ID_BLOCK         0xA1
#define ID_REFERENCEBLOCK 0xFB
/* Cues children */
#define ID_CUEPOINT      0xBB
#define ID_CUETIME       0xB3
#define ID_CUETRACKPOSITIONS 0xB7
#define ID_CUETRACK      0xF7
#define ID_CUECLUSTERPOSITION 0xF1

/* Cue times stay in ticks: Segment children arrive in any order, so the
 * TimestampScale may not be final when Cues parse — the seek search converts
 * per comparison, when the scale is settled. */
typedef struct { int64_t time_ticks; int64_t abs_offset; } webm_cue_t;

typedef struct {
    basis_media_sink_t* sink;
    basis_read_fn read;
    void* ctx;
    basis_reseek_fn reseek;   /* NULL: the byte source can't reposition */
    void* reseek_ctx;

    int64_t pos;              /* absolute stream offset consumed so far */
    int64_t segment_base;     /* absolute offset of the Segment data (Cue/Seek
                               * positions are relative to it) */

    /* Info */
    int64_t ts_scale_ns;      /* ns per tick; default 1 000 000 (1 ms) */
    int64_t duration_us;      /* from Info->Duration; 0 = unknown */

    /* the selected (first supported) video track */
    int     track_num;        /* 0 = none selected */
    basis_codec_t codec;
    int     width, height;
    uint8_t extradata[2048];  /* AV1: configOBUs from CodecPrivate; VP9: none */
    int     extradata_len;
    int     announced;
    int     duration_sent;
    char    bad_codec[32];    /* first unsupported video CodecID, for the error */
    int     saw_tracks;

    /* the selected audio track (Opus). A WebM may carry video, audio, or both;
     * an audio-only Opus WebM (YouTube's audio legs) is valid input. */
    int     audio_track_num;  /* 0 = none selected */
    basis_codec_t audio_codec;
    int     audio_channels;
    int     audio_rate;       /* announce rate; Opus always decodes at 48 kHz */
    int64_t audio_codec_delay_us; /* CodecDelay: subtracted from audio block times */
    uint8_t audio_extradata[64]; /* OpusHead: 19 fixed + up to 8-ch mapping table */
    int     audio_extradata_len;

    /* Cues index */
    webm_cue_t* cues;
    uint32_t ncues, cues_cap;
    int64_t seekhead_cues_rel; /* Cues offset from SeekHead, -1 = none seen */

    /* cluster walk state. cluster_ts persists across cluster boundaries, so a
     * block arriving before its cluster's Timestamp element uses the last
     * known cluster time rather than failing. */
    int64_t cluster_ts;       /* ticks; last known cluster base time */
    int     in_cluster;

    uint8_t* blockbuf;        /* reused block payload buffer */
    int64_t  blockcap;
} webm_t;

/* ---- byte source --------------------------------------------------------- */

static int wread_exact(webm_t* w, uint8_t* buf, int64_t n) {
    int64_t got = 0;
    while (got < n) {
        if (!w->sink->is_running(w->sink->user)) return 0;
        int want = (n - got) > (1 << 20) ? (1 << 20) : (int)(n - got);
        int r = w->read(w->ctx, buf + got, want);
        if (r <= 0) return 0;
        got += r;
        w->pos += r;
    }
    return 1;
}

static int wskip(webm_t* w, int64_t n) {
    uint8_t tmp[16384];
    while (n > 0) {
        if (!w->sink->is_running(w->sink->user)) return 0;
        int want = n > (int64_t)sizeof(tmp) ? (int)sizeof(tmp) : (int)n;
        int r = w->read(w->ctx, tmp, want);
        if (r <= 0) return 0;
        w->pos += r;
        n -= r;
    }
    return 1;
}

/* ---- EBML varints off the stream ----------------------------------------- */

static int vint_len(uint8_t first) {
    for (int i = 0; i < 8; ++i)
        if (first & (0x80u >> i)) return i + 1;
    return 0; /* 0x00: reserved/invalid */
}

/* Element ID: 1-4 bytes, marker bit kept. Returns 1 ok, 0 EOF/stop at the first
 * byte (clean element boundary), -1 malformed/short read. */
static int read_id(webm_t* w, uint32_t* id) {
    uint8_t b;
    if (!w->sink->is_running(w->sink->user)) return 0;
    int r = w->read(w->ctx, &b, 1);
    if (r <= 0) return 0;
    w->pos += 1;
    int len = vint_len(b);
    if (len < 1 || len > 4) return -1;   /* EBMLMaxIDLength 4 is validated */
    uint32_t v = b;
    for (int i = 1; i < len; ++i) {
        if (!wread_exact(w, &b, 1)) return -1;
        v = (v << 8) | b;
    }
    *id = v;
    return 1;
}

/* Element size: 1-8 bytes, marker stripped. *size = -1 for unknown size (all
 * value bits set). Returns 1 ok, 0 malformed/short read. */
static int read_size(webm_t* w, int64_t* size) {
    uint8_t b;
    if (!wread_exact(w, &b, 1)) return 0;
    int len = vint_len(b);
    if (len < 1 || len > 8) return 0;
    uint64_t v = b & (0xFFu >> len);
    uint64_t all1 = (0xFFu >> len);
    int allones = ((b & (0xFFu >> len)) == all1);
    for (int i = 1; i < len; ++i) {
        if (!wread_exact(w, &b, 1)) return 0;
        v = (v << 8) | b;
        if (b != 0xFF) allones = 0;
    }
    *size = allones ? -1 : (int64_t)v;
    return 1;
}

/* ---- EBML values inside a buffered element ------------------------------- */

typedef struct { const uint8_t* p; int64_t len; int64_t off; } ebuf_t;

static int ebuf_id(ebuf_t* b, uint32_t* id) {
    if (b->off >= b->len) return 0;
    int len = vint_len(b->p[b->off]);
    if (len < 1 || len > 4 || b->off + len > b->len) return -1;
    uint32_t v = 0;
    for (int i = 0; i < len; ++i) v = (v << 8) | b->p[b->off + i];
    b->off += len;
    *id = v;
    return 1;
}

static int ebuf_size(ebuf_t* b, int64_t* size) {
    if (b->off >= b->len) return 0;
    int len = vint_len(b->p[b->off]);
    if (len < 1 || len > 8 || b->off + len > b->len) return 0;
    uint64_t v = b->p[b->off] & (0xFFu >> len);
    int allones = (v == (0xFFu >> len));
    for (int i = 1; i < len; ++i) {
        v = (v << 8) | b->p[b->off + i];
        if (b->p[b->off + i] != 0xFF) allones = 0;
    }
    b->off += len;
    *size = allones ? -1 : (int64_t)v;
    return 1;
}

static uint64_t ebml_uint(const uint8_t* p, int64_t len) {
    uint64_t v = 0;
    if (len > 8) len = 8;
    for (int64_t i = 0; i < len; ++i) v = (v << 8) | p[i];
    return v;
}

static double ebml_float(const uint8_t* p, int64_t len) {
    if (len == 4) {
        uint32_t u = (uint32_t)ebml_uint(p, 4);
        float f;
        memcpy(&f, &u, 4);
        return (double)f;
    }
    if (len == 8) {
        uint64_t u = ebml_uint(p, 8);
        double d;
        memcpy(&d, &u, 8);
        return d;
    }
    return 0.0;
}

/* ---- time math ----------------------------------------------------------- */

static int64_t add_i64_sat(int64_t a, int64_t b) {
    if (b > 0 && a > INT64_MAX - b) return INT64_MAX;
    if (b < 0 && a < INT64_MIN - b) return INT64_MIN;
    return a + b;
}

/* ticks * ts_scale_ns / 1000 -> microseconds, saturating. Ticks and scale are
 * both remote content; the multiply is split so it can't overflow int64, and
 * the scale is bounded here as well as at parse — past ~1.8e16 the fractional
 * term (r <= 999) would wrap uint64 before the clamp could see it. */
static int64_t ticks_to_us(int64_t ticks, int64_t scale_ns) {
    if (scale_ns <= 0 || scale_ns > 1000000000) scale_ns = 1000000;
    int neg = ticks < 0;
    uint64_t t = neg ? (uint64_t)(-(ticks + 1)) + 1u : (uint64_t)ticks;
    uint64_t s = t / 1000u, r = t % 1000u;
    uint64_t us;
    if (s > (uint64_t)INT64_MAX / (uint64_t)scale_ns)
        us = (uint64_t)INT64_MAX;
    else
        us = s * (uint64_t)scale_ns + r * (uint64_t)scale_ns / 1000u;
    if (us > (uint64_t)INT64_MAX) us = (uint64_t)INT64_MAX;
    return neg ? -(int64_t)us : (int64_t)us;
}

/* ---- element helpers ------------------------------------------------------ */

/* Reads a whole element body into a malloc'd buffer (caller frees). NULL on
 * bound/alloc/short-read. */
static uint8_t* read_element(webm_t* w, int64_t size, int64_t cap) {
    if (size < 0 || size > cap) return NULL;
    uint8_t* buf = (uint8_t*)malloc((size_t)(size ? size : 1));
    if (!buf) return NULL;
    if (!wread_exact(w, buf, size)) { free(buf); return NULL; }
    return buf;
}

/* ---- header elements ------------------------------------------------------ */

/* EBML header: validate DocType and the ID/size length limits (parsers that
 * skip this can be walked into reading garbage as IDs). */
static int parse_ebml_header(const uint8_t* p, int64_t len) {
    ebuf_t b = { p, len, 0 };
    uint32_t id;
    int64_t sz;
    char doctype[16] = "matroska";  /* absent DocType defaults to matroska */
    while (ebuf_id(&b, &id) == 1) {
        if (!ebuf_size(&b, &sz) || sz < 0 || sz > b.len - b.off) return 0;
        const uint8_t* body = b.p + b.off;
        if (id == ID_DOCTYPE && sz > 0) {
            int64_t n = sz < (int64_t)sizeof(doctype) - 1 ? sz : (int64_t)sizeof(doctype) - 1;
            memcpy(doctype, body, (size_t)n);
            doctype[n] = 0;
        } else if (id == ID_MAXIDLENGTH) {
            if (ebml_uint(body, sz) > 4) return 0;
        } else if (id == ID_MAXSIZELENGTH) {
            if (ebml_uint(body, sz) > 8) return 0;
        }
        b.off += sz;
    }
    return strcmp(doctype, "webm") == 0 || strcmp(doctype, "matroska") == 0;
}

static void parse_info(webm_t* w, const uint8_t* p, int64_t len) {
    ebuf_t b = { p, len, 0 };
    uint32_t id;
    int64_t sz;
    double dur_ticks = 0.0;
    while (ebuf_id(&b, &id) == 1) {
        if (!ebuf_size(&b, &sz) || sz < 0 || sz > b.len - b.off) return;
        const uint8_t* body = b.p + b.off;
        if (id == ID_TIMESTAMPSCALE) {
            /* bounded at 1 s/tick: a larger scale is hostile (it would also
             * overflow the timestamp math), not a plausible mux */
            uint64_t v = ebml_uint(body, sz);
            if (v > 0 && v <= 1000000000u) w->ts_scale_ns = (int64_t)v;
        } else if (id == ID_DURATION) {
            dur_ticks = ebml_float(body, sz);
        }
        b.off += sz;
    }
    if (dur_ticks > 0.0 && dur_ticks < 9e15)
        w->duration_us = ticks_to_us((int64_t)dur_ticks, w->ts_scale_ns);
}

/* Validate an OpusHead (CodecPrivate) per RFC 7845 §5.1 before the demuxer
 * commits to the track: magic, version, channel count, and the family-specific
 * layout. Family 0 is mono/stereo with no mapping table; family 1 (and the
 * undefined family 255) carry a stream/coupled count and an N-byte channel map;
 * every other family is reserved. Rejecting here means a malformed header
 * surfaces as an error rather than a track the decoder later refuses. */
static int valid_opushead(const uint8_t* p, int64_t len) {
    if (!p || len < 19 || memcmp(p, "OpusHead", 8) != 0) return 0;
    if (p[8] > 15) return 0;                             /* major version: >15 is incompatible */
    int ch = p[9];
    if (ch < 1 || ch > 8) return 0;
    int family = p[18];
    if (family == 0) return ch <= 2;                     /* no mapping table */
    if (family == 1 || family == 255) {
        if (len < 21 + (int64_t)ch) return 0;            /* streams + coupled + N-byte map */
        int streams = p[19], coupled = p[20];
        if (streams < 1 || coupled > streams || streams + coupled > 255) return 0;
        for (int i = 0; i < ch; ++i)                     /* map addresses a real stream, or 255 = silent */
            if (p[21 + i] != 255 && p[21 + i] >= streams + coupled) return 0;
        return 1;
    }
    return 0;                                            /* reserved family */
}

/* One TrackEntry. Selects the first supported video track; remembers the first
 * unsupported video CodecID for the error message. The CodecID switch is the
 * extension point: A_OPUS (Opus item) adds a branch here. */
static void parse_track_entry(webm_t* w, const uint8_t* p, int64_t len) {
    ebuf_t b = { p, len, 0 };
    uint32_t id;
    int64_t sz;
    int num = 0, type = 0, width = 0, height = 0, channels = 0;
    char codec[32] = {0};
    const uint8_t* priv = NULL;
    int64_t priv_len = 0;
    int64_t codec_delay_ns = 0;
    while (ebuf_id(&b, &id) == 1) {
        if (!ebuf_size(&b, &sz) || sz < 0 || sz > b.len - b.off) return;
        const uint8_t* body = b.p + b.off;
        switch (id) {
            case ID_TRACKNUMBER: num = (int)ebml_uint(body, sz); break;
            case ID_TRACKTYPE:   type = (int)ebml_uint(body, sz); break;
            case ID_CODECID: {
                int64_t n = sz < (int64_t)sizeof(codec) - 1 ? sz : (int64_t)sizeof(codec) - 1;
                memcpy(codec, body, (size_t)n);
                codec[n] = 0;
                break;
            }
            case ID_CODECPRIVATE: priv = body; priv_len = sz; break;
            case ID_CODECDELAY: codec_delay_ns = (int64_t)ebml_uint(body, sz); break;
            case ID_VIDEO: {
                ebuf_t vb = { body, sz, 0 };
                uint32_t vid;
                int64_t vsz;
                while (ebuf_id(&vb, &vid) == 1) {
                    if (!ebuf_size(&vb, &vsz) || vsz < 0 || vsz > vb.len - vb.off) break;
                    if (vid == ID_PIXELWIDTH)  width = (int)ebml_uint(vb.p + vb.off, vsz);
                    if (vid == ID_PIXELHEIGHT) height = (int)ebml_uint(vb.p + vb.off, vsz);
                    vb.off += vsz;
                }
                break;
            }
            case ID_AUDIO: {
                ebuf_t ab = { body, sz, 0 };
                uint32_t aid;
                int64_t asz;
                while (ebuf_id(&ab, &aid) == 1) {
                    if (!ebuf_size(&ab, &asz) || asz < 0 || asz > ab.len - ab.off) break;
                    if (aid == ID_CHANNELS) channels = (int)ebml_uint(ab.p + ab.off, asz);
                    ab.off += asz;
                }
                break;
            }
            default: break;
        }
        b.off += sz;
    }
    if (num <= 0) return;
    if (type == 1) {                         /* video (first supported track wins) */
        if (w->track_num) return;
        if (strcmp(codec, "V_VP9") == 0) {
            /* a VP9 track needs no CodecPrivate (rare, ignored) */
            w->track_num = num;
            w->codec = BASIS_CODEC_VP9;
            w->width = width;
            w->height = height;
        } else if (strcmp(codec, "V_AV1") == 0) {
            w->track_num = num;
            w->codec = BASIS_CODEC_AV1;
            w->width = width;
            w->height = height;
            /* CodecPrivate is the full av1C record (same bytes as the MP4 box
             * payload): 4 header bytes, then configOBUs. Only the configOBUs are
             * stored — the record header is not valid OBU syntax, and the decode
             * backends want a feedable OBU blob. Absent CodecPrivate is legal;
             * decoders parse the in-band sequence header. */
            if (priv && priv_len > 4 && priv_len - 4 <= (int64_t)sizeof(w->extradata)) {
                memcpy(w->extradata, priv + 4, (size_t)(priv_len - 4));
                w->extradata_len = (int)(priv_len - 4);
            }
        } else if (!w->bad_codec[0]) {
            strncpy(w->bad_codec, codec[0] ? codec : "unknown", sizeof(w->bad_codec) - 1);
        }
    } else if (type == 2) {                  /* audio (first supported track wins) */
        if (w->audio_track_num) return;
        if (strcmp(codec, "A_OPUS") == 0) {
            /* Only select the track once the OpusHead fully validates — announcing
             * an Opus track the decoder then rejects (bad/absent header) would
             * leave audio-only playback silent instead of reporting the stream. */
            if (priv && priv_len <= (int64_t)sizeof(w->audio_extradata)
                && valid_opushead(priv, priv_len)) {
                w->audio_track_num = num;
                w->audio_codec = BASIS_CODEC_OPUS;
                w->audio_rate = 48000;       /* Opus always decodes at 48 kHz */
                memcpy(w->audio_extradata, priv, (size_t)priv_len);
                w->audio_extradata_len = (int)priv_len;
                w->audio_channels = priv[9];
                /* CodecDelay (ns) is subtracted from block times to reach
                 * presentation time; for Opus it is the pre-skip, so the priming
                 * the decoder produces lands before 0 and is dropped, anchoring
                 * real audio at 0. A compliant file always sets it; if absent,
                 * fall back to the OpusHead pre-skip the decoder itself drops. */
                w->audio_codec_delay_us = codec_delay_ns > 0
                    ? codec_delay_ns / 1000
                    : (int64_t)(priv[10] | (priv[11] << 8)) * 1000000LL / 48000;
            }
        }
        /* other audio CodecIDs (A_VORBIS, A_AAC, …) stay skipped */
    }
}

static void parse_tracks(webm_t* w, const uint8_t* p, int64_t len) {
    ebuf_t b = { p, len, 0 };
    uint32_t id;
    int64_t sz;
    while (ebuf_id(&b, &id) == 1) {
        if (!ebuf_size(&b, &sz) || sz < 0 || sz > b.len - b.off) return;
        if (id == ID_TRACKENTRY) parse_track_entry(w, b.p + b.off, sz);
        b.off += sz;
    }
    w->saw_tracks = 1;
}

/* SeekHead: remember where a trailing Cues element sits (position relative to
 * the Segment data start). First Cues entry wins across multiple SeekHeads. */
static void parse_seekhead(webm_t* w, const uint8_t* p, int64_t len) {
    ebuf_t b = { p, len, 0 };
    uint32_t id;
    int64_t sz;
    while (ebuf_id(&b, &id) == 1) {
        if (!ebuf_size(&b, &sz) || sz < 0 || sz > b.len - b.off) return;
        if (id == ID_SEEK) {
            ebuf_t sb = { b.p + b.off, sz, 0 };
            uint32_t sid;
            int64_t ssz;
            uint32_t target = 0;
            int64_t position = -1;
            while (ebuf_id(&sb, &sid) == 1) {
                if (!ebuf_size(&sb, &ssz) || ssz < 0 || ssz > sb.len - sb.off) break;
                if (sid == ID_SEEKID) target = (uint32_t)ebml_uint(sb.p + sb.off, ssz);
                if (sid == ID_SEEKPOSITION) position = (int64_t)ebml_uint(sb.p + sb.off, ssz);
                sb.off += ssz;
            }
            if (target == ID_CUES && position >= 0 && w->seekhead_cues_rel < 0)
                w->seekhead_cues_rel = position;
        }
        b.off += sz;
    }
}

static int cues_add(webm_t* w, int64_t time_ticks, int64_t abs_offset) {
    if (w->ncues >= WEBM_MAX_CUES) return 1; /* hostile-input guard: index truncated,
                                              * seeks past it land on the last cue */
    if (w->ncues == w->cues_cap) {
        uint32_t cap = w->cues_cap ? w->cues_cap * 2 : 256;
        webm_cue_t* c = (webm_cue_t*)realloc(w->cues, (size_t)cap * sizeof(*c));
        if (!c) return 0;
        w->cues = c;
        w->cues_cap = cap;
    }
    w->cues[w->ncues].time_ticks = time_ticks;
    w->cues[w->ncues].abs_offset = abs_offset;
    w->ncues++;
    return 1;
}

static void parse_cues(webm_t* w, const uint8_t* p, int64_t len) {
    ebuf_t b = { p, len, 0 };
    uint32_t id;
    int64_t sz;
    while (ebuf_id(&b, &id) == 1) {
        if (!ebuf_size(&b, &sz) || sz < 0 || sz > b.len - b.off) return;
        if (id == ID_CUEPOINT) {
            ebuf_t cb = { b.p + b.off, sz, 0 };
            uint32_t cid;
            int64_t csz;
            int64_t time_ticks = -1, pos_match = -1, pos_first = -1;
            while (ebuf_id(&cb, &cid) == 1) {
                if (!ebuf_size(&cb, &csz) || csz < 0 || csz > cb.len - cb.off) break;
                if (cid == ID_CUETIME) {
                    time_ticks = (int64_t)ebml_uint(cb.p + cb.off, csz);
                } else if (cid == ID_CUETRACKPOSITIONS) {
                    /* one CueTrackPositions per cued track: use the selected
                     * video track's position; the first one is the fallback
                     * when no CueTrack matches (or none is present) */
                    ebuf_t pb = { cb.p + cb.off, csz, 0 };
                    uint32_t pid;
                    int64_t psz, ctrack = -1, cpos = -1;
                    while (ebuf_id(&pb, &pid) == 1) {
                        if (!ebuf_size(&pb, &psz) || psz < 0 || psz > pb.len - pb.off) break;
                        if (pid == ID_CUETRACK) ctrack = (int64_t)ebml_uint(pb.p + pb.off, psz);
                        if (pid == ID_CUECLUSTERPOSITION && cpos < 0)
                            cpos = (int64_t)ebml_uint(pb.p + pb.off, psz);
                        pb.off += psz;
                    }
                    if (cpos >= 0) {
                        if (pos_first < 0) pos_first = cpos;
                        if (w->track_num && ctrack == w->track_num && pos_match < 0)
                            pos_match = cpos;
                    }
                }
                cb.off += csz;
            }
            int64_t cluster_rel = pos_match >= 0 ? pos_match : pos_first;
            if (time_ticks >= 0 && cluster_rel >= 0)
                if (!cues_add(w, time_ticks, add_i64_sat(w->segment_base, cluster_rel)))
                    return;
        }
        b.off += sz;
    }
}

/* ---- blocks --------------------------------------------------------------- */

/* Emits the frames of one (Simple)Block payload. `key` is the SimpleBlock flag
 * bit, or the no-ReferenceBlock verdict for a BlockGroup Block. */
/* Opus TOC -> samples at 48 kHz (frame size * frame count), for advancing the
 * timestamp between laced Opus packets that share one block timestamp. */
static int opus_packet_samples(const uint8_t* p, int len) {
    static const int kFrame48k[32] = {
        480, 960, 1920, 2880,  480, 960, 1920, 2880,  480, 960, 1920, 2880,
        480, 960,  480,  960,  120, 240,  480,  960,  120, 240,  480,  960,
        120, 240,  480,  960,  120, 240,  480,  960
    };
    if (len < 1) return 0;
    int config = p[0] >> 3;
    int code = p[0] & 0x3;
    int frames = code == 0 ? 1 : code == 3 ? (len >= 2 ? (p[1] & 0x3F) : 0) : 2;
    long s = (long)kFrame48k[config] * frames;
    if (s < 0) s = 0; if (s > 5760) s = 5760;   /* Opus per-packet max */
    return (int)s;
}

static void emit_block(webm_t* w, const uint8_t* p, int64_t len, int key) {
    /* track number is itself an EBML varint (usually 1 byte, parse properly) */
    if (len < 1) return;
    int tlen = vint_len(p[0]);
    if (tlen < 1 || tlen > 8 || (int64_t)tlen + 3 > len) return;
    uint64_t track = (uint64_t)(p[0] & (0xFFu >> tlen));
    for (int i = 1; i < tlen; ++i) track = (track << 8) | p[i];
    int is_video = w->track_num && (int)track == w->track_num;
    int is_audio = w->audio_track_num && (int)track == w->audio_track_num;
    if (!is_video && !is_audio) return;      /* other tracks are skipped */

    /* signed s16 relative timestamp (negative is legal — saturating math) */
    int16_t rel = (int16_t)((p[tlen] << 8) | p[tlen + 1]);
    uint8_t flags = p[tlen + 2];
    const uint8_t* body = p + tlen + 3;
    int64_t blen = len - tlen - 3;

    int64_t ticks = add_i64_sat(w->cluster_ts, (int64_t)rel);
    int64_t pts_us = ticks_to_us(ticks, w->ts_scale_ns);
    /* Matroska CodecDelay: block times are on the encoder timeline; subtract it
     * so presentation time is right. For Opus the early (priming) frames go
     * negative and drop, leaving real audio at 0 — matching the Ogg lane. The
     * audio PTS must be allowed to stay negative for that pre-roll, so only video
     * is clamped to 0. (codec_delay is >= 0, so negating for the saturating add
     * can't overflow.) */
    if (is_audio) pts_us = add_i64_sat(pts_us, -w->audio_codec_delay_us);
    else if (pts_us < 0) pts_us = 0;

    int lacing = (flags >> 1) & 0x3;   /* 00 none, 01 Xiph, 10 fixed, 11 EBML */
    if (lacing == 0) {
        if (blen > 0) {
            if (is_video)
                w->sink->on_video_au(w->sink->user, body, (int)blen, pts_us, pts_us, key);
            else
                w->sink->on_audio_frame(w->sink->user, body, (int)blen, pts_us);
        }
        return;
    }

    /* laced: count byte, then per-scheme sizes; each frame is its own AU with
     * the block timestamp. Video essentially never laces — this is defensive
     * completeness so a hostile file can't desync the walk. */
    if (blen < 1) return;
    int nframes = body[0] + 1;
    int64_t off = 1;
    int64_t sizes[WEBM_MAX_LACES];

    if (lacing == 2) {                 /* fixed: equal split, must divide evenly */
        int64_t payload = blen - off;
        if (payload <= 0 || payload % nframes) return;
        for (int i = 0; i < nframes; ++i) sizes[i] = payload / nframes;
    } else if (lacing == 1) {          /* Xiph: 255-run sums, last = remainder */
        int64_t total = 0;
        for (int i = 0; i < nframes - 1; ++i) {
            int64_t s = 0;
            for (;;) {
                if (off >= blen) return;
                uint8_t v = body[off++];
                s += v;
                if (v != 255) break;
            }
            sizes[i] = s;
            total += s;
        }
        sizes[nframes - 1] = blen - off - total;
        if (sizes[nframes - 1] < 0) return;
    } else {                           /* EBML: first absolute, rest signed deltas */
        /* every size is bounded to [0, blen] the moment it's computed — raw
         * varints run to 2^56 and 254 chained deltas could otherwise reach
         * signed-overflow (UB) before any downstream check sees them; with
         * per-size bounds the totals stay far inside int64 */
        if (off >= blen) return;
        int l = vint_len(body[off]);
        if (l < 1 || l > 8 || off + l > blen) return;
        uint64_t v = body[off] & (0xFFu >> l);
        for (int i = 1; i < l; ++i) v = (v << 8) | body[off + i];
        off += l;
        if (v > (uint64_t)blen) return;
        sizes[0] = (int64_t)v;
        int64_t total = sizes[0];
        for (int i = 1; i < nframes - 1; ++i) {
            if (off >= blen) return;
            l = vint_len(body[off]);
            if (l < 1 || l > 8 || off + l > blen) return;
            uint64_t raw = body[off] & (0xFFu >> l);
            for (int k = 1; k < l; ++k) raw = (raw << 8) | body[off + k];
            off += l;
            /* signed vint: value - (2^(7*len-1) - 1) */
            int64_t delta = (int64_t)raw - ((1LL << (7 * l - 1)) - 1);
            if (delta > blen || delta < -blen) return;
            sizes[i] = sizes[i - 1] + delta;
            if (sizes[i] < 0 || sizes[i] > blen) return;
            total += sizes[i];
        }
        if (nframes > 1) {
            sizes[nframes - 1] = blen - off - total;
            if (sizes[nframes - 1] < 0) return;
        }
    }

    int64_t frame_pts = pts_us;
    for (int i = 0; i < nframes; ++i) {
        if (off + sizes[i] > blen) return;
        if (sizes[i] > 0) {
            if (is_video)
                w->sink->on_video_au(w->sink->user, body + off, (int)sizes[i], pts_us, pts_us,
                                     i == 0 ? key : 0);
            else {
                w->sink->on_audio_frame(w->sink->user, body + off, (int)sizes[i], frame_pts);
                /* each laced Opus packet carries its own samples: advance the
                 * timestamp so they don't all land on the block time (saturating,
                 * since a hostile cluster time could push frame_pts near INT64_MAX). */
                if (w->audio_codec == BASIS_CODEC_OPUS)
                    frame_pts = add_i64_sat(frame_pts,
                        (int64_t)opus_packet_samples(body + off, (int)sizes[i]) * 1000000LL / 48000);
            }
        }
        off += sizes[i];
    }
}

/* BlockGroup: whole-buffered (bounded by the block cap plus slack); the Block's
 * keyframe verdict is the absence of a ReferenceBlock sibling. */
static void parse_block_group(webm_t* w, const uint8_t* p, int64_t len) {
    ebuf_t b = { p, len, 0 };
    uint32_t id;
    int64_t sz;
    const uint8_t* block = NULL;
    int64_t block_len = 0;
    int has_ref = 0;
    while (ebuf_id(&b, &id) == 1) {
        if (!ebuf_size(&b, &sz) || sz < 0 || sz > b.len - b.off) return;
        if (id == ID_BLOCK && !block) { block = b.p + b.off; block_len = sz; }
        if (id == ID_REFERENCEBLOCK) has_ref = 1;
        b.off += sz;
    }
    if (block) emit_block(w, block, block_len, !has_ref);
}

/* ---- Cues acquisition / seek ---------------------------------------------- */

/* Trailing Cues (default ffmpeg mux): the start-of-Segment SeekHead gives the
 * offset; ranged-fetch the element now and come back. Only called when the
 * source can reposition. Failure to fetch degrades to cueless; failure to
 * come BACK is fatal (the stream position is gone). Returns 0 on the fatal
 * case only. */
static int fetch_trailing_cues(webm_t* w, int64_t resume_abs) {
    int64_t cues_abs = add_i64_sat(w->segment_base, w->seekhead_cues_rel);
    if (w->reseek(w->reseek_ctx, cues_abs) != 0) return 1; /* cueless */
    w->pos = cues_abs;

    uint32_t id;
    int64_t sz;
    /* a bad/unreadable Cues element just leaves ncues == 0 (cueless) */
    if (read_id(w, &id) == 1 && id == ID_CUES &&
        read_size(w, &sz) && sz >= 0 && sz <= WEBM_MAX_HEADER) {
        uint8_t* buf = read_element(w, sz, WEBM_MAX_HEADER);
        if (buf) {
            parse_cues(w, buf, sz);
            free(buf);
        }
    }

    if (w->reseek(w->reseek_ctx, resume_abs) != 0) {
        w->sink->on_error(w->sink->user, "WebM: source failed to reposition after index fetch");
        return 0;
    }
    w->pos = resume_abs;
    return 1;
}

/* Absolute seek (cluster granularity): greatest cue at or before the target,
 * reposition there, reset the cluster walk state. The cue'd cluster starts on
 * a keyframe (muxers cue keyframes; the SimpleBlock flag confirms). */
static int maybe_seek(webm_t* w) {
    if (!w->reseek || !w->sink->take_seek || w->ncues == 0 || !w->announced) return 0;
    int64_t target;
    if (!w->sink->take_seek(w->sink->user, &target)) return 0;

    uint32_t lo = 0, hi = w->ncues;
    while (hi - lo > 1) {              /* binary search: greatest cue time <= target */
        uint32_t mid = lo + (hi - lo) / 2;
        if (ticks_to_us(w->cues[mid].time_ticks, w->ts_scale_ns) <= target) lo = mid;
        else hi = mid;
    }

    if (w->reseek(w->reseek_ctx, w->cues[lo].abs_offset) != 0) return 0;
    w->pos = w->cues[lo].abs_offset;
    w->in_cluster = 0;
    w->cluster_ts = 0;
    return 1;
}

/* ---- announce -------------------------------------------------------------- */

/* Duration is published once, and only under the honest-seek-bar conditions
 * (Cues index + repositionable source, so duration > 0 always means seek
 * works). Header elements may legally arrive after the first cluster, so this
 * is re-checked whenever one lands — a late Info or in-stream Cues still
 * lights the seek UI. */
static void publish_duration(webm_t* w) {
    if (w->announced && !w->duration_sent && w->ncues > 0 && w->reseek &&
        w->duration_us > 0 && w->sink->on_duration) {
        w->sink->on_duration(w->sink->user, w->duration_us);
        w->duration_sent = 1;
    }
}

/* Headers are done (first Cluster reached): announce the selected track or
 * error out. Duration is reported only when the Cues index and a
 * repositionable source are both in hand — a reported duration always means
 * the seek bar works. */
static int announce(webm_t* w, int64_t first_cluster_abs) {
    /* An unsupported video track is a hard error even when a decodable audio
     * track is present: the file carries video the user expects to see, and
     * degrading to audio under a black screen is the failure the regression
     * guards against. Only a file with no video track at all (a genuine
     * audio-only Opus WebM, e.g. YouTube's audio legs) plays audio-only. */
    if (w->bad_codec[0] && !w->track_num) {
        char msg[96];
        snprintf(msg, sizeof(msg), "video codec '%s' is not supported (supported: V_VP9, V_AV1)",
                 w->bad_codec);
        w->sink->on_error(w->sink->user, msg);
        return 0;
    }
    if (!w->track_num && !w->audio_track_num) {
        w->sink->on_error(w->sink->user, "WebM has no track this player supports");
        return 0;
    }

    if (w->ncues == 0 && w->seekhead_cues_rel >= 0 && w->reseek) {
        if (!fetch_trailing_cues(w, first_cluster_abs)) return 0;
    }

    if (w->track_num)
        w->sink->on_video_format(w->sink->user, w->codec,
                                 w->extradata_len ? w->extradata : NULL, w->extradata_len,
                                 w->width, w->height);
    if (w->audio_track_num)
        w->sink->on_audio_format(w->sink->user, w->audio_codec, w->audio_rate, w->audio_channels,
                                 w->audio_extradata_len ? w->audio_extradata : NULL,
                                 w->audio_extradata_len);
    w->announced = 1;

    publish_duration(w);
    return 1;
}

/* ---- main walk ------------------------------------------------------------- */

static int is_segment_child(uint32_t id) {
    return id == ID_CLUSTER || id == ID_CUES || id == ID_SEEKHEAD || id == ID_INFO ||
           id == ID_TRACKS || id == ID_CHAPTERS || id == ID_TAGS || id == ID_ATTACHMENTS;
}

int basis_webm_run(basis_media_sink_t* sink, basis_read_fn read, void* ctx,
                   basis_reseek_fn reseek, void* reseek_ctx) {
    webm_t w;
    memset(&w, 0, sizeof(w));
    w.sink = sink;
    w.read = read;
    w.ctx = ctx;
    w.reseek = reseek;
    w.reseek_ctx = reseek_ctx;
    w.ts_scale_ns = 1000000;
    w.seekhead_cues_rel = -1;

    uint32_t id;
    int64_t sz;

    /* EBML header, then the Segment */
    if (read_id(&w, &id) != 1 || id != ID_EBML || !read_size(&w, &sz) ||
        sz < 0 || sz > 4096) {
        sink->on_error(sink->user, "not a WebM/Matroska stream");
        goto out;
    }
    {
        uint8_t* buf = read_element(&w, sz, 4096);
        int ok = buf && parse_ebml_header(buf, sz);
        free(buf);
        if (!ok) {
            sink->on_error(sink->user, "WebM: unsupported EBML header (DocType or ID/size limits)");
            goto out;
        }
    }
    if (read_id(&w, &id) != 1 || id != ID_SEGMENT || !read_size(&w, &sz)) {
        sink->on_error(sink->user, "WebM: missing Segment element");
        goto out;
    }
    w.segment_base = w.pos;   /* Cue/Seek positions are relative to this */

    for (;;) {
        if (!sink->is_running(sink->user)) break;

        if (maybe_seek(&w))
            continue;

        int64_t elem_start = w.pos;
        int r = read_id(&w, &id);
        if (r == 0) break;             /* clean EOF at an element boundary (or stop) */
        if (r < 0 || !read_size(&w, &sz)) {
            /* an engine stop mid-header is a clean exit, not a parse error */
            if (sink->is_running(sink->user))
                sink->on_error(sink->user, "malformed WebM (bad element header)");
            break;
        }

        /* only Segment (handled above) and Cluster may be unknown-size; an
         * unknown-size Cluster simply ends at the next Segment-child ID —
         * the ID-driven dispatch below makes that work with no bookkeeping */
        if (sz < 0 && id != ID_CLUSTER) {
            sink->on_error(sink->user, "malformed WebM (unknown-size element)");
            break;
        }

        if (is_segment_child(id)) w.in_cluster = (id == ID_CLUSTER);

        switch (id) {
            case ID_CLUSTER:
                if (!w.saw_tracks) {
                    sink->on_error(sink->user, "malformed WebM (media before track headers)");
                    goto out;
                }
                if (!w.announced && !announce(&w, elem_start))
                    goto out;
                /* the cluster's children stream through the walk below */
                break;

            case ID_INFO:
            case ID_TRACKS:
            case ID_SEEKHEAD:
            case ID_CUES: {
                uint8_t* buf = read_element(&w, sz, WEBM_MAX_HEADER);
                if (!buf) {
                    sink->on_error(sink->user, "malformed WebM (oversized or truncated header element)");
                    goto out;
                }
                if (id == ID_INFO) parse_info(&w, buf, sz);
                else if (id == ID_TRACKS) parse_tracks(&w, buf, sz);
                else if (id == ID_SEEKHEAD) parse_seekhead(&w, buf, sz);
                else if (w.ncues == 0) parse_cues(&w, buf, sz);
                /* first Cues index wins: the walk reaches a trailing Cues it
                 * already ranged-fetched at open, and appending it again would
                 * break the seek search's sorted-array assumption */
                free(buf);
                publish_duration(&w);  /* Info/Cues after the first cluster still count */
                break;
            }

            case ID_TIMESTAMP:
                if (w.in_cluster && sz >= 0 && sz <= 8) {
                    uint8_t tmp[8];
                    if (!wread_exact(&w, tmp, sz)) goto read_fail;
                    uint64_t v = ebml_uint(tmp, sz);
                    w.cluster_ts = v > (uint64_t)INT64_MAX ? INT64_MAX : (int64_t)v;
                } else if (!wskip(&w, sz)) {
                    goto read_fail;
                }
                break;

            case ID_SIMPLEBLOCK:
            case ID_BLOCKGROUP: {
                if (!w.in_cluster || sz > WEBM_MAX_BLOCK) {
                    if (!wskip(&w, sz)) goto read_fail;
                    break;
                }
                /* a block before the cluster's Timestamp element uses the last
                 * known cluster time rather than failing */
                if (sz > w.blockcap) {
                    free(w.blockbuf);
                    w.blockcap = sz + (sz >> 1) + 4096;
                    w.blockbuf = (uint8_t*)malloc((size_t)w.blockcap);
                    if (!w.blockbuf) { w.blockcap = 0; goto out; }
                }
                if (!wread_exact(&w, w.blockbuf, sz)) goto read_fail;
                if (id == ID_SIMPLEBLOCK) {
                    /* keyframe = flags bit 0x80 (flags sit after the track
                     * varint and the s16 timestamp) */
                    int tlen = sz >= 1 ? vint_len(w.blockbuf[0]) : 0;
                    int key = tlen >= 1 && (int64_t)tlen + 3 <= sz &&
                              (w.blockbuf[tlen + 2] & 0x80) != 0;
                    emit_block(&w, w.blockbuf, sz, key);
                } else {
                    parse_block_group(&w, w.blockbuf, sz);
                }
                break;
            }

            case ID_VOID:
            case ID_CRC32:
            default:
                /* anything else — including whole Chapters/Tags/Attachments and
                 * unknown cluster children (BlockDuration, DiscardPadding, …) —
                 * is skipped by size */
                if (!wskip(&w, sz)) goto read_fail;
                break;
        }
        continue;

    read_fail:
        /* mid-element EOF/stop: an engine stop is a clean exit; a truncated
         * element on a live-ish source just ends the stream */
        break;
    }

out:
    free(w.blockbuf);
    free(w.cues);
    return 0;
}
