/* WinHTTP byte source — OS TLS/HTTP, no third-party deps. */
#include "basis_win_http.h"

#include <windows.h>
#include <winhttp.h>
#include <stdio.h>
#include <string.h>
#include <stdlib.h>
#include <wchar.h>

#pragma comment(lib, "winhttp.lib")

typedef struct {
    HINTERNET session;
    HINTERNET connect;
    HINTERNET request;
    int response_complete;
    int seekable;            /* finite Content-Length + Accept-Ranges: bytes (VOD) */
    int range_ok;            /* server answered the bytes=0- probe with a 206 */
    long long content_length;/* body size, or -1 when unknown/chunked/live */
    wchar_t* path;           /* request path, kept for ranged re-requests */
    DWORD open_flags;        /* WINHTTP_FLAG_SECURE when https */
} win_http_t;

/* Whole-field unsigned parse, or -1 for anything that isn't one. _wcstoi64 would
 * take a numeric prefix ("123junk" -> 123) and saturate to the signed maximum on
 * overflow, either of which hands a bogus finite length to the seek and pacing
 * logic. Header values are remote input, so the field must be digits and nothing
 * else, and must fit. */
static long long parse_u64_exact(const wchar_t* s, const wchar_t* end) {
    if (!s || s >= end) return -1;
    unsigned long long v = 0;
    for (; s < end; s++) {
        if (*s < L'0' || *s > L'9') return -1;
        unsigned d = (unsigned)(*s - L'0');
        if (v > (0x7FFFFFFFFFFFFFFFULL - d) / 10ULL) return -1;
        v = v * 10ULL + d;
    }
    return (long long)v;
}

/* A whole field value, with the optional padding a header value may carry trimmed
 * off. That padding is only ever legal around the value, never inside it. */
static long long parse_u64_field(const wchar_t* s) {
    if (!s) return -1;
    const wchar_t* end = s + wcslen(s);
    while (s < end && (*s == L' ' || *s == L'\t')) s++;
    while (end > s && (end[-1] == L' ' || end[-1] == L'\t')) end--;
    return parse_u64_exact(s, end);
}

/* Complete length out of a "bytes <first>-<last>/<complete>" Content-Range, read for
 * the bytes=0- probe specifically. The grammar carries no whitespace of its own, so
 * only the outer field padding is tolerated and the delimiters must be exact — a value
 * like "not-a-range/123" must not pass on the strength of its tail. The bounds have to
 * be coherent, and first must be 0, because that is what the probe asked for: a body
 * starting anywhere else is not the one the caller believes it is reading. Either "*"
 * form reports unknown. */
static long long parse_content_range_total(const wchar_t* s) {
    if (!s) return -1;
    const wchar_t* end = s + wcslen(s);
    while (s < end && (*s == L' ' || *s == L'\t')) s++;
    while (end > s && (end[-1] == L' ' || end[-1] == L'\t')) end--;

    const size_t unitLen = 6;   /* "bytes" and the single SP the grammar allows */
    if ((size_t)(end - s) <= unitLen || _wcsnicmp(s, L"bytes ", unitLen) != 0) return -1;
    s += unitLen;

    const wchar_t* dash = wcschr(s, L'-');
    const wchar_t* slash = wcschr(s, L'/');
    if (!dash || !slash || dash >= slash || slash >= end) return -1;

    long long first = parse_u64_exact(s, dash);
    long long last = parse_u64_exact(dash + 1, slash);
    long long total = parse_u64_exact(slash + 1, end);
    if (first != 0 || last < 0 || total <= 0 || last >= total) return -1;
    return total;
}

static wchar_t* to_w(const char* s) {
    int n = MultiByteToWideChar(CP_UTF8, 0, s, -1, NULL, 0);
    wchar_t* w = (wchar_t*)malloc((size_t)n * sizeof(wchar_t));
    if (w) MultiByteToWideChar(CP_UTF8, 0, s, -1, w, n);
    return w;
}

