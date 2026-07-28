/*
 * basis_jni_https.c — bridges to java.net.URL/HttpsURLConnection so the portable
 * MPEG-TS / fMP4 demuxers can read HTTPS streams on Android. See header for the
 * contract and why this exists.
 *
 * Method/class IDs are cached at JNI_OnLoad (the only place the harness reliably
 * gives us the *system* class loader — FindClass from arbitrary threads can hit
 * the calling thread's class loader and miss app classes; for java.net types it
 * works either way, but caching avoids the per-read FindClass cost).
 */

#include "basis_jni_https.h"

#include <jni.h>
#include <android/log.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include <stdio.h>

#define LOG_TAG "basis_media"
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO,  LOG_TAG, __VA_ARGS__)

static JavaVM* g_jvm = NULL;

static struct {
    jclass  url_cls;            /* java/net/URL                                  */
    jmethodID url_ctor;         /* URL(String)                                   */
    jmethodID url_open;         /* openConnection() -> URLConnection             */

    jclass  conn_cls;           /* java/net/URLConnection                        */
    jmethodID conn_set_ct;      /* setConnectTimeout(int)                        */
    jmethodID conn_set_rt;      /* setReadTimeout(int)                           */
    jmethodID conn_set_req;     /* setRequestProperty(String, String)            */
    jmethodID conn_connect;     /* connect()                                     */
    jmethodID conn_get_is;      /* getInputStream() -> InputStream               */
    jmethodID conn_get_hdr;     /* getHeaderField(String) -> String              */

    jclass  http_conn_cls;      /* java/net/HttpURLConnection                    */
    jmethodID http_set_follow;  /* setInstanceFollowRedirects(boolean)           */
    jmethodID http_get_code;    /* getResponseCode() -> int                      */
    jmethodID http_disconnect;  /* disconnect()                                  */

    jclass  is_cls;             /* java/io/InputStream                           */
    jmethodID is_read;          /* read(byte[], int, int) -> int                 */
    jmethodID is_close;         /* close()                                       */
} g_ids;

static int g_init_ok = 0;

JNIEXPORT jint JNICALL JNI_OnLoad(JavaVM* vm, void* reserved) {
    (void)reserved;
    g_jvm = vm;

    JNIEnv* env = NULL;
    if ((*vm)->GetEnv(vm, (void**)&env, JNI_VERSION_1_6) != JNI_OK) return JNI_ERR;

    jclass url        = (*env)->FindClass(env, "java/net/URL");
    jclass conn       = (*env)->FindClass(env, "java/net/URLConnection");
    jclass httpconn   = (*env)->FindClass(env, "java/net/HttpURLConnection");
    jclass is         = (*env)->FindClass(env, "java/io/InputStream");
    if (!url || !conn || !httpconn || !is) {
        if ((*env)->ExceptionCheck(env)) (*env)->ExceptionClear(env);
        return JNI_VERSION_1_6;
    }

    g_ids.url_cls       = (jclass)(*env)->NewGlobalRef(env, url);
    g_ids.conn_cls      = (jclass)(*env)->NewGlobalRef(env, conn);
    g_ids.http_conn_cls = (jclass)(*env)->NewGlobalRef(env, httpconn);
    g_ids.is_cls        = (jclass)(*env)->NewGlobalRef(env, is);

    g_ids.url_ctor       = (*env)->GetMethodID(env, g_ids.url_cls, "<init>", "(Ljava/lang/String;)V");
    g_ids.url_open       = (*env)->GetMethodID(env, g_ids.url_cls, "openConnection", "()Ljava/net/URLConnection;");
    g_ids.conn_set_ct    = (*env)->GetMethodID(env, g_ids.conn_cls, "setConnectTimeout", "(I)V");
    g_ids.conn_set_rt    = (*env)->GetMethodID(env, g_ids.conn_cls, "setReadTimeout", "(I)V");
    g_ids.conn_set_req   = (*env)->GetMethodID(env, g_ids.conn_cls, "setRequestProperty", "(Ljava/lang/String;Ljava/lang/String;)V");
    g_ids.conn_connect   = (*env)->GetMethodID(env, g_ids.conn_cls, "connect", "()V");
    g_ids.conn_get_is    = (*env)->GetMethodID(env, g_ids.conn_cls, "getInputStream", "()Ljava/io/InputStream;");
    g_ids.conn_get_hdr   = (*env)->GetMethodID(env, g_ids.conn_cls, "getHeaderField", "(Ljava/lang/String;)Ljava/lang/String;");
    g_ids.http_set_follow= (*env)->GetMethodID(env, g_ids.http_conn_cls, "setInstanceFollowRedirects", "(Z)V");
    g_ids.http_get_code  = (*env)->GetMethodID(env, g_ids.http_conn_cls, "getResponseCode", "()I");
    g_ids.http_disconnect= (*env)->GetMethodID(env, g_ids.http_conn_cls, "disconnect", "()V");
    g_ids.is_read        = (*env)->GetMethodID(env, g_ids.is_cls, "read", "([BII)I");
    g_ids.is_close       = (*env)->GetMethodID(env, g_ids.is_cls, "close", "()V");

    if ((*env)->ExceptionCheck(env)) {
        (*env)->ExceptionClear(env);
        return JNI_VERSION_1_6; /* leave g_init_ok == 0 — open() will refuse */
    }

    g_init_ok = 1;
    return JNI_VERSION_1_6;
}

