/*
 * basis_demux_dump — dump a container's elementary stream as JSON.
 *
 * Compiles the real protocol demuxers from Native~/protocol against an
 * observing sink and prints every access unit the demuxer emits: index,
 * pts_us, dts_us, size, keyframe flag, and an MD5 of the payload. Track
 * announces, the reported duration and any error string come out alongside.
 *
 * The point is that ffprobe emits the same fields for the same file
 * (-show_packets -show_data_hash md5), so the two can be diffed exactly. No
 * decoder, no Unity, no PluginAPI headers — the protocol layer is plain C
 * against a callback struct, so this links on any host with a C compiler.
 *
 * Build (VS dev prompt):
 *   set NAT=..\..\..\Basis\Packages\com.basis.mediaplayer\Native~
 *   cl /nologo /O2 /W4 /I %NAT% basis_demux_dump.c ^
 *      %NAT%\protocol\basis_webm.c %NAT%\protocol\basis_mp4.c ^
 *      %NAT%\protocol\basis_ts.c %NAT%\protocol\basis_wav.c ^
 *      %NAT%\protocol\basis_ogg.c %NAT%\protocol\basis_bitstream.c ^
 *      %NAT%\protocol\basis_caption.c
 *
 * Run:
 *   basis_demux_dump [-demux webm|mp4|ts|wav|ogg] [-seek US] [-noreseek] FILE
 *
 * Annex-B note: for H.264/H.265 the demuxers hand out Annex-B access units,
 * whereas ffprobe hashes the packet as stored in the container (avcC length-
 * prefixed inside MP4). Payload MD5s therefore only line up for codecs stored
 * as-is — VP9, AV1, AAC, LPCM. -au-md5-only-when-comparable records that per
 * track via "payload_is_container_form" so the comparator knows to fall back
 * to size/pts comparison for H.26x-in-MP4.
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>

/* 64-bit file seek: MSVC spells it _fseeki64; POSIX is fseeko with 64-bit off_t.
 * This tool builds on Windows (local) and Linux (CI), so keep it portable. */
#if defined(_WIN32)
#define dump_fseek64(f, off) _fseeki64((f), (off), SEEK_SET)
#else
#include <sys/types.h>
#define dump_fseek64(f, off) fseeko((f), (off_t)(off), SEEK_SET)
#endif

#include "basis_media_internal.h"
#include "protocol/basis_webm.h"
#include "protocol/basis_mp4.h"
#include "protocol/basis_ts.h"
#include "protocol/basis_wav.h"
#include "protocol/basis_ogg.h"
#include "protocol/basis_mp3.h"

/* ---- MD5 (RFC 1321) ------------------------------------------------------ */

typedef struct {
    uint32_t a, b, c, d;
    uint64_t len;
    uint8_t buf[64];
    size_t buf_len;
} md5_t;

static const uint32_t MD5_K[64] = {
    0xd76aa478u, 0xe8c7b756u, 0x242070dbu, 0xc1bdceeeu,
    0xf57c0fafu, 0x4787c62au, 0xa8304613u, 0xfd469501u,
    0x698098d8u, 0x8b44f7afu, 0xffff5bb1u, 0x895cd7beu,
    0x6b901122u, 0xfd987193u, 0xa679438eu, 0x49b40821u,
    0xf61e2562u, 0xc040b340u, 0x265e5a51u, 0xe9b6c7aau,
    0xd62f105du, 0x02441453u, 0xd8a1e681u, 0xe7d3fbc8u,
    0x21e1cde6u, 0xc33707d6u, 0xf4d50d87u, 0x455a14edu,
    0xa9e3e905u, 0xfcefa3f8u, 0x676f02d9u, 0x8d2a4c8au,
    0xfffa3942u, 0x8771f681u, 0x6d9d6122u, 0xfde5380cu,
    0xa4beea44u, 0x4bdecfa9u, 0xf6bb4b60u, 0xbebfbc70u,
    0x289b7ec6u, 0xeaa127fau, 0xd4ef3085u, 0x04881d05u,
    0xd9d4d039u, 0xe6db99e5u, 0x1fa27cf8u, 0xc4ac5665u,
    0xf4292244u, 0x432aff97u, 0xab9423a7u, 0xfc93a039u,
    0x655b59c3u, 0x8f0ccc92u, 0xffeff47du, 0x85845dd1u,
    0x6fa87e4fu, 0xfe2ce6e0u, 0xa3014314u, 0x4e0811a1u,
    0xf7537e82u, 0xbd3af235u, 0x2ad7d2bbu, 0xeb86d391u
};

