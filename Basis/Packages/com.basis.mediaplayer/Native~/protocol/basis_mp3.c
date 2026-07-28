/*
 * basis_mp3.c — standalone MPEG audio Layer III (.mp3) demuxer.
 *
 * MP3 has no container: it is a bare stream of frames, each a 4-byte header
 * (11-bit sync 0xFFE, MPEG version, layer, bitrate/sample-rate index, padding)
 * followed by the coded frame. There is no length prefix, so framing is derived
 * from the header (frame_len = 144000*kbps/sr + pad for MPEG-1, 72000*kbps/sr
 * for MPEG-2/2.5). The 11-bit sync also occurs inside audio data, so a candidate
 * is only trusted once the *next* frame's sync validates at frame_len -- the
 * false-sync guard ExoPlayer's Mp3Extractor uses (it wants several consecutive
 * headers; we require the next one and resync a byte at a time otherwise).
 *
 * Layer III only: the OS decoders (Media Foundation MP3 DMO, Android
 * audio/mpeg) are Layer III, and real Layer I/II files are extinct -- a Layer
 * I/II header is treated as a failed sync. Each frame is emitted as one audio
 * AU with a PTS derived from the cumulative sample count; the decoders parse the
 * header themselves, so no init data is announced.
 *
 * A leading Xing/Info/VBRI header frame is parsed for duration and the seek
 * table, then dropped (as ffmpeg does) rather than emitted. Seeking is
 * approximate, as it is in every MP3 player: the Xing TOC (or a proportional /
 * CBR-bitrate mapping) picks a byte offset, and the requested time is taken as
 * the landed time. Needs a reseek hook (reseekable HTTP VOD); forward-only
 * otherwise.
 *
 * Attacker-controlled bytes throughout, so every length is bounded (fuzz target:
 * tools/media-fuzz/fuzz_mp3).
 */
#include "basis_mp3.h"

#include <string.h>

#define MP3_BUF (16 * 1024)     /* holds several frames (MPEG-1 max frame 1441 B) */
#define MP3_MAX_FRAME 2881      /* 144000*320/8000 + 1, a hard ceiling on frame_len */

typedef struct { int sr, ch, frame_len, samples, crc_len; } mp3_frame_t;

/* Parsed leading VBR/CBR header frame (Xing/Info/VBRI). */
typedef struct {
    int have;              /* a header frame was seen */
    int is_cbr;            /* "Info" tag (LAME writes it for CBR) */
    int64_t frames;        /* audio frame count, if signalled */
    int64_t bytes;         /* total stream byte count, if signalled */
    int have_toc;
    uint8_t toc[100];      /* Xing seek table: byte-percent per time-percent */
} mp3_vbr_t;

/* Layer III bitrate (kbps) by index; 0 = free-format, -1 = invalid. */
static const int kBrV1[16] = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, -1 };
static const int kBrV2[16] = { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, -1 };
/* Sample rate by [version][index]; version 0=MPEG2.5 1=reserved 2=MPEG2 3=MPEG1. */
static const int kSr[4][4] = {
    { 11025, 12000, 8000, 0 }, { 0, 0, 0, 0 }, { 22050, 24000, 16000, 0 }, { 44100, 48000, 32000, 0 }
};

/* Parse a 4-byte frame header; returns 1 and fills *f for a valid Layer III one. */
static int parse_header(const uint8_t* h, mp3_frame_t* f) {
    if (h[0] != 0xFF || (h[1] & 0xE0) != 0xE0) return 0;   /* 11-bit sync */
    int ver = (h[1] >> 3) & 0x3;
    int layer = (h[1] >> 1) & 0x3;
    if (ver == 1 || layer != 1) return 0;                  /* reserved version / not Layer III */
    int br_idx = (h[2] >> 4) & 0xF;
    int sr_idx = (h[2] >> 2) & 0x3;
    int pad = (h[2] >> 1) & 0x1;
    if (br_idx == 0 || br_idx == 15 || sr_idx == 3) return 0; /* free-format / invalid / reserved */
    int mpeg1 = (ver == 3);
    int kbps = mpeg1 ? kBrV1[br_idx] : kBrV2[br_idx];
    int sr = kSr[ver][sr_idx];
    if (kbps <= 0 || sr <= 0) return 0;
    f->sr = sr;
    f->ch = ((h[3] >> 6) & 0x3) == 3 ? 1 : 2;              /* mode 3 = mono */
    f->frame_len = (mpeg1 ? 144000 : 72000) * kbps / sr + pad;
    f->samples = mpeg1 ? 1152 : 576;
    f->crc_len = (h[1] & 0x01) ? 0 : 2;   /* protection bit 0 => a 2-byte CRC sits before the side info */
    return f->frame_len > 4 && f->frame_len <= MP3_MAX_FRAME;
}