/* ---- helpers ------------------------------------------------------------ */

typedef struct {
    JNIEnv* env;
    int     attached;
} jenv_lease;

static int jenv_acquire(jenv_lease* lease) {
    lease->env = NULL;
    lease->attached = 0;
    if (!g_jvm) return -1;
    jint rc = (*g_jvm)->GetEnv(g_jvm, (void**)&lease->env, JNI_VERSION_1_6);
    if (rc == JNI_OK) return 0;
    if (rc == JNI_EDETACHED) {
        if ((*g_jvm)->AttachCurrentThread(g_jvm, &lease->env, NULL) != JNI_OK) return -1;
        lease->attached = 1;
        return 0;
    }
    return -1;
}

static void jenv_release(jenv_lease* lease) {
    if (lease->attached && g_jvm) (*g_jvm)->DetachCurrentThread(g_jvm);
}

static void log_and_clear_pending(JNIEnv* env, const char* where) {
    if ((*env)->ExceptionCheck(env)) {
        (*env)->ExceptionDescribe(env);
        (*env)->ExceptionClear(env);
        LOGE("basis_jni_https: %s: java exception (cleared)", where);
    }
}

/* ---- context ------------------------------------------------------------ */

typedef struct {
    jobject conn;       /* global ref: HttpURLConnection                    */
    jobject is;         /* global ref: InputStream                          */
    jbyteArray scratch; /* global ref: reusable byte[scratch_cap]           */
    int scratch_cap;
    int eof;
    int seekable;       /* finite, byte-range-fetchable body (VOD detect)   */
    int range_ok;       /* probe answered 206 — ranged re-request honoured  */
    long long total_bytes;   /* read cursor (absolute offset); reset on reseek   */
    long long content_length;/* HTTP body size, captured once at open; -1 unknown */
    char* url;          /* kept for ranged re-requests (reseek)             */
    int timeout_ms;
} https_ctx;

/* Reads a response header into buf; returns 0 when absent. */
static int get_header(JNIEnv* env, jobject conn, const char* name, char* buf, int cap) {
    jstring key = (*env)->NewStringUTF(env, name);
    jstring val = (jstring)(*env)->CallObjectMethod(env, conn, g_ids.conn_get_hdr, key);
    (*env)->DeleteLocalRef(env, key);
    if ((*env)->ExceptionCheck(env)) { (*env)->ExceptionClear(env); return 0; }
    if (!val) return 0;
    const char* c = (*env)->GetStringUTFChars(env, val, NULL);
    int ok = 0;
    if (c) { snprintf(buf, (size_t)cap, "%s", c); ok = 1; (*env)->ReleaseStringUTFChars(env, val, c); }
    (*env)->DeleteLocalRef(env, val);
    return ok;
}

/* Opens a connected HttpURLConnection GET for `url` with the given Range header
 * value, following redirects. On success returns a local ref to the connection
 * and writes the HTTP status to *out_code (0 for a non-HTTP connection); the
 * caller reads headers / getInputStream and owns the ref. Returns NULL on any
 * failure, with the java exception cleared. Shared by open and reseek so the
 * connect sequence lives in one place. */
