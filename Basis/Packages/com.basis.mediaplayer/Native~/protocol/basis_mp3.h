/* Standalone MP3 demuxer -> MPEG-1/2 Layer III audio into a basis_media_sink.
 * Skips a leading ID3v2 tag, locks onto the frame sync (validating consecutive
 * headers so audio-data false syncs don't trip it), and emits each frame as one
 * audio AU. A leading Xing/Info/VBRI header is parsed for duration and the seek
 * table, then dropped. Layer III only. The OS decoders parse the frame headers,
 * so no decoder init data is announced.
 *
 * reseek repositions the byte source for absolute seeks (Xing TOC or CBR-bitrate
 * mapping); pass NULL for forward-only sources. */
#ifndef BASIS_MP3_H
#define BASIS_MP3_H

#include "../basis_media_internal.h"

#ifdef __cplusplus
extern "C" {
#endif

/* 1 if the leading bytes look like a Layer III frame header (container sniff). */
int basis_mp3_sniff(const uint8_t* b, int n);

int basis_mp3_run(basis_media_sink_t* sink, basis_read_fn read, void* ctx,
                  basis_reseek_fn reseek, void* reseek_ctx);

#ifdef __cplusplus
}
#endif
#endif