extern "C" void* basis_win_http_open(const char* url) {
    if (!url) return NULL;
    win_http_t* h = (win_http_t*)calloc(1, sizeof(win_http_t));
    if (!h) return NULL;

    wchar_t* wurl = to_w(url);
    if (!wurl) { free(h); return NULL; }

    URL_COMPONENTS uc;
    memset(&uc, 0, sizeof(uc));
    uc.dwStructSize = sizeof(uc);
    wchar_t host[256] = {0};
    wchar_t path[2048] = {0};
    uc.lpszHostName = host; uc.dwHostNameLength = 255;
    uc.lpszUrlPath = path; uc.dwUrlPathLength = 2047;
    if (!WinHttpCrackUrl(wurl, 0, 0, &uc)) { free(wurl); free(h); return NULL; }

    h->session = WinHttpOpen(L"BasisMediaPlayer/1.0",
                             WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY,
                             WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    if (!h->session) { free(wurl); free(h); return NULL; }

    h->connect = WinHttpConnect(h->session, host, uc.nPort, 0);
    if (!h->connect) { WinHttpCloseHandle(h->session); free(wurl); free(h); return NULL; }

    DWORD flags = (uc.nScheme == INTERNET_SCHEME_HTTPS) ? WINHTTP_FLAG_SECURE : 0;
    h->open_flags = flags;
    h->path = _wcsdup(path);
    h->request = WinHttpOpenRequest(h->connect, L"GET", path, NULL,
                                    WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, flags);
    if (!h->request) {
        WinHttpCloseHandle(h->connect); WinHttpCloseHandle(h->session);
        free(h->path); free(wurl); free(h); return NULL;
    }

    /* SSRF: never let a public URL redirect down to plaintext (the classic
     * https://public -> http://127.0.0.1 downgrade). Same-scheme redirects still
     * follow, but a private https target has no valid cert and fails the TLS check. */
    DWORD redirectPolicy = WINHTTP_OPTION_REDIRECT_POLICY_DISALLOW_HTTPS_TO_HTTP;
    WinHttpSetOption(h->request, WINHTTP_OPTION_REDIRECT_POLICY, &redirectPolicy, sizeof(redirectPolicy));

    /* The bytes=0- probe: identical body, but a server that really implements
     * ranges answers 206. Only that proves a later ranged re-request will be
     * honoured — Accept-Ranges alone is advertisement (Python's SimpleHTTP
     * handler, for one, advertises it and then serves 200 + the whole file). */
    if (!WinHttpSendRequest(h->request, L"Range: bytes=0-", (DWORD)-1L,
                            WINHTTP_NO_REQUEST_DATA, 0, 0, 0) ||
        !WinHttpReceiveResponse(h->request, NULL)) {
        WinHttpCloseHandle(h->request); WinHttpCloseHandle(h->connect); WinHttpCloseHandle(h->session);
        free(h->path); free(wurl); free(h); return NULL;
    }

    /* check status code */
    DWORD code = 0, sz = sizeof(code);
    WinHttpQueryHeaders(h->request, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
                        WINHTTP_HEADER_NAME_BY_INDEX, &code, &sz, WINHTTP_NO_HEADER_INDEX);
    free(wurl);
    if (code < 200 || code >= 400) {
        WinHttpCloseHandle(h->request); WinHttpCloseHandle(h->connect); WinHttpCloseHandle(h->session);
        free(h->path); free(h); return NULL;
    }
    h->range_ok = (code == 206);

    /* Seekability (for live-vs-VOD auto-detection): a finite, range-fetchable
     * body — on-demand content. Range support is proven either by the probe
     * answering 206 (nginx omits Accept-Ranges on 206 responses, so the status
     * is the only signal there) or by an Accept-Ranges: bytes advertisement.
     * Finiteness has to come from somewhere too, so that a chunked / open-ended
     * live stream is never mistaken for VOD (which would mis-pace it) — either a
     * Content-Length for the body that arrived, or a Content-Range stating the
     * representation total. Those are different quantities and only the second is
     * a length for the whole source; see the split below. Advertised-only range
     * support still counts as on-demand for pacing, but never for seeking:
     * can_reseek and reseek both additionally require the probe's 206. */
    {
        wchar_t field[128] = {0}; DWORD fsz = sizeof(field);
        long long bodyLen = -1;   /* what this response carries */
        long long total = -1;     /* the whole representation, where it can be proven */

        /* Content-Length is read as a string and parsed, not queried with
         * WINHTTP_QUERY_FLAG_NUMBER64. Wine's winhttp omits NUMBER64 from its
         * QUERY_MODIFIER_MASK, so the flag is left in the attribute index, the header
         * lookup misses and the query fails outright — every source then looks
         * non-seekable under Proton. The 32-bit WINHTTP_QUERY_FLAG_NUMBER that Wine does
         * support truncates past 4GB, so it isn't the answer either. */
        if (WinHttpQueryHeaders(h->request, WINHTTP_QUERY_CONTENT_LENGTH,
                WINHTTP_HEADER_NAME_BY_INDEX, field, &fsz, WINHTTP_NO_HEADER_INDEX)) {
            bodyLen = parse_u64_field(field);
        }

        if (h->range_ok) {
            /* On a 206 the Content-Length covers the returned part, which a range-capping
             * proxy can make far smaller than the file, so it can never stand in for the
             * total. Content-Range is the only thing that can. */
            field[0] = 0; fsz = sizeof(field);
            if (WinHttpQueryHeaders(h->request, WINHTTP_QUERY_CONTENT_RANGE,
                    WINHTTP_HEADER_NAME_BY_INDEX, field, &fsz, WINHTTP_NO_HEADER_INDEX)) {
                total = parse_content_range_total(field);
            }
        } else {
            total = bodyLen;   /* 200: the body is the whole representation */
        }

        wchar_t ranges[64] = {0}; DWORD rsz = sizeof(ranges);
        BOOL haveRanges = WinHttpQueryHeaders(h->request, WINHTTP_QUERY_ACCEPT_RANGES,
            WINHTTP_HEADER_NAME_BY_INDEX, ranges, &rsz, WINHTTP_NO_HEADER_INDEX);
        int rangeable = h->range_ok || (haveRanges && _wcsicmp(ranges, L"bytes") == 0);

        /* Finite and rangeable is what makes this on-demand rather than live, and that
         * is all the delivery pacing needs. Knowing the complete length is a separate
         * question: a 206 that won't state one still paces correctly, it just reports an
         * unknown size rather than passing the part length off as the whole. */
        h->seekable = ((bodyLen > 0 || total > 0) && rangeable) ? 1 : 0;
        h->content_length = (total > 0) ? total : -1;
    }
    return h;
}

extern "C" int basis_win_http_is_seekable(void* ctx) {
    win_http_t* h = (win_http_t*)ctx;
    return h ? h->seekable : 0;
}

extern "C" long long basis_win_http_content_length(void* ctx) {
    win_http_t* h = (win_http_t*)ctx;
    return h ? h->content_length : -1;
}

extern "C" int basis_win_http_can_reseek(void* ctx) {
    win_http_t* h = (win_http_t*)ctx;
    return h ? (h->seekable && h->range_ok) : 0;
}

extern "C" int basis_win_http_read(void* ctx, uint8_t* buf, int len) {
    win_http_t* h = (win_http_t*)ctx;
    if (!h || h->response_complete || len <= 0) return 0;
    DWORD read = 0;
    if (!WinHttpReadData(h->request, buf, (DWORD)len, &read)) return -1;
    if (read == 0) { h->response_complete = 1; return 0; }
    return (int)read;
}

extern "C" void basis_win_http_abort(void* ctx) {
    win_http_t* h = (win_http_t*)ctx;
    if (!h) return;
    /* Closing the request handle makes a pending WinHttpReadData on another thread fail and
     * return at once (the documented way to cancel a synchronous WinHTTP read). Null it so
     * a racing read sees NULL (-> -1) and the later basis_win_http_close skips it. */
    HINTERNET req = h->request;
    h->request = NULL;
    h->response_complete = 1;
    if (req) WinHttpCloseHandle(req);
}

/* Replaces the current response with a ranged GET on the same connection so the
 * stream continues from `offset`. Only valid on a seekable body. The caller must
 * guarantee no concurrent basis_win_http_read is in flight (park or abort the
 * reading thread first — a prior basis_win_http_abort is fine, this re-opens).
 * Returns 0 on success; on failure the source is left request-less and reads
 * report EOF. */
extern "C" int basis_win_http_reseek(void* ctx, long long offset) {
    win_http_t* h = (win_http_t*)ctx;
    if (!h || !h->seekable || !h->range_ok || !h->path || offset < 0) return -1;

    HINTERNET old_req = h->request;
    h->request = NULL;
    if (old_req) WinHttpCloseHandle(old_req);

    HINTERNET req = WinHttpOpenRequest(h->connect, L"GET", h->path, NULL,
                                       WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, h->open_flags);
    if (!req) return -1;
    DWORD redirectPolicy = WINHTTP_OPTION_REDIRECT_POLICY_DISALLOW_HTTPS_TO_HTTP;
    WinHttpSetOption(req, WINHTTP_OPTION_REDIRECT_POLICY, &redirectPolicy, sizeof(redirectPolicy));

    wchar_t range[64];
    swprintf(range, 64, L"Range: bytes=%lld-", offset);
    if (!WinHttpSendRequest(req, range, (DWORD)-1L, WINHTTP_NO_REQUEST_DATA, 0, 0, 0) ||
        !WinHttpReceiveResponse(req, NULL)) {
        WinHttpCloseHandle(req);
        return -1;
    }

    /* 206 = ranged body starting at offset. A 200 means the server ignored the
     * Range and restarted at byte 0 — the bytes would be silently misaligned. */
    DWORD code = 0, sz = sizeof(code);
    WinHttpQueryHeaders(req, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
                        WINHTTP_HEADER_NAME_BY_INDEX, &code, &sz, WINHTTP_NO_HEADER_INDEX);
    if (code != 206 && !(code == 200 && offset == 0)) {
        WinHttpCloseHandle(req);
        return -1;
    }

    h->request = req;
    h->response_complete = 0;
    return 0;
}

extern "C" void basis_win_http_close(void* ctx) {
    win_http_t* h = (win_http_t*)ctx;
    if (!h) return;
    if (h->request) WinHttpCloseHandle(h->request);
    if (h->connect) WinHttpCloseHandle(h->connect);
    if (h->session) WinHttpCloseHandle(h->session);
    free(h->path);
    free(h);
}
