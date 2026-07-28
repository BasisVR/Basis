/*
 * basis_decoder_stub.c — no-op decode/graphics backend for platforms without an
 * OS-codec implementation (Linux/macOS). Lets the portable protocol/demux core
 * compile and link for unit-testing the demuxers; it never produces frames.
 *
 * Windows uses windows/*.cpp + unity glue; Android uses android/*.c + unity glue.
 */

#include "../basis_media_native.h"
#include "../basis_media_internal.h"

#include <stdlib.h>

struct basis_decoder { int unused; };

basis_decoder_t* basis_decoder_create(basis_media_engine_t* engine) {
    (void)engine;
    return (basis_decoder_t*)calloc(1, sizeof(basis_decoder_t));
}
void basis_decoder_destroy(basis_decoder_t* d) { free(d); }

int basis_decoder_probe_video_codec(int codec) { (void)codec; return 0; }

int basis_decoder_set_video_format(basis_decoder_t* d, basis_codec_t c, const uint8_t* e, int el, int w, int h) {
    (void)d;(void)c;(void)e;(void)el;(void)w;(void)h; return 0;
}
int basis_decoder_set_audio_format(basis_decoder_t* d, basis_codec_t c, int sr, int ch, const uint8_t* a, int al) {
    (void)d;(void)c;(void)sr;(void)ch;(void)a;(void)al; return 0;
}
int basis_decoder_submit_video(basis_decoder_t* d, const uint8_t* a, int l, int64_t p, int k) {
    (void)d;(void)a;(void)l;(void)p;(void)k; return 0;
}
int basis_decoder_submit_audio(basis_decoder_t* d, const uint8_t* a, int l, int64_t p) {
    (void)d;(void)a;(void)l;(void)p; return 0;
}
int basis_decoder_try_open_url(basis_decoder_t* d, const char* url) { (void)d;(void)url; return 0; }

int  basis_decoder_render_update(basis_decoder_t* d) { (void)d; return 0; }
void basis_decoder_render_release(basis_decoder_t* d) { (void)d; }

void*    basis_decoder_get_texture(basis_decoder_t* d, int* w, int* h) { (void)d; if(w)*w=0; if(h)*h=0; return NULL; }
uint64_t basis_decoder_get_frame_counter(basis_decoder_t* d) { (void)d; return 0; }
int      basis_decoder_get_video_size(basis_decoder_t* d, int* w, int* h) { (void)d;(void)w;(void)h; return -1; }
int      basis_decoder_get_frame_origin(basis_decoder_t* d) { (void)d; return 0; }
int64_t  basis_decoder_get_position_us(basis_decoder_t* d) { (void)d; return -1; }
void     basis_decoder_notify_end_of_stream(basis_decoder_t* d) { (void)d; }
int      basis_decoder_presentation_pending(basis_decoder_t* d) { (void)d; return 0; }
int      basis_decoder_get_audio_format(basis_decoder_t* d, int* r, int* c) { (void)d;(void)r;(void)c; return -1; }
int      basis_decoder_read_audio(basis_decoder_t* d, float* o, int m) { (void)d;(void)o;(void)m; return 0; }
int      basis_decoder_get_debug(basis_decoder_t* d, char* buf, int size) { (void)d; if (buf && size > 0) buf[0] = 0; return 0; }
void     basis_decoder_set_buffer(basis_decoder_t* d, int mode, int ms) { (void)d;(void)mode;(void)ms; }
void     basis_decoder_set_audio_latency(basis_decoder_t* d, int latency_us) { (void)d;(void)latency_us; }
void     basis_decoder_set_output_texture(basis_decoder_t* d, void* nt, int w, int h) { (void)d;(void)nt;(void)w;(void)h; }

/* graphics stubs */
basis_gfx_api_t basis_gfx_get_api(void) { return BASIS_GFX_NONE; }
void* basis_gfx_get_d3d11_device(void) { return NULL; }
void* basis_gfx_get_d3d12_device(void) { return NULL; }
void* basis_gfx_get_d3d12_queue(void) { return NULL; }
void* basis_gfx_get_unity_vulkan(void) { return NULL; }
uint64_t basis_gfx_vk_instance(void) { return 0; }
uint64_t basis_gfx_vk_device(void) { return 0; }
uint64_t basis_gfx_vk_physical_device(void) { return 0; }
uint64_t basis_gfx_vk_graphics_queue(void) { return 0; }
uint32_t basis_gfx_vk_graphics_queue_family(void) { return 0; }

static void BASIS_CALL stub_render_event(int e, void* d) { (void)e; (void)d; }
BASIS_API basis_render_event_func BASIS_CALL basis_media_get_render_event_func(void) { return stub_render_event; }
