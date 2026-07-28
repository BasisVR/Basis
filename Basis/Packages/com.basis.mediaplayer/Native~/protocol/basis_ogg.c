/*
 * basis_ogg.c — Ogg demuxer for Opus (`.opus` files).
 *
 * Ogg framing: pages begin with the "OggS" capture pattern, a 27-byte header
 * (version, header-type flags, granule position, serial, page seq, CRC, and a
 * segment count), a segment table, then the page body. A packet is a run of
 * segments; a 255 lacing value continues the packet (into the next segment, and
 * across a page boundary when the page ends on 255), any value < 255 ends it.
 *
 * Scope: one logical Opus bitstream. First packet = OpusHead (identification),
 * second = OpusTags (comments, skipped), then audio packets. Per-packet PTS
 * accumulates from Opus TOC-derived durations (the encoder pre-skip stays a
 * decode-layer concern, matching the WebM lane). Chained streams (a new serial
 * after EOS) are treated as end-of-stream. Each page's CRC-32 is verified; a
 * page that fails is dropped and the walk resyncs on the next "OggS".
 *
 * Seek: Ogg has no index, so seeking is granule bisection over the byte range
 * (the caller must supply a reseek and the total size). Duration is the last
 * page's granule, read once at open by seeking to the tail. Both are gated on a
 * seekable source; a live/unknown-size stream plays forward with no duration.
 *
 * Everything here parses attacker-controlled bytes, so every length is bounded
 * before use (this is a fuzz target: tools/media-fuzz/fuzz_ogg).
 */
#include "basis_ogg.h"

#include <stdlib.h>
#include <string.h>

#define OGG_HDR 27                       /* fixed page header size */
#define OGG_MAX_BODY (255 * 255)         /* max page body (255 segments * 255) */
#define OGG_MAX_PACKET (8 * 1024 * 1024) /* runaway guard for reassembled packets */
#define OGG_TAIL 65536                    /* tail window scanned for the last page */

typedef struct {
    basis_media_sink_t* sink;
    basis_read_fn read;
    void* ctx;
    basis_reseek_fn reseek;
    void* reseek_ctx;
    int64_t size;             /* total stream size, or -1 when unknown (no seek) */
    int64_t pos;              /* absolute read offset */

    uint32_t serial;
    int have_serial;
    int ended;
    uint32_t last_page_seq;   /* to detect a dropped page (sequence gap) */
    int have_page_seq;

    long long packet_index;   /* 0 = OpusHead, 1 = OpusTags, 2+ = audio */
    int channels, pre_skip;
    int announced;
    int64_t next_pts_us;

    uint8_t* pkt;             /* packet reassembled across segments/pages */
    int pkt_len, pkt_cap;
} ogg_t;

/* One parsed, CRC-valid page. */
typedef struct {
    int64_t granule;
    uint32_t serial;
    uint32_t page_seq;
    uint8_t htype;
    uint8_t segtab[255];
    int nsegs;
    uint8_t body[OGG_MAX_BODY];
    int bodylen;
} ogg_page_t;

/* ---- Ogg CRC-32 (poly 0x04c11db7, MSB-first, no reflection, no final xor) --
 * Chainable so header + segment table + body hash contiguously across regions. */
static uint32_t ogg_crc(uint32_t crc, const uint8_t* p, int n) {
    for (int i = 0; i < n; ++i) {
        crc ^= (uint32_t)p[i] << 24;
        for (int b = 0; b < 8; ++b)
            crc = (crc & 0x80000000u) ? (crc << 1) ^ 0x04c11db7u : (crc << 1);
    }
    return crc;
}

static int oread(ogg_t* o, uint8_t* buf, int n) {
    int got = 0;
    while (got < n) {
        if (!o->sink->is_running(o->sink->user)) return 0;
        int r = o->read(o->ctx, buf + got, n - got);
        if (r <= 0) return 0;
        got += r;
        o->pos += r;
    }
    return 1;
}

