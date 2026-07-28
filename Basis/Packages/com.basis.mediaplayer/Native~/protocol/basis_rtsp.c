/*
 * basis_rtsp.c — RTSP client with negotiated transport: rtsp:// attempts UDP
 * (Transport: RTP/AVP;unicast;client_port=N-N+1) and falls back to RTP
 * interleaved over the TCP control channel on refusal, socket error, or a
 * no-data timer; rtspt:// pins the TCP-interleaved transport and never
 * probes UDP.
 *
 * Flow: DESCRIBE (parse SDP) -> SETUP (per media) -> PLAY, then read RTP.
 * Video is depacketized (H.264 single/STAP-A/FU-A, H.265 single/AP/FU) into
 * Annex B access units; AAC (MPEG4-GENERIC) into raw frames. UDP sessions add
 * a small reorder hold-back, sequence-gap access-unit drops, RTCP receiver
 * reports, and GET_PARAMETER keepalive; a host that fails UDP is remembered
 * for a few minutes so later loads go straight to TCP.
 *
 * Scope notes: Basic auth only (no Digest); one video + one audio media;
 * unicast only (multicast SDPs take the TCP path). These cover VRCDN-style
 * public endpoints; Digest/auth-heavy servers need a follow-up.
 */

#include "basis_rtsp.h"
#include "basis_io.h"
#include "basis_bitstream.h"

#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <ctype.h>

#if defined(_WIN32)
  #define strncasecmp _strnicmp
  #define WIN32_LEAN_AND_MEAN
  #include <windows.h>
#else
  #include <strings.h>
  #include <time.h>
#endif

/* UDP transport tuning. The no-data deadlines are deliberately snappy: a
 * false fallback lands on TCP-interleaved, which works wherever UDP does, so
 * over-triggering costs nothing observable while under-triggering stalls the
 * user. */
#define RTSP_UDP_START_TIMEOUT_MS 3000   /* PLAY ok but nothing at all yet */
#define RTSP_UDP_MEDIA_TIMEOUT_MS 15000  /* RTCP alive but RTP never arrives (asymmetric
                                          * filtering; also covers long-GOP servers that
                                          * hold media until the next keyframe) */
#define RTSP_UDP_STALL_TIMEOUT_MS 5000   /* media stopped mid-play (RTP only — RTCP
                                          * flowing without media is not playback) */
#define RTSP_UDP_RR_INTERVAL_MS   5000   /* RTCP receiver-report cadence */
#define RTSP_UDP_NEG_TTL_MS       (10 * 60 * 1000) /* per-host "UDP failed" memory */
#define RTSP_REORDER_SLOTS        16     /* held-back out-of-order packets per track */
#define RTSP_REORDER_HOLD_MS      40     /* how long a hole may stall delivery */
#define RTSP_REORDER_PKT_MAX      4096   /* one UDP RTP datagram (MTU-bound) */

/* ---- base64 ------------------------------------------------------------- */

static int b64dec(const char* in, int inlen, uint8_t* out, int outcap) {
    int8_t tab[256];
    for (int i = 0; i < 256; ++i) tab[i] = -1;
    const char* a = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    for (int i = 0; i < 64; ++i) tab[(unsigned char)a[i]] = (int8_t)i;

    int val = 0, bits = 0, op = 0;
    for (int i = 0; i < inlen; ++i) {
        int c = (unsigned char)in[i];
        if (c == '=' || tab[c] < 0) continue;
        val = (val << 6) | tab[c];
        bits += 6;
        if (bits >= 8) {
            bits -= 8;
            if (op < outcap) out[op++] = (uint8_t)((val >> bits) & 0xFF);
        }
    }
    return op;
}

/* ---- RTSP request/response --------------------------------------------- */

typedef struct {
    basis_io_t* io;
    int cseq;
    char session[128];
    char authb64[344];   /* Basic auth value, or empty. Sized for base64 of the
                          * longest user:pass the URL can carry (up[256] below):
                          * 4*ceil(255/3) = 340 chars + NUL. */
    char last_status[160]; /* last response status line, for diagnostics */
    char www_auth[256];    /* last WWW-Authenticate header value, if any */
    char location[1024];   /* last Location header (redirects) */
    char rtp_info[1024];   /* last RTP-Info header (PLAY: per-track rtptime) */
    char transport[512];   /* last Transport header (SETUP: server_port/source) */
    int  sess_timeout_s;   /* Session header timeout= (0 = server sent none) */
} rtsp_t;

static int rtsp_send(rtsp_t* r, const char* method, const char* url, const char* extra) {
    char req[2048];
    int n = snprintf(req, sizeof(req),
        "%s %s RTSP/1.0\r\nCSeq: %d\r\nUser-Agent: BasisMediaPlayer/1.0\r\n",
        method, url, ++r->cseq);
    if (r->session[0]) n += snprintf(req + n, sizeof(req) - n, "Session: %s\r\n", r->session);
    if (r->authb64[0]) n += snprintf(req + n, sizeof(req) - n, "Authorization: Basic %s\r\n", r->authb64);
    if (extra) n += snprintf(req + n, sizeof(req) - n, "%s", extra);
    n += snprintf(req + n, sizeof(req) - n, "\r\n");
    return basis_io_write_full(r->io, (const uint8_t*)req, n) == n ? 0 : -1;
}

/* Reads an RTSP response: status + headers, then body by Content-Length.
 * Returns status code, copies body (<=bodycap) and Session id if present. */
static int rtsp_recv(rtsp_t* r, char* body, int bodycap, int* bodylen) {
    char line[1024];
    int code = -1, content_len = 0;
    int li = 0;
    r->last_status[0] = 0;
    r->www_auth[0] = 0;
    r->location[0] = 0;
    r->transport[0] = 0;
    /* header lines until blank */
    for (;;) {
        li = 0;
        for (;;) {
            uint8_t c;
            if (basis_io_read_full(r->io, &c, 1) != 1) return -1;
            if (c == '\n') break;
            if (c != '\r' && li < (int)sizeof(line) - 1) line[li++] = (char)c;
        }
        line[li] = 0;
        if (li == 0) break; /* end of headers */
        if (code < 0 && strncmp(line, "RTSP/1.0", 8) == 0) {
            code = atoi(line + 9);
            strncpy(r->last_status, line, sizeof(r->last_status) - 1);
            r->last_status[sizeof(r->last_status) - 1] = 0;
        }
        else if (strncasecmp(line, "Content-Length:", 15) == 0) content_len = atoi(line + 15);
        else if (strncasecmp(line, "WWW-Authenticate:", 17) == 0) {
            const char* v = line + 17; while (*v == ' ') v++;
            strncpy(r->www_auth, v, sizeof(r->www_auth) - 1);
            r->www_auth[sizeof(r->www_auth) - 1] = 0;
        }
        else if (strncasecmp(line, "Location:", 9) == 0) {
            const char* v = line + 9; while (*v == ' ') v++;
            strncpy(r->location, v, sizeof(r->location) - 1);
            r->location[sizeof(r->location) - 1] = 0;
        }
        else if (strncasecmp(line, "Session:", 8) == 0) {
            const char* s = line + 8; while (*s == ' ') s++;
            int j = 0; while (s[j] && s[j] != ';' && j < (int)sizeof(r->session) - 1) { r->session[j] = s[j]; j++; }
            r->session[j] = 0;
            const char* to = strstr(s, "timeout=");
            if (to) { int t = atoi(to + 8); if (t > 0) r->sess_timeout_s = t; }
        }
        else if (strncasecmp(line, "Transport:", 10) == 0) {
            const char* v = line + 10; while (*v == ' ') v++;
            strncpy(r->transport, v, sizeof(r->transport) - 1);
            r->transport[sizeof(r->transport) - 1] = 0;
        }
        else if (strncasecmp(line, "RTP-Info:", 9) == 0) {
            const char* v = line + 9; while (*v == ' ') v++;
            strncpy(r->rtp_info, v, sizeof(r->rtp_info) - 1);
            r->rtp_info[sizeof(r->rtp_info) - 1] = 0;
        }
    }
    if (bodylen) *bodylen = 0;
    if (content_len > 0) {
        int want = body ? (content_len < bodycap ? content_len : bodycap) : 0;
        int got = want > 0 ? basis_io_read_full(r->io, (uint8_t*)body, want) : 0;
        if (bodylen) *bodylen = got;
        /* drain whatever the caller's buffer didn't take, so an ignored or
         * oversized body can't desynchronise the next reply on the socket */
        for (int rest = content_len - got; rest > 0; ) {
            uint8_t tmp[256]; int t = rest < (int)sizeof(tmp) ? rest : (int)sizeof(tmp);
            if (basis_io_read_full(r->io, tmp, t) != t) break;
            rest -= t;
        }
    }
    return code;
}