static const int MD5_S[64] = {
    7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22,
    5,  9, 14, 20, 5,  9, 14, 20, 5,  9, 14, 20, 5,  9, 14, 20,
    4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23,
    6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21
};

static uint32_t md5_rol(uint32_t x, int c) { return (x << c) | (x >> (32 - c)); }

static void md5_init(md5_t* m) {
    m->a = 0x67452301u; m->b = 0xefcdab89u;
    m->c = 0x98badcfeu; m->d = 0x10325476u;
    m->len = 0; m->buf_len = 0;
}

static void md5_block(md5_t* m, const uint8_t* p) {
    uint32_t M[16], A = m->a, B = m->b, C = m->c, D = m->d;
    int i;
    for (i = 0; i < 16; i++) {
        M[i] = (uint32_t)p[i * 4] | ((uint32_t)p[i * 4 + 1] << 8) |
               ((uint32_t)p[i * 4 + 2] << 16) | ((uint32_t)p[i * 4 + 3] << 24);
    }
    for (i = 0; i < 64; i++) {
        uint32_t F;
        int g;
        if (i < 16)      { F = (B & C) | (~B & D);           g = i; }
        else if (i < 32) { F = (D & B) | (~D & C);           g = (5 * i + 1) & 15; }
        else if (i < 48) { F = B ^ C ^ D;                    g = (3 * i + 5) & 15; }
        else             { F = C ^ (B | ~D);                 g = (7 * i) & 15; }
        F += A + MD5_K[i] + M[g];
        A = D; D = C; C = B;
        B += md5_rol(F, MD5_S[i]);
    }
    m->a += A; m->b += B; m->c += C; m->d += D;
}

static void md5_update(md5_t* m, const uint8_t* p, size_t n) {
    m->len += (uint64_t)n;
    while (n) {
        size_t take = 64 - m->buf_len;
        if (take > n) take = n;
        memcpy(m->buf + m->buf_len, p, take);
        m->buf_len += take; p += take; n -= take;
        if (m->buf_len == 64) { md5_block(m, m->buf); m->buf_len = 0; }
    }
}

static void md5_final(md5_t* m, char out_hex[33]) {
    static const uint8_t PAD[64] = { 0x80 };
    uint64_t bits = m->len * 8;
    uint8_t tail[8];
    size_t padlen;
    uint32_t words[4];
    int i;

    padlen = (m->buf_len < 56) ? (56 - m->buf_len) : (120 - m->buf_len);
    md5_update(m, PAD, padlen);

    for (i = 0; i < 8; i++) tail[i] = (uint8_t)(bits >> (8 * i));
    md5_update(m, tail, 8);

    words[0] = m->a; words[1] = m->b; words[2] = m->c; words[3] = m->d;
    for (i = 0; i < 16; i++) {
        uint8_t byte = (uint8_t)(words[i / 4] >> (8 * (i % 4)));
        sprintf(out_hex + i * 2, "%02x", byte);
    }
    out_hex[32] = 0;
}

static void md5_hex(const uint8_t* p, int n, char out_hex[33]) {
    md5_t m;
    md5_init(&m);
    md5_update(&m, p, (size_t)n);
    md5_final(&m, out_hex);
}

/* ---- JSON string escaping ------------------------------------------------ */

static void json_puts(FILE* f, const char* s) {
    fputc('"', f);
    for (; s && *s; s++) {
        unsigned char c = (unsigned char)*s;
        switch (c) {
            case '"':  fputs("\\\"", f); break;
            case '\\': fputs("\\\\", f); break;
            case '\n': fputs("\\n", f);  break;
            case '\r': fputs("\\r", f);  break;
            case '\t': fputs("\\t", f);  break;
            default:
                if (c < 0x20) fprintf(f, "\\u%04x", c);
                else fputc(c, f);
        }
    }
    fputc('"', f);
}

/* ---- Harness ------------------------------------------------------------- */

typedef struct {
    FILE* f;
    int allow_reseek;

    /* video track */
    int v_announced;
    basis_codec_t v_codec;
    int v_w, v_h, v_extradata_len;
    char v_extradata_md5[33];
    long long v_aus, v_keys;

    /* audio track */
    int a_announced;
    basis_codec_t a_codec;
    int a_rate, a_channels, a_asc_len;
    char a_extradata_md5[33];
    long long a_frames;

    long long duration_us;
    int duration_reported;
    char error[512];
    int ended;

    /* seek driver */
    long long seek_at_au;
    long long seek_target;
    int seek_pending, seek_taken;
    long long resume_pts;
    int resume_key, resume_seen;

    /* output */
    FILE* out;
    int first_au_printed;
} dump_t;

