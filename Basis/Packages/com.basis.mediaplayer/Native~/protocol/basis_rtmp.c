/*
 * basis_rtmp.c — minimal RTMP client: simple handshake, AMF0 connect/createStream/
 * play, chunk-stream reassembly, and FLV AVC/AAC tag parsing -> sink.
 *
 * Scope: plaintext rtmp:// pull of a single A/V stream. The simple (non-digest)
 * handshake is used; AMF parsing is limited to what's needed to drive play and
 * read the createStream result. This is the least-exercised demuxer — RTSPT and
 * MPEG-TS are the primary paths — so expect to iterate here for picky servers.
 */

#include "basis_rtmp.h"
#include "basis_io.h"
#include "basis_bitstream.h"

#include <stdlib.h>
#include <string.h>
#include <stdio.h>

#define RTMP_DEFAULT_CHUNK 128
#define MAX_CSID 64

typedef struct {
    int      type;
    uint32_t ts;
    uint32_t len;
    int      stream_id;
    uint8_t* buf;
    int      have;       /* bytes accumulated */
    int      cap;
    uint32_t last_ts_delta;
    int      ext_ts;     /* last Type 0/1/2 header on this stream used a 0xFFFFFF (extended) timestamp */
} chunk_state_t;

typedef struct {
    basis_io_t* io;
    int in_chunk_size;
    int out_chunk_size;
    chunk_state_t cs[MAX_CSID];
    int video_nls;
    int video_announced;
    basis_codec_t video_codec;
    int audio_announced;
    int audio_sr, audio_ch, audio_obj;
    uint32_t window_ack_size; /* server's Window Ack Size (type 5); 0 = unset */
    uint32_t bytes_in;        /* bytes received, reported in our Acknowledgement (type 3) */
    uint32_t bytes_acked;     /* bytes_in at the last Acknowledgement we sent */
} rtmp_t;

/* ---- byte writers ------------------------------------------------------- */

static void be24(uint8_t* p, uint32_t v) { p[0]=(v>>16)&0xFF; p[1]=(v>>8)&0xFF; p[2]=v&0xFF; }
static void be32(uint8_t* p, uint32_t v) { p[0]=(v>>24)&0xFF; p[1]=(v>>16)&0xFF; p[2]=(v>>8)&0xFF; p[3]=v&0xFF; }

static int amf_str(uint8_t* p, const char* s) { int n=(int)strlen(s); p[0]=0x02; p[1]=(n>>8)&0xFF; p[2]=n&0xFF; memcpy(p+3,s,n); return 3+n; }
static int amf_key(uint8_t* p, const char* s) { int n=(int)strlen(s); p[0]=(n>>8)&0xFF; p[1]=n&0xFF; memcpy(p+2,s,n); return 2+n; }
static int amf_num(uint8_t* p, double v) {
    p[0]=0x00; uint64_t u; memcpy(&u,&v,8);
    for (int i=0;i<8;++i) p[1+i]=(uint8_t)((u>>(56-8*i))&0xFF);
    return 9;
}
static int amf_bool(uint8_t* p, int b){ p[0]=0x01; p[1]=b?1:0; return 2; }
static int amf_null(uint8_t* p){ p[0]=0x05; return 1; }

/* Sends one RTMP message (fmt 0) on the given chunk stream / message stream. */
static int rtmp_send_msg(rtmp_t* r, int csid, int type, int msid, const uint8_t* data, int len) {
    uint8_t hdr[12];
    hdr[0] = (uint8_t)(csid & 0x3F);            /* fmt 0 */
    be24(hdr + 1, 0);                           /* timestamp 0 */
    be24(hdr + 4, (uint32_t)len);
    hdr[7] = (uint8_t)type;
    hdr[8]=(uint8_t)(msid&0xFF); hdr[9]=(uint8_t)((msid>>8)&0xFF); hdr[10]=(uint8_t)((msid>>16)&0xFF); hdr[11]=(uint8_t)((msid>>24)&0xFF);
    if (basis_io_write_full(r->io, hdr, 12) != 12) return -1;

    int off = 0;
    while (off < len) {
        int n = len - off;
        if (n > r->out_chunk_size) n = r->out_chunk_size;
        if (basis_io_write_full(r->io, data + off, n) != n) return -1;
        off += n;
        if (off < len) {
            uint8_t c = (uint8_t)(0xC0 | (csid & 0x3F)); /* fmt 3 continuation */
            if (basis_io_write_full(r->io, &c, 1) != 1) return -1;
        }
    }
    return 0;
}