/* ---- SDP parse --------------------------------------------------------- */

typedef struct {
    basis_codec_t codec;
    int pt;            /* RTP payload type */
    int clock;         /* RTP clock rate */
    int channels;      /* audio */
    char control[512]; /* a=control */
    uint8_t extradata[1024];
    int extradata_len;
    uint8_t asc[16];
    int asc_len;
} sdp_media_t;

static void append_param_set_b64(sdp_media_t* m, const char* b64, int len) {
    static const uint8_t sc[4] = {0,0,0,1};
    uint8_t tmp[512];
    int n = b64dec(b64, len, tmp, sizeof(tmp));
    if (n <= 0) return;
    if (m->extradata_len + 4 + n > (int)sizeof(m->extradata)) return;
    memcpy(m->extradata + m->extradata_len, sc, 4); m->extradata_len += 4;
    memcpy(m->extradata + m->extradata_len, tmp, n); m->extradata_len += n;
}

static int hexval(int c){ if(c>='0'&&c<='9')return c-'0'; c|=32; if(c>='a'&&c<='f')return c-'a'+10; return -1; }

static void parse_fmtp(sdp_media_t* m, const char* fmtp) {
    /* H.264: sprop-parameter-sets=<sps_b64>,<pps_b64> */
    const char* sp = strstr(fmtp, "sprop-parameter-sets=");
    if (sp) {
        sp += 21;
        const char* comma = strchr(sp, ',');
        const char* end = sp; while (*end && *end != ';' && *end != '\r' && *end != '\n') end++;
        if (comma && comma < end) {
            append_param_set_b64(m, sp, (int)(comma - sp));
            append_param_set_b64(m, comma + 1, (int)(end - (comma + 1)));
        } else {
            append_param_set_b64(m, sp, (int)(end - sp));
        }
    }
    /* H.265: sprop-vps / sprop-sps / sprop-pps */
    const char* tags[3] = { "sprop-vps=", "sprop-sps=", "sprop-pps=" };
    for (int i = 0; i < 3; ++i) {
        const char* t = strstr(fmtp, tags[i]);
        if (t) { t += strlen(tags[i]); const char* end = t; while (*end && *end != ';' && *end != '\r' && *end != '\n') end++; append_param_set_b64(m, t, (int)(end - t)); }
    }
    /* AAC MPEG4-GENERIC: config=<hex ASC> */
    const char* cfg = strstr(fmtp, "config=");
    if (cfg) {
        cfg += 7; int n = 0;
        while (cfg[0] && cfg[1] && cfg[0] != ';' && n < (int)sizeof(m->asc)) {
            int hi = hexval(cfg[0]), lo = hexval(cfg[1]);
            if (hi < 0 || lo < 0) break;
            m->asc[n++] = (uint8_t)((hi << 4) | lo); cfg += 2;
        }
        m->asc_len = n;
    }
}

/* Parses SDP into up to one video + one audio media. Returns count. */
static int parse_sdp(const char* sdp, int len, sdp_media_t* video, sdp_media_t* audio) {
    int have_v = 0, have_a = 0;
    sdp_media_t* cur = NULL;
    const char* p = sdp; const char* end = sdp + len;
    char line[1024];
    while (p < end) {
        int li = 0;
        while (p < end && *p != '\n' && li < (int)sizeof(line) - 1) { if (*p != '\r') line[li++] = *p; p++; }
        if (p < end) p++;
        line[li] = 0;
        if (line[0] == 'm' && line[1] == '=') {
            if (strncmp(line + 2, "video", 5) == 0 && !have_v) { cur = video; memset(cur, 0, sizeof(*cur)); cur->codec = BASIS_CODEC_H264; cur->clock = 90000; have_v = 1; sscanf(line + 2, "video %*d %*s %d", &cur->pt); }
            else if (strncmp(line + 2, "audio", 5) == 0 && !have_a) { cur = audio; memset(cur, 0, sizeof(*cur)); cur->codec = BASIS_CODEC_AAC; cur->clock = 48000; cur->channels = 2; have_a = 1; sscanf(line + 2, "audio %*d %*s %d", &cur->pt); }
            else cur = NULL;
        } else if (cur && line[0] == 'a' && line[1] == '=') {
            const char* a = line + 2;
            if (strncmp(a, "rtpmap:", 7) == 0) {
                int pt = 0; char name[64] = {0}; int clk = 0, ch = 0;
                sscanf(a + 7, "%d %63[^/]/%d/%d", &pt, name, &clk, &ch);
                if (clk) cur->clock = clk;
                if (ch) cur->channels = ch;
                if (strncasecmp(name, "H265", 4) == 0 || strncasecmp(name, "HEVC", 4) == 0) cur->codec = BASIS_CODEC_H265;
                else if (strncasecmp(name, "H264", 4) == 0) cur->codec = BASIS_CODEC_H264;
                else if (strncasecmp(name, "mpeg4-generic", 13) == 0 || strncasecmp(name, "MP4A", 4) == 0) cur->codec = BASIS_CODEC_AAC;
            } else if (strncmp(a, "fmtp:", 5) == 0) {
                parse_fmtp(cur, a + 5);
            } else if (strncmp(a, "control:", 8) == 0) {
                strncpy(cur->control, a + 8, sizeof(cur->control) - 1);
            }
        }
    }
    return (have_v ? 1 : 0) + (have_a ? 1 : 0);
}

static void build_control_url(const char* base, const char* control, char* out, int outcap) {
    if (!control[0] || strcmp(control, "*") == 0) { snprintf(out, outcap, "%s", base); return; }
    if (strncmp(control, "rtsp://", 7) == 0) { snprintf(out, outcap, "%s", control); return; }
    if (control[0] == '/') snprintf(out, outcap, "%s%s", base, control);
    else snprintf(out, outcap, "%s/%s", base, control);
}

/* ---- RTP depacketization ----------------------------------------------- */

