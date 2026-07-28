/*
 * basis_ts.c — MPEG-TS demuxer (PAT/PMT/PES) -> H.264/H.265 + AAC/LPCM.
 *
 * Live TS over HTTP is Annex-B video + ADTS audio in 188-byte packets; m2ts
 * streams (192-byte packets, a 4-byte TP_extra_header before each sync byte)
 * carry the same tables plus Blu-ray HDMV LPCM audio (stream_type 0x80). We
 * resync on 0x47 (detecting the packet stride), follow PAT->PMT to find the
 * elementary PIDs, reassemble PES per PID, and push access units / audio
 * frames into the sink. PCR/PTS are 90 kHz.
 */

#include "basis_ts.h"
#include "basis_bitstream.h"

#include <stdlib.h>
#include <string.h>

#define TS_PKT 188

typedef struct {
    int pid;
    basis_codec_t codec;
    uint8_t* buf;
    int len, cap;
    int64_t pts_us;
    int started;
} es_accum_t;

typedef struct {
    basis_media_sink_t* sink;
    basis_read_fn read;
    void* ctx;

    int pmt_pid;
    int video_pid;
    int audio_pid;
    basis_codec_t video_codec;
    basis_codec_t audio_codec;

    es_accum_t v;
    es_accum_t a;

    int audio_announced;
    int audio_sr, audio_ch, audio_profile;
    int video_announced;
    int pkt_size;   /* 0 until detected; 188, or 192 for m2ts */
} ts_t;

/* A PES buffer only flushes on the next payload-unit-start for its PID, so a
 * stream that sets PUSI once and never again would grow this without bound. Cap
 * it: a single video PES (unbounded PES_packet_length, delimited by the next
 * PUSI) can legitimately reach a few MiB at high bitrate/4K, well under this. */
#define TS_MAX_PES (8 * 1024 * 1024)

static int accum_reserve(es_accum_t* e, int extra) {
    int64_t need = (int64_t)e->len + extra;
    if (need <= e->cap) return 1;
    if (need > TS_MAX_PES) return 0;
    int64_t ncap = e->cap ? e->cap : 65536;
    while (ncap < need) ncap *= 2;
    if (ncap > TS_MAX_PES) ncap = TS_MAX_PES;
    uint8_t* nb = (uint8_t*)realloc(e->buf, (size_t)ncap);
    if (!nb) return 0;
    e->buf = nb; e->cap = (int)ncap;
    return 1;
}

static int64_t pts_to_us(int64_t pts90) { return pts90 * 1000 / 90; }

static int64_t parse_pes_pts(const uint8_t* p, int len, int* payload_off) {
    /* p points at the PES packet start (00 00 01 stream_id ...) */
    if (len < 9 || p[0] != 0 || p[1] != 0 || p[2] != 1) { *payload_off = 0; return -1; }
    int flags = (p[7] >> 6) & 0x3;
    int hdr_len = p[8];
    int64_t pts = -1;
    if ((flags & 0x2) && len >= 14) {
        pts = ((int64_t)(p[9] >> 1 & 0x7) << 30) |
              ((int64_t)p[10] << 22) |
              ((int64_t)(p[11] >> 1) << 15) |
              ((int64_t)p[12] << 7) |
              ((int64_t)(p[13] >> 1));
    }
    *payload_off = 9 + hdr_len;
    return pts;
}