static int64_t rd_le64(const uint8_t* p) {
    uint64_t v = 0;
    for (int i = 7; i >= 0; --i) v = (v << 8) | p[i];
    return (int64_t)v;
}
static uint32_t rd_le32(const uint8_t* p) {
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}

/* Scan to the next "OggS", read the whole page, verify its CRC. Returns 1 with
 * *pg filled, or 0 at EOF. A CRC failure drops the page and resyncs. */
static int read_page(ogg_t* o, ogg_page_t* pg) {
    for (;;) {
        int match = 0;
        static const char cap[4] = { 'O', 'g', 'g', 'S' };
        while (match < 4) {                    /* find the capture pattern */
            uint8_t c;
            if (!oread(o, &c, 1)) return 0;
            if (c == (uint8_t)cap[match]) match++;
            else match = (c == 'O') ? 1 : 0;
        }

        uint8_t hdr[OGG_HDR];
        memcpy(hdr, cap, 4);
        if (!oread(o, hdr + 4, OGG_HDR - 4)) return 0;
        pg->nsegs = hdr[26];
        if (pg->nsegs > 0 && !oread(o, pg->segtab, pg->nsegs)) return 0;
        pg->bodylen = 0;
        for (int i = 0; i < pg->nsegs; ++i) pg->bodylen += pg->segtab[i];
        if (pg->bodylen > 0 && !oread(o, pg->body, pg->bodylen)) return 0;

        uint32_t stored = rd_le32(hdr + 22);
        hdr[22] = hdr[23] = hdr[24] = hdr[25] = 0;
        uint32_t crc = ogg_crc(0, hdr, OGG_HDR);
        crc = ogg_crc(crc, pg->segtab, pg->nsegs);
        crc = ogg_crc(crc, pg->body, pg->bodylen);
        if (crc != stored) continue;           /* damaged page: keep scanning */

        pg->htype = hdr[5];
        pg->granule = rd_le64(hdr + 6);
        pg->serial = rd_le32(hdr + 14);
        pg->page_seq = rd_le32(hdr + 18);
        return 1;
    }
}

static int64_t granule_to_us(int64_t granule, int pre_skip) {
    /* Guard the subtraction (INT64_MIN - pre_skip is UB) and the scale-up. */
    if (granule <= pre_skip) return 0;
    int64_t s = granule - pre_skip;
    int64_t seconds = s / 48000;
    if (seconds > INT64_MAX / 1000000LL) return INT64_MAX;
    return seconds * 1000000LL + (s % 48000) * 1000000LL / 48000;
}

/* Opus TOC -> samples at 48 kHz (frame size * frame count). */
static const int kOpusFrame48k[32] = {
    480, 960, 1920, 2880,  480, 960, 1920, 2880,  480, 960, 1920, 2880,
    480, 960,  480,  960,  120, 240,  480,  960,  120, 240,  480,  960,
    120, 240,  480,  960,  120, 240,  480,  960
};
static int opus_packet_samples(const uint8_t* p, int len) {
    if (len < 1) return 0;
    int config = p[0] >> 3;
    int code = p[0] & 0x3;
    int frames = code == 0 ? 1 : code == 3 ? (len >= 2 ? (p[1] & 0x3F) : 0) : 2;
    long s = (long)kOpusFrame48k[config] * frames;
    if (s < 0) s = 0; if (s > 5760) s = 5760;   /* Opus per-packet max */
    return (int)s;
}

