/*
 * basis_mp4.c — MP4 demuxer -> H.264/H.265/VP9/AV1 + AAC. Handles both MP4 shapes:
 *
 *   fragmented (fMP4/CMAF): init segment (moov) then moof+mdat fragments —
 *     live streams, DASH/adaptive legs, HLS fMP4 segments. Each moof's
 *     tfhd/tfdt/trun tables slice the following mdat.
 *
 *   progressive (classic): one moov carrying the full sample tables
 *     (stts/ctts/stsc/stco/stsz/stss) and one big mdat — plain recorded .mp4
 *     files. Requires faststart layout (moov before mdat): the byte source
 *     only moves forward, so a trailing moov can't index media that has
 *     already streamed past — that shape gets a clear error instead.
 *
 * Both shapes parse moov for track config (codec, timescale, avcC/hvcC ->
 * Annex B extradata, av1C -> configOBUs, esds -> AAC ASC) and emit tracks
 * interleaved in decode
 * order (fragments) or the muxer's file order (progressive).
 *
 * Common-case assumptions (sufficient for VRCDN-style fMP4 and web-optimised
 * progressive files, flagged for iteration):
 *   - one video track + one audio track
 *   - one trun per traf, mdat samples contiguous in trun order
 *   - version 0/1 boxes; stsz sample sizes (stz2 raises a clear error)
 */

#include "basis_mp4.h"
#include "basis_bitstream.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>

#define MP4_MAX_FRAGS 4   /* per-moof trun runs (audio + video, with headroom) */
#define MP4_MAX_BOX   (256LL * 1024 * 1024)  /* cap for boxes buffered whole (moov/moof/fragment mdat) */
#define MP4_MAX_SAMPLE (64LL * 1024 * 1024)  /* per-sample cap: a single access unit read + Annex-B-converted
                                              * at once, so bound it well below MP4_MAX_BOX (a 256 MiB sample
                                              * would balloon to ~1 GiB of transient buffers before the pending
                                              * queue is even consulted). Generous — real keyframes are a few MiB. */
#define MP4_MAX_TABLE (16u * 1024 * 1024)    /* per-table entry cap (~hours of samples) */
#define MP4_MAX_FRAG_SAMPLES (1u << 20)      /* per-trun sample cap (a fragment is seconds of media) */

/* Classic (progressive) sample tables + walk cursor. Valid when sample_count
 * and chunk_count are non-zero; fMP4 init segments carry these boxes with zero
 * entries, which leaves the whole struct inert. */
typedef struct {
    uint32_t  sample_count;
    uint32_t  const_size;     /* stsz sample_size != 0 => every sample this size */
    uint32_t* sizes;          /* per-sample sizes when const_size == 0 */
    uint32_t  stts_count;     /* {sample_count, delta} run pairs */
    uint32_t* stts;
    uint32_t  ctts_count;     /* {sample_count, offset} run pairs; sign depends on ctts_version */
    uint32_t* ctts;
    int       ctts_version;   /* FullBox version: 0 = unsigned offsets, 1 = signed */
    int       has_stz2;       /* compact sample sizes present (unsupported) */
    uint32_t  stsc_count;     /* {first_chunk, samples_per_chunk} pairs */
    uint32_t* stsc;
    uint32_t  chunk_count;
    uint64_t* chunk_offsets;  /* absolute file offsets (stco or co64) */
    uint32_t  stss_count;     /* sync-sample numbers, 1-based; NULL => all sync */
    uint32_t* stss;

    /* edit-list mapping onto the movie timeline (from elst, resolved once the
     * movie timescale is known): presentation = media time - media_start,
     * shifted late by pts_delay_us of initial empty edits. */
    int64_t   media_start;    /* media-timescale ticks */
    int64_t   pts_delay_us;

    /* cursor: the next sample to emit */
    uint32_t next;            /* global sample index, 0-based */
    uint32_t chunk;           /* current chunk, 0-based */
    uint32_t chunk_sample;    /* sample index within the current chunk */
    uint64_t offset;          /* absolute file offset of the next sample */
    uint32_t stsc_i;
    uint32_t stts_i, stts_used;
    uint32_t ctts_i, ctts_used;
    uint32_t stss_i;
    int64_t  dts;             /* running, in track timescale */
} mp4_ctab_t;

typedef struct {
    int track_id;
    int is_video;
    basis_codec_t codec;
    int timescale;
    int width, height;        /* VisualSampleEntry fixed fields (codecs without
                               * in-band parameter sets announce these) */
    int vide_handler;         /* mdia/hdlr said 'vide' (for the unsupported-codec error) */
    uint32_t stsd_fourcc;     /* first unrecognised stsd entry type, 0 if none */
    uint8_t extradata[2048];
    int extradata_len;
    int nal_len_size;
    uint8_t asc[16];
    int asc_len;
    int sr, ch, obj;
    int announced;
    /* raw elst entries; durations are movie-timescale, media times media-timescale */
    uint32_t elst_count;
    struct { uint64_t duration; int64_t media_time; } elst[8];
    mp4_ctab_t ctab;
} mp4_track_t;

/* One trun run within a moof: its track, where its samples sit (data-offset,
 * relative to the moof start), and the per-sample table. A moof carries one of
 * these per track (audio + video), all slicing the single following mdat. */
typedef struct {
    int       track_id;
    int       data_offset;
    int64_t   base_dts;
    uint32_t  default_dur;
    uint32_t* sizes;
    uint32_t* durs;
    int64_t*  ctos;   /* composition offsets, widened: trun v0 unsigned, v1 signed */
    int       count;
    int       cap;
} mp4_frag_t;

typedef struct {
    int64_t start_us;
    int64_t end_us;
    uint64_t offset; /* absolute top-level box boundary for this subsegment */
} mp4_sidx_ref_t;

typedef struct {
    mp4_sidx_ref_t* refs;
    uint32_t count;
    uint32_t cap;
    int64_t duration_us;
} mp4_sidx_index_t;

typedef struct {
    basis_media_sink_t* sink;
    basis_read_fn read;
    void* ctx;
    basis_reseek_fn reseek; /* NULL: the byte source can't reposition */
    void* reseek_ctx;
    mp4_track_t tracks[2];
    int ntracks;
    int movie_timescale;    /* from mvhd; units of elst segment durations */
    int64_t movie_duration; /* from mvhd, movie-timescale ticks; 0 when absent */

    uint32_t unsupported_video_fourcc; /* stsd type of a dropped 'vide' track, 0 if none */
    int      saw_unsupported_video;

    int64_t pos;          /* absolute stream offset consumed so far */
    int     mdat_skipped; /* media data streamed past before any index arrived */
    int64_t mdat_seek_pos;/* first skipped mdat's payload start, -1 until one is skipped;
                             a range-capable source seeks back here for a trailing moov */

    /* runs from the last moof (one per traf/trun), consumed against its mdat */
    mp4_frag_t frags[MP4_MAX_FRAGS];
    int nfrags;
    mp4_sidx_index_t sidx;
} mp4_t;

static uint16_t rd16(const uint8_t* p) { return (uint16_t)(((uint16_t)p[0] << 8) | p[1]); }
static uint32_t rd32(const uint8_t* p) { return ((uint32_t)p[0]<<24)|((uint32_t)p[1]<<16)|((uint32_t)p[2]<<8)|p[3]; }
static uint64_t rd64(const uint8_t* p) { return ((uint64_t)rd32(p)<<32)|rd32(p+4); }

/* Saturating int64 add/subtract. Timestamps, durations and composition offsets
 * are all remote MP4 content; left raw, a hostile file can overflow the running
 * DTS or a box-end sum, and signed overflow is UB (so ordinary downstream bounds
 * checks wouldn't make those paths safe). Saturating keeps the arithmetic in
 * defined territory — a corrupt value reads as far-future/past and is gated or
 * dropped by the same checks that handle any out-of-range sample. */
static int64_t add_i64_sat(int64_t a, int64_t b) {
    if (b > 0 && a > INT64_MAX - b) return INT64_MAX;
    if (b < 0 && a < INT64_MIN - b) return INT64_MIN;
    return a + b;
}
static int64_t sub_i64_sat(int64_t a, int64_t b) {
    if (b < 0 && a > INT64_MAX + b) return INT64_MAX;  /* a - b with b<0 is a + |b| */
    if (b > 0 && a < INT64_MIN + b) return INT64_MIN;
    return a - b;
}

/* Timescale ticks -> microseconds, overflow-safe for any stream-supplied v and
 * timescale. Whole-seconds * 1000000 overflows int64 at a crafted small timescale
 * (a timescale of 1 makes seconds == v), so the multiply is range-checked and
 * saturates to the int64 range rather than invoking UB; the fractional term
 * (r < ts) stays comfortably in 64 bits. Negative v (pre-roll, edit shifts) maps
 * through by magnitude so the split stays well-defined. */