static void flush_video(ts_t* t) {
    es_accum_t* e = &t->v;
    if (!e->started || e->len <= 0) { e->len = 0; e->started = 0; return; }

    int off = 0;
    int64_t pts = parse_pes_pts(e->buf, e->len, &off);
    if (off < 0 || off >= e->len) { e->len = 0; e->started = 0; return; }

    const uint8_t* au = e->buf + off;
    int au_len = e->len - off;
    int64_t pts_us = pts >= 0 ? pts_to_us(pts) : e->pts_us;

    if (!t->video_announced) {
        int w = 0, h = 0;
        if (t->video_codec == BASIS_CODEC_H264) {
            int pos = 0, no, nl, have_sps = 0;
            while ((pos = basis_annexb_next(au, au_len, pos, &no, &nl)) >= 0) {
                if (nl > 0 && basis_h264_nal_type(au[no]) == 7) {
                    basis_h264_sps_dimensions(au + no, nl, &w, &h);
                    if (w > 0 && h > 0) { have_sps = 1; break; }
                }
            }
            /* Mid-GOP join (or an SPS we couldn't read dimensions from): drop this
             * AU — it can't decode without its IDR anyway — and wait for the next
             * SPS-bearing keyframe instead of announcing 0x0 and latching. */
            if (!have_sps) { e->len = 0; e->started = 0; return; }
        }
        t->sink->on_video_format(t->sink->user, t->video_codec, NULL, 0, w, h);
        t->video_announced = 1;
    }

    int key = (t->video_codec == BASIS_CODEC_H265) ? basis_h265_is_keyframe(au, au_len)
                                                   : basis_h264_is_keyframe(au, au_len);
    t->sink->on_video_au(t->sink->user, au, au_len, pts_us, pts_us, key);

    e->len = 0;
    e->started = 0;
}

/* Blu-ray channel_assignment -> channel count (0 = reserved/unsupported). */
static const int kLpcmChannels[16] = { 0, 1, 0, 2, 3, 3, 4, 4, 5, 6, 7, 8, 0, 0, 0, 0 };

/* HDMV LPCM PES payload: a 4-byte header (16-bit data length; channel
 * assignment + sample-rate code; bits-per-sample code) followed by big-endian
 * PCM in Blu-ray channel order. The decode layer converts and reorders; the
 * raw assignment + bits codes travel in the format announce's config blob. */
static void flush_audio_lpcm(ts_t* t, const uint8_t* p, int remain, int64_t pts_us) {
    if (remain <= 4) return;
    if (!t->audio_announced) {
        int assign = (p[2] >> 4) & 0xF;
        int rate_code = p[2] & 0xF;
        int bits_code = (p[3] >> 6) & 0x3;   /* 1 = 16-bit, 3 = 24-bit */
        int ch = kLpcmChannels[assign];
        /* Only announce formats the LPCM decode path actually plays: 48 kHz,
         * 16- or 24-bit. The decode layer plays at the stream rate with no
         * resampler and no 20-bit unpack, so announcing 96/192 kHz or 20-bit
         * would have the decoder drop it and leave the sink half-configured.
         * Leaving them unannounced is graceful silence (video keeps playing),
         * matching the AAC path's behaviour for unsupported audio. */
        if (rate_code != 1 || (bits_code != 1 && bits_code != 3) || ch <= 0) return;
        uint8_t cfg[2] = { (uint8_t)assign, (uint8_t)bits_code };
        t->audio_sr = 48000; t->audio_ch = ch; t->audio_profile = 0;
        t->sink->on_audio_format(t->sink->user, BASIS_CODEC_LPCM, 48000, ch, cfg, 2);
        t->audio_announced = 1;
    }
    int dlen = (p[0] << 8) | p[1];
    int alen = remain - 4;
    if (dlen > 0 && dlen < alen) alen = dlen;
    t->sink->on_audio_frame(t->sink->user, p + 4, alen, pts_us);
}