typedef struct {
    basis_media_sink_t* sink;
    sdp_media_t* video;
    sdp_media_t* audio;
    int v_channel, a_channel;

    uint8_t* au; int au_len, au_cap;     /* current video access unit (Annex B) */
    int64_t au_ts;                       /* extended RTP ts of current AU */
    int have_au_ts;
    int video_announced;
    int audio_announced;

    /* Shared-timeline PTS. Each RTP stream starts at a random timestamp
     * (RFC 3550), so raw per-track timestamps are unrelated; RTP-Info's
     * rtptime is each track's timestamp at the shared PLAY point, and
     * subtracting it puts both tracks on one timeline (audio release is
     * PTS-gated against the video presentation clock, so the tracks MUST
     * agree on a base). Without RTP-Info a track zero-bases at its first
     * packet. The extended counters unwrap the 32-bit timestamp (~13h at
     * 90kHz). */
    int64_t v_base, a_base; int have_v_base, have_a_base;
    int64_t v_ext, a_ext;   int have_v_ext, have_a_ext;

    /* AAC AU fragment reassembly (RFC 3640): an AU larger than the RTP
     * payload arrives as several packets — high-bitrate multichannel AAC
     * exceeds the ~1440-byte payload routinely (a 384kbps 5.1 frame averages
     * ~1.5KB), so without reassembly most frames of such a stream arrive
     * truncated and decode as noise. Reassembly keys on the RTP marker bit
     * (0 = a slice, 1 = the slice completing the AU): senders disagree on
     * whether a fragment's AU-header carries the full AU size (the RFC) or
     * the slice size (gortsplib/mediamtx), so the header size can't signal
     * fragmentation, but the marker semantics are common to both. Fragments
     * of one AU share an RTP timestamp. */
    uint8_t* afrag; int afrag_len, afrag_cap;
    int afrag_active;      /* a reassembly is in flight */
    int afrag_drop;        /* frame discarded (over cap): skip its remaining packets */
    int64_t afrag_rel;     /* base-relative extended ts of the AU */

    uint8_t* fu; int fu_len, fu_cap;     /* FU reassembly */
    int fu_active;
    uint8_t fu_nal_header0, fu_nal_header1; /* reconstructed NAL header (1 byte h264 / 2 bytes h265) */
    int fu_is_h265;

    /* UDP loss handling: a sequence gap taints the access unit under
     * assembly — it's discarded at its boundary instead of delivered with
     * missing slices. */
    int v_drop;
} depkt_t;

static const uint8_t SC4[4] = {0,0,0,1};

/* Per-AU / per-fragment reassembly ceiling. A server that never sets the RTP
 * marker (or streams FU/afrag forever) would otherwise grow these without bound;
 * a real assembled frame stays well under this. */
#define RTP_MAX_BUF (16 * 1024 * 1024)

static int grow(uint8_t** b, int* cap, int need, int max) {
    if (need <= *cap) return 1;
    if (need < 0 || need > max) return 0;    /* refuse a hostile / overflowed target */
    int64_t nc = *cap ? *cap : 65536;
    while (nc < need) nc *= 2;
    if (nc > max) nc = max;
    uint8_t* nb = (uint8_t*)realloc(*b, (size_t)nc);
    if (!nb) return 0;
    *b = nb; *cap = (int)nc; return 1;
}

static void au_append_nal(depkt_t* d, const uint8_t* nal, int len) {
    /* Over the cap: mark the whole AU dropped so deliver_au discards it rather
     * than hand the decoder a partial NAL missing this slice. */
    if (!grow(&d->au, &d->au_cap, d->au_len + 4 + len, RTP_MAX_BUF)) { d->v_drop = 1; return; }
    memcpy(d->au + d->au_len, SC4, 4); d->au_len += 4;
    memcpy(d->au + d->au_len, nal, len); d->au_len += len;
}

static int64_t rtp_ts_to_us(int64_t ts, int clock) { return clock > 0 ? ts * 1000000 / clock : ts; }

/* True when one URL is a full suffix of the other (handles relative vs
 * absolute control URLs without prefix-collision false positives, e.g.
 * trackID=1 vs trackID=11). */
static int url_suffix_match(const char* a, const char* b) {
    size_t la = strlen(a), lb = strlen(b);
    if (!la || !lb) return 0;
    const char* lo = (la >= lb) ? a : b;
    size_t      ll = (la >= lb) ? la : lb;
    const char* sh = (la >= lb) ? b : a;
    size_t      sl = (la >= lb) ? lb : la;
    return strcmp(lo + (ll - sl), sh) == 0;
}

/* Extends a 32-bit RTP timestamp into the track's running 64-bit counter.
 * The first packet anchors near the base (RTP-Info rtptime when the server
 * sent one, else itself); after that each packet moves the counter by the
 * signed 32-bit delta, which survives wrap and tolerates reordering. */
static int64_t rtp_ts_extend(uint32_t ts, int64_t* ext, int* have_ext,
                             int64_t* base, int* have_base) {
    if (!*have_ext) {
        if (!*have_base) { *base = (int64_t)ts; *have_base = 1; }
        *ext = *base + (int32_t)(ts - (uint32_t)*base);
        *have_ext = 1;
    } else {
        *ext += (int32_t)(ts - (uint32_t)*ext);
    }
    return *ext - *base;
}

static void deliver_au(depkt_t* d) {
    if (d->v_drop) { d->au_len = 0; d->v_drop = 0; return; }
    if (d->au_len <= 0) return;
    if (!d->video_announced) {
        int w = 0, h = 0;
        if (d->video->codec == BASIS_CODEC_H264 && d->video->extradata_len > 0) {
            int pos = 0, no, nl;
            while ((pos = basis_annexb_next(d->video->extradata, d->video->extradata_len, pos, &no, &nl)) >= 0)
                if (nl > 0 && basis_h264_nal_type(d->video->extradata[no]) == 7) { basis_h264_sps_dimensions(d->video->extradata + no, nl, &w, &h); break; }
        }
        d->sink->on_video_format(d->sink->user, d->video->codec, d->video->extradata, d->video->extradata_len, w, h);
        d->video_announced = 1;
    }
    int key = d->video->codec == BASIS_CODEC_H265 ? basis_h265_is_keyframe(d->au, d->au_len)
                                                  : basis_h264_is_keyframe(d->au, d->au_len);
    int64_t pts = rtp_ts_to_us(d->au_ts - d->v_base, d->video->clock);
    d->sink->on_video_au(d->sink->user, d->au, d->au_len, pts, pts, key);
    d->au_len = 0;
}