static int64_t ticks_to_us(int64_t v, int timescale) {
    uint64_t ts = timescale > 0 ? (uint64_t)timescale : 90000u;
    int neg = v < 0;
    uint64_t uv = neg ? (uint64_t)(-(v + 1)) + 1u : (uint64_t)v;
    uint64_t s = uv / ts, r = uv % ts;
    uint64_t us = s > (uint64_t)INT64_MAX / 1000000u
        ? (uint64_t)INT64_MAX
        : s * 1000000u + r * 1000000u / ts;
    if (us > (uint64_t)INT64_MAX) us = (uint64_t)INT64_MAX;
    return neg ? -(int64_t)us : (int64_t)us;
}

static int read_exact(mp4_t* m, uint8_t* buf, int n) {
    int got = 0;
    while (got < n) {
        if (!m->sink->is_running(m->sink->user)) break;
        int r = m->read(m->ctx, buf + got, n - got);
        if (r <= 0) break;
        got += r;
    }
    m->pos += got;
    return got;
}

static int skip_bytes(mp4_t* m, int64_t n) {
    uint8_t tmp[16384];
    while (n > 0) {
        if (!m->sink->is_running(m->sink->user)) return -1;
        int want = n > (int64_t)sizeof(tmp) ? (int)sizeof(tmp) : (int)n;
        int r = m->read(m->ctx, tmp, want);
        if (r <= 0) return -1;
        m->pos += r;
        n -= r;
    }
    return 0;
}

/* Reads a top-level box header. *body_len is the payload length, or -1 for a
 * size-0 box (extends to end of stream). The caller decides whether to buffer,
 * stream, or skip the payload. */
static int read_box_header(mp4_t* m, uint32_t* type, int64_t* body_len) {
    uint8_t hdr[8];
    if (read_exact(m, hdr, 8) != 8) return -1;
    int64_t size = rd32(hdr);
    *type = rd32(hdr + 4);
    int header = 8;
    if (size == 1) {
        uint8_t ext[8];
        if (read_exact(m, ext, 8) != 8) return -1;
        size = (int64_t)rd64(ext);
        header = 16;
    }
    if (size == 0) { *body_len = -1; return 0; }
    if (size < header) return -1;
    *body_len = size - header;
    return 0;
}

static mp4_track_t* track_by_id(mp4_t* m, int id) {
    for (int i = 0; i < m->ntracks; ++i) if (m->tracks[i].track_id == id) return &m->tracks[i];
    return NULL;
}

static void sidx_clear(mp4_sidx_index_t* idx) {
    free(idx->refs);
    memset(idx, 0, sizeof(*idx));
}

static int sidx_reserve(mp4_sidx_index_t* idx, uint32_t n) {
    if (idx->cap >= n) return 1;
    uint32_t cap = idx->cap ? idx->cap : 32;
    while (cap < n) cap *= 2;
    mp4_sidx_ref_t* refs = (mp4_sidx_ref_t*)realloc(idx->refs, (size_t)cap * sizeof(*refs));
    if (!refs) return 0;
    idx->refs = refs;
    idx->cap = cap;
    return 1;
}

static int sidx_add_ref(mp4_sidx_index_t* idx, const mp4_sidx_ref_t* ref) {
    if (!sidx_reserve(idx, idx->count + 1)) return 0;
    idx->refs[idx->count++] = *ref;
    if (ref->end_us > idx->duration_us) idx->duration_us = ref->end_us;
    return 1;
}

static const mp4_sidx_ref_t* sidx_find_ref(const mp4_sidx_index_t* idx, int64_t target_us) {
    if (!idx || idx->count == 0) return NULL;
    for (uint32_t i = 0; i < idx->count; ++i) {
        const mp4_sidx_ref_t* r = &idx->refs[i];
        if (target_us >= r->start_us && target_us < r->end_us) return r;
    }
    if (target_us <= idx->refs[0].start_us) return &idx->refs[0];
    return &idx->refs[idx->count - 1];
}

/* ---- moov parsing ------------------------------------------------------- */

/* Reads an MP4 expandable descriptor length (7 bits/byte, high bit continues,
 * max 4 bytes) at *j, advancing *j past it, into *out. Returns 1 on a length
 * that terminates within 4 bytes and within el; 0 if the payload runs out
 * (truncated) or a fourth byte still has its continuation bit set (malformed). */
static int esds_desc_len(const uint8_t* p, int el, int* j, uint32_t* out) {
    uint32_t v = 0;
    for (int n = 0; n < 4; ++n) {
        if (*j >= el) return 0;                       /* truncated */
        int b = p[(*j)++];
        v = (v << 7) | (uint32_t)(b & 0x7F);
        if (!(b & 0x80)) { *out = v; return 1; }      /* terminating byte */
    }
    return 0;                                          /* unterminated after 4 bytes */
}

/* Reads nb bits (nb <= 24, MSB-first) from buf at *pos, advancing *pos. Returns
 * the value, or -1 and sets *pos < 0 if the read runs past the buffer. */
static int asc_read_bits(const uint8_t* buf, int len, int* pos, int nb) {
    if (*pos < 0) return -1;
    int v = 0;
    for (int i = 0; i < nb; ++i) {
        int b = *pos + i;
        if (b >= len * 8) { *pos = -1; return -1; }
        v = (v << 1) | ((buf[b >> 3] >> (7 - (b & 7))) & 1);
    }
    *pos += nb;
    return v;
}

/* Extracts the AudioSpecificConfig from an esds payload (after the box's 4-byte
 * version/flags) by walking the descriptor tree: ES_Descriptor (0x03) ->
 * DecoderConfigDescriptor (0x04) -> DecoderSpecificInfo (0x05). Returns the ASC
 * length (>=2) or 0. A flat scan for the 0x05 tag byte false-matches a 0x05
 * inside the DecoderConfig's bufferSizeDB/bitrate fields (e.g. maxBitrate
 * 0x000534xx), reads a bogus length and drops the config — so the tree must be
 * walked by tag+length. */
static int esds_extract_asc(const uint8_t* ep, int el, uint8_t* asc, int cap) {
    int j = 0;
    uint32_t len;
    if (j >= el || ep[j++] != 0x03) return 0;                 /* ES_Descriptor */
    if (!esds_desc_len(ep, el, &j, &len) || len > (uint32_t)(el - j)) return 0;
    int es_end = j + (int)len;                                /* the ES descriptor bounds its children */
    if (j + 3 > es_end) return 0;
    int flags = ep[j + 2]; j += 3;                            /* ES_ID(2) + flags(1) */
    if (flags & 0x80) j += 2;                                 /* streamDependenceFlag */
    if (flags & 0x40) { if (j >= es_end) return 0; j += 1 + ep[j]; } /* URL_flag */
    if (flags & 0x20) j += 2;                                 /* OCRstreamFlag */
    if (j >= es_end || ep[j++] != 0x04) return 0;             /* DecoderConfigDescriptor */
    if (!esds_desc_len(ep, es_end, &j, &len) || len < 13 || len > (uint32_t)(es_end - j)) return 0;
    int dec_end = j + (int)len;                               /* DecoderConfig bounds the ASC */
    j += 13;                                                  /* objType+streamType+bufSize(3)+maxBR(4)+avgBR(4) */
    if (j >= dec_end || ep[j++] != 0x05) return 0;            /* DecoderSpecificInfo */
    uint32_t dlen;
    if (!esds_desc_len(ep, dec_end, &j, &dlen)) return 0;
    if (dlen < 2 || (int)dlen > cap || (int)dlen > dec_end - j) return 0;
    memcpy(asc, ep + j, dlen);
    return (int)dlen;
}

/* Returns the esds objectTypeIndication (0x40 = AAC, 0x6B = MPEG-1 audio / MP3,
 * 0x69 = MPEG-2 audio / MP3), or 0 if the descriptor tree can't be walked. Same
 * walk as esds_extract_asc, stopping at the DecoderConfig's first byte. */
static int esds_object_type(const uint8_t* ep, int el) {
    int j = 0;
    uint32_t len;
    if (j >= el || ep[j++] != 0x03) return 0;                 /* ES_Descriptor */
    if (!esds_desc_len(ep, el, &j, &len) || len > (uint32_t)(el - j)) return 0;
    int es_end = j + (int)len;
    if (j + 3 > es_end) return 0;
    int flags = ep[j + 2]; j += 3;
    if (flags & 0x80) j += 2;
    if (flags & 0x40) { if (j >= es_end) return 0; j += 1 + ep[j]; }
    if (flags & 0x20) j += 2;
    if (j >= es_end || ep[j++] != 0x04) return 0;             /* DecoderConfigDescriptor */
    if (!esds_desc_len(ep, es_end, &j, &len) || len < 1 || len > (uint32_t)(es_end - j)) return 0;
    return ep[j];                                             /* objectTypeIndication */
}

