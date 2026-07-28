/* Ogg demuxer -> Opus audio into a basis_media_sink. Parses `.opus` files: the
 * OpusHead identification header, OpusTags (skipped), then Opus audio packets
 * reassembled across pages. Pulls bytes through the supplied read callback.
 *
 * Ogg has no index, so seeking is granule bisection over the byte range (the
 * same approach ExoPlayer's DefaultOggSeeker and VLC's oggseek.c take): it needs
 * a working reseek and the total stream size. stream_size < 0 (live / unknown
 * size / no reseek) means forward playback with no duration reported. */
#ifndef BASIS_OGG_H
#define BASIS_OGG_H

#include "../basis_media_internal.h"

#ifdef __cplusplus
extern "C" {
#endif

int basis_ogg_run(basis_media_sink_t* sink, basis_read_fn read, void* ctx,
                  basis_reseek_fn reseek, void* reseek_ctx, int64_t stream_size);

#ifdef __cplusplus
}
#endif
#endif
