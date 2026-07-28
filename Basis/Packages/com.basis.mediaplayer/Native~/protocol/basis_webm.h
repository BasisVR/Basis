/* WebM/Matroska demuxer feeding VP9/AV1 video and/or Opus audio into a
 * basis_media_sink. Announces the first supported video track and the first
 * A_OPUS audio track (either or both); other audio (A_VORBIS, A_AAC, …) and
 * subtitle tracks are skipped. A WebM whose video codec isn't supported errors
 * clearly. Pulls bytes through the supplied read callback. */
#ifndef BASIS_WEBM_H
#define BASIS_WEBM_H

#include "../basis_media_internal.h"

#ifdef __cplusplus
extern "C" {
#endif

/* reseek/reseek_ctx (optional, NULL when the source can't reposition) let the
 * demuxer fetch a trailing Cues index at open and honour sink->take_seek
 * requests with a ranged refetch. Duration is only reported when the Cues
 * index and a repositionable source are both in hand — a reported duration
 * always means seeking works. */
int basis_webm_run(basis_media_sink_t* sink, basis_read_fn read, void* ctx,
                   basis_reseek_fn reseek, void* reseek_ctx);

#ifdef __cplusplus
}
#endif
#endif