static void depkt_video(depkt_t* d, const uint8_t* rtp, int len) {
    if (len < 12) return;
    int cc = rtp[0] & 0x0F;
    int marker = (rtp[1] >> 7) & 1;
    uint32_t ts = ((uint32_t)rtp[4] << 24) | (rtp[5] << 16) | (rtp[6] << 8) | rtp[7];
    int hdr = 12 + cc * 4;
    if ((rtp[0] & 0x10)) { /* extension */
        if (len < hdr + 4) return;
        int extlen = (rtp[hdr + 2] << 8) | rtp[hdr + 3];
        hdr += 4 + extlen * 4;
    }
    if (hdr >= len) return;
    const uint8_t* p = rtp + hdr;
    int plen = len - hdr;

    rtp_ts_extend(ts, &d->v_ext, &d->have_v_ext, &d->v_base, &d->have_v_base);
    if (d->have_au_ts && d->v_ext != d->au_ts) { deliver_au(d); }
    d->au_ts = d->v_ext; d->have_au_ts = 1;

    int is_h265 = d->video->codec == BASIS_CODEC_H265;
    if (!is_h265) {
        int nt = p[0] & 0x1F;
        if (nt >= 1 && nt <= 23) {                 /* single NAL */
            au_append_nal(d, p, plen);
        } else if (nt == 24) {                      /* STAP-A */
            int i = 1;
            while (i + 2 <= plen) {
                int nsz = (p[i] << 8) | p[i + 1]; i += 2;
                if (i + nsz > plen) break;
                au_append_nal(d, p + i, nsz); i += nsz;
            }
        } else if (nt == 28) {                      /* FU-A */
            if (plen < 2) return;
            int s = (p[1] >> 7) & 1, e = (p[1] >> 6) & 1;
            int otype = p[1] & 0x1F;
            if (s) { d->fu_active = 1; d->fu_len = 0; d->fu_nal_header0 = (uint8_t)((p[0] & 0xE0) | otype); }
            if (d->fu_active) {
                if (grow(&d->fu, &d->fu_cap, d->fu_len + (plen - 2), RTP_MAX_BUF)) {
                    memcpy(d->fu + d->fu_len, p + 2, plen - 2); d->fu_len += plen - 2;
                } else { d->v_drop = 1; d->fu_active = 0; }   /* over cap: drop the AU */
            }
            if (e && d->fu_active) {
                if (grow(&d->au, &d->au_cap, d->au_len + 4 + 1 + d->fu_len, RTP_MAX_BUF)) {
                    memcpy(d->au + d->au_len, SC4, 4); d->au_len += 4;
                    d->au[d->au_len++] = d->fu_nal_header0;
                    memcpy(d->au + d->au_len, d->fu, d->fu_len); d->au_len += d->fu_len;
                } else { d->v_drop = 1; }
                d->fu_active = 0;
            }
        }
    } else {
        int nt = (p[0] >> 1) & 0x3F;
        if (nt <= 47) {                              /* single NAL (HEVC) */
            au_append_nal(d, p, plen);
        } else if (nt == 48) {                       /* AP (aggregation) */
            int i = 2;
            while (i + 2 <= plen) {
                int nsz = (p[i] << 8) | p[i + 1]; i += 2;
                if (i + nsz > plen) break;
                au_append_nal(d, p + i, nsz); i += nsz;
            }
        } else if (nt == 49) {                        /* FU (HEVC) */
            if (plen < 3) return;
            int s = (p[2] >> 7) & 1, e = (p[2] >> 6) & 1;
            int otype = p[2] & 0x3F;
            if (s) {
                d->fu_active = 1; d->fu_len = 0;
                d->fu_nal_header0 = (uint8_t)((p[0] & 0x81) | (otype << 1));
                d->fu_nal_header1 = p[1];
            }
            if (d->fu_active) {
                if (grow(&d->fu, &d->fu_cap, d->fu_len + (plen - 3), RTP_MAX_BUF)) {
                    memcpy(d->fu + d->fu_len, p + 3, plen - 3); d->fu_len += plen - 3;
                } else { d->v_drop = 1; d->fu_active = 0; }   /* over cap: drop the AU */
            }
            if (e && d->fu_active) {
                if (grow(&d->au, &d->au_cap, d->au_len + 4 + 2 + d->fu_len, RTP_MAX_BUF)) {
                    memcpy(d->au + d->au_len, SC4, 4); d->au_len += 4;
                    d->au[d->au_len++] = d->fu_nal_header0;
                    d->au[d->au_len++] = d->fu_nal_header1;
                    memcpy(d->au + d->au_len, d->fu, d->fu_len); d->au_len += d->fu_len;
                } else { d->v_drop = 1; }
                d->fu_active = 0;
            }
        }
    }

    if (marker) deliver_au(d);
}

static void depkt_audio(depkt_t* d, const uint8_t* rtp, int len) {
    if (len < 12 || !d->audio) return;
    int cc = rtp[0] & 0x0F;
    uint32_t ts = ((uint32_t)rtp[4] << 24) | (rtp[5] << 16) | (rtp[6] << 8) | rtp[7];
    int hdr = 12 + cc * 4;
    if (hdr >= len) return;
    const uint8_t* p = rtp + hdr; int plen = len - hdr;

    /* MPEG4-GENERIC: 2-byte AU-headers-length (bits), then 16-bit AU-headers
     * (13-bit size + 3-bit index), then AU data. */
    if (plen < 2) return;
    int au_headers_bits = (p[0] << 8) | p[1];
    int num = au_headers_bits / 16;
    int dpos = 2 + num * 2;
    if (num <= 0 || dpos > plen) { /* assume single AU, no header */ num = 1; dpos = 0; }

    if (!d->audio_announced) {
        int sr = d->audio->clock ? d->audio->clock : 48000;
        int ch = d->audio->channels ? d->audio->channels : 2;
        d->sink->on_audio_format(d->sink->user, BASIS_CODEC_AAC, sr, ch,
                                 d->audio->asc_len ? d->audio->asc : NULL, d->audio->asc_len);
        d->audio_announced = 1;
    }

    int64_t rel = rtp_ts_extend(ts, &d->a_ext, &d->have_a_ext, &d->a_base, &d->have_a_base);

    /* A different timestamp while a reassembly is in flight means the tail
     * fragments were lost — drop the partial AU rather than deliver a
     * truncated frame. */
    if (d->afrag_active && rel != d->afrag_rel) { d->afrag_active = 0; d->afrag_len = 0; }

    int marker = (rtp[1] >> 7) & 1;
    /* A frame discarded over the cap keeps dropping its remaining packets (same
     * timestamp) so the marker tail isn't emitted as a truncated standalone AU.
     * Cleared by the marker (frame end) or a new timestamp. */
    if (d->afrag_drop) {
        if (rel != d->afrag_rel) d->afrag_drop = 0;
        else { if (marker) d->afrag_drop = 0; return; }
    }
    if (!marker || d->afrag_active) {
        /* A slice of a fragmented AU (fragments never aggregate: a marker=0
         * packet is always one slice). Accumulate; the marker=1 slice
         * completes the AU. */
        int avail = plen - dpos;
        if (avail <= 0) return;
        if (!d->afrag_active) { d->afrag_active = 1; d->afrag_len = 0; d->afrag_rel = rel; }
        if (grow(&d->afrag, &d->afrag_cap, d->afrag_len + avail, RTP_MAX_BUF)) {
            memcpy(d->afrag + d->afrag_len, p + dpos, avail);
            d->afrag_len += avail;
        } else {
            /* over cap: drop the frame and latch so its tail (incl. the marker
             * packet) isn't emitted as a partial. */
            d->afrag_active = 0; d->afrag_len = 0; d->afrag_drop = !marker; return;
        }
        if (marker && d->afrag_len > 0) {
            int64_t pts = rtp_ts_to_us(d->afrag_rel, d->audio->clock ? d->audio->clock : 48000);
            d->sink->on_audio_frame(d->sink->user, d->afrag, d->afrag_len, pts);
            d->afrag_active = 0; d->afrag_len = 0;
        }
        return;
    }

    int off = dpos;
    for (int i = 0; i < num; ++i) {
        int sz;
        if (dpos == 0) sz = plen; /* whole payload */
        else sz = ((p[2 + i * 2] << 8) | p[2 + i * 2 + 1]) >> 3;
        if (off + sz > plen) sz = plen - off;
        if (sz <= 0) break;
        /* The packet timestamp covers its first AU; each further aggregated
         * AU advances by one AAC frame (1024 samples; the RTP clock is the
         * sample rate). */
        int64_t pts = rtp_ts_to_us(rel + (int64_t)i * 1024, d->audio->clock ? d->audio->clock : 48000);
        d->sink->on_audio_frame(d->sink->user, p + off, sz, pts);
        off += sz;
    }
}

