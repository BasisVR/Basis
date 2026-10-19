#include <math.h>
#include <stdlib.h>
#include <string.h>

#define MELS 80
#define CH 256
#define HID 512
#define VIS 15
#define VIS_PAD 16
#define BLOCKS 10
#define MAX_T 32
#define PANEL 8

typedef float v4 __attribute__((vector_size(16)));
typedef float v4u __attribute__((vector_size(16), aligned(4)));

#define LOAD(p) (*(const v4u*)(p))
#define STORE(p, v) (*(v4u*)(p) = (v))

static const int dilations[BLOCKS] = { 1, 2, 4, 8, 16, 32, 1, 2, 4, 8 };

typedef struct
{
    float norm_w[CH], norm_b[CH], dw0[CH], dw1[CH], dw2[CH], dw_b[CH], pw1_b[HID], pw2_b[CH];
    float pw1[CH * HID], pw2[HID * CH];
} block_weights;

typedef struct
{
    float mean[MELS], std[MELS], in_b[CH], in_w[MELS * CH];
    block_weights blocks[BLOCKS];
    float out_w[CH], out_b[CH], head_b[VIS_PAD], head_w[CH * VIS_PAD];
    float x[MAX_T * MELS], h[MAX_T * CH], n[MAX_T * CH], d[MAX_T * CH], p[MAX_T * HID], logits[MAX_T * VIS_PAD];
} basis_ols_net;

typedef struct
{
    float* ring[BLOCKS];
    int head[BLOCKS];
} basis_ols_session;

typedef struct
{
    basis_ols_session* session;
    int start, count;
} segment;

int basis_ols_weight_count(void)
{
    return MELS * 2 + CH * MELS + CH + BLOCKS * (CH * 2 + CH * 3 + CH + HID * CH + HID + CH * HID + CH) + CH * 2 + VIS + CH * VIS;
}

static const float* take(const float** cursor, int count)
{
    const float* at = *cursor;
    *cursor += count;
    return at;
}

static void pack(const float* src, int outputs, int inputs, int src_outputs, int output_major, float* dst)
{
    for (int p = 0; p < outputs / PANEL; p++)
        for (int i = 0; i < inputs; i++)
            for (int k = 0; k < PANEL; k++)
            {
                int j = p * PANEL + k;
                dst[((size_t)p * inputs + i) * PANEL + k] = j >= src_outputs ? 0.0f : output_major ? src[(size_t)j * inputs + i] : src[(size_t)i * src_outputs + j];
            }
}

void* basis_ols_net_create(const float* weights, int count)
{
    if (count != basis_ols_weight_count()) return NULL;
    basis_ols_net* net = (basis_ols_net*)calloc(1, sizeof(basis_ols_net));
    if (!net) return NULL;
    const float* cursor = weights;
    memcpy(net->mean, take(&cursor, MELS), sizeof(net->mean));
    memcpy(net->std, take(&cursor, MELS), sizeof(net->std));
    pack(take(&cursor, CH * MELS), CH, MELS, CH, 1, net->in_w);
    memcpy(net->in_b, take(&cursor, CH), sizeof(net->in_b));
    for (int b = 0; b < BLOCKS; b++)
    {
        block_weights* w = &net->blocks[b];
        memcpy(w->norm_w, take(&cursor, CH), sizeof(w->norm_w));
        memcpy(w->norm_b, take(&cursor, CH), sizeof(w->norm_b));
        const float* dw = take(&cursor, CH * 3);
        for (int c = 0; c < CH; c++)
        {
            w->dw0[c] = dw[c * 3];
            w->dw1[c] = dw[c * 3 + 1];
            w->dw2[c] = dw[c * 3 + 2];
        }
        memcpy(w->dw_b, take(&cursor, CH), sizeof(w->dw_b));
        pack(take(&cursor, HID * CH), HID, CH, HID, 1, w->pw1);
        memcpy(w->pw1_b, take(&cursor, HID), sizeof(w->pw1_b));
        pack(take(&cursor, CH * HID), CH, HID, CH, 1, w->pw2);
        memcpy(w->pw2_b, take(&cursor, CH), sizeof(w->pw2_b));
    }
    memcpy(net->out_w, take(&cursor, CH), sizeof(net->out_w));
    memcpy(net->out_b, take(&cursor, CH), sizeof(net->out_b));
    memcpy(net->head_b, take(&cursor, VIS), sizeof(float) * VIS);
    pack(take(&cursor, CH * VIS), VIS_PAD, CH, VIS, 0, net->head_w);
    return net;
}