static void flush_audio(ts_t* t) {
    es_accum_t* e = &t->a;
    if (!e->started || e->len <= 0) { e->len = 0; e->started = 0; return; }

    int off = 0;
    int64_t pts = parse_pes_pts(e->buf, e->len, &off);
    if (off < 0 || off >= e->len) { e->len = 0; e->started = 0; return; }

    const uint8_t* p = e->buf + off;
    int remain = e->len - off;
    int64_t base_us = pts >= 0 ? pts_to_us(pts) : e->pts_us;
    int frame_idx = 0;

    if (t->audio_codec == BASIS_CODEC_LPCM) {
        flush_audio_lpcm(t, p, remain, base_us);
        e->len = 0;
        e->started = 0;
        return;
    }

    while (remain >= 7) {
        basis_adts_t ad;
        if (basis_adts_parse(p, remain, &ad) != 0) break;
        if (ad.frame_len > remain) break;

        if (!t->audio_announced) {
            uint8_t asc[2];
            basis_aac_build_asc(ad.profile + 1, ad.sample_rate, ad.channels, asc);
            int ach = basis_aac_channels_from_config(ad.channels);
            t->audio_sr = ad.sample_rate; t->audio_ch = ach; t->audio_profile = ad.profile;
            t->sink->on_audio_format(t->sink->user, BASIS_CODEC_AAC, ad.sample_rate, ach, asc, 2);
            t->audio_announced = 1;
        }

        int raw_len = ad.frame_len - ad.header_len;
        int64_t fpts = base_us + (int64_t)frame_idx * 1024 * 1000000 / (ad.sample_rate > 0 ? ad.sample_rate : 48000);
        t->sink->on_audio_frame(t->sink->user, p + ad.header_len, raw_len, fpts);

        p += ad.frame_len;
        remain -= ad.frame_len;
        frame_idx++;
    }

    e->len = 0;
    e->started = 0;
}

static void feed_es(ts_t* t, es_accum_t* e, int pusi, const uint8_t* payload, int plen, int is_video) {
    if (pusi) {
        if (is_video) flush_video(t); else flush_audio(t);
        e->started = 1;
    }
    if (!e->started) return;
    if (!accum_reserve(e, plen)) { e->len = 0; e->started = 0; return; } /* over cap: drop, resync on next PUSI */
    memcpy(e->buf + e->len, payload, (size_t)plen);
    e->len += plen;
}

static void parse_pat(ts_t* t, const uint8_t* p, int plen) {
    /* Need the pointer field plus the 8-byte section header after it. */
    if (plen < 1 || 1 + p[0] + 8 > plen) return;
    int ptr = p[0];                 /* pointer field */
    const uint8_t* s = p + 1 + ptr;
    int avail = plen - 1 - ptr;
    int section_len = ((s[1] & 0x0F) << 8) | s[2];
    /* section_len is attacker-controlled (12 bits); this demuxer reads a single
     * packet, so clamp it to what is present rather than run off the buffer. */
    int total = 3 + section_len;
    if (total > avail) total = avail;
    int prog_bytes = total - 8 - 4; /* minus 8-byte header, 4-byte CRC */
    for (int i = 0; i + 4 <= prog_bytes; i += 4) {
        const uint8_t* prog = s + 8 + i;
        int program = (prog[0] << 8) | prog[1];
        int pid = ((prog[2] & 0x1F) << 8) | prog[3];
        if (program != 0) { t->pmt_pid = pid; break; } /* first real program */
    }
}

static void parse_pmt(ts_t* t, const uint8_t* p, int plen) {
    if (plen < 1 || 1 + p[0] + 12 > plen) return;
    int ptr = p[0];
    const uint8_t* s = p + 1 + ptr;
    int avail = plen - 1 - ptr;
    int section_len = ((s[1] & 0x0F) << 8) | s[2];
    int prog_info_len = ((s[10] & 0x0F) << 8) | s[11];
    int total = 3 + section_len;
    if (total > avail) total = avail; /* clamp: section_len is untrusted */
    int es_end = total - 4;           /* up to CRC */
    for (int i = 12 + prog_info_len; i + 5 <= es_end; ) {
        const uint8_t* es = s + i;
        int stype = es[0];
        int pid = ((es[1] & 0x1F) << 8) | es[2];
        int eslen = ((es[3] & 0x0F) << 8) | es[4];
        if ((stype == 0x1B) && t->video_pid < 0) { t->video_pid = pid; t->video_codec = BASIS_CODEC_H264; t->v.pid = pid; }
        else if ((stype == 0x24) && t->video_pid < 0) { t->video_pid = pid; t->video_codec = BASIS_CODEC_H265; t->v.pid = pid; }
        else if ((stype == 0x0F || stype == 0x11) && t->audio_pid < 0) { t->audio_pid = pid; t->audio_codec = BASIS_CODEC_AAC; t->a.pid = pid; }
        else if (stype == 0x80 && t->audio_pid < 0) { t->audio_pid = pid; t->audio_codec = BASIS_CODEC_LPCM; t->a.pid = pid; }
        i += 5 + eslen;
    }
}