static jobject https_connect(JNIEnv* env, const char* url, int timeout_ms,
                             const char* range_val, jint* out_code) {
    *out_code = 0;

    jstring jurl = (*env)->NewStringUTF(env, url);
    if (!jurl) return NULL;
    jobject urlObj = (*env)->NewObject(env, g_ids.url_cls, g_ids.url_ctor, jurl);
    (*env)->DeleteLocalRef(env, jurl);
    if ((*env)->ExceptionCheck(env) || !urlObj) {
        log_and_clear_pending(env, "new URL");
        if (urlObj) (*env)->DeleteLocalRef(env, urlObj);
        return NULL;
    }

    jobject conn = (*env)->CallObjectMethod(env, urlObj, g_ids.url_open);
    (*env)->DeleteLocalRef(env, urlObj);
    if ((*env)->ExceptionCheck(env) || !conn) {
        log_and_clear_pending(env, "openConnection");
        if (conn) (*env)->DeleteLocalRef(env, conn);
        return NULL;
    }

    if (timeout_ms > 0) {
        (*env)->CallVoidMethod(env, conn, g_ids.conn_set_ct, (jint)timeout_ms);
        (*env)->CallVoidMethod(env, conn, g_ids.conn_set_rt, (jint)timeout_ms);
    }
    jstring agent_key = (*env)->NewStringUTF(env, "User-Agent");
    jstring agent_val = (*env)->NewStringUTF(env, "BasisMediaPlayer/1.0");
    (*env)->CallVoidMethod(env, conn, g_ids.conn_set_req, agent_key, agent_val);
    (*env)->DeleteLocalRef(env, agent_key);
    (*env)->DeleteLocalRef(env, agent_val);

    jstring range_key = (*env)->NewStringUTF(env, "Range");
    jstring range_str = (*env)->NewStringUTF(env, range_val);
    (*env)->CallVoidMethod(env, conn, g_ids.conn_set_req, range_key, range_str);
    (*env)->DeleteLocalRef(env, range_key);
    (*env)->DeleteLocalRef(env, range_str);

    /* HttpURLConnection (and its HttpsURLConnection subclass) gets redirect + status APIs. */
    if ((*env)->IsInstanceOf(env, conn, g_ids.http_conn_cls))
        (*env)->CallVoidMethod(env, conn, g_ids.http_set_follow, JNI_TRUE);

    (*env)->CallVoidMethod(env, conn, g_ids.conn_connect);
    if ((*env)->ExceptionCheck(env)) {
        log_and_clear_pending(env, "connect");
        (*env)->DeleteLocalRef(env, conn);
        return NULL;
    }

    if ((*env)->IsInstanceOf(env, conn, g_ids.http_conn_cls)) {
        jint code = (*env)->CallIntMethod(env, conn, g_ids.http_get_code);
        if ((*env)->ExceptionCheck(env)) { log_and_clear_pending(env, "getResponseCode"); code = 0; }
        *out_code = code;
        /* Reject 3xx, not just 4xx/5xx: setInstanceFollowRedirects handles
         * same-protocol redirects transparently (getResponseCode returns the
         * final 2xx), so a surviving 3xx is a redirect HttpURLConnection won't
         * follow — a cross-protocol http<->https hop. getInputStream would then
         * return the redirect page, not the media; fail cleanly instead. */
        if (code < 200 || code >= 300) {
            LOGE("basis_jni_https: HTTP %d for %s", (int)code, url);
            (*env)->CallVoidMethod(env, conn, g_ids.http_disconnect);
            if ((*env)->ExceptionCheck(env)) (*env)->ExceptionClear(env);
            (*env)->DeleteLocalRef(env, conn);
            return NULL;
        }
    }
    return conn; /* local ref */
}

void* basis_jni_https_open(const char* url, int timeout_ms) {
    if (!url) return NULL;
    if (!g_init_ok) { LOGE("basis_jni_https: JNI not initialised"); return NULL; }

    jenv_lease L; if (jenv_acquire(&L) != 0) return NULL;
    JNIEnv* env = L.env;

    https_ctx* h = (https_ctx*)calloc(1, sizeof(*h));
    if (!h) { jenv_release(&L); return NULL; }

    /* The bytes=0- probe: identical body, but a server that really implements
     * ranges answers 206 — the seekability signal the live-vs-VOD delivery
     * auto-detect needs (mirrors the WinHTTP source; nginx omits Accept-Ranges
     * on 206 responses, so the status is the only proof there). */
    jint code = 0;
    jobject conn = https_connect(env, url, timeout_ms, "bytes=0-", &code);
    if (!conn) { free(h); jenv_release(&L); return NULL; }

    /* Seekability (live-vs-VOD auto-detect): a finite, range-fetchable body is
     * on-demand. Range support is proven by the probe answering 206 or by an
     * Accept-Ranges: bytes advertisement; a known Content-Length is required
     * either way so a chunked / open-ended live stream is never mistaken for VOD
     * (which would mis-pace it). range_ok keeps the stricter 206-only proof that
     * a later ranged refetch relies on. */
    {
        char ranges[64], clen[32];
        h->range_ok = (code == 206);
        int rangeable = h->range_ok;
        if (!rangeable && get_header(env, conn, "Accept-Ranges", ranges, sizeof(ranges)))
            rangeable = strcasecmp(ranges, "bytes") == 0;
        long long len = 0;
        if (get_header(env, conn, "Content-Length", clen, sizeof(clen)))
            len = atoll(clen);
        h->seekable = (rangeable && len > 0) ? 1 : 0;
        h->content_length = len > 0 ? len : -1;
    }

    jobject is = (*env)->CallObjectMethod(env, conn, g_ids.conn_get_is);
    if ((*env)->ExceptionCheck(env) || !is) {
        log_and_clear_pending(env, "getInputStream");
        if ((*env)->IsInstanceOf(env, conn, g_ids.http_conn_cls))
            (*env)->CallVoidMethod(env, conn, g_ids.http_disconnect);
        log_and_clear_pending(env, "disconnect");
        (*env)->DeleteLocalRef(env, conn);
        free(h); jenv_release(&L); return NULL;
    }

    h->conn = (*env)->NewGlobalRef(env, conn);
    h->is   = (*env)->NewGlobalRef(env, is);
    h->url  = strdup(url);
    h->timeout_ms = timeout_ms;

    (*env)->DeleteLocalRef(env, is);
    (*env)->DeleteLocalRef(env, conn);

    LOGI("basis_jni_https: open ok for %s", url);
    jenv_release(&L);
    return h;
}