void basis_ols_net_destroy(void* net)
{
    free(net);
}

void basis_ols_session_reset(void* handle)
{
    basis_ols_session* s = (basis_ols_session*)handle;
    if (!s) return;
    for (int b = 0; b < BLOCKS; b++)
    {
        memset(s->ring[b], 0, sizeof(float) * 2 * dilations[b] * CH);
        s->head[b] = 0;
    }
}

void basis_ols_session_destroy(void* handle)
{
    basis_ols_session* s = (basis_ols_session*)handle;
    if (!s) return;
    for (int b = 0; b < BLOCKS; b++) free(s->ring[b]);
    free(s);
}

void* basis_ols_session_create(void)
{
    basis_ols_session* s = (basis_ols_session*)calloc(1, sizeof(basis_ols_session));
    if (!s) return NULL;
    for (int b = 0; b < BLOCKS; b++)
    {
        s->ring[b] = (float*)calloc((size_t)2 * dilations[b] * CH, sizeof(float));
        if (!s->ring[b])
        {
            basis_ols_session_destroy(s);
            return NULL;
        }
    }
    return s;
}

static void layer_norm(const float* x, const float* w, const float* b, float* y)
{
    double sum = 0.0, sum_sq = 0.0;
    for (int i = 0; i < CH; i++)
    {
        sum += x[i];
        sum_sq += (double)x[i] * x[i];
    }
    double mean = sum / CH;
    double var = sum_sq / CH - mean * mean;
    float m = (float)mean, inv = (float)(1.0 / sqrt((var > 0.0 ? var : 0.0) + 1e-5));
    for (int i = 0; i < CH; i++) y[i] = (x[i] - m) * inv * w[i] + b[i];
}

static void gemm(const float* in, int in_stride, int frames, int inputs, const float* packed, const float* bias, float* out, int outputs)
{
    for (int p = 0; p < outputs / PANEL; p++)
    {
        const float* w = packed + (size_t)p * inputs * PANEL;
        v4 b0 = LOAD(bias + p * PANEL), b1 = LOAD(bias + p * PANEL + 4);
        float* o = out + p * PANEL;
        int t = 0;
        for (; t + 4 <= frames; t += 4)
        {
            const float* x0 = in + t * in_stride;
            const float* x1 = x0 + in_stride;
            const float* x2 = x1 + in_stride;
            const float* x3 = x2 + in_stride;
            v4 a00 = b0, a01 = b1, a10 = b0, a11 = b1, a20 = b0, a21 = b1, a30 = b0, a31 = b1;
            for (int i = 0; i < inputs; i++)
            {
                v4 w0 = LOAD(w + i * PANEL), w1 = LOAD(w + i * PANEL + 4);
                float s0 = x0[i], s1 = x1[i], s2 = x2[i], s3 = x3[i];
                a00 += s0 * w0; a01 += s0 * w1;
                a10 += s1 * w0; a11 += s1 * w1;
                a20 += s2 * w0; a21 += s2 * w1;
                a30 += s3 * w0; a31 += s3 * w1;
            }
            STORE(o + t * outputs, a00); STORE(o + t * outputs + 4, a01);
            STORE(o + (t + 1) * outputs, a10); STORE(o + (t + 1) * outputs + 4, a11);
            STORE(o + (t + 2) * outputs, a20); STORE(o + (t + 2) * outputs + 4, a21);
            STORE(o + (t + 3) * outputs, a30); STORE(o + (t + 3) * outputs + 4, a31);
        }
        for (; t + 2 <= frames; t += 2)
        {
            const float* x0 = in + t * in_stride;
            const float* x1 = x0 + in_stride;
            v4 a00 = b0, a01 = b1, a10 = b0, a11 = b1;
            for (int i = 0; i < inputs; i++)
            {
                v4 w0 = LOAD(w + i * PANEL), w1 = LOAD(w + i * PANEL + 4);
                float s0 = x0[i], s1 = x1[i];
                a00 += s0 * w0; a01 += s0 * w1;
                a10 += s1 * w0; a11 += s1 * w1;
            }
            STORE(o + t * outputs, a00); STORE(o + t * outputs + 4, a01);
            STORE(o + (t + 1) * outputs, a10); STORE(o + (t + 1) * outputs + 4, a11);
        }
        for (; t < frames; t++)
        {
            const float* x0 = in + t * in_stride;
            v4 a0 = b0, a1 = b1;
            for (int i = 0; i < inputs; i++)
            {
                float s0 = x0[i];
                a0 += s0 * LOAD(w + i * PANEL);
                a1 += s0 * LOAD(w + i * PANEL + 4);
            }
            STORE(o + t * outputs, a0); STORE(o + t * outputs + 4, a1);
        }
    }
}