static void parse_stsd(mp4_track_t* t, const uint8_t* p, int len) {
    /* stsd: version/flags(4) entry_count(4) then sample entries */
    if (len < 8) return;
    int n = (int)rd32(p + 4);
    int off = 8;
    for (int e = 0; e < n && off + 8 <= len; ++e) {
        int esize = (int)rd32(p + off);
        uint32_t etype = rd32(p + off + 4);
        const uint8_t* ent = p + off;
        if (esize < 8 || esize > len - off) break;

        if (etype == 0x61766331 /*avc1*/ || etype == 0x68766331 /*hvc1*/ || etype == 0x68657631 /*hev1*/) {
            t->is_video = 1;
            t->codec = (etype == 0x61766331) ? BASIS_CODEC_H264 : BASIS_CODEC_H265;
            /* VisualSampleEntry fixed fields. H.264 refines these from the SPS at
             * announce; H.265 has no SPS reader, so without this it announces 0x0. */
            if (esize >= 36) {
                t->width = rd16(ent + 32);
                t->height = rd16(ent + 34);
            }
            /* visual sample entry header is 78 bytes, then child boxes (avcC/hvcC) */
            int co = 8 + 78;
            while (co + 8 <= esize) {
                int csz = (int)rd32(ent + co);
                uint32_t ct = rd32(ent + co + 4);
                if (csz < 8 || csz > esize - co) break;
                if (ct == 0x61766343 /*avcC*/ || ct == 0x68766343 /*hvcC*/) {
                    int nls = 4;
                    int got = basis_avcc_extradata_to_annexb(ent + co + 8, csz - 8,
                                t->codec == BASIS_CODEC_H265, t->extradata, sizeof(t->extradata), &nls);
                    if (got > 0) { t->extradata_len = got; t->nal_len_size = nls; }
                }
                co += csz;
            }
        } else if (etype == 0x76703039 /*vp09*/) {
            t->is_video = 1;
            t->codec = BASIS_CODEC_VP9;
            /* VisualSampleEntry fixed fields; vpcC carries nothing the decoder
             * needs at init (VP9 has no out-of-band config), so no extradata. */
            if (esize >= 36) {
                t->width = rd16(ent + 32);
                t->height = rd16(ent + 34);
            }
        } else if (etype == 0x61763031 /*av01*/) {
            t->is_video = 1;
            t->codec = BASIS_CODEC_AV1;
            if (esize >= 36) {
                t->width = rd16(ent + 32);
                t->height = rd16(ent + 34);
            }
            /* av1C: 4 record-header bytes (marker/version, profile/level packing,
             * flags/presentation-delay), then configOBUs. Store only the
             * configOBUs — the header bytes are not valid OBU syntax and the
             * decode backends want a feedable OBU blob. */
            int co = 8 + 78;
            while (co + 8 <= esize) {
                int csz = (int)rd32(ent + co);
                uint32_t ct = rd32(ent + co + 4);
                if (csz < 8 || csz > esize - co) break;
                if (ct == 0x61763143 /*av1C*/ && csz > 12 &&
                    csz - 12 <= (int)sizeof(t->extradata)) {
                    memcpy(t->extradata, ent + co + 12, (size_t)(csz - 12));
                    t->extradata_len = csz - 12;
                }
                co += csz;
            }
        } else if (etype == 0x6d703461 /*mp4a*/ && esize >= 8 + 28) {
            t->is_video = 0;
            t->codec = BASIS_CODEC_AAC;
            /* audio sample entry header 28 bytes, then esds */
            t->ch = (ent[8 + 16] << 8) | ent[8 + 17];
            t->sr = (int)(rd32(ent + 8 + 24) >> 16);
            int co = 8 + 28;
            while (co + 8 <= esize) {
                int csz = (int)rd32(ent + co);
                uint32_t ct = rd32(ent + co + 4);
                if (csz < 8 || csz > esize - co) break;
                if (ct == 0x65736473 /*esds*/ && csz >= 12) {
                    /* esds payload after the box's 4-byte version/flags */
                    const uint8_t* ep = ent + co + 12; int el = csz - 12;
                    int oti = esds_object_type(ep, el);
                    if (oti == 0x6B /*MPEG-1 audio*/ || oti == 0x69 /*MPEG-2 audio*/) {
                        /* MP3 in MP4: no AudioSpecificConfig; the sr/ch from the
                         * sample entry stand, and the decoder reads frame headers.
                         * The OTI names MPEG-1/2 audio without a layer, but MP4 only
                         * carries Layer III here in practice; a non-III stream would
                         * be handled by the platform MPEG-audio decoder or, on the
                         * Layer-III-only Windows path, rejected (muted, video intact). */
                        t->codec = BASIS_CODEC_MP3;
                        co += csz;
                        continue;
                    }
                    int n = esds_extract_asc(ep, el, t->asc, (int)sizeof(t->asc));
                    if (n >= 2) {
                        /* Walk the ASC bit fields so the escape forms parse
                         * correctly: audioObjectType 31 carries 6 extra bits
                         * (AOT = 32 + value), and samplingFrequencyIndex 15 a
                         * 24-bit explicit rate, both of which shift the
                         * channelConfiguration that follows. */
                        int bit = 0;
                        int aot = asc_read_bits(t->asc, n, &bit, 5);
                        if (aot == 31) aot = 32 + asc_read_bits(t->asc, n, &bit, 6);
                        int sri = asc_read_bits(t->asc, n, &bit, 4);
                        int sr = sri == 15 ? asc_read_bits(t->asc, n, &bit, 24)
                                           : basis_aac_sample_rate_from_index(sri);
                        int chcfg = asc_read_bits(t->asc, n, &bit, 4);
                        if (bit >= 0) {   /* every field fit inside the ASC */
                            t->asc_len = n;   /* only publish a fully-parsed config */
                            t->obj = aot;
                            if (!t->sr && sr > 0) t->sr = sr;
                            if (!t->ch) t->ch = basis_aac_channels_from_config(chcfg);
                        }
                    }
                }
                co += csz;
            }
        } else if (!t->stsd_fourcc) {
            t->stsd_fourcc = etype;   /* names the codec in the unsupported error */
        }
        off += esize;
    }
}

/* Reads a fixed-stride sample-table box into a u32 array, keeping `keep` of
 * the `stride` u32 fields per entry. Returns NULL (count 0) on any bound or
 * allocation failure — an incomplete table just leaves the file unplayable as
 * progressive rather than risking a bad walk. */
static uint32_t* parse_table(const uint8_t* p, int len, int stride, int keep, uint32_t* out_count) {
    *out_count = 0;
    if (len < 8) return NULL;
    uint32_t n = rd32(p + 4);
    if (!n || n > MP4_MAX_TABLE) return NULL;
    if ((int64_t)8 + (int64_t)n * stride * 4 > len) return NULL;
    uint32_t* v = (uint32_t*)malloc((size_t)n * keep * sizeof(uint32_t));
    if (!v) return NULL;
    for (uint32_t i = 0; i < n; ++i)
        for (int k = 0; k < keep; ++k)
            v[i * keep + k] = rd32(p + 8 + (int64_t)i * stride * 4 + k * 4);
    *out_count = n;
    return v;
}

static void parse_stsz(mp4_ctab_t* c, const uint8_t* p, int len) {
    if (len < 12) return;
    uint32_t cs = rd32(p + 4);
    uint32_t n = rd32(p + 8);
    if (!n || n > MP4_MAX_TABLE) return;
    if (cs) { c->const_size = cs; c->sample_count = n; return; }
    if ((int64_t)12 + (int64_t)n * 4 > len) return;
    c->sizes = (uint32_t*)malloc((size_t)n * sizeof(uint32_t));
    if (!c->sizes) return;
    for (uint32_t i = 0; i < n; ++i) c->sizes[i] = rd32(p + 12 + (int64_t)i * 4);
    c->sample_count = n;
}

static void parse_elst(mp4_track_t* t, const uint8_t* p, int len) {
    if (t->elst_count || len < 8) return;
    int ver = p[0];
    uint32_t n = rd32(p + 4);
    int stride = ver == 1 ? 20 : 12;
    if (!n || (int64_t)8 + (int64_t)n * stride > len) return;
    uint32_t keep = n < 8 ? n : 8;  /* only the leading edits shape the walk */
    for (uint32_t i = 0; i < keep; ++i) {
        const uint8_t* e = p + 8 + (size_t)i * stride;
        if (ver == 1) { t->elst[i].duration = rd64(e); t->elst[i].media_time = (int64_t)rd64(e + 8); }
        else          { t->elst[i].duration = rd32(e); t->elst[i].media_time = (int32_t)rd32(e + 4); }
    }
    t->elst_count = keep;
}