/* Container sniff: 1 when the leading bytes are a valid Layer III frame header.
 * The frame-sync heuristic is the weakest magic, so the core sniffs it last. */
int basis_mp3_sniff(const uint8_t* b, int n) {
    mp3_frame_t f;
    return n >= 4 && parse_header(b, &f);
}

static uint32_t rd_be32(const uint8_t* p) {
    return ((uint32_t)p[0] << 24) | ((uint32_t)p[1] << 16) | ((uint32_t)p[2] << 8) | p[3];
}

/* If the first frame is a Xing/Info (CBR) or VBRI header, parse its counts and
 * seek table into *v and return 1 (the caller drops the frame). Xing/Info sit
 * past the side-info block; VBRI is always at byte 36. */
static int parse_vbr_header(const uint8_t* p, const mp3_frame_t* f, mp3_vbr_t* v) {
    memset(v, 0, sizeof(*v));
    int mpeg1 = (f->samples == 1152);
    int si = mpeg1 ? (f->ch == 1 ? 17 : 32) : (f->ch == 1 ? 9 : 17);
    int off = 4 + f->crc_len + si;
    if (off + 8 <= f->frame_len &&
        (memcmp(p + off, "Xing", 4) == 0 || memcmp(p + off, "Info", 4) == 0)) {
        v->have = 1;
        v->is_cbr = (p[off] == 'I');
        uint32_t flags = rd_be32(p + off + 4);
        int q = off + 8;
        if ((flags & 0x1) && q + 4 <= f->frame_len) { v->frames = rd_be32(p + q); q += 4; }
        if ((flags & 0x2) && q + 4 <= f->frame_len) { v->bytes = rd_be32(p + q); q += 4; }
        if ((flags & 0x4) && q + 100 <= f->frame_len) { memcpy(v->toc, p + q, 100); v->have_toc = 1; }
        return 1;
    }
    if (36 + 26 <= f->frame_len && memcmp(p + 36, "VBRI", 4) == 0) {
        v->have = 1;                       /* Fraunhofer VBR: counts only, no Xing-form TOC */
        v->bytes = rd_be32(p + 36 + 10);
        v->frames = rd_be32(p + 36 + 14);
        return 1;
    }
    return 0;
}