static int ensure_scratch(JNIEnv* env, https_ctx* h, int want) {
    if (h->scratch && h->scratch_cap >= want) return 0;
    if (h->scratch) {
        (*env)->DeleteGlobalRef(env, h->scratch);
        h->scratch = NULL;
        h->scratch_cap = 0;
    }
    int cap = want < 16384 ? 16384 : want;
    jbyteArray local = (*env)->NewByteArray(env, cap);
    if (!local) return -1;
    h->scratch = (jbyteArray)(*env)->NewGlobalRef(env, local);
    (*env)->DeleteLocalRef(env, local);
    h->scratch_cap = cap;
    return h->scratch ? 0 : -1;
}

int basis_jni_https_is_seekable(void* ctx) {
    https_ctx* h = (https_ctx*)ctx;
    return h ? h->seekable : 0;
}

long long basis_jni_https_content_length(void* ctx) {
    https_ctx* h = (https_ctx*)ctx;
    return h ? h->content_length : -1;
}

int basis_jni_https_can_reseek(void* ctx) {
    https_ctx* h = (https_ctx*)ctx;
    return h ? (h->seekable && h->range_ok) : 0;
}

void basis_jni_https_abort(void* ctx) {
    https_ctx* h = (https_ctx*)ctx;
    if (!h) return;
    jenv_lease L; if (jenv_acquire(&L) != 0) return;
    JNIEnv* env = L.env;
    /* Disconnecting closes the underlying socket, so a read blocked in
     * InputStream.read() on the reader thread throws and returns at once (the
     * counterpart to closing the WinHTTP request handle). The read path sets eof
     * on that exception; reseek clears it when it installs the new stream. */
    if (h->conn && (*env)->IsInstanceOf(env, h->conn, g_ids.http_conn_cls)) {
        (*env)->CallVoidMethod(env, h->conn, g_ids.http_disconnect);
        if ((*env)->ExceptionCheck(env)) (*env)->ExceptionClear(env);
    }
    jenv_release(&L);
}