static void emit_packet(ogg_t* o, const uint8_t* p, int len) {
    if (len <= 0) return;
    if (o->packet_index == 0) {
        if (len >= 19 && memcmp(p, "OpusHead", 8) == 0) {
            o->channels = p[9];
            o->pre_skip = p[10] | (p[11] << 8);
            if (o->channels >= 1 && o->channels <= 8) {
                /* Start the timeline at -pre_skip so the priming samples the
                 * decoder produces first land before media-time 0 and are dropped
                 * (basis_frames_before_origin), leaving real audio anchored at 0. */
                o->next_pts_us = -((int64_t)o->pre_skip * 1000000LL / 48000);
                o->sink->on_audio_format(o->sink->user, BASIS_CODEC_OPUS, 48000,
                                         o->channels, p, len);
                o->announced = 1;
            }
        }
    } else if (o->packet_index == 1) {
        /* OpusTags: comment header, no audio — skip. */
    } else if (o->announced) {
        o->sink->on_audio_frame(o->sink->user, p, len, o->next_pts_us);
        o->next_pts_us += (int64_t)opus_packet_samples(p, len) * 1000000LL / 48000;
    }
    o->packet_index++;
}

/* Append body bytes to the pending packet; flush on any lacing value < 255. The
 * final segment being 255 leaves the packet open to continue on the next page. */
static void feed_page(ogg_t* o, const ogg_page_t* pg) {
    int off = 0;
    int i = 0;
    int continued = pg->htype & 0x01;           /* first packet continues the prior page */

    /* A page-sequence gap means a page was dropped between this one and the last
     * (a CRC failure, most likely). Any pending prefix belongs to a packet whose
     * continuation we lost, so it can't be completed — discard it. The
     * continuation-flag reconciliation below then skips this page's leading
     * fragment if it was continuing that lost packet. */
    if (o->have_page_seq && pg->page_seq != o->last_page_seq + 1) o->pkt_len = 0;
    o->last_page_seq = pg->page_seq;
    o->have_page_seq = 1;

    /* Reconcile the pending prefix with the page's continuation flag so a
     * discontinuity (a dropped CRC page, or landing here after a seek) can't
     * splice unrelated bytes. A pending prefix with no continuation means the
     * previous packet never completed — drop it. A continuation with no prefix
     * means we joined mid-packet — skip that leading fragment up to and
     * including its first terminator, then start clean. */
    if (o->pkt_len > 0 && !continued) o->pkt_len = 0;
    if (o->pkt_len == 0 && continued) {
        for (; i < pg->nsegs; ++i) {
            int seg = pg->segtab[i];
            if (off + seg > pg->bodylen) return;
            off += seg;
            if (seg < 255) { ++i; break; }      /* fragment ended: resume from the next packet */
        }
    }

    for (; i < pg->nsegs; ++i) {
        int seg = pg->segtab[i];
        if (off + seg > pg->bodylen) return;
        if (o->pkt_len + seg > o->pkt_cap) {
            int ncap = o->pkt_cap ? o->pkt_cap : 4096;
            while (ncap < o->pkt_len + seg && ncap < OGG_MAX_PACKET) ncap *= 2;
            if (ncap < o->pkt_len + seg) { o->pkt_len = 0; return; }
            uint8_t* nb = (uint8_t*)realloc(o->pkt, (size_t)ncap);
            if (!nb) { o->pkt_len = 0; return; }
            o->pkt = nb; o->pkt_cap = ncap;
        }
        memcpy(o->pkt + o->pkt_len, pg->body + off, (size_t)seg);
        o->pkt_len += seg;
        off += seg;
        if (seg < 255) { emit_packet(o, o->pkt, o->pkt_len); o->pkt_len = 0; }
    }
}

static int do_reseek(ogg_t* o, int64_t abs) {
    if (!o->reseek || abs < 0) return 0;
    if (o->reseek(o->reseek_ctx, abs) != 0) return 0;
    o->pos = abs;
    o->pkt_len = 0;                              /* drop any partial packet */
    o->have_page_seq = 0;                        /* seek is a legitimate seq discontinuity */
    return 1;
}

/* Duration = the last page's granule. Seek to the tail, read every page in it,
 * keep the final granule, then return to the start. Gated on a seekable source. */
