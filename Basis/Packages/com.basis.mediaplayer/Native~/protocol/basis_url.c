#include "basis_url.h"

#include <string.h>
#include <stdlib.h>
#include <ctype.h>

static void lower_copy(char* dst, size_t dst_sz, const char* src, size_t n) {
    size_t i = 0;
    for (; i < n && i + 1 < dst_sz && src[i]; ++i) dst[i] = (char)tolower((unsigned char)src[i]);
    dst[i] = 0;
}

int basis_url_parse(const char* url, basis_url_t* out) {
    if (!url || !out) return -1;
    memset(out, 0, sizeof(*out));

    const char* p = strstr(url, "://");
    if (!p) return -1;
    size_t scheme_len = (size_t)(p - url);
    if (scheme_len == 0 || scheme_len >= sizeof(out->scheme)) return -1;
    lower_copy(out->scheme, sizeof(out->scheme), url, scheme_len);

    const char* authority = p + 3;

    /* Optional userinfo (user:pass@) — only meaningful before the first '/'. */
    const char* slash = strchr(authority, '/');
    const char* at = strchr(authority, '@');
    if (at && (!slash || at < slash)) {
        const char* colon = (const char*)memchr(authority, ':', (size_t)(at - authority));
        if (colon) {
            lower_copy(out->user, sizeof(out->user), authority, (size_t)(colon - authority));
            /* password preserves case */
            size_t pl = (size_t)(at - (colon + 1));
            if (pl >= sizeof(out->pass)) pl = sizeof(out->pass) - 1;
            memcpy(out->pass, colon + 1, pl);
            out->pass[pl] = 0;
        } else {
            lower_copy(out->user, sizeof(out->user), authority, (size_t)(at - authority));
        }
        authority = at + 1;
        slash = strchr(authority, '/');
    }

    /* host[:port] ends at the first '/' (path) or '?' (query). A RIST URL carries the
     * query straight after the authority — rist://host:port?secret=… — with no path. */
    const char* host_end = authority;
    while (*host_end && *host_end != '/' && *host_end != '?') ++host_end;
    const char* colon = NULL;
    if (authority[0] == '[') {
        /* IPv6 literal */
        const char* rb = strchr(authority, ']');
        if (!rb || rb > host_end) return -1;
        size_t hl = (size_t)(rb - (authority + 1));
        if (hl >= sizeof(out->host)) hl = sizeof(out->host) - 1;
        memcpy(out->host, authority + 1, hl);
        out->host[hl] = 0;
        if (rb + 1 < host_end && rb[1] == ':') colon = rb + 1;
    } else {
        colon = (const char*)memchr(authority, ':', (size_t)(host_end - authority));
        size_t hl = (size_t)((colon ? colon : host_end) - authority);
        if (hl == 0 || hl >= sizeof(out->host)) return -1;
        memcpy(out->host, authority, hl);
        out->host[hl] = 0;
    }

    if (colon && colon < host_end) {
        out->port = atoi(colon + 1);
    }

    /* path and/or query: everything from the first '/' or '?'. Default "/" for a bare
     * host:port. */
    if (*host_end) {
        size_t pathlen = strlen(host_end);
        /* Reject rather than truncate. Callers dispatch on what the path ends
         * with, so a clipped path silently reroutes the URL to the wrong
         * handler instead of failing where it can be reported. */
        if (pathlen >= sizeof(out->path)) return -1;
        memcpy(out->path, host_end, pathlen);
        out->path[pathlen] = 0;
    } else {
        out->path[0] = '/';
        out->path[1] = 0;
    }

    /* scheme normalisation + default ports + TLS */
    if (strcmp(out->scheme, "rtspt") == 0) {
        /* rtspt = RTSP with RTP pinned to the TCP control channel (no UDP attempt). */
        strcpy(out->scheme, "rtsp");
        if (out->port == 0) out->port = 554;
        out->tls = 0;
        out->force_tcp = 1;
    } else if (strcmp(out->scheme, "rtsp") == 0) {
        if (out->port == 0) out->port = 554;
    } else if (strcmp(out->scheme, "rtmp") == 0) {
        if (out->port == 0) out->port = 1935;
    } else if (strcmp(out->scheme, "rtmps") == 0) {
        if (out->port == 0) out->port = 443;
        out->tls = 1;
    } else if (strcmp(out->scheme, "http") == 0) {
        if (out->port == 0) out->port = 80;
    } else if (strcmp(out->scheme, "https") == 0) {
        if (out->port == 0) out->port = 443;
        out->tls = 1;
    } else if (strcmp(out->scheme, "rist") == 0) {
        /* RIST has no well-known port; the caller always supplies host:port.
         * Encryption (secret/aes-type) rides in the query, parsed by librist. */
        out->tls = 0;
    } else {
        return -1; /* unsupported scheme */
    }
    return 0;
}

int basis_url_is_rtsp(const basis_url_t* u) {
    return u && strcmp(u->scheme, "rtsp") == 0;
}

int basis_url_is_rtmp(const basis_url_t* u) {
    return u && (strcmp(u->scheme, "rtmp") == 0 || strcmp(u->scheme, "rtmps") == 0);
}

int basis_url_is_rist(const basis_url_t* u) {
    return u && strcmp(u->scheme, "rist") == 0;
}
