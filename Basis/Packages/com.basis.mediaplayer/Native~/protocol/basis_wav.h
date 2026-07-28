/* RIFF/WAVE demuxer feeding integer PCM into a basis_media_sink as
 * BASIS_CODEC_LPCM (little-endian, WAVE channel order — flags byte in the
 * format announce's config blob). Pulls bytes through the supplied read
 * callback. reseek repositions the byte source for absolute seeks (linear
 * PCM: data-start + time x byte-rate); NULL means sequential-only, no
 * duration is reported and the timeline plays forward. */
#ifndef BASIS_WAV_H
#define BASIS_WAV_H

#include "../basis_media_internal.h"

#ifdef __cplusplus
extern "C" {
#endif

int basis_wav_run(basis_media_sink_t* sink, basis_read_fn read, void* ctx,
                  basis_reseek_fn reseek, void* reseek_ctx);

#ifdef __cplusplus
}
#endif
#endif