static void parse_chunk_offsets(mp4_ctab_t* c, const uint8_t* p, int len, int is64) {
    if (len < 8) return;
    uint32_t n = rd32(p + 4);
    int stride = is64 ? 8 : 4;
    if (!n || n > MP4_MAX_TABLE) return;
    if ((int64_t)8 + (int64_t)n * stride > len) return;
    c->chunk_offsets = (uint64_t*)malloc((size_t)n * sizeof(uint64_t));
    if (!c->chunk_offsets) return;
    for (uint32_t i = 0; i < n; ++i)
        c->chunk_offsets[i] = is64 ? rd64(p + 8 + (int64_t)i * 8) : (uint64_t)rd32(p + 8 + (int64_t)i * 4);
    c->chunk_count = n;
}

/* Container-box nesting cap. Real files nest a handful deep (moov>trak>mdia>
 * minf>stbl); a crafted file can nest thousands deep with 8-byte headers to
 * exhaust the demux-thread stack, so bail well before that. */
#define MP4_MAX_BOX_DEPTH 32

static void parse_box_tree(mp4_t* m, mp4_track_t* t, const uint8_t* p, int len, int depth);

/* Tracks are selected by role — the first supported video track and the first
 * supported audio track, wherever they sit in the moov. A fixed first-two-traks
 * cut would leave a valid file ordered audio,audio,video silently video-less. */
static void parse_trak(mp4_t* m, const uint8_t* p, int len, int depth) {
    mp4_track_t t;
    memset(&t, 0, sizeof(t));
    t.nal_len_size = 4;
    t.timescale = 90000;
    parse_box_tree(m, &t, p, len, depth);

    int have_video = 0, have_audio = 0;
    for (int i = 0; i < m->ntracks; ++i) {
        if (m->tracks[i].is_video) have_video = 1;
        else have_audio = 1;
    }
    if (t.codec != BASIS_CODEC_NONE && (t.is_video ? !have_video : !have_audio)) {
        m->tracks[m->ntracks++] = t;
        return;
    }

    /* a dropped unsupported video-handler track is remembered so the post-moov
     * check can fail loudly instead of playing silently without video */
    if (t.vide_handler && t.codec == BASIS_CODEC_NONE && !m->saw_unsupported_video) {
        m->saw_unsupported_video = 1;
        m->unsupported_video_fourcc = t.stsd_fourcc;
    }
    free(t.ctab.sizes); free(t.ctab.stts); free(t.ctab.ctts);
    free(t.ctab.stsc); free(t.ctab.chunk_offsets); free(t.ctab.stss);
}

static void parse_box_tree(mp4_t* m, mp4_track_t* t, const uint8_t* p, int len, int depth) {
    if (depth > MP4_MAX_BOX_DEPTH) return;
    int off = 0;
    while (off + 8 <= len) {
        int sz = (int)rd32(p + off);
        uint32_t ty = rd32(p + off + 4);
        if (sz < 8 || sz > len - off) break;
        const uint8_t* body = p + off + 8;
        int blen = sz - 8;
        switch (ty) {
            case 0x7472616b: parse_trak(m, body, blen, depth + 1); break;      /* trak */
            case 0x6d646961: /* mdia */
            case 0x6d696e66: /* minf */
            case 0x65647473: /* edts */
            case 0x7374626c: parse_box_tree(m, t, body, blen, depth + 1); break; /* stbl */
            case 0x6d766864: /* mvhd: movie timescale (elst duration units) + duration */
                if (blen >= 4) {
                    int ver = body[0];
                    int tsoff = ver == 1 ? 4 + 8 + 8 : 4 + 4 + 4;
                    if (tsoff + 4 <= blen) m->movie_timescale = (int)rd32(body + tsoff);
                    if (ver == 1) { if (tsoff + 4 + 8 <= blen) { uint64_t d = rd64(body + tsoff + 4);
                                    /* a duration past int64 range is corrupt — treat as unknown, not negative */
                                    m->movie_duration = d > (uint64_t)INT64_MAX ? 0 : (int64_t)d; } }
                    else          { uint32_t d = tsoff + 4 + 4 <= blen ? rd32(body + tsoff + 4) : 0;
                                    if (d != 0xFFFFFFFFu) m->movie_duration = d; } /* all-ones = unknown */
                }
                break;
            case 0x656c7374: if (t) parse_elst(t, body, blen); break;    /* elst */
            case 0x68646c72: /* hdlr: version/flags(4) pre_defined(4) handler_type(4) */
                if (t && blen >= 12 && rd32(body + 8) == 0x76696465 /*vide*/) t->vide_handler = 1;
                break;
            case 0x6d646864: /* mdhd: version(1) flags(3) ... timescale */
                if (t && blen >= 4) { int ver = body[0]; int tsoff = ver == 1 ? 4 + 8 + 8 : 4 + 4 + 4; if (tsoff + 4 <= blen) t->timescale = (int)rd32(body + tsoff); }
                break;
            case 0x746b6864: /* tkhd: track id */
                if (t && blen >= 4) { int ver = body[0]; int idoff = ver == 1 ? 4 + 8 + 8 : 4 + 4 + 4; if (idoff + 4 <= blen) t->track_id = (int)rd32(body + idoff); }
                break;
            case 0x73747364: if (t) parse_stsd(t, body, blen); break;    /* stsd */
            /* classic sample tables (progressive MP4); zero-entry versions in
             * fMP4 init segments parse to nothing and stay inert. First box of
             * each kind wins — a duplicate in a malformed stbl would otherwise
             * overwrite (and leak) the earlier allocation. */
            case 0x73747473: if (t && !t->ctab.stts) t->ctab.stts = parse_table(body, blen, 2, 2, &t->ctab.stts_count); break;  /* stts */
            case 0x63747473: /* ctts: version 0 offsets are unsigned, version 1 signed */
                if (t && !t->ctab.ctts) {
                    t->ctab.ctts = parse_table(body, blen, 2, 2, &t->ctab.ctts_count);
                    t->ctab.ctts_version = blen >= 1 ? body[0] : 0;
                }
                break;
            case 0x73747363: if (t && !t->ctab.stsc) t->ctab.stsc = parse_table(body, blen, 3, 2, &t->ctab.stsc_count); break;  /* stsc */
            case 0x73747373: if (t && !t->ctab.stss) t->ctab.stss = parse_table(body, blen, 1, 1, &t->ctab.stss_count); break;  /* stss */
            case 0x7374737a: if (t && !t->ctab.sample_count) parse_stsz(&t->ctab, body, blen); break;                           /* stsz */
            case 0x73747a32: if (t) t->ctab.has_stz2 = 1; break;         /* stz2: compact sizes, unsupported */
            case 0x7374636f: if (t && !t->ctab.chunk_offsets) parse_chunk_offsets(&t->ctab, body, blen, 0); break;              /* stco */
            case 0x636f3634: if (t && !t->ctab.chunk_offsets) parse_chunk_offsets(&t->ctab, body, blen, 1); break;              /* co64 */
            default: break;
        }
        off += sz;
    }
}

static void announce_tracks(mp4_t* m) {
    for (int i = 0; i < m->ntracks; ++i) {
        mp4_track_t* t = &m->tracks[i];
        if (t->announced) continue;
        if (t->is_video) {
            int w = t->width, h = t->height;
            if (t->codec == BASIS_CODEC_H264 && t->extradata_len) {
                int pos=0,no,nl;
                while ((pos=basis_annexb_next(t->extradata,t->extradata_len,pos,&no,&nl))>=0)
                    if (nl>0 && basis_h264_nal_type(t->extradata[no])==7){ basis_h264_sps_dimensions(t->extradata+no,nl,&w,&h); break; }
            }
            m->sink->on_video_format(m->sink->user, t->codec, t->extradata, t->extradata_len, w, h);
        } else {
            m->sink->on_audio_format(m->sink->user, t->codec ? t->codec : BASIS_CODEC_AAC,
                                     t->sr ? t->sr : 48000, t->ch ? t->ch : 2,
                                     t->asc_len ? t->asc : NULL, t->asc_len);
        }
        t->announced = 1;
    }
}

/* ---- sidx (direct fMP4 VOD seek index) ---------------------------------- */