static const char* codec_name(basis_codec_t c) {
    switch (c) {
        case BASIS_CODEC_H264: return "h264";
        case BASIS_CODEC_H265: return "h265";
        case BASIS_CODEC_VP9:  return "vp9";
        case BASIS_CODEC_AV1:  return "av1";
        case BASIS_CODEC_AAC:  return "aac";
        case BASIS_CODEC_MP3:  return "mp3";
        case BASIS_CODEC_LPCM: return "lpcm";
        case BASIS_CODEC_OPUS: return "opus";
        case BASIS_CODEC_NONE: return "none";
        default: return "unknown";
    }
}

/* ffprobe hashes the packet as the container stores it. Our demuxers convert
 * H.26x to Annex B, so only these codecs' payload MD5s are comparable. */
static int payload_is_container_form(basis_codec_t c) {
    return c == BASIS_CODEC_VP9 || c == BASIS_CODEC_AV1 ||
           c == BASIS_CODEC_AAC || c == BASIS_CODEC_MP3 || c == BASIS_CODEC_LPCM ||
           c == BASIS_CODEC_OPUS;
}

static int h_read(void* ctx, uint8_t* buf, int len) {
    dump_t* h = (dump_t*)ctx;
    size_t n = fread(buf, 1, (size_t)len, h->f);
    return (int)n;
}

static int h_reseek(void* ctx, int64_t abs) {
    dump_t* h = (dump_t*)ctx;
    return dump_fseek64(h->f, abs) == 0 ? 0 : -1;
}

static void emit_au(dump_t* h, const char* kind, const uint8_t* data, int len,
                    int64_t pts_us, int64_t dts_us, int key, long long index) {
    char hex[33];
    md5_hex(data, len, hex);
    if (h->first_au_printed) fputs(",\n", h->out);
    h->first_au_printed = 1;
    fprintf(h->out,
            "    {\"track\":\"%s\",\"index\":%lld,\"pts_us\":%lld,\"dts_us\":%lld,"
            "\"size\":%d,\"key\":%s,\"md5\":\"%s\"}",
            kind, index, (long long)pts_us, (long long)dts_us, len,
            key ? "true" : "false", hex);
}

static void s_video_format(void* u, basis_codec_t codec, const uint8_t* ed, int el,
                           int w, int h) {
    dump_t* d = (dump_t*)u;
    d->v_announced = 1;
    d->v_codec = codec;
    d->v_w = w; d->v_h = h;
    d->v_extradata_len = el;
    if (ed && el > 0) md5_hex(ed, el, d->v_extradata_md5);
    else d->v_extradata_md5[0] = 0;
}

static void s_video_au(void* u, const uint8_t* data, int len,
                       int64_t pts_us, int64_t dts_us, int key) {
    dump_t* d = (dump_t*)u;
    emit_au(d, "video", data, len, pts_us, dts_us, key, d->v_aus);
    d->v_aus++;
    if (key) d->v_keys++;
    if (d->seek_taken && !d->resume_seen) {
        d->resume_seen = 1;
        d->resume_pts = pts_us;
        d->resume_key = key;
    }
    if (d->seek_at_au >= 0 && d->v_aus == d->seek_at_au && !d->seek_taken) {
        d->seek_pending = 1;
    }
}

static void s_audio_format(void* u, basis_codec_t codec, int rate, int ch,
                           const uint8_t* asc, int asc_len) {
    dump_t* d = (dump_t*)u;
    d->a_announced = 1;
    d->a_codec = codec;
    d->a_rate = rate; d->a_channels = ch;
    d->a_asc_len = asc_len;
    if (asc && asc_len > 0) md5_hex(asc, asc_len, d->a_extradata_md5);
    else d->a_extradata_md5[0] = 0;
}

static void s_audio_frame(void* u, const uint8_t* data, int len, int64_t pts_us) {
    dump_t* d = (dump_t*)u;
    emit_au(d, "audio", data, len, pts_us, pts_us, 1, d->a_frames);
    if (!d->v_announced && d->seek_taken && !d->resume_seen) {
        d->resume_seen = 1;
        d->resume_pts = pts_us;
        d->resume_key = 1;
    }
    d->a_frames++;
    /* Audio-only streams (MP3/Ogg) have no video track, so drive the seek off the
     * audio count. Gate on the absence of an announced video track, not on v_aus==0:
     * in an A/V stream the audio can reach the seek point before the first video AU. */
    if (!d->v_announced && d->seek_at_au >= 0 && d->a_frames == d->seek_at_au && !d->seek_taken)
        d->seek_pending = 1;
}