/* ---- handshake (simple) ------------------------------------------------- */

static int rtmp_handshake(rtmp_t* r) {
    uint8_t c0c1[1 + 1536];
    c0c1[0] = 3; /* version */
    /* time(4)=0, zero(4)=0, then random */
    memset(c0c1 + 1, 0, 8);
    for (int i = 9; i < 1 + 1536; ++i) c0c1[i] = (uint8_t)(i * 37);
    if (basis_io_write_full(r->io, c0c1, sizeof(c0c1)) != (int)sizeof(c0c1)) return -1;

    uint8_t s0s1s2[1 + 1536 + 1536];
    if (basis_io_read_full(r->io, s0s1s2, sizeof(s0s1s2)) != (int)sizeof(s0s1s2)) return -1;

    /* echo S1 back as C2 */
    if (basis_io_write_full(r->io, s0s1s2 + 1, 1536) != 1536) return -1;
    return 0;
}

/* ---- chunk reader ------------------------------------------------------- */

/* Reads one complete RTMP message into cs[csid]. Returns the csid of the message
 * completed this call, or -1 on error. Control messages are handled inline and
 * also returned (caller filters by type). */
static int rtmp_read_message(rtmp_t* r) {
    uint8_t b0;
    if (basis_io_read_full(r->io, &b0, 1) != 1) return -1;
    int fmt = (b0 >> 6) & 0x3;
    int csid = b0 & 0x3F;
    if (csid == 0) { uint8_t e; if (basis_io_read_full(r->io,&e,1)!=1) return -1; csid = 64 + e; }
    else if (csid == 1) { uint8_t e[2]; if (basis_io_read_full(r->io,e,2)!=2) return -1; csid = 64 + e[0] + e[1]*256; }
    if (csid >= MAX_CSID) csid = csid % MAX_CSID; /* clamp to our table */

    chunk_state_t* c = &r->cs[csid];
    uint8_t mh[11];
    uint32_t ts_field = 0;
    if (fmt <= 2) { if (basis_io_read_full(r->io, mh, 3) != 3) return -1; ts_field = (mh[0]<<16)|(mh[1]<<8)|mh[2]; }
    if (fmt <= 1) {
        if (basis_io_read_full(r->io, mh, 4) != 4) return -1; /* msg len(3) + type(1) */
        c->len = (mh[0]<<16)|(mh[1]<<8)|mh[2];
        c->type = mh[3];
    }
    if (fmt == 0) {
        uint8_t sid[4]; if (basis_io_read_full(r->io, sid, 4) != 4) return -1;
        c->stream_id = sid[0] | (sid[1]<<8) | (sid[2]<<16) | ((int)((uint32_t)sid[3]<<24));
    }
    /* Timestamp: a 24-bit field of 0xFFFFFF means the real value (absolute for Type 0,
     * delta for Type 1/2) follows in a 4-byte extended field — present on this chunk,
     * and on every Type 3 chunk continuing a stream whose last header indicated it.
     * Resolve the effective value first, then apply it once, so the 0xFFFFFF marker is
     * never folded into the timestamp. Track the flag per chunk stream so Type 3 reads
     * those bytes too (else they are mis-read as payload and the chunk stream desyncs). */
    if (fmt <= 2) {
        c->ext_ts = (ts_field == 0xFFFFFF);
        uint32_t ts = ts_field;
        if (c->ext_ts) {
            uint8_t ext[4]; if (basis_io_read_full(r->io, ext, 4) != 4) return -1;
            ts = ((uint32_t)ext[0]<<24)|((uint32_t)ext[1]<<16)|((uint32_t)ext[2]<<8)|ext[3];
        }
        if (fmt == 0) c->ts = ts; else c->ts += ts;
    } else if (c->ext_ts) { /* Type 3 continuation carries the extended timestamp; consume it */
        uint8_t ext[4]; if (basis_io_read_full(r->io, ext, 4) != 4) return -1;
    }

    /* (re)start accumulation if this is the first chunk of a message */
    if (c->have >= (int)c->len) c->have = 0;
    if ((int)c->len > c->cap) {
        int nc = c->cap ? c->cap : 4096;
        while (nc < (int)c->len) nc *= 2;
        uint8_t* nb = (uint8_t*)realloc(c->buf, (size_t)nc);
        if (!nb) return -1;
        c->buf = nb; c->cap = nc;
    }

    int remain = (int)c->len - c->have;
    int n = remain < r->in_chunk_size ? remain : r->in_chunk_size;
    if (n > 0) {
        if (basis_io_read_full(r->io, c->buf + c->have, n) != n) return -1;
        c->have += n;
        r->bytes_in += (uint32_t)n;
    }
    return csid;
}