static void parse_sidx(mp4_t* m, const uint8_t* p, int len, int64_t box_end) {
    if (!m || !p || len < 24 || box_end < 0) return;
    int version = p[0];
    if (version > 1) return;

    uint32_t timescale = rd32(p + 8);
    if (timescale == 0) return;

    int off = 12;
    uint64_t earliest;
    uint64_t first_offset;
    if (version == 0) {
        if (len < off + 8) return;
        earliest = rd32(p + off);
        first_offset = rd32(p + off + 4);
        off += 8;
    } else {
        if (len < off + 16) return;
        earliest = rd64(p + off);
        first_offset = rd64(p + off + 8);
        off += 16;
    }

    if (off > len - 4) return;
    uint16_t ref_count = rd16(p + off + 2); /* reserved(16), reference_count(16) */
    off += 4;
    if ((size_t)ref_count > (size_t)(len - off) / 12) return;

    mp4_sidx_index_t next;
    memset(&next, 0, sizeof(next));
    uint64_t box_end_u = (uint64_t)box_end;
    if (first_offset > UINT64_MAX - box_end_u) return;
    uint64_t ref_offset = box_end_u + first_offset;
    uint64_t pts = earliest;

    for (uint16_t i = 0; i < ref_count; ++i, off += 12) {
        uint32_t ref = rd32(p + off);
        uint32_t size = ref & 0x7FFFFFFFU;
        uint32_t dur = rd32(p + off + 4);
        uint32_t sap = rd32(p + off + 8);
        int ref_type = (ref & 0x80000000U) != 0;
        int starts_with_sap = (sap & 0x80000000U) != 0;
        int sap_type = (sap & 0x70000000U) >> 28;
        uint32_t sap_delta = sap & 0x0FFFFFFFU;

        /* This path supports the direct integrated fMP4 shape where sidx
         * references complete moof+mdat subsegments that begin on a SAP type-1, 2
         * or unspecified(0). Nested sidx references are deliberately not exposed
         * as seekable yet. */
        if (ref_type || !starts_with_sap || sap_type > 2 || sap_delta != 0 || size == 0 || dur == 0 ||
            ref_offset > (uint64_t)INT64_MAX || pts > (uint64_t)INT64_MAX ||
            dur > (uint64_t)INT64_MAX - pts) {
            sidx_clear(&next);
            return;
        }

        mp4_sidx_ref_t r;
        r.start_us = ticks_to_us((int64_t)pts, (int)timescale);
        r.end_us = ticks_to_us((int64_t)(pts + dur), (int)timescale);
        r.offset = ref_offset;
        if (r.end_us <= r.start_us || !sidx_add_ref(&next, &r)) {
            sidx_clear(&next);
            return;
        }

        if (ref_offset > UINT64_MAX - size) {
            sidx_clear(&next);
            return;
        }
        ref_offset += size;
        pts += dur;
    }

    if (next.count == 0 || next.duration_us <= 0) {
        sidx_clear(&next);
        return;
    }

    sidx_clear(&m->sidx);
    m->sidx = next;
    if (m->reseek && m->sink->on_duration) {
        m->sink->on_duration(m->sink->user, m->sidx.duration_us);
    }
}

static int maybe_seek_fragment(mp4_t* m) {
    if (!m->reseek || !m->sink->take_seek || m->sidx.count == 0) return 0;

    int64_t seek_us;
    if (!m->sink->take_seek(m->sink->user, &seek_us)) return 0;
    const mp4_sidx_ref_t* ref = sidx_find_ref(&m->sidx, seek_us);
    if (!ref) return 0;
    if (ref->offset > (uint64_t)INT64_MAX) return 0;

    if (m->reseek(m->reseek_ctx, (int64_t)ref->offset) != 0) return 0;
    m->pos = (int64_t)ref->offset;
    m->nfrags = 0;
    m->mdat_skipped = 0;
    return 1;
}

/* ---- classic (progressive) sample walk ---------------------------------- */

/* A track's tables are walkable when every table the walk depends on parsed
 * completely. stss/ctts are optional. */
static int classic_track_ready(const mp4_ctab_t* c) {
    return c->sample_count && c->chunk_count && c->stts_count && c->stsc_count &&
           (c->const_size || c->sizes);
}

static int classic_ready(mp4_t* m) {
    for (int i = 0; i < m->ntracks; ++i)
        if (classic_track_ready(&m->tracks[i].ctab)) return 1;
    return 0;
}

/* Longest track's stts total — the fallback when mvhd carries no duration. */
static int64_t classic_total_duration_us(mp4_t* m) {
    int64_t best = 0;
    for (int i = 0; i < m->ntracks; ++i) {
        const mp4_ctab_t* c = &m->tracks[i].ctab;
        if (!classic_track_ready(c)) continue;
        /* sample_count * delta is two u32 fields — their product alone can top
         * int64, so accumulate unsigned and clamp before the conversion. */
        uint64_t ticks = 0;
        for (uint32_t k = 0; k < c->stts_count; ++k)
            ticks += (uint64_t)c->stts[k * 2] * c->stts[k * 2 + 1];
        int64_t us = ticks_to_us(ticks > (uint64_t)INT64_MAX ? INT64_MAX : (int64_t)ticks,
                                 m->tracks[i].timescale);
        if (us > best) best = us;
    }
    return best;
}

/* Resolve a track's edit list against the movie timescale: initial empty edits
 * (media_time -1) delay the whole track; the first normal edit's media_time is
 * the media-time origin (encoder priming / initial trim). Later edit segments
 * would need a segment-aware walk and are ignored. Samples ahead of the origin
 * keep their (negative) presentation time rather than being dropped — video
 * there can still be reference data the decoder needs. */
static void classic_apply_elst(mp4_t* m, mp4_track_t* t) {
    mp4_ctab_t* c = &t->ctab;
    for (uint32_t i = 0; i < t->elst_count; ++i) {
        if (t->elst[i].media_time < 0) {
            c->pts_delay_us = add_i64_sat(c->pts_delay_us,
                ticks_to_us((int64_t)t->elst[i].duration, m->movie_timescale > 0 ? m->movie_timescale : 1000));
            continue;
        }
        c->media_start = t->elst[i].media_time;
        break;
    }
}

static void classic_init_cursor(mp4_ctab_t* c) {
    if (!classic_track_ready(c)) { c->sample_count = 0; return; }
    c->next = 0;
    c->chunk = 0;
    c->chunk_sample = 0;
    c->offset = c->chunk_offsets[0];
    c->stsc_i = 0;
    c->stts_i = 0; c->stts_used = 0;
    c->ctts_i = 0; c->ctts_used = 0;
    c->stss_i = 0;
    c->dts = 0;
}

static uint32_t classic_size(const mp4_ctab_t* c, uint32_t i) {
    return c->const_size ? c->const_size : c->sizes[i];
}

static void classic_advance(mp4_ctab_t* c) {
    uint32_t size = classic_size(c, c->next);
    c->dts = add_i64_sat(c->dts, (int64_t)c->stts[c->stts_i * 2 + 1]);
    if (++c->stts_used >= c->stts[c->stts_i * 2] && c->stts_i + 1 < c->stts_count) {
        c->stts_i++; c->stts_used = 0;
    }
    if (c->ctts && ++c->ctts_used >= c->ctts[c->ctts_i * 2] && c->ctts_i + 1 < c->ctts_count) {
        c->ctts_i++; c->ctts_used = 0;
    }
    c->next++;
    c->chunk_sample++;
    if (c->chunk_sample >= c->stsc[c->stsc_i * 2 + 1]) {  /* samples-per-chunk of the current run */
        c->chunk++;
        c->chunk_sample = 0;
        while (c->stsc_i + 1 < c->stsc_count && c->chunk + 1 >= c->stsc[(c->stsc_i + 1) * 2])
            c->stsc_i++;
        if (c->chunk < c->chunk_count) c->offset = c->chunk_offsets[c->chunk];
    } else {
        c->offset += size;
    }
}

/* A sample read from the file but not yet handed to the sink: file order can
 * run ahead of delivery order when the muxer interleaves chunks coarsely.
 * Video is stored already converted to Annex B with its keyframe verdict. */
typedef struct mp4_pend {
    struct mp4_pend* next;
    int64_t pts_us;
    int64_t dts_us;
    int     key;
    int     size;
    uint8_t data[];
} mp4_pend_t;

#define MP4_MAX_PENDING (32LL * 1024 * 1024)  /* parked-bytes bound; past it deliver anyway */

/* Decode time of the cursor's next sample, mapped like emission. */
static int64_t classic_sample_dts_us(const mp4_track_t* t) {
    const mp4_ctab_t* c = &t->ctab;
    return add_i64_sat(ticks_to_us(sub_i64_sat(c->dts, c->media_start), t->timescale), c->pts_delay_us);
}

/* Next undelivered decode time for a track: the parked front, else the table
 * cursor's sample while it still points inside this mdat. */