int basis_jni_https_reseek(void* ctx, long long offset) {
    https_ctx* h = (https_ctx*)ctx;
    if (!h || !h->seekable || !h->range_ok || !h->url || offset < 0) return -1;

    jenv_lease L; if (jenv_acquire(&L) != 0) return -1;
    JNIEnv* env = L.env;

    /* Tear down the old response (abort may already have disconnected it). */
    if (h->is) {
        (*env)->CallVoidMethod(env, h->is, g_ids.is_close);
        if ((*env)->ExceptionCheck(env)) (*env)->ExceptionClear(env);
        (*env)->DeleteGlobalRef(env, h->is); h->is = NULL;
    }
    if (h->conn) {
        if ((*env)->IsInstanceOf(env, h->conn, g_ids.http_conn_cls))
            (*env)->CallVoidMethod(env, h->conn, g_ids.http_disconnect);
        if ((*env)->ExceptionCheck(env)) (*env)->ExceptionClear(env);
        (*env)->DeleteGlobalRef(env, h->conn); h->conn = NULL;
    }

    char range[64];
    snprintf(range, sizeof(range), "bytes=%lld-", offset);
    jint code = 0;
    jobject conn = https_connect(env, h->url, h->timeout_ms, range, &code);
    if (!conn) { h->eof = 1; jenv_release(&L); return -1; }

    /* 206 = ranged body starting at offset. A 200 means the server ignored the
     * Range and restarted at byte 0 — the bytes would be silently misaligned. */
    if (code != 206 && !(code == 200 && offset == 0)) {
        (*env)->CallVoidMethod(env, conn, g_ids.http_disconnect);
        if ((*env)->ExceptionCheck(env)) (*env)->ExceptionClear(env);
        (*env)->DeleteLocalRef(env, conn);
        h->eof = 1; jenv_release(&L); return -1;
    }

    jobject is = (*env)->CallObjectMethod(env, conn, g_ids.conn_get_is);
    if ((*env)->ExceptionCheck(env) || !is) {
        log_and_clear_pending(env, "reseek getInputStream");
        (*env)->CallVoidMethod(env, conn, g_ids.http_disconnect);
        if ((*env)->ExceptionCheck(env)) (*env)->ExceptionClear(env);
        (*env)->DeleteLocalRef(env, conn);
        h->eof = 1; jenv_release(&L); return -1;
    }

    h->conn = (*env)->NewGlobalRef(env, conn);
    h->is   = (*env)->NewGlobalRef(env, is);
    h->eof  = 0;
    h->total_bytes = offset;
    (*env)->DeleteLocalRef(env, is);
    (*env)->DeleteLocalRef(env, conn);
    jenv_release(&L);
    return 0;
}

int basis_jni_https_read(void* ctx, uint8_t* buf, int len) {
    https_ctx* h = (https_ctx*)ctx;
    if (!h || h->eof || !buf || len <= 0) return 0;

    jenv_lease L; if (jenv_acquire(&L) != 0) return -1;
    JNIEnv* env = L.env;

    if (ensure_scratch(env, h, len) != 0) { jenv_release(&L); return -1; }

    int want = len < h->scratch_cap ? len : h->scratch_cap;
    jint n = 0;
    int zero_reads = 0;
    for (;;) {
        n = (*env)->CallIntMethod(env, h->is, g_ids.is_read, h->scratch, 0, want);
        if ((*env)->ExceptionCheck(env)) {
            log_and_clear_pending(env, "InputStream.read");
            LOGE("basis_jni_https: read exception after %lld bytes", h->total_bytes);
            h->eof = 1;
            jenv_release(&L);
            return -1;
        }
        if (n != 0) break;
        /* Java's read(byte[], 0, len>0) contract is to block until data, EOF or
         * error — a 0 return is a stack bug. The byte-source contract has no
         * retry signal (0 means EOF and would end the stream), so absorb the
         * anomaly here and read again — bounded, because a stream that keeps
         * returning 0 without blocking would otherwise spin this thread
         * forever; past the bound it is broken, and a terminal error routes it
         * to the engine's error path rather than a fake clean EOF. */
        if (++zero_reads >= 1000) {
            LOGE("basis_jni_https: persistent zero-byte reads after %lld bytes", h->total_bytes);
            h->eof = 1;
            jenv_release(&L);
            return -1;
        }
    }
    if (n < 0) {
        LOGI("basis_jni_https: clean EOF after %lld bytes", h->total_bytes);
        h->eof = 1; jenv_release(&L); return 0;
    }
    (*env)->GetByteArrayRegion(env, h->scratch, 0, n, (jbyte*)buf);
    h->total_bytes += n;
    jenv_release(&L);
    return (int)n;
}

void basis_jni_https_close(void* ctx) {
    https_ctx* h = (https_ctx*)ctx;
    if (!h) return;

    jenv_lease L;
    if (jenv_acquire(&L) == 0) {
        JNIEnv* env = L.env;
        if (h->is) {
            (*env)->CallVoidMethod(env, h->is, g_ids.is_close);
            if ((*env)->ExceptionCheck(env)) (*env)->ExceptionClear(env);
            (*env)->DeleteGlobalRef(env, h->is);
        }
        if (h->conn) {
            if ((*env)->IsInstanceOf(env, h->conn, g_ids.http_conn_cls))
                (*env)->CallVoidMethod(env, h->conn, g_ids.http_disconnect);
            if ((*env)->ExceptionCheck(env)) (*env)->ExceptionClear(env);
            (*env)->DeleteGlobalRef(env, h->conn);
        }
        if (h->scratch) (*env)->DeleteGlobalRef(env, h->scratch);
        jenv_release(&L);
    }
    free(h->url);
    free(h);
}