/* ---- UDP transport helpers ---------------------------------------------- */

static int64_t now_ms(void) {
#if defined(_WIN32)
    return (int64_t)GetTickCount64();
#else
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (int64_t)ts.tv_sec * 1000 + ts.tv_nsec / 1000000;
#endif
}

/* Hosts where UDP recently failed: later loads skip the probe and go straight
 * to TCP, so a blackholed network pays the no-data deadline once per host, not
 * on every URL change. Process-global and unsynchronised by design — a racing
 * writer costs at most one extra or one skipped probe, both benign. */
static struct { char host[256]; int port; int64_t expires; } udp_neg[8];

static int udp_neg_blocked(const char* host, int port) {
    int64_t now = now_ms();
    for (int i = 0; i < 8; ++i)
        if (udp_neg[i].expires > now && udp_neg[i].port == port &&
            strncmp(udp_neg[i].host, host, sizeof(udp_neg[i].host)) == 0)
            return 1;
    return 0;
}

static void udp_neg_add(const char* host, int port) {
    int64_t now = now_ms();
    int slot = 0;
    for (int i = 0; i < 8; ++i) {
        if (udp_neg[i].expires <= now) { slot = i; break; }
        if (udp_neg[i].expires < udp_neg[slot].expires) slot = i;
    }
    strncpy(udp_neg[slot].host, host, sizeof(udp_neg[slot].host) - 1);
    udp_neg[slot].host[sizeof(udp_neg[slot].host) - 1] = 0;
    udp_neg[slot].port = port;
    udp_neg[slot].expires = now + RTSP_UDP_NEG_TTL_MS;
}

/* Small per-track reorder hold-back. UDP delivers out of order on real
 * networks; without a hold-back every swap would read as loss and drop an
 * access unit. Packets ahead of the expected sequence wait in slots until the
 * hole fills, the window overflows, or the hole has stalled delivery for
 * RTSP_REORDER_HOLD_MS — the latter two declare a gap. Anything worse than
 * mild reordering is the TCP fallback's job, so the window stays small. */
typedef void (*rtp_pkt_fn)(void* ctx, const uint8_t* pkt, int len);
typedef void (*rtp_gap_fn)(void* ctx);

typedef struct {
    struct {
        uint16_t seq;
        int used, len;
        int64_t arrived;
        uint8_t buf[RTSP_REORDER_PKT_MAX];
    } slot[RTSP_REORDER_SLOTS];
    uint16_t expected;
    int have_expected;
    int nqueued;
} rtp_reorder_t;

static uint16_t rtp_seq_of(const uint8_t* pkt) { return (uint16_t)((pkt[2] << 8) | pkt[3]); }

/* Delivers every queued packet that is now in order. */
static void reorder_drain(rtp_reorder_t* rb, rtp_pkt_fn deliver, void* ctx) {
    int progressed = 1;
    while (progressed && rb->nqueued > 0) {
        progressed = 0;
        for (int i = 0; i < RTSP_REORDER_SLOTS; ++i) {
            if (rb->slot[i].used && rb->slot[i].seq == rb->expected) {
                deliver(ctx, rb->slot[i].buf, rb->slot[i].len);
                rb->slot[i].used = 0;
                rb->nqueued--;
                rb->expected++;
                progressed = 1;
            }
        }
    }
}

/* Advances past a hole: marks the gap, then resumes from the lowest queued
 * sequence (or `resume` when the queue is empty). */
static void reorder_skip(rtp_reorder_t* rb, uint16_t resume,
                         rtp_pkt_fn deliver, rtp_gap_fn gap, void* ctx) {
    gap(ctx);
    uint16_t lowest = resume;
    int have = 0;
    for (int i = 0; i < RTSP_REORDER_SLOTS; ++i) {
        if (!rb->slot[i].used) continue;
        if (!have || (int16_t)(rb->slot[i].seq - lowest) < 0) { lowest = rb->slot[i].seq; have = 1; }
    }
    rb->expected = lowest;
    reorder_drain(rb, deliver, ctx);
}

static void reorder_push(rtp_reorder_t* rb, const uint8_t* pkt, int len, int64_t now,
                         rtp_pkt_fn deliver, rtp_gap_fn gap, void* ctx) {
    if (len < 12 || len > RTSP_REORDER_PKT_MAX) return;
    uint16_t seq = rtp_seq_of(pkt);
    if (!rb->have_expected) { rb->have_expected = 1; rb->expected = seq; }

    int16_t delta = (int16_t)(seq - rb->expected);
    if (delta < 0) return;                 /* duplicate or too late — drop */
    if (delta == 0) {
        deliver(ctx, pkt, len);
        rb->expected++;
        reorder_drain(rb, deliver, ctx);
        return;
    }
    if (delta > RTSP_REORDER_SLOTS) {      /* hole too wide for the window */
        reorder_skip(rb, seq, deliver, gap, ctx);
        if (seq == rb->expected) { deliver(ctx, pkt, len); rb->expected++; reorder_drain(rb, deliver, ctx); }
        else reorder_push(rb, pkt, len, now, deliver, gap, ctx);
        return;
    }
    int free_i = -1;
    for (int i = 0; i < RTSP_REORDER_SLOTS; ++i) {
        if (rb->slot[i].used) { if (rb->slot[i].seq == seq) return; /* dup */ }
        else if (free_i < 0) free_i = i;
    }
    if (free_i < 0) {
        /* Full window. Unreachable while the invariants hold (16 slots over a
         * 16-value in-window range with duplicates dropped above), but if it
         * ever happens, skip the hole and re-evaluate rather than clobbering a
         * live slot. */
        reorder_skip(rb, seq, deliver, gap, ctx);
        int16_t d2 = (int16_t)(seq - rb->expected);
        if (d2 < 0) return;                        /* now stale — drop */
        if (d2 == 0) { deliver(ctx, pkt, len); rb->expected++; reorder_drain(rb, deliver, ctx); return; }
        for (int i = 0; i < RTSP_REORDER_SLOTS; ++i)
            if (!rb->slot[i].used) { free_i = i; break; }
        if (free_i < 0) return;                    /* window still full — drop */
    }
    rb->slot[free_i].used = 1;
    rb->slot[free_i].seq = seq;
    rb->slot[free_i].len = len;
    rb->slot[free_i].arrived = now;
    memcpy(rb->slot[free_i].buf, pkt, (size_t)len);
    rb->nqueued++;
}

/* Called on the poll tick: a hole that has stalled queued packets past the
 * hold window is declared lost. */
static void reorder_tick(rtp_reorder_t* rb, int64_t now,
                         rtp_pkt_fn deliver, rtp_gap_fn gap, void* ctx) {
    if (rb->nqueued == 0) return;
    int64_t oldest = now;
    for (int i = 0; i < RTSP_REORDER_SLOTS; ++i)
        if (rb->slot[i].used && rb->slot[i].arrived < oldest) oldest = rb->slot[i].arrived;
    if (now - oldest >= RTSP_REORDER_HOLD_MS)
        reorder_skip(rb, rb->expected, deliver, gap, ctx);
}