static int64_t classic_next_due(const mp4_track_t* t, const mp4_pend_t* head, int64_t mdat_end) {
    if (head) return head->dts_us;
    const mp4_ctab_t* c = &t->ctab;
    if (c->next >= c->sample_count || (int64_t)c->offset >= mdat_end) return INT64_MAX;
    return classic_sample_dts_us(t);
}

static void emit_pend(mp4_t* m, const mp4_track_t* t, const mp4_pend_t* s) {
    if (t->is_video) m->sink->on_video_au(m->sink->user, s->data, s->size, s->pts_us, s->dts_us, s->key);
    else m->sink->on_audio_frame(m->sink->user, s->data, s->size, s->pts_us);
}

/* Repositions a track cursor to the sample at target_us — for video, the
 * preceding sync sample so decode restarts on a keyframe. Replays the cursor
 * from the table start: the tables are in memory, so a full replay is cheap
 * relative to the network refetch the seek triggers anyway. */
static void classic_seek_cursor(mp4_track_t* t, int64_t target_us, int want_key) {
    mp4_ctab_t* c = &t->ctab;
    classic_init_cursor(c);
    if (!c->sample_count) return;
    for (;;) {
        if (c->next + 1 >= c->sample_count) break;
        int64_t next_dts = add_i64_sat(c->dts, (int64_t)c->stts[c->stts_i * 2 + 1]);
        int64_t next_us = add_i64_sat(ticks_to_us(sub_i64_sat(next_dts, c->media_start), t->timescale), c->pts_delay_us);
        if (next_us > target_us) break;
        classic_advance(c);
    }
    if (want_key && c->stss) {
        uint32_t key1 = 1; /* stss is sorted 1-based; find the floor of next+1 */
        for (uint32_t k = 0; k < c->stss_count; ++k) {
            if (c->stss[k] <= c->next + 1) key1 = c->stss[k];
            else break;
        }
        uint32_t key0 = key1 ? key1 - 1 : 0;
        classic_init_cursor(c);
        while (c->next < key0) classic_advance(c);
    }
}

/* Walks the classic tables against one mdat. The byte source only moves
 * forward, so samples are read in file-offset order — the muxer's chunk
 * interleave — but delivered in decode-time order across tracks: with a
 * coarse interleave (a chunk of video ahead of the matching audio) the
 * delivery-paced chunk in front would otherwise hold the other track's
 * earlier samples past their turn and starve it. Samples whose file order
 * runs ahead of their delivery turn are parked in per-track queues, bounded
 * by MP4_MAX_PENDING — past the bound (or once nothing more can be read from
 * this mdat) the earliest parked sample delivers regardless. Samples whose
 * offsets lie beyond this mdat wait for a later one. mdat_end is the absolute
 * end of the payload, or INT64_MAX for a size-0 box. */
static int consume_progressive(mp4_t* m, int64_t mdat_end) {
    uint8_t* buf = NULL;
    size_t cap = 0;
    int rc = 0;
    mp4_pend_t* head[2] = { NULL, NULL };
    mp4_pend_t* tail[2] = { NULL, NULL };
    int64_t queued = 0;   /* payload bytes parked across both queues */

    for (;;) {
        if (!m->sink->is_running(m->sink->user)) { rc = -1; break; }

        /* absolute seek: reposition mid-walk when the engine posts a target */
        int64_t seek_us;
        if (m->reseek && m->sink->take_seek && m->sink->take_seek(m->sink->user, &seek_us)) {
            uint64_t new_off = UINT64_MAX;
            for (int i = 0; i < m->ntracks; ++i) {
                mp4_track_t* t = &m->tracks[i];
                if (!classic_track_ready(&t->ctab)) continue;
                classic_seek_cursor(t, seek_us, t->is_video);
                if (t->ctab.next < t->ctab.sample_count && t->ctab.offset < new_off)
                    new_off = t->ctab.offset;
            }
            if (new_off != UINT64_MAX && (int64_t)new_off < mdat_end &&
                m->reseek(m->reseek_ctx, (int64_t)new_off) == 0) {
                m->pos = (int64_t)new_off;
                for (int i = 0; i < 2; ++i) { /* parked samples predate the jump */
                    while (head[i]) { mp4_pend_t* s = head[i]; head[i] = s->next; free(s); }
                    tail[i] = NULL;
                }
                queued = 0;
            }
            /* On a failed refetch the cursors stay moved: samples now behind the
             * stream head drop via the existing unreachable-bytes check and the
             * walk degrades to forward-only rather than derailing. */
            continue;
        }

        /* the next sample in file order, while any remains in this mdat */
        mp4_track_t* rt = NULL;
        for (int i = 0; i < m->ntracks; ++i) {
            mp4_ctab_t* c = &m->tracks[i].ctab;
            if (c->next >= c->sample_count) continue;
            if ((int64_t)c->offset >= mdat_end) continue;
            if (!rt || c->offset < rt->ctab.offset) rt = &m->tracks[i];
        }

        /* deliver the earliest parked sample once no other track can still
         * produce an earlier one — or when forced (bound hit, or nothing left
         * to read in this mdat) */
        int e = -1;
        for (int i = 0; i < m->ntracks; ++i)
            if (head[i] && (e < 0 || head[i]->dts_us < head[e]->dts_us)) e = i;
        if (e >= 0) {
            int64_t barrier = INT64_MAX;
            for (int i = 0; i < m->ntracks; ++i) {
                if (i == e) continue;
                int64_t due = classic_next_due(&m->tracks[i], head[i], mdat_end);
                if (due < barrier) barrier = due;
            }
            if (head[e]->dts_us <= barrier || queued > MP4_MAX_PENDING || !rt) {
                mp4_pend_t* s = head[e];
                head[e] = s->next;
                if (!head[e]) tail[e] = NULL;
                queued -= s->size;
                emit_pend(m, &m->tracks[e], s);
                free(s);
                continue;
            }
        }
        if (!rt) break;

        mp4_ctab_t* c = &rt->ctab;
        uint32_t ssize = classic_size(c, c->next);
        if (ssize == 0 || ssize > (uint32_t)MP4_MAX_SAMPLE || (int64_t)c->offset < m->pos ||
            (mdat_end != INT64_MAX && (int64_t)c->offset > mdat_end - (int64_t)ssize)) {
            /* oversized (past the per-sample cap), behind the stream head, or
             * overrunning the box: the table points at bytes we can't safely read —
             * drop the sample and keep walking */
            classic_advance(c);
            continue;
        }
        if (skip_bytes(m, (int64_t)c->offset - m->pos) != 0) { rc = -1; break; }
        if (ssize > cap) {
            free(buf);
            cap = (size_t)ssize + ((size_t)ssize >> 1) + 4096;
            buf = (uint8_t*)malloc(cap);
            if (!buf) { cap = 0; rc = -1; break; }
        }
        if (read_exact(m, buf, (int)ssize) != (int)ssize) { rc = -1; break; }

        int64_t cto = 0;
        if (c->ctts) {
            uint32_t raw = c->ctts[c->ctts_i * 2 + 1];
            cto = c->ctts_version ? (int64_t)(int32_t)raw : (int64_t)raw;
        }
        int64_t pts_ticks = sub_i64_sat(add_i64_sat(c->dts, cto), c->media_start);
        int64_t pts_us = add_i64_sat(ticks_to_us(pts_ticks, rt->timescale), c->pts_delay_us);
        int64_t dts_us = classic_sample_dts_us(rt);

        mp4_pend_t* s = NULL;
        if (rt->is_video) {
            int key = 1;
            if (c->stss) {
                /* stss is sorted 1-based sample numbers; catch the cursor up past
                 * any entries skipped by dropped samples before comparing */
                while (c->stss_i < c->stss_count && c->stss[c->stss_i] < c->next + 1) c->stss_i++;
                key = c->stss_i < c->stss_count && c->stss[c->stss_i] == c->next + 1;
                if (key) c->stss_i++;
            }
            if (rt->codec == BASIS_CODEC_VP9 || rt->codec == BASIS_CODEC_AV1) {
                /* raw sample exactly as stored (VP9 superframes and AV1 temporal
                 * units stay whole) */
                s = (mp4_pend_t*)malloc(sizeof(*s) + ssize);
                if (s) {
                    memcpy(s->data, buf, ssize);
                    s->size = (int)ssize;
                    if (c->stss) s->key = key;
                    else s->key = rt->codec == BASIS_CODEC_AV1
                        ? basis_av1_is_keyframe(buf, (int)ssize)
                        : basis_vp9_is_keyframe(buf, (int)ssize);
                }
            } else {
                int nls = rt->nal_len_size ? rt->nal_len_size : 4;
                int outcap = basis_avcc_annexb_cap((int)ssize, nls);
                s = (mp4_pend_t*)malloc(sizeof(*s) + (size_t)outcap);
                if (s) {
                    int n = basis_avcc_to_annexb(buf, (int)ssize, nls, s->data, outcap);
                    if (n > 0) {
                        if (c->stss == NULL)
                            key = rt->codec == BASIS_CODEC_H265 ? basis_h265_is_keyframe(s->data, n) : basis_h264_is_keyframe(s->data, n);
                        s->size = n;
                        s->key = key;
                    } else { free(s); s = NULL; }
                }
            }
        } else {
            s = (mp4_pend_t*)malloc(sizeof(*s) + ssize);
            if (s) { memcpy(s->data, buf, ssize); s->size = (int)ssize; s->key = 0; }
        }
        if (s) {
            int ti = (int)(rt - m->tracks);
            s->pts_us = pts_us;
            s->dts_us = dts_us;
            s->next = NULL;
            if (tail[ti]) tail[ti]->next = s; else head[ti] = s;
            tail[ti] = s;
            queued += s->size;
        }
        classic_advance(c);
    }

    /* the loop drains the queues before exiting cleanly; anything still parked
     * here means the engine is stopping — release without delivering */
    for (int i = 0; i < 2; ++i)
        while (head[i]) { mp4_pend_t* s = head[i]; head[i] = s->next; free(s); }

    free(buf);
    if (rc == 0 && mdat_end != INT64_MAX && m->pos < mdat_end)
        return skip_bytes(m, mdat_end - m->pos);
    return rc;
}