static void compute_duration(ogg_t* o) {
    if (!o->reseek || o->size <= 0) return;
    int64_t resume = o->pos;                    /* come back to mid-stream, not 0 */
    int64_t tail = o->size > OGG_TAIL ? o->size - OGG_TAIL : 0;
    if (!do_reseek(o, tail)) return;
    ogg_page_t pg;
    int64_t last = -1;
    while (o->pos < o->size && read_page(o, &pg)) {
        /* Only the selected logical stream: a chained file's tail may belong to a
         * later stream with its own reset granule, which isn't our duration. */
        if (pg.serial == o->serial && pg.granule >= 0) last = pg.granule;
    }
    do_reseek(o, resume);
    if (last > 0 && o->sink->on_duration)
        o->sink->on_duration(o->sink->user, granule_to_us(last, o->pre_skip));
}

/* Granule bisection: land on the last page whose end granule is <= target. */
static void seek_to_us(ogg_t* o, int64_t target_us) {
    if (!o->reseek || o->size <= 0) return;
    if (target_us < 0) target_us = 0;
    /* us -> 48k samples, split so target_us * 48 can't overflow int64. */
    int64_t target_g = (target_us / 1000) * 48 + (target_us % 1000) * 48 / 1000 + o->pre_skip;
    int64_t lo = 0, hi = o->size, best = 0;
    ogg_page_t pg;
    for (int it = 0; it < 40 && hi - lo > 4096; ++it) {
        int64_t mid = lo + (hi - lo) / 2;
        if (!do_reseek(o, mid)) return;
        if (!read_page(o, &pg)) { hi = mid; continue; }
        if (pg.serial != o->serial) { hi = mid; continue; } /* a later chain: our stream is earlier */
        if (pg.granule < 0) { lo = mid + 1; continue; } /* no granule here: go later */
        if (pg.granule <= target_g) { lo = mid; best = mid; }
        else hi = mid;
    }
    if (!do_reseek(o, best)) return;
    /* Anchor the post-seek timeline to the landed page's real granule, not the
     * requested time: the page ends at or before the target, so labelling the
     * audio that follows with target_us would play earlier samples late. Consume
     * the landed page to read its end granule; the run loop resumes at the next
     * page, whose first packet begins exactly there. (The decoder handles its
     * own pre-roll from that point.) */
    if (read_page(o, &pg) && pg.serial == o->serial)
        o->next_pts_us = granule_to_us(pg.granule, o->pre_skip);
    else
        o->next_pts_us = target_us;
}

int basis_ogg_run(basis_media_sink_t* sink, basis_read_fn read, void* ctx,
                  basis_reseek_fn reseek, void* reseek_ctx, int64_t stream_size) {
    ogg_t o;
    memset(&o, 0, sizeof(o));
    o.sink = sink; o.read = read; o.ctx = ctx;
    o.reseek = reseek; o.reseek_ctx = reseek_ctx; o.size = stream_size;

    ogg_page_t pg;
    int did_duration = 0;

    while (sink->is_running(sink->user)) {
        if (o.ended) break;
        if (!read_page(&o, &pg)) break;

        if (pg.htype & 0x02) {                  /* BOS */
            if (!o.have_serial) { o.serial = pg.serial; o.have_serial = 1; }
        }
        if (!o.have_serial || pg.serial != o.serial) continue; /* other stream */

        feed_page(&o, &pg);

        /* Once OpusHead is in hand, learn the duration and honour seeks. */
        if (o.announced && !did_duration) { did_duration = 1; compute_duration(&o); }
        if (o.announced && sink->take_seek) {
            int64_t target;
            if (sink->take_seek(sink->user, &target)) seek_to_us(&o, target);
        }

        if (pg.htype & 0x04) { o.ended = 1; break; } /* EOS */
    }

    free(o.pkt);
    if (!o.announced) sink->on_error(sink->user, "Ogg has no Opus stream this player supports");
    return 0;
}