/* Minimal RTCP receiver report (no report blocks): keeps servers that expect
 * receiver liveness from tearing the session down, and doubles as the NAT
 * keepalive on the RTCP pinhole. */
static void send_rtcp_rr(basis_io_t* io) {
    static const uint8_t rr[8] = { 0x80, 0xC9, 0x00, 0x01, 'B', 'A', 'S', 'I' };
    if (io) basis_io_send(io, rr, (int)sizeof(rr));
}

/* Opens the client-side NAT pinholes before the server starts sending: a
 * throwaway datagram on the RTP port (version bits invalid, receivers drop
 * it) and a receiver report on the RTCP port. */
static void hole_punch(basis_io_t* rtp, basis_io_t* rtcp) {
    static const uint8_t nul[4] = { 0, 0, 0, 0 };
    for (int i = 0; i < 2; ++i) {
        if (rtp) basis_io_send(rtp, nul, (int)sizeof(nul));
        send_rtcp_rr(rtcp);
    }
}

/* Parses server_port=a-b and source=<host> from a SETUP Transport header. */
static int parse_transport_udp(const char* t, int* rtp_port, int* rtcp_port,
                               char* source, int source_cap) {
    if (source_cap > 0) source[0] = 0;
    if (!t || strstr(t, "multicast")) return -1;   /* unicast only */
    const char* sp = strstr(t, "server_port=");
    if (!sp) return -1;
    int a = 0, b = 0;
    if (sscanf(sp + 12, "%d-%d", &a, &b) < 1 || a <= 0) return -1;
    *rtp_port = a;
    *rtcp_port = b > 0 ? b : a + 1;
    const char* src = strstr(t, "source=");
    if (src) {
        src += 7;
        int j = 0;
        while (src[j] && src[j] != ';' && src[j] != ' ' && j < source_cap - 1) { source[j] = src[j]; j++; }
        if (source_cap > 0) source[j] = 0;
    }
    return 0;
}

/* True when the SDP pins media to a multicast group (c= line, 224-239). */
static int sdp_is_multicast(const char* sdp) {
    const char* c = sdp;
    while ((c = strstr(c, "c=IN IP4 ")) != NULL) {
        int o = atoi(c + 9);
        if (o >= 224 && o <= 239) return 1;
        c += 9;
    }
    return 0;
}

/* Everything one UDP media session owns beyond the control connection. */
typedef struct {
    basis_io_t *v_rtp, *v_rtcp, *a_rtp, *a_rtcp;
    rtp_reorder_t *v_rb, *a_rb;
} udp_state_t;

static void udp_state_close(udp_state_t* u) {
    if (u->v_rtp)  basis_io_close(u->v_rtp);
    if (u->v_rtcp) basis_io_close(u->v_rtcp);
    if (u->a_rtp)  basis_io_close(u->a_rtp);
    if (u->a_rtcp) basis_io_close(u->a_rtcp);
    free(u->v_rb);
    free(u->a_rb);
    memset(u, 0, sizeof(*u));
}

/* Reorder-buffer callbacks: deliver feeds the depacketizers unchanged; a gap
 * taints whatever is mid-assembly so it dies at its boundary instead of
 * reaching the decoder truncated. */
static void udp_deliver_video(void* ctx, const uint8_t* pkt, int len) { depkt_video((depkt_t*)ctx, pkt, len); }
static void udp_deliver_audio(void* ctx, const uint8_t* pkt, int len) { depkt_audio((depkt_t*)ctx, pkt, len); }
static void udp_gap_video(void* ctx) {
    depkt_t* d = (depkt_t*)ctx;
    d->fu_active = 0;
    d->v_drop = 1;
}
static void udp_gap_audio(void* ctx) {
    depkt_t* d = (depkt_t*)ctx;
    d->afrag_active = 0;
    d->afrag_len = 0;
}

/* ---- main run ----------------------------------------------------------- */

/* Poll-driven UDP session loop: RTP datagrams reach the depacketizers through
 * the reorder hold-back, RTCP counts as liveness, control-channel bytes are
 * drained (keepalive replies, server notices). Enforces the no-data deadlines
 * from the top of the file. Returns 0 on clean stop, -1 when the control
 * connection ends, 1 to fall back to TCP. */
static int udp_read_loop(rtsp_t* r, depkt_t* d, udp_state_t* u, const char* base_url) {
    int64_t start = now_ms();
    int64_t last_rtp = 0;     /* 0 = no media yet */
    int64_t last_any = 0;     /* RTP or RTCP; 0 = nothing at all yet */
    int64_t last_rr = start;
    int64_t last_ka = start;
    int64_t ka_interval = (int64_t)(r->sess_timeout_s > 0 ? r->sess_timeout_s : 60) * 1000 / 2;
    if (ka_interval < 5000)  ka_interval = 5000;
    if (ka_interval > 30000) ka_interval = 30000;

    uint8_t pkt[RTSP_REORDER_PKT_MAX];

    while (d->sink->is_running(d->sink->user)) {
        basis_io_t* ios[5];
        ios[0] = r->io;
        ios[1] = u->v_rtp;  ios[2] = u->v_rtcp;
        ios[3] = u->a_rtp;  ios[4] = u->a_rtcp;
        int mask = basis_io_poll_read(ios, 5, 100);
        int64_t now = now_ms();
        if (mask < 0) return 1;

        if (mask & (1 << 1)) {
            int n = basis_io_read(u->v_rtp, pkt, (int)sizeof(pkt));
            if (n <= 0) return 1;   /* ICMP refusal / socket error: instant fallback */
            last_rtp = last_any = now;
            reorder_push(u->v_rb, pkt, n, now, udp_deliver_video, udp_gap_video, d);
        }
        if ((mask & (1 << 3)) && u->a_rtp) {
            int n = basis_io_read(u->a_rtp, pkt, (int)sizeof(pkt));
            if (n <= 0) return 1;
            last_rtp = last_any = now;
            reorder_push(u->a_rb, pkt, n, now, udp_deliver_audio, udp_gap_audio, d);
        }
        if (mask & (1 << 2)) {
            int n = basis_io_read(u->v_rtcp, pkt, (int)sizeof(pkt));
            if (n <= 0) return 1;
            last_any = now;   /* sender reports prove the path before media flows */
        }
        if ((mask & (1 << 4)) && u->a_rtcp) {
            int n = basis_io_read(u->a_rtcp, pkt, (int)sizeof(pkt));
            if (n <= 0) return 1;
            last_any = now;
        }
        if (mask & 1) {
            int n = basis_io_read(r->io, pkt, (int)sizeof(pkt));
            /* Control connection gone. Before any media that usually means the
             * server aborted the UDP session (e.g. its own UDP egress is
             * filtered — mediamtx tears down readers on a failed send), so a
             * TCP retry is worth one attempt. Once media has flowed it's the
             * stream ending, which no transport change fixes. */
            if (n <= 0) return last_rtp ? -1 : 1;
        }

        reorder_tick(u->v_rb, now, udp_deliver_video, udp_gap_video, d);
        if (u->a_rtp) reorder_tick(u->a_rb, now, udp_deliver_audio, udp_gap_audio, d);

        /* Three arms, all falling back to TCP: dead silence after PLAY; RTCP
         * alive but media never starting; and media that started then stopped
         * (RTCP deliberately doesn't reset this one — a path that still
         * carries reports but no longer carries media is not playback). */
        if (!last_any && now - start > RTSP_UDP_START_TIMEOUT_MS) return 1;
        if (!last_rtp && now - start > RTSP_UDP_MEDIA_TIMEOUT_MS) return 1;
        if (last_rtp && now - last_rtp > RTSP_UDP_STALL_TIMEOUT_MS) return 1;

        if (now - last_rr >= RTSP_UDP_RR_INTERVAL_MS) {
            send_rtcp_rr(u->v_rtcp);
            if (u->a_rtcp) send_rtcp_rr(u->a_rtcp);
            last_rr = now;
        }
        if (now - last_ka >= ka_interval) {
            /* the session would otherwise idle out server-side: RTP no longer
             * rides the control connection */
            rtsp_send(r, "GET_PARAMETER", base_url, NULL);
            last_ka = now;
        }
    }
    return 0;
}