static void depthwise(basis_ols_session* s, int b, const block_weights* w, const float* n, int frames, float* out)
{
    int dil = dilations[b], len = 2 * dil, head = s->head[b];
    float* ring = s->ring[b];
    for (int t = 0; t < frames; t++)
    {
        const float* cur = n + t * CH;
        const float* mid = t >= dil ? n + (t - dil) * CH : ring + ((head + t + dil) % len) * CH;
        const float* old = t >= len ? n + (t - len) * CH : ring + ((head + t) % len) * CH;
        float* o = out + t * CH;
        for (int c = 0; c < CH; c += 4) STORE(o + c, LOAD(old + c) * LOAD(w->dw0 + c) + LOAD(mid + c) * LOAD(w->dw1 + c) + LOAD(cur + c) * LOAD(w->dw2 + c) + LOAD(w->dw_b + c));
    }
    for (int t = frames > len ? frames - len : 0; t < frames; t++) memcpy(ring + ((head + t) % len) * CH, n + t * CH, sizeof(float) * CH);
    s->head[b] = (head + frames) % len;
}

static void run_chunk(basis_ols_net* net, const segment* segments, int count, int frames, const float* mel, float* logits)
{
    for (int i = 0; i < frames * MELS; i++) net->x[i] = (mel[i] - net->mean[i % MELS]) / net->std[i % MELS];
    gemm(net->x, MELS, frames, MELS, net->in_w, net->in_b, net->h, CH);
    for (int b = 0; b < BLOCKS; b++)
    {
        const block_weights* w = &net->blocks[b];
        for (int t = 0; t < frames; t++) layer_norm(net->h + t * CH, w->norm_w, w->norm_b, net->n + t * CH);
        for (int g = 0; g < count; g++) depthwise(segments[g].session, b, w, net->n + segments[g].start * CH, segments[g].count, net->d + segments[g].start * CH);
        gemm(net->d, CH, frames, CH, w->pw1, w->pw1_b, net->p, HID);
        for (int i = 0; i < frames * HID; i++)
        {
            float v = net->p[i];
            net->p[i] = v * (erff(v / 1.41421354f) + 1.0f) * 0.5f;
        }
        gemm(net->p, HID, frames, HID, w->pw2, w->pw2_b, net->d, CH);
        for (int i = 0; i < frames * CH; i += 4) STORE(net->h + i, LOAD(net->h + i) + LOAD(net->d + i));
    }
    for (int t = 0; t < frames; t++) layer_norm(net->h + t * CH, net->out_w, net->out_b, net->n + t * CH);
    gemm(net->n, CH, frames, CH, net->head_w, net->head_b, net->logits, VIS_PAD);
    for (int t = 0; t < frames; t++) memcpy(logits + t * VIS, net->logits + t * VIS_PAD, sizeof(float) * VIS);
}

int basis_ols_step(void* handle, void* const* sessions, const int* frames, int count, const float* mel, float* logits)
{
    basis_ols_net* net = (basis_ols_net*)handle;
    if (!net) return 0;
    segment segments[MAX_T];
    int used = 0, filled = 0, done = 0;
    for (int k = 0; k < count; k++)
    {
        if (!sessions[k]) return -1;
        for (int left = frames[k]; left > 0;)
        {
            int part = left < MAX_T - filled ? left : MAX_T - filled;
            segments[used].session = (basis_ols_session*)sessions[k];
            segments[used].start = filled;
            segments[used].count = part;
            used++;
            filled += part;
            left -= part;
            if (filled == MAX_T)
            {
                run_chunk(net, segments, used, filled, mel + (size_t)done * MELS, logits + (size_t)done * VIS);
                done += filled;
                used = filled = 0;
            }
        }
    }
    if (filled > 0)
    {
        run_chunk(net, segments, used, filled, mel + (size_t)done * MELS, logits + (size_t)done * VIS);
        done += filled;
    }
    return done;
}