static void s_state(void* u, basis_media_state_t s) { (void)u; (void)s; }

static void s_error(void* u, const char* msg) {
    dump_t* d = (dump_t*)u;
    if (msg && !d->error[0]) {
        strncpy(d->error, msg, sizeof(d->error) - 1);
        d->error[sizeof(d->error) - 1] = 0;
    }
}

static void s_eos(void* u) { ((dump_t*)u)->ended = 1; }

static void s_duration(void* u, int64_t us) {
    dump_t* d = (dump_t*)u;
    d->duration_us = us;
    d->duration_reported = 1;
}

static int s_take_seek(void* u, int64_t* out) {
    dump_t* d = (dump_t*)u;
    if (!d->seek_pending) return 0;
    d->seek_pending = 0;
    d->seek_taken = 1;
    *out = d->seek_target;
    return 1;
}

static int s_is_running(void* u) { (void)u; return 1; }

/* ---- Container detection ------------------------------------------------- */

/* Mirrors the engine's own dispatch (basis_media_core.c): sniff the 16-byte head
 * for EBML/ftyp/RIFF, and fall through to MPEG-TS for everything else. TS is the
 * default rather than a sniff hit, which is what lets m2ts through — its sync
 * byte sits at offset 4 behind the TP_extra_header, so a head[0] test never
 * matches it, and basis_ts_run detects the 192-byte packet size itself. */
static const char* detect(FILE* f) {
    uint8_t head[16];
    size_t n = fread(head, 1, sizeof(head), f);
    dump_fseek64(f, 0);
    if (n >= 4 && head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3)
        return "webm";
    if (n >= 12 && (!memcmp(head + 4, "ftyp", 4) || !memcmp(head + 4, "styp", 4) ||
                    !memcmp(head + 4, "moov", 4) || !memcmp(head + 4, "moof", 4)))
        return "mp4";
    if (n >= 12 && !memcmp(head, "RIFF", 4) && !memcmp(head + 8, "WAVE", 4))
        return "wav";
    if (n >= 4 && !memcmp(head, "OggS", 4))
        return "ogg";
    /* MP3 last (weakest magic): an "ID3" tag or a validated Layer III frame
     * header. basis_mp3_sniff parses version/layer/bitrate/rate, so an ADTS AAC
     * sync word (FF F1 / FF F9) does not match. */
    if (n >= 3 && !memcmp(head, "ID3", 3)) return "mp3";
    if (basis_mp3_sniff(head, n)) return "mp3";
    return "ts";
}