/* ---- FLV tag handling --------------------------------------------------- */

static void handle_video(rtmp_t* r, basis_media_sink_t* sink, chunk_state_t* c) {
    if (c->have < 5) return;
    const uint8_t* p = c->buf;
    int codecid = p[0] & 0x0F;
    int avc_pkt = p[1];
    int32_t cts = (p[2]<<16)|(p[3]<<8)|p[4];
    if (cts & 0x800000) cts |= ~0xFFFFFF; /* sign extend 24-bit */
    int64_t pts_us = ((int64_t)c->ts + cts) * 1000;
    const uint8_t* data = p + 5;
    int dlen = c->have - 5;

    int hevc = (codecid == 12);
    r->video_codec = hevc ? BASIS_CODEC_H265 : BASIS_CODEC_H264;

    if (avc_pkt == 0) { /* sequence header: avcC/hvcC -> Annex B extradata */
        uint8_t ed[2048]; int nls = 4;
        int n = basis_avcc_extradata_to_annexb(data, dlen, hevc, ed, sizeof(ed), &nls);
        if (n > 0) {
            r->video_nls = nls;
            int w = 0, h = 0;
            if (!hevc) {
                int pos=0,no,nl;
                while ((pos=basis_annexb_next(ed,n,pos,&no,&nl))>=0)
                    if (nl>0 && basis_h264_nal_type(ed[no])==7){ basis_h264_sps_dimensions(ed+no,nl,&w,&h); break; }
            }
            sink->on_video_format(sink->user, r->video_codec, ed, n, w, h);
            r->video_announced = 1;
        }
    } else if (avc_pkt == 1) { /* NALUs (avcc) -> annex B */
        /* Media before the avcC sequence header (mid-stream join): its SPS — and
         * the frame size — only travel in the header, and nothing here decodes
         * without it. Drop and wait for the header rather than announcing 0x0 and
         * latching. */
        if (!r->video_announced) return;
        int nls = r->video_nls ? r->video_nls : 4;
        int cap = basis_avcc_annexb_cap(dlen, nls);
        uint8_t* out = (uint8_t*)malloc((size_t)cap);
        if (out) {
            int n = basis_avcc_to_annexb(data, dlen, nls, out, cap);
            if (n > 0) {
                int key = (p[0] >> 4) == 1; /* FLV frametype 1 = keyframe */
                sink->on_video_au(sink->user, out, n, pts_us, (int64_t)c->ts * 1000, key);
            }
            free(out);
        }
    }
}

static void handle_audio(rtmp_t* r, basis_media_sink_t* sink, chunk_state_t* c) {
    if (c->have < 2) return;
    const uint8_t* p = c->buf;
    int soundformat = (p[0] >> 4) & 0x0F;
    if (soundformat != 10) return; /* only AAC */
    int aac_pkt = p[1];
    int64_t pts_us = (int64_t)c->ts * 1000;
    const uint8_t* data = p + 2;
    int dlen = c->have - 2;

    if (aac_pkt == 0) { /* ASC */
        if (dlen >= 2) {
            int obj = (data[0] >> 3) & 0x1F;
            int sri = ((data[0] & 0x7) << 1) | ((data[1] >> 7) & 1);
            int ch = (data[1] >> 3) & 0xF;
            r->audio_obj = obj; r->audio_sr = basis_aac_sample_rate_from_index(sri); r->audio_ch = ch;
            sink->on_audio_format(sink->user, BASIS_CODEC_AAC, r->audio_sr, r->audio_ch, data, dlen);
            r->audio_announced = 1;
        }
    } else if (aac_pkt == 1) { /* raw AAC */
        if (!r->audio_announced) { sink->on_audio_format(sink->user, BASIS_CODEC_AAC, 48000, 2, NULL, 0); r->audio_announced = 1; }
        sink->on_audio_frame(sink->user, data, dlen, pts_us);
    }
}