int basis_mp3_run(basis_media_sink_t* sink, basis_read_fn read, void* ctx,
                  basis_reseek_fn reseek, void* reseek_ctx) {
    uint8_t buf[MP3_BUF];
    int len = 0, eof = 0, announced = 0, locked = 0;
    int64_t samples = 0;    /* cumulative; PTS is rounded from this, not accumulated */
    int64_t src_pos = 0;    /* total bytes read from the source */
    int64_t audio_start = 0, mpeg_start = 0, duration_us = 0;
    int a_sr = 0, a_ch = 0, cbr_bps = 0;
    mp3_vbr_t vbr; vbr.have = 0;

    /* Skip a leading ID3v2 tag if present: "ID3", 2 version + 1 flags bytes,
     * then a syncsafe u28 size (7 bits per byte). Cover art can be large, so the
     * payload is consumed by reads, not buffered. Stacked tags loop. */
    while (sink->is_running(sink->user)) {
        uint8_t tag[10];
        int got = 0;
        while (got < 10) {
            int r = read(ctx, tag + got, 10 - got);
            if (r <= 0) { eof = 1; break; }
            got += r; src_pos += r;
        }
        if (got < 10) break;
        if (memcmp(tag, "ID3", 3) != 0) { memcpy(buf, tag, 10); len = 10; break; }
        int64_t sz = ((int64_t)(tag[6] & 0x7F) << 21) | ((tag[7] & 0x7F) << 14) |
                     ((tag[8] & 0x7F) << 7) | (tag[9] & 0x7F);
        if (tag[3] == 4 && (tag[5] & 0x10)) sz += 10;       /* footer: ID3v2.4 only */
        uint8_t skip[4096];
        while (sz > 0 && sink->is_running(sink->user)) {
            int want = sz < (int64_t)sizeof(skip) ? (int)sz : (int)sizeof(skip);
            int r = read(ctx, skip, want);
            if (r <= 0) { eof = 1; break; }
            sz -= r; src_pos += r;
        }
    }

    while (sink->is_running(sink->user) && !(eof && len < 4)) {
        /* Absolute-seek handshake: map the target to a byte offset and reposition the
         * source. Only reseekable sources (HTTP VOD) provide a hook and, in turn, report
         * a duration — so take_seek is polled only when reseek exists, and a non-reseekable
         * MP3 exposes no seek to take. */
        int64_t target_us;
        if (announced && reseek && sink->take_seek && sink->take_seek(sink->user, &target_us)) {
            if (target_us < 0) target_us = 0;
            int64_t pos;
            if (vbr.have && duration_us > 0 && vbr.bytes > audio_start - mpeg_start) {
                double frac = (double)target_us / (double)duration_us;
                if (frac > 1.0) frac = 1.0;
                if (vbr.have_toc) {
                    double pct = frac * 100.0;
                    int a = (int)pct; if (a > 99) a = 99;
                    double fa = vbr.toc[a];
                    double fb = (a < 99) ? vbr.toc[a + 1] : 256.0;
                    frac = (fa + (fb - fa) * (pct - a)) / 256.0;
                }
                pos = audio_start + (int64_t)(frac * (double)(vbr.bytes - (audio_start - mpeg_start)));
            } else {
                pos = audio_start + (int64_t)((double)target_us * (double)cbr_bps / 1000000.0);
            }
            if (pos < audio_start) pos = audio_start;
            if (reseek(reseek_ctx, pos) == 0) {
                src_pos = pos; len = 0; eof = 0; locked = 0;
                samples = (int64_t)((double)target_us * (double)a_sr / 1000000.0 + 0.5);
                continue;
            }
        }

        if (!eof && len < MP3_BUF) {                        /* top up */
            int r = read(ctx, buf + len, MP3_BUF - len);
            if (r <= 0) eof = 1; else { len += r; src_pos += r; }
        }
        int i = 0;
        while (i + 4 <= len) {
            mp3_frame_t f;
            if (!parse_header(buf + i, &f)) { i++; locked = 0; continue; }
            if (i + f.frame_len > len) {                    /* need the whole frame */
                if (eof) i = len;                           /* trailing partial: drop */
                break;
            }
            if (!locked) {
                /* Confirm this is a real frame, not a data false-sync: the next
                 * header must validate at frame_len (wait for it unless at EOF). */
                if (i + f.frame_len + 4 > len) { if (!eof) break; }
                else {
                    mp3_frame_t nf;
                    /* A real next frame shares this one's rate/channels/samples
                     * (VBR varies only the bitrate), so a mismatch is a false sync. */
                    if (!parse_header(buf + i + f.frame_len, &nf) ||
                        nf.sr != f.sr || nf.ch != f.ch || nf.samples != f.samples) { i++; continue; }
                }
                locked = 1;
            }
            /* Reject a frame whose format drifts from the announced one: the fixed
             * announce can't describe it, and feeding it on desyncs the decoder. */
            if (announced && (f.sr != a_sr || f.ch != a_ch)) { i++; locked = 0; continue; }
            if (!vbr.have && !announced && parse_vbr_header(buf + i, &f, &vbr)) {
                /* Xing/Info/VBRI header: seek table + counts, decodes to silence.
                 * Drop it so playback starts on the first real audio frame. */
                mpeg_start = (src_pos - len) + i;   /* the Xing frame is the first MPEG
                                                     * frame; vbr.bytes counts from here,
                                                     * so a leading ID3 tag is excluded */
                i += f.frame_len;
                continue;
            }
            if (!announced) {
                sink->on_audio_format(sink->user, BASIS_CODEC_MP3, f.sr, f.ch, NULL, 0);
                announced = 1;
                a_sr = f.sr;
                a_ch = f.ch;
                audio_start = (src_pos - len) + i;          /* first real frame's offset */
                cbr_bps = f.samples > 0 ? f.frame_len * f.sr / f.samples : 0;
                if (vbr.have && vbr.frames > 0)
                    duration_us = vbr.frames * (int64_t)f.samples * 1000000LL / f.sr;
                /* Report duration only when reseekable — a duration implies a working seek
                 * bar (matching MP4/WebM/Ogg). duration_us stays set for the VBR seek math. */
                if (duration_us > 0 && reseek && sink->on_duration)
                    sink->on_duration(sink->user, duration_us);
            }
            int64_t pts_us = (samples * 1000000LL + f.sr / 2) / f.sr; /* round-to-nearest */
            sink->on_audio_frame(sink->user, buf + i, f.frame_len, pts_us);
            samples += f.samples;
            i += f.frame_len;
        }
        if (i > 0) { memmove(buf, buf + i, (size_t)(len - i)); len -= i; }
        else if (eof) break;                                /* no progress + EOF */
        else if (len == MP3_BUF) { memmove(buf, buf + 1, MP3_BUF - 1); len--; } /* no sync in a full buffer: slip a byte */
    }

    if (!announced) sink->on_error(sink->user, "no MP3 (Layer III) frames found");
    return 0;
}
