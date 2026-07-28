/* Minimal absolute-URL parser for the media schemes we accept. */
#ifndef BASIS_URL_H
#define BASIS_URL_H

#ifdef __cplusplus
extern "C" {
#endif

typedef struct basis_url {
    char scheme[16];   /* lowercased: rtsp, rtspt, rtmp, rtmps, http, https, rist */
    char host[256];
    int  port;         /* defaulted per scheme when absent */
    /* Everything after the host, including leading '/' and query. Sized to hold
     * the path of any URL the engine accepts (its url buffer is 2048), because
     * callers route on the path's content — a CDN-signed URL that overflowed
     * here would lose its ".m3u8" tail and be dispatched as something it isn't. */
    char path[2048];
    char user[128];
    char pass[128];
    int  tls;          /* 1 for rtmps/https */
    int  force_tcp;    /* 1 for rtspt: RTSP skips UDP negotiation, TCP-interleaved only */
} basis_url_t;

/* Returns 0 on success. Fills defaults: rtsp/rtspt=554, rtmp=1935, rtmps=443,
 * http=80, https=443. rtspt is normalised to scheme "rtsp" with tls=0 and
 * force_tcp=1 (the "t" pins RTP to the TCP control channel; plain rtsp
 * negotiates UDP first and falls back to TCP). */
int basis_url_parse(const char* url, basis_url_t* out);

/* 1 if the scheme uses our custom TCP control protocols (rtsp/rtmp family). */
int basis_url_is_rtsp(const basis_url_t* u);
int basis_url_is_rtmp(const basis_url_t* u);
int basis_url_is_rist(const basis_url_t* u);

#ifdef __cplusplus
}
#endif
#endif