/* ---- moof parsing ------------------------------------------------------- */

static int frag_reserve(mp4_frag_t* f, int n) {
    if (n <= f->cap) return 1;
    int nc = f->cap ? f->cap * 2 : 256;
    while (nc < n) nc *= 2;
    uint32_t* s = (uint32_t*)realloc(f->sizes, (size_t)nc * sizeof(uint32_t));
    if (s) f->sizes = s;
    uint32_t* d = (uint32_t*)realloc(f->durs, (size_t)nc * sizeof(uint32_t));
    if (d) f->durs = d;
    int64_t*  c = (int64_t*)realloc(f->ctos, (size_t)nc * sizeof(int64_t));
    if (c) f->ctos = c;
    if (!s || !d || !c) return 0;
    f->cap = nc;
    return 1;
}

/* Parse one traf into a fragment run per trun. tfhd/tfdt give the track and base
 * decode time; each trun gives a data-offset (relative to the moof) and samples. */
static void parse_traf(mp4_t* m, const uint8_t* p, int len) {
    int off = 0;
    int track_id = 0;
    uint32_t default_dur = 0, default_size = 0;
    int64_t base_dts = 0;
    while (off + 8 <= len) {
        int sz = (int)rd32(p + off);
        uint32_t ty = rd32(p + off + 4);
        if (sz < 8 || sz > len - off) break;
        const uint8_t* b = p + off + 8;
        int blen = sz - 8;
        if (ty == 0x74666864 && blen >= 8) { /* tfhd */
            uint32_t flags = rd32(b) & 0xFFFFFF;
            track_id = (int)rd32(b + 4);
            int q = 8;
            if (flags & 0x000001) q += 8;  /* base-data-offset */
            if (flags & 0x000002) q += 4;  /* sample-description-index */
            if (flags & 0x000008) { if (q + 4 > blen) { off += sz; continue; } default_dur = rd32(b + q); q += 4; }
            if (flags & 0x000010) { if (q + 4 > blen) { off += sz; continue; } default_size = rd32(b + q); q += 4; }
        } else if (ty == 0x74666474 && blen >= 8) { /* tfdt */
            int ver = b[0];
            if (ver != 1) base_dts = (int64_t)rd32(b + 4);
            else if (blen >= 12) base_dts = (int64_t)rd64(b + 4);
        } else if (ty == 0x7472756e && m->nfrags < MP4_MAX_FRAGS) { /* trun */
            if (blen < 8) { off += sz; continue; }
            mp4_frag_t* f = &m->frags[m->nfrags];
            /* FullBox version selects composition-offset signedness: v0 offsets are
             * unsigned, v1 signed. Capture it before the flags mask drops it. */
            int trun_version = b[0];
            uint32_t flags = rd32(b) & 0xFFFFFF;
            uint32_t count = rd32(b + 4);
            int q = 8;
            int data_offset = 0;
            if (flags & 0x000001) { if (q + 4 > blen) { off += sz; continue; } data_offset = (int)rd32(b + q); q += 4; } /* data-offset */
            if (flags & 0x000004) { if (q + 4 > blen) { off += sz; continue; } q += 4; } /* first-sample-flags */
            /* the declared per-sample table must fit inside this box */
            int per = 4 * (!!(flags & 0x000100) + !!(flags & 0x000200) + !!(flags & 0x000400) + !!(flags & 0x000800));
            if (count == 0 || count > MP4_MAX_FRAG_SAMPLES ||
                (int64_t)count * per > (int64_t)blen - q) { off += sz; continue; }
            if (!frag_reserve(f, (int)count)) { off += sz; continue; }
            for (uint32_t i = 0; i < count; ++i) {
                uint32_t dur = default_dur, size = default_size;
                int64_t cto = 0;
                if (flags & 0x000100) { dur = rd32(b + q); q += 4; }
                if (flags & 0x000200) { size = rd32(b + q); q += 4; }
                if (flags & 0x000400) { q += 4; }            /* sample flags */
                if (flags & 0x000800) { uint32_t raw = rd32(b + q); q += 4;
                    cto = trun_version == 1 ? (int64_t)(int32_t)raw : (int64_t)raw; }
                f->sizes[i] = size; f->durs[i] = dur; f->ctos[i] = cto;
            }
            f->track_id = track_id;
            f->base_dts = base_dts;
            f->data_offset = data_offset;
            f->default_dur = default_dur;
            f->count = (int)count;
            m->nfrags++;
        }
        off += sz;
    }
}

static void parse_moof(mp4_t* m, const uint8_t* p, int len) {
    m->nfrags = 0;
    int off = 0;
    while (off + 8 <= len) {
        int sz = (int)rd32(p + off);
        uint32_t ty = rd32(p + off + 4);
        if (sz < 8 || sz > len - off) break;
        if (ty == 0x74726166) parse_traf(m, p + off + 8, sz - 8); /* traf */
        off += sz;
    }
}

/* A moof's trafs share one mdat. Each trun's data-offset is relative to the moof
 * start; the smallest maps to the first byte of this mdat's payload, so subtract
 * it to place each run within the buffer we were handed.
 *
 * The runs are emitted as a DTS-ordered merge, not back to back: a run is one
 * track's whole fragment, so run-at-a-time emission would hand the decoder a
 * fragment of video before any of that fragment's audio — and with delivery
 * pacing on (VOD, HLS) the paced video burst makes the trailing audio arrive a
 * full fragment late, starving the audio-gated presentation clock. Merging by
 * decode time interleaves the tracks at frame granularity, like the TS demuxer,
 * and each sample's decode time rides along to the sink so the pacing gate
 * holds it no later than its decode turn. Order within a run (decode order)
 * is preserved. */