int main(int argc, char** argv) {
    const char* path = NULL;
    const char* demux = NULL;
    long long seek_target = -1;
    int noreseek = 0;
    dump_t h;
    basis_media_sink_t sink;
    int rc, i;
    FILE* f;

    for (i = 1; i < argc; i++) {
        if (!strcmp(argv[i], "-demux") && i + 1 < argc)      demux = argv[++i];
        else if (!strcmp(argv[i], "-seek") && i + 1 < argc)  seek_target = atoll(argv[++i]);
        else if (!strcmp(argv[i], "-noreseek"))              noreseek = 1;
        else if (argv[i][0] != '-')                          path = argv[i];
        else {
            fprintf(stderr, "unknown option: %s\n", argv[i]);
            return 2;
        }
    }
    if (!path) {
        fprintf(stderr,
                "usage: basis_demux_dump [-demux webm|mp4|ts|wav|ogg|mp3] [-seek US] "
                "[-noreseek] FILE\n");
        return 2;
    }

    f = fopen(path, "rb");
    if (!f) { fprintf(stderr, "cannot open %s\n", path); return 2; }

    if (!demux) {
        demux = detect(f);
        if (!demux) {
            fprintf(stderr, "cannot detect container for %s (use -demux)\n", path);
            fclose(f);
            return 2;
        }
    }

    memset(&h, 0, sizeof(h));
    h.f = f;
    h.out = stdout;
    h.allow_reseek = !noreseek;
    h.seek_at_au = (seek_target >= 0) ? 100 : -1;
    h.seek_target = seek_target;
    h.duration_us = 0;

    memset(&sink, 0, sizeof(sink));
    sink.user = &h;
    sink.on_video_format = s_video_format;
    sink.on_video_au = s_video_au;
    sink.on_audio_format = s_audio_format;
    sink.on_audio_frame = s_audio_frame;
    sink.on_state = s_state;
    sink.on_error = s_error;
    sink.on_end_of_stream = s_eos;
    sink.on_duration = s_duration;
    sink.on_transport = NULL;
    sink.take_seek = (seek_target >= 0) ? s_take_seek : NULL;
    sink.is_running = s_is_running;

    fputs("{\n  \"access_units\": [\n", h.out);

    if (!strcmp(demux, "webm"))
        rc = basis_webm_run(&sink, h_read, &h, h.allow_reseek ? h_reseek : NULL,
                            h.allow_reseek ? &h : NULL);
    else if (!strcmp(demux, "mp4"))
        rc = basis_mp4_run(&sink, h_read, &h, h.allow_reseek ? h_reseek : NULL,
                           h.allow_reseek ? &h : NULL);
    else if (!strcmp(demux, "ts"))
        rc = basis_ts_run(&sink, h_read, &h);
    else if (!strcmp(demux, "wav"))
        rc = basis_wav_run(&sink, h_read, &h, h.allow_reseek ? h_reseek : NULL,
                           h.allow_reseek ? &h : NULL);
    else if (!strcmp(demux, "ogg")) {
        fseek(f, 0, SEEK_END);
        long long ogg_size = ftell(f);
        dump_fseek64(f, 0);
        rc = basis_ogg_run(&sink, h_read, &h, h.allow_reseek ? h_reseek : NULL,
                           h.allow_reseek ? &h : NULL, h.allow_reseek ? ogg_size : -1);
    }
    else if (!strcmp(demux, "mp3"))
        rc = basis_mp3_run(&sink, h_read, &h, h.allow_reseek ? h_reseek : NULL,
                           h.allow_reseek ? &h : NULL);
    else {
        fprintf(stderr, "unknown demuxer: %s\n", demux);
        fclose(f);
        return 2;
    }

    fputs("\n  ],\n", h.out);

    fprintf(h.out, "  \"demuxer\": ");        json_puts(h.out, demux);
    fprintf(h.out, ",\n  \"file\": ");        json_puts(h.out, path);
    fprintf(h.out, ",\n  \"run_rc\": %d", rc);
    fprintf(h.out, ",\n  \"ended\": %s", h.ended ? "true" : "false");
    fprintf(h.out, ",\n  \"error\": ");       json_puts(h.out, h.error);
    fprintf(h.out, ",\n  \"duration_reported\": %s",
            h.duration_reported ? "true" : "false");
    fprintf(h.out, ",\n  \"duration_us\": %lld", h.duration_us);

    fputs(",\n  \"video\": ", h.out);
    if (h.v_announced) {
        fprintf(h.out,
                "{\"codec\":\"%s\",\"width\":%d,\"height\":%d,\"extradata_len\":%d,"
                "\"extradata_md5\":\"%s\",\"au_count\":%lld,\"key_count\":%lld,"
                "\"payload_is_container_form\":%s}",
                codec_name(h.v_codec), h.v_w, h.v_h, h.v_extradata_len,
                h.v_extradata_md5, h.v_aus, h.v_keys,
                payload_is_container_form(h.v_codec) ? "true" : "false");
    } else {
        fputs("null", h.out);
    }

    fputs(",\n  \"audio\": ", h.out);
    if (h.a_announced) {
        fprintf(h.out,
                "{\"codec\":\"%s\",\"sample_rate\":%d,\"channels\":%d,"
                "\"asc_len\":%d,\"extradata_md5\":\"%s\",\"frame_count\":%lld,"
                "\"payload_is_container_form\":%s}",
                codec_name(h.a_codec), h.a_rate, h.a_channels, h.a_asc_len,
                h.a_extradata_md5, h.a_frames,
                payload_is_container_form(h.a_codec) ? "true" : "false");
    } else {
        fputs("null", h.out);
    }

    fputs(",\n  \"seek\": ", h.out);
    if (seek_target >= 0) {
        fprintf(h.out,
                "{\"requested_us\":%lld,\"taken\":%s,\"resume_pts_us\":%lld,"
                "\"resume_key\":%s}",
                seek_target, h.seek_taken ? "true" : "false",
                h.resume_seen ? h.resume_pts : -1,
                h.resume_key ? "true" : "false");
    } else {
        fputs("null", h.out);
    }

    fputs("\n}\n", h.out);

    fclose(f);
    return 0;
}