static void handle_packet(ts_t* t, const uint8_t* pkt) {
    if (pkt[0] != 0x47) return;
    int pusi = (pkt[1] >> 6) & 0x1;
    int pid = ((pkt[1] & 0x1F) << 8) | pkt[2];
    int afc = (pkt[3] >> 4) & 0x3;
    int has_payload = afc & 0x1;
    if (!has_payload) return;

    int off = 4;
    if (afc & 0x2) { int al = pkt[4]; off = 5 + al; }
    if (off >= TS_PKT) return;
    const uint8_t* payload = pkt + off;
    int plen = TS_PKT - off;

    if (pid == 0) { parse_pat(t, payload, plen); return; }
    if (pid == t->pmt_pid && t->pmt_pid > 0) { parse_pmt(t, payload, plen); return; }
    if (pid == t->video_pid && t->video_pid > 0) { feed_es(t, &t->v, pusi, payload, plen, 1); return; }
    if (pid == t->audio_pid && t->audio_pid > 0) { feed_es(t, &t->a, pusi, payload, plen, 0); return; }
}

int basis_ts_run(basis_media_sink_t* sink, basis_read_fn read, void* ctx) {
    ts_t t;
    memset(&t, 0, sizeof(t));
    t.sink = sink; t.read = read; t.ctx = ctx;
    t.pmt_pid = -1; t.video_pid = -1; t.audio_pid = -1;

    uint8_t rb[TS_PKT * 16];
    int rb_len = 0;

    while (sink->is_running(sink->user)) {
        int space = (int)sizeof(rb) - rb_len;
        if (space <= 0) { rb_len = 0; space = (int)sizeof(rb); } /* desync guard */
        int n = read(ctx, rb + rb_len, space);
        if (n == BASIS_READ_REPOSITION) {
            /* The source seeked. Drop the partial packet buffer and both PES
             * accumulations so the next emitted AU is a post-seek one; a stale
             * pre-seek AU would otherwise re-anchor pacing to the old timeline.
             * The learned packet stride and announced formats stay valid across
             * the same rendition. Take the seek to re-anchor the pace clock on
             * this (demux) thread, atomically with this flush. */
            rb_len = 0;
            t.v.len = 0; t.v.started = 0;
            t.a.len = 0; t.a.started = 0;
            if (sink->take_seek) { int64_t discard; sink->take_seek(sink->user, &discard); }
            continue;
        }
        if (n <= 0) break;
        rb_len += n;

        int i = 0;
        for (;;) {
            /* Stride detection needs two packets of lookahead beyond the sync
             * byte; once locked, one packet at a time. */
            int need = t.pkt_size ? t.pkt_size : 2 * 192 + 1;
            if (rb_len - i < need) break;
            if (rb[i] != 0x47) { i++; continue; }            /* resync */
            if (!t.pkt_size) {
                if (rb[i + 188] == 0x47 && rb[i + 376] == 0x47) t.pkt_size = 188;
                else if (rb[i + 192] == 0x47 && rb[i + 384] == 0x47) t.pkt_size = 192; /* m2ts */
                else { i++; continue; }
            }
            handle_packet(&t, rb + i);
            i += t.pkt_size;
        }
        /* keep the tail */
        if (i > 0) { memmove(rb, rb + i, (size_t)(rb_len - i)); rb_len -= i; }
    }

    flush_video(&t);
    flush_audio(&t);
    free(t.v.buf);
    free(t.a.buf);
    return 0;
}