static void consume_mdat(mp4_t* m, const uint8_t* data, int len) {
    if (m->nfrags <= 0) return;
    int base_off = m->frags[0].data_offset;
    for (int k = 1; k < m->nfrags; ++k)
        if (m->frags[k].data_offset < base_off) base_off = m->frags[k].data_offset;

    mp4_track_t* trk[MP4_MAX_FRAGS];
    int64_t pos[MP4_MAX_FRAGS];
    int     idx[MP4_MAX_FRAGS];
    int64_t dts[MP4_MAX_FRAGS];
    for (int k = 0; k < m->nfrags; ++k) {
        const mp4_frag_t* f = &m->frags[k];
        trk[k] = track_by_id(m, f->track_id);
        pos[k] = (int64_t)f->data_offset - base_off;
        idx[k] = (trk[k] && pos[k] >= 0) ? 0 : f->count; /* unknown track / bad offset: skip run */
        dts[k] = f->base_dts;
    }

    for (;;) {
        if (maybe_seek_fragment(m))
            break;

        int k = -1;
        int64_t best_us = 0;
        for (int j = 0; j < m->nfrags; ++j) {
            if (idx[j] >= m->frags[j].count) continue;
            int64_t us = ticks_to_us(dts[j], trk[j]->timescale);
            if (k < 0 || us < best_us) { k = j; best_us = us; }
        }
        if (k < 0) break;

        const mp4_frag_t* f = &m->frags[k];
        mp4_track_t* t = trk[k];
        int i = idx[k];
        uint32_t ssize = f->sizes[i];
        if (ssize == 0 || ssize > (uint32_t)len || pos[k] > (int64_t)len - (int64_t)ssize) {
            idx[k] = f->count; continue; /* run truncated */
        }
        int64_t pts_us = ticks_to_us(add_i64_sat(dts[k], f->ctos[i]), t->timescale);

        if (t->is_video && (t->codec == BASIS_CODEC_VP9 || t->codec == BASIS_CODEC_AV1)) {
            /* raw sample exactly as stored (VP9 superframes and AV1 temporal
             * units stay whole) */
            int key = t->codec == BASIS_CODEC_AV1
                ? basis_av1_is_keyframe(data + pos[k], (int)ssize)
                : basis_vp9_is_keyframe(data + pos[k], (int)ssize);
            m->sink->on_video_au(m->sink->user, data + pos[k], (int)ssize, pts_us, best_us, key);
        } else if (t->is_video) {
            int nls = t->nal_len_size ? t->nal_len_size : 4;
            int cap = basis_avcc_annexb_cap((int)ssize, nls);
            uint8_t* out = (uint8_t*)malloc((size_t)cap);
            if (out) {
                int n = basis_avcc_to_annexb(data + pos[k], (int)ssize, nls, out, cap);
                if (n > 0) {
                    int key = t->codec == BASIS_CODEC_H265 ? basis_h265_is_keyframe(out, n) : basis_h264_is_keyframe(out, n);
                    m->sink->on_video_au(m->sink->user, out, n, pts_us, best_us, key);
                }
                free(out);
            }
        } else {
            m->sink->on_audio_frame(m->sink->user, data + pos[k], (int)ssize, pts_us);
        }

        pos[k] += ssize;
        dts[k] = add_i64_sat(dts[k], f->durs[i] ? f->durs[i] : f->default_dur);
        idx[k]++;
    }
}

int basis_mp4_run(basis_media_sink_t* sink, basis_read_fn read, void* ctx,
                  basis_reseek_fn reseek, void* reseek_ctx) {
    mp4_t m; memset(&m, 0, sizeof(m));
    m.sink = sink; m.read = read; m.ctx = ctx;
    m.reseek = reseek; m.reseek_ctx = reseek_ctx;
    m.mdat_seek_pos = -1;

    while (sink->is_running(sink->user)) {
        if (maybe_seek_fragment(&m))
            continue;

        uint32_t type; int64_t body;
        if (read_box_header(&m, &type, &body) != 0) break;

        if (type == 0x6d646174) { /* mdat */
            if (classic_ready(&m)) {
                /* progressive: stream the payload sample-by-sample (no size cap) */
                int64_t end = body < 0 ? INT64_MAX : add_i64_sat(m.pos, body);
                if (consume_progressive(&m, end) != 0) break;
                continue;
            }
            if (m.nfrags > 0 && body >= 0 && body <= MP4_MAX_BOX) {
                /* fragment payload: small — buffer whole and slice by the moof's runs */
                uint8_t* buf = (uint8_t*)malloc((size_t)(body ? body : 1));
                if (!buf) break;
                if (read_exact(&m, buf, (int)body) != (int)body) { free(buf); break; }
                consume_mdat(&m, buf, (int)body);
                free(buf);
                continue;
            }
            /* media data with no index yet: the moov may still follow (trailing-
             * moov progressive file). Skip it, but remember where the first one
             * started so a range-capable source can seek back once the moov's
             * sample tables arrive. On such a source seek straight past the mdat
             * rather than streaming its payload — otherwise reaching a trailing
             * moov reads the whole file before playback can begin. */
            m.mdat_skipped = 1;
            if (m.mdat_seek_pos < 0) m.mdat_seek_pos = m.pos;
            if (body < 0) break;
            if (m.reseek && m.reseek(m.reseek_ctx, add_i64_sat(m.pos, body)) == 0)
                m.pos = add_i64_sat(m.pos, body);
            else if (skip_bytes(&m, body) != 0)
                break;
            continue;
        }

        if (body < 0 || body > MP4_MAX_BOX) break;
        uint8_t* buf = (uint8_t*)malloc((size_t)(body ? body : 1));
        if (!buf) break;
        if (read_exact(&m, buf, (int)body) != (int)body) { free(buf); break; }

        switch (type) {
            case 0x6d6f6f76: /* moov */
                parse_box_tree(&m, NULL, buf, (int)body, 0);
                for (int i = 0; i < m.ntracks; ++i) {
                    classic_apply_elst(&m, &m.tracks[i]);
                    classic_init_cursor(&m.tracks[i].ctab);
                }
                announce_tracks(&m);
                /* A track whose sizes live in stz2 has no walkable tables; playing
                 * the remaining track would silently drop this one, so fail loudly. */
                for (int i = 0; i < m.ntracks; ++i)
                    if (m.tracks[i].ctab.has_stz2 && !classic_track_ready(&m.tracks[i].ctab)) {
                        sink->on_error(sink->user,
                            "MP4 uses a compact (stz2) sample-size table, which isn't supported; remux (ffmpeg -c copy) to rewrite the sample tables");
                        break;
                    }
                /* A video track whose codec we can't decode, with no supported
                 * video track beside it: error naming the codec rather than
                 * playing the audio track alone under a black screen. */
                if (m.saw_unsupported_video) {
                    int have_video = 0;
                    for (int i = 0; i < m.ntracks; ++i)
                        if (m.tracks[i].is_video) have_video = 1;
                    if (!have_video) {
                        char msg[128];
                        char cc[5] = "????";
                        for (int i = 0; i < 4; ++i) {
                            char ch = (char)(m.unsupported_video_fourcc >> (24 - 8 * i));
                            if (ch >= 0x20 && ch < 0x7F) cc[i] = ch;
                        }
                        snprintf(msg, sizeof(msg),
                                 "video codec '%s' is not supported (supported: H.264, H.265, VP9, AV1)",
                                 m.unsupported_video_fourcc ? cc : "unknown");
                        sink->on_error(sink->user, msg);
                    }
                }
                if (m.mdat_skipped && classic_ready(&m)) {
                    /* Trailing-moov progressive: the media streamed past before its
                     * index arrived. On a range-capable source (reseek wired) the
                     * mdat is still reachable — seek back to the first one and stream
                     * from there now that the sample tables are in hand; the walk is
                     * offset-driven, so a single mdat or several play alike. A one-way
                     * stream can't reach it, so it keeps the remux refusal. */
                    if (m.reseek && m.mdat_seek_pos >= 0 &&
                        m.reseek(m.reseek_ctx, m.mdat_seek_pos) == 0) {
                        m.pos = m.mdat_seek_pos;
                        if (sink->on_duration) {
                            int64_t dur_us = m.movie_duration > 0
                                ? ticks_to_us(m.movie_duration, m.movie_timescale > 0 ? m.movie_timescale : 1000)
                                : classic_total_duration_us(&m);
                            if (dur_us > 0) sink->on_duration(sink->user, dur_us);
                        }
                        free(buf);
                        consume_progressive(&m, INT64_MAX);
                        goto done; /* played to EOF (or stopped); the trailing moov is behind us */
                    }
                    sink->on_error(sink->user,
                        "progressive MP4 stores its index (moov) after the media data, which can't play over a one-way stream; remux with faststart (ffmpeg -movflags +faststart)");
                    break;
                }
                /* Faststart progressive: complete timeline in hand — report it, but
                 * only when the byte source can honour seeks (a non-zero duration is
                 * the managed layer's seekability signal). fMP4 durations come from
                 * the layer that knows them (HLS VOD). */
                if (sink->is_running(sink->user) && classic_ready(&m) && m.reseek && sink->on_duration) {
                    int64_t dur_us = m.movie_duration > 0
                        ? ticks_to_us(m.movie_duration, m.movie_timescale > 0 ? m.movie_timescale : 1000)
                        : classic_total_duration_us(&m);
                    if (dur_us > 0) sink->on_duration(sink->user, dur_us);
                }
                break;
            case 0x6d6f6f66: /* moof */
                parse_moof(&m, buf, (int)body);
                break;
            case 0x73696478: /* sidx */
                parse_sidx(&m, buf, (int)body, m.pos);
                break;
            default:
                break; /* ftyp, styp, free, ... ignored */
        }
        free(buf);
    }

done:
    for (int k = 0; k < MP4_MAX_FRAGS; ++k) {
        free(m.frags[k].sizes); free(m.frags[k].durs); free(m.frags[k].ctos);
    }
    for (int i = 0; i < 2; ++i) {
        mp4_ctab_t* c = &m.tracks[i].ctab;
        free(c->sizes); free(c->stts); free(c->ctts);
        free(c->stsc); free(c->chunk_offsets); free(c->stss);
    }
    sidx_clear(&m.sidx);
    return 0;
}