/* ---- AMF helpers for control ------------------------------------------- */

/* find the createStream result's stream id (first AMF number after the command
 * name "_result" and transaction id). Best-effort. */
static double amf_find_stream_id(const uint8_t* p, int len) {
    /* Every multi-byte read is bounds-checked: p/len come straight off the wire,
     * so a truncated _result must fall through to the default, not over-read. */
    int i = 0;
    /* skip command string: 0x02, u16 len, bytes */
    if (i + 3 <= len && p[i] == 0x02) { int n=(p[i+1]<<8)|p[i+2]; i += 3 + n; }
    /* transaction id (number): 0x00 + 8 bytes */
    if (i < len && p[i] == 0x00) i += 9;
    /* command object / null */
    if (i < len && p[i] == 0x05) i += 1;
    else if (i < len && p[i] == 0x03) { /* skip object */ i++; while (i+3<=len){ int n=(p[i]<<8)|p[i+1]; if(n==0 && p[i+2]==0x09){i+=3;break;} i+=2+n; if(i<len){ /* skip value */ if(p[i]==0x00)i+=9; else if(i+3<=len && p[i]==0x02){int m=(p[i+1]<<8)|p[i+2];i+=3+m;} else if(p[i]==0x01)i+=2; else i++; } } }
    /* stream id number: 0x00 + 8 bytes */
    if (i + 9 <= len && p[i] == 0x00) {
        uint64_t u=0; for(int k=0;k<8;++k) u=(u<<8)|p[i+1+k];
        double d; memcpy(&d,&u,8); return d;
    }
    return 1.0;
}