/* One RTSP session over one transport. Returns 0 on clean stop, -1 after a
 * reported error or stream end, and 1 (UDP sessions only) when the caller
 * should retry the whole session over TCP-interleaved. */
static int run_session(basis_media_sink_t* sink, const basis_url_t* url, int use_udp) {
    rtsp_t r; memset(&r, 0, sizeof(r));
    char base_url[1024];
    snprintf(base_url, sizeof(base_url), "rtsp://%s:%d%s", url->host, url->port, url->path);

    if (url->user[0]) {
        char up[256]; int n = snprintf(up, sizeof(up), "%s:%s", url->user, url->pass);
        /* base64 of user:pass */
        static const char* A = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        int o = 0;
        for (int i = 0; i < n; i += 3) {
            if (o > (int)sizeof(r.authb64) - 5) break;   /* 4 chars + NUL still fit */
            int b0 = (unsigned char)up[i];
            int b1 = i + 1 < n ? (unsigned char)up[i + 1] : 0;
            int b2 = i + 2 < n ? (unsigned char)up[i + 2] : 0;
            r.authb64[o++] = A[b0 >> 2];
            r.authb64[o++] = A[((b0 & 3) << 4) | (b1 >> 4)];
            r.authb64[o++] = i + 1 < n ? A[((b1 & 15) << 2) | (b2 >> 6)] : '=';
            r.authb64[o++] = i + 2 < n ? A[b2 & 63] : '=';
        }
        r.authb64[o] = 0;
    }

    r.io = basis_io_connect(url->host, url->port, 10000);
    if (!r.io) { sink->on_error(sink->user, "RTSP: TCP connect failed"); return -1; }

    char body[8192]; int blen = 0;

    /* Go straight to DESCRIBE. OPTIONS is only a capability probe — it costs a full
     * round trip on every connect and some servers drop the link right after it
     * (which is why DESCRIBE-first was already the recovery path). Auth is sent
     * pre-emptively on every request, so OPTIONS wasn't needed for discovery. */
    int desc_send = rtsp_send(&r, "DESCRIBE", base_url, "Accept: application/sdp\r\n");
    int code = (desc_send == 0) ? rtsp_recv(&r, body, sizeof(body) - 1, &blen) : -2;

    /* one reconnect retry if the first request didn't land (transient/half-open) */
    if (desc_send != 0 || code < 0) {
        basis_io_close(r.io);
        r.io = basis_io_connect(url->host, url->port, 10000);
        if (r.io) {
            basis_io_set_read_timeout(r.io, 10000);
            r.cseq = 0; r.session[0] = 0;
            desc_send = rtsp_send(&r, "DESCRIBE", base_url, "Accept: application/sdp\r\n");
            code = (desc_send == 0) ? rtsp_recv(&r, body, sizeof(body) - 1, &blen) : -2;
        }
    }

    if (code != 200 || blen <= 0) {
        char emsg[800];
        snprintf(emsg, sizeof(emsg),
                 "RTSP: DESCRIBE failed (desc_send=%d desc='%s' code=%d body=%dB auth='%s' loc='%s' url=%s)",
                 desc_send, r.last_status[0] ? r.last_status : "<none>", code, blen,
                 r.www_auth[0] ? r.www_auth : "none", r.location[0] ? r.location : "none", base_url);
        sink->on_error(sink->user, emsg);
        if (r.io) basis_io_close(r.io);
        return -1;
    }
    body[blen] = 0;

    sdp_media_t video, audio; int nmedia;
    memset(&video, 0, sizeof(video)); memset(&audio, 0, sizeof(audio));
    video.pt = audio.pt = -1; /* pt 0 is a valid payload type; -1 marks the media absent */
    nmedia = parse_sdp(body, blen, &video, &audio);
    if (nmedia == 0 || video.pt < 0) { sink->on_error(sink->user, "RTSP: no usable media in SDP"); basis_io_close(r.io); return -1; }

    /* Multicast media can't ride the unicast UDP path; downgrade in place
     * (nothing transport-specific has happened yet, so no restart needed). */
    if (use_udp && sdp_is_multicast(body)) use_udp = 0;

    udp_state_t u; memset(&u, 0, sizeof(u));
    char udp_host[256] = {0};
    if (use_udp) {
        /* The data path must reuse the control connection's validated peer
         * address — resolving the hostname again could land elsewhere. */
        if (basis_io_peer_addr(r.io, udp_host, sizeof(udp_host)) != 0) use_udp = 0;
    }

    int interleave = 0;
    int v_channel = -1, a_channel = -1;
    char v_url[1024] = {0}, a_url[1024] = {0};
    build_control_url(base_url, video.control, v_url, sizeof(v_url));
    if (audio.pt >= 0) build_control_url(base_url, audio.control, a_url, sizeof(a_url));

    if (use_udp) {
        /* UDP SETUP. Any refusal (461 and friends), missing server_port, or
         * socket failure falls the WHOLE session back to TCP — no mixed
         * transports, and no on_error: the fallback is expected behaviour. */
        char extra[160], source[256];
        int lp = 0, sp_rtp = 0, sp_rtcp = 0;
        int fell = 0;

        do {
            if (basis_io_udp_open_pair(udp_host, &u.v_rtp, &u.v_rtcp, &lp) != 0) { fell = 1; break; }
            snprintf(extra, sizeof(extra), "Transport: RTP/AVP;unicast;client_port=%d-%d\r\n", lp, lp + 1);
            rtsp_send(&r, "SETUP", v_url, extra);
            if (rtsp_recv(&r, NULL, 0, NULL) != 200 ||
                parse_transport_udp(r.transport, &sp_rtp, &sp_rtcp, source, sizeof(source)) != 0) { fell = 1; break; }
            {
                /* source= may name a different sender; it passes the same
                 * address guard inside udp_connect before anything binds to it */
                const char* dst = source[0] ? source : udp_host;
                if (basis_io_udp_connect(u.v_rtp, dst, sp_rtp) != 0 ||
                    basis_io_udp_connect(u.v_rtcp, dst, sp_rtcp) != 0) { fell = 1; break; }
            }
            hole_punch(u.v_rtp, u.v_rtcp);

            if (audio.pt >= 0) {
                if (basis_io_udp_open_pair(udp_host, &u.a_rtp, &u.a_rtcp, &lp) != 0) { fell = 1; break; }
                snprintf(extra, sizeof(extra), "Transport: RTP/AVP;unicast;client_port=%d-%d\r\n", lp, lp + 1);
                rtsp_send(&r, "SETUP", a_url, extra);
                if (rtsp_recv(&r, NULL, 0, NULL) != 200 ||
                    parse_transport_udp(r.transport, &sp_rtp, &sp_rtcp, source, sizeof(source)) != 0) { fell = 1; break; }
                {
                    const char* dst = source[0] ? source : udp_host;
                    if (basis_io_udp_connect(u.a_rtp, dst, sp_rtp) != 0 ||
                        basis_io_udp_connect(u.a_rtcp, dst, sp_rtcp) != 0) { fell = 1; break; }
                }
                hole_punch(u.a_rtp, u.a_rtcp);
            }

            u.v_rb = (rtp_reorder_t*)calloc(1, sizeof(*u.v_rb));
            u.a_rb = (rtp_reorder_t*)calloc(1, sizeof(*u.a_rb));
            if (!u.v_rb || !u.a_rb) { fell = 1; break; }
        } while (0);

        if (fell) {
            rtsp_send(&r, "TEARDOWN", base_url, NULL);
            udp_state_close(&u);
            basis_io_close(r.io);
            return 1;
        }
    } else {
        /* SETUP video on interleaved channels 0-1 */
        char extra[128];
        snprintf(extra, sizeof(extra), "Transport: RTP/AVP/TCP;unicast;interleaved=%d-%d\r\n", interleave, interleave + 1);
        rtsp_send(&r, "SETUP", v_url, extra);
        if (rtsp_recv(&r, NULL, 0, NULL) != 200) {
            char e[360]; snprintf(e, sizeof(e), "RTSP: SETUP video failed (status='%s' url=%s)", r.last_status, v_url);
            sink->on_error(sink->user, e); basis_io_close(r.io); return -1;
        }
        v_channel = interleave; interleave += 2;

        if (audio.pt >= 0) {
            snprintf(extra, sizeof(extra), "Transport: RTP/AVP/TCP;unicast;interleaved=%d-%d\r\n", interleave, interleave + 1);
            rtsp_send(&r, "SETUP", a_url, extra);
            if (rtsp_recv(&r, NULL, 0, NULL) == 200) { a_channel = interleave; interleave += 2; }
        }
    }

    r.rtp_info[0] = 0;
    rtsp_send(&r, "PLAY", base_url, "Range: npt=0.000-\r\n");
    if (rtsp_recv(&r, NULL, 0, NULL) != 200) {
        char e[320]; snprintf(e, sizeof(e), "RTSP: PLAY failed (status='%s')", r.last_status);
        sink->on_error(sink->user, e); basis_io_close(r.io); return -1;
    }

    basis_io_set_read_timeout(r.io, 10000);
    if (sink->on_transport)
        sink->on_transport(sink->user,
            use_udp        ? "RTSP over UDP" :
            url->force_tcp ? "RTSP over TCP" :
                             "RTSP over TCP (UDP unavailable)");
    sink->on_state(sink->user, BASIS_MEDIA_STATE_BUFFERING);

    depkt_t d; memset(&d, 0, sizeof(d));
    d.sink = sink; d.video = &video; d.audio = audio.pt >= 0 ? &audio : NULL;
    d.v_channel = v_channel; d.a_channel = a_channel;

    /* RTP-Info from the PLAY response: each entry's rtptime is that track's
     * RTP timestamp at the shared play point — the per-track PTS base that
     * puts video and audio on one timeline. Entries are matched to tracks by
     * their control URL, falling back to SETUP order. A track the header
     * doesn't cover zero-bases at its first packet. */
    {
        const char* s = r.rtp_info;
        int idx = 0;
        while (s && *s) {
            const char* e = strchr(s, ',');
            size_t n = e ? (size_t)(e - s) : strlen(s);
            char entry[600];
            if (n >= sizeof(entry)) n = sizeof(entry) - 1;
            memcpy(entry, s, n); entry[n] = 0;
            const char* t = strstr(entry, "rtptime=");
            if (t) {
                int64_t base = (int64_t)(uint32_t)strtoul(t + 8, NULL, 10);
                char u[560] = {0};
                const char* up = strstr(entry, "url=");
                if (up) { up += 4; size_t m = strcspn(up, ";"); if (m >= sizeof(u)) m = sizeof(u) - 1; memcpy(u, up, m); u[m] = 0; }
                int is_audio = u[0] && a_url[0] && url_suffix_match(u, a_url);
                int is_video = !is_audio && u[0] && url_suffix_match(u, v_url);
                if (!is_audio && !is_video) { is_video = idx == 0; is_audio = idx == 1; }
                if (is_video && !d.have_v_base) { d.v_base = base; d.have_v_base = 1; }
                else if (is_audio && !d.have_a_base) { d.a_base = base; d.have_a_base = 1; }
            }
            s = e ? e + 1 : NULL;
            idx++;
        }
    }

    uint8_t* pkt = NULL; int pkt_cap = 0;
    int rc = 0;
    if (use_udp) {
        rc = udp_read_loop(&r, &d, &u, base_url);
    } else {
        /* interleaved frame: '$' <channel:1> <len:2> <RTP...> */
        while (sink->is_running(sink->user)) {
            uint8_t magic;
            if (basis_io_read_full(r.io, &magic, 1) != 1) { rc = -1; break; }
            if (magic != '$') {
                /* Could be an interim RTSP response (e.g., keepalive). Skip the line. */
                continue;
            }
            uint8_t hdr[3];
            if (basis_io_read_full(r.io, hdr, 3) != 3) { rc = -1; break; }
            int channel = hdr[0];
            int plen = (hdr[1] << 8) | hdr[2];
            if (plen <= 0 || plen > 4 * 1024 * 1024) { rc = -1; break; }
            if (!grow(&pkt, &pkt_cap, plen, RTP_MAX_BUF)) { rc = -1; break; }
            if (basis_io_read_full(r.io, pkt, plen) != plen) { rc = -1; break; }

            if (channel == d.v_channel) depkt_video(&d, pkt, plen);
            else if (channel == d.a_channel) depkt_audio(&d, pkt, plen);
            /* RTCP channels (odd) are ignored */
        }
    }

    if (rc != 1 && d.au_len > 0) deliver_au(&d);

    /* TEARDOWN best-effort */
    rtsp_send(&r, "TEARDOWN", base_url, NULL);

    free(pkt); free(d.au); free(d.fu); free(d.afrag);
    udp_state_close(&u);
    basis_io_close(r.io);
    return rc;
}

int basis_rtsp_run(basis_media_sink_t* sink, const basis_url_t* url) {
    /* rtsp:// negotiates UDP first and falls back to TCP-interleaved on a
     * refusal, socket error, or the serial no-data timer — the same shape
     * FFmpeg's and VLC's clients use, with a shorter deadline (a false
     * fallback lands on TCP, which works wherever UDP does, so the timer can
     * afford to be snappy). rtspt:// pins TCP and never probes, as does any
     * host that failed UDP within the negative-cache window. */
    if (!url->force_tcp && !udp_neg_blocked(url->host, url->port)) {
        int rc = run_session(sink, url, 1);
        if (rc != 1) return rc;
        udp_neg_add(url->host, url->port);
        if (!sink->is_running(sink->user)) return 0;
    }
    return run_session(sink, url, 0);
}