int basis_rtmp_run(basis_media_sink_t* sink, const basis_url_t* url) {
    rtmp_t r; memset(&r, 0, sizeof(r));
    r.in_chunk_size = RTMP_DEFAULT_CHUNK;
    r.out_chunk_size = 4096;
    r.video_nls = 4;

    r.io = basis_io_connect(url->host, url->port, 10000);
    if (!r.io) { sink->on_error(sink->user, "RTMP: TCP connect failed"); return -1; }
    if (rtmp_handshake(&r) != 0) { sink->on_error(sink->user, "RTMP: handshake failed"); basis_io_close(r.io); return -1; }

    /* app = first path segment; stream name = remainder */
    char app[256] = {0}, play_name[512] = {0};
    {
        const char* s = url->path; while (*s == '/') s++;
        const char* slash = strchr(s, '/');
        if (slash) { int n = (int)(slash - s); if (n > 255) n = 255; memcpy(app, s, n); app[n]=0; snprintf(play_name, sizeof(play_name), "%s", slash + 1); }
        else { snprintf(app, sizeof(app), "%s", s); }
    }
    char tc_url[1024]; snprintf(tc_url, sizeof(tc_url), "rtmp://%s:%d/%s", url->host, url->port, app);

    /* connect */
    {
        uint8_t b[1024]; int n = 0;
        n += amf_str(b + n, "connect");
        n += amf_num(b + n, 1);
        b[n++] = 0x03; /* object */
        n += amf_key(b + n, "app");      n += amf_str(b + n, app);
        n += amf_key(b + n, "type");     n += amf_str(b + n, "nonprivate");
        n += amf_key(b + n, "flashVer"); n += amf_str(b + n, "LNX 9,0,124,2");
        n += amf_key(b + n, "tcUrl");    n += amf_str(b + n, tc_url);
        b[n++] = 0; b[n++] = 0; b[n++] = 0x09; /* object end */
        if (rtmp_send_msg(&r, 3, 20, 0, b, n) != 0) { sink->on_error(sink->user, "RTMP: connect send failed"); basis_io_close(r.io); return -1; }
    }

    /* set our outgoing chunk size */
    { uint8_t b[4]; be32(b, (uint32_t)r.out_chunk_size); rtmp_send_msg(&r, 2, 1, 0, b, 4); }

    int stream_id = 1;
    int played = 0;
    int created = 0;

    sink->on_state(sink->user, BASIS_MEDIA_STATE_BUFFERING);

    while (sink->is_running(sink->user)) {
        int csid = rtmp_read_message(&r);
        if (csid < 0) break;
        /* Acknowledge received bytes once we cross the server's window, else a
         * spec-compliant server stops sending when the unacked window fills. */
        if (r.window_ack_size && (r.bytes_in - r.bytes_acked) >= r.window_ack_size) {
            uint8_t ack[4]; be32(ack, r.bytes_in);
            rtmp_send_msg(&r, 2, 3, 0, ack, 4);
            r.bytes_acked = r.bytes_in;
        }
        chunk_state_t* c = &r.cs[csid];
        if (c->have < (int)c->len) continue; /* message not complete yet */

        switch (c->type) {
            case 1: /* Set Chunk Size */
                if (c->have >= 4) r.in_chunk_size = ((c->buf[0]&0x7F)<<24)|(c->buf[1]<<16)|(c->buf[2]<<8)|c->buf[3];
                break;
            case 4: /* User Control — answer PingRequest(6) with PingResponse(7) so the
                     * server's keepalive doesn't time out and drop the connection. */
                if (c->have >= 6 && (((uint32_t)c->buf[0]<<8)|c->buf[1]) == 6) {
                    uint8_t pong[6]; pong[0]=0; pong[1]=7;
                    memcpy(pong+2, c->buf+2, 4); /* echo the server's timestamp */
                    rtmp_send_msg(&r, 2, 4, 0, pong, 6);
                }
                break;
            case 5: /* Window Ack Size — remember it; we Acknowledge once we cross it */
                if (c->have >= 4)
                    r.window_ack_size = ((uint32_t)c->buf[0]<<24)|((uint32_t)c->buf[1]<<16)|((uint32_t)c->buf[2]<<8)|c->buf[3];
                break;
            case 6: /* Set Peer Bandwidth */
                break;
            case 20: case 17: { /* AMF0 / AMF3 command */
                const uint8_t* p = c->buf; int off = (c->type == 17) ? 1 : 0;
                if (c->have > off + 2 && p[off] == 0x02) {
                    int nlen = (p[off+1]<<8)|p[off+2];
                    const char* name = (const char*)(p + off + 3);
                    /* The first _result is connect's (created==0) and carries no stream id; it
                     * falls through below to send createStream (created=-1). Only the createStream
                     * _result (created==-1) carries the stream id, so extract + play on that one. */
                    if (created == -1 && nlen == 7 && off + 3 + 7 <= c->have &&
                        strncmp(name, "_result", 7) == 0) {
                        stream_id = (int)amf_find_stream_id(p + off, c->have - off);
                        created = 1;
                    }
                }
                if (!created) {
                    /* respond to connect _result with createStream */
                    uint8_t b[64]; int n=0; n+=amf_str(b+n,"createStream"); n+=amf_num(b+n,2); n+=amf_null(b+n);
                    rtmp_send_msg(&r, 3, 20, 0, b, n);
                    created = -1; /* pending */
                } else if (created == 1 && !played) {
                    uint8_t b[640]; int n=0;
                    n+=amf_str(b+n,"play"); n+=amf_num(b+n,0); n+=amf_null(b+n); n+=amf_str(b+n,play_name); n+=amf_num(b+n,-2000);
                    rtmp_send_msg(&r, 8, 20, stream_id, b, n);
                    played = 1;
                    sink->on_state(sink->user, BASIS_MEDIA_STATE_PLAYING);
                }
                break;
            }
            case 9:  handle_video(&r, sink, c); break;
            case 8:  handle_audio(&r, sink, c); break;
            case 18: break; /* onMetaData — ignored */
            default: break;
        }
        c->have = 0; /* consume */
    }

    for (int i = 0; i < MAX_CSID; ++i) free(r.cs[i].buf);
    basis_io_close(r.io);
    return 0;
}
