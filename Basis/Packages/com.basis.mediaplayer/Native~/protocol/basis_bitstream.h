/* H.264/H.265 NAL helpers + AAC ADTS/ASC helpers shared by the demuxers. */
#ifndef BASIS_BITSTREAM_H
#define BASIS_BITSTREAM_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ---- Annex B (start-code) NAL iteration --------------------------------- */

/* Finds the next NAL unit (between 00 00 01 / 00 00 00 01 start codes) at or
 * after `from`. On success sets *nal_off / *nal_len (payload excluding the
 * start code) and returns the index just past this NAL; -1 when none remain. */
int basis_annexb_next(const uint8_t* data, int size, int from, int* nal_off, int* nal_len);

/* H.264 NAL type = byte & 0x1F ; H.265 NAL type = (byte >> 1) & 0x3F */
int basis_h264_nal_type(uint8_t first_byte);
int basis_h265_nal_type(uint8_t first_byte);

/* 1 if the access unit contains an IDR/keyframe NAL. */
int basis_h264_is_keyframe(const uint8_t* annexb, int len);
int basis_h265_is_keyframe(const uint8_t* annexb, int len);

/* ---- AVCC (length-prefixed) -> Annex B ---------------------------------- */

/* Converts a length-prefixed sample (e.g. an MP4 sample or FLV AVC NALU) to
 * Annex B, prepending a 4-byte start code to each NALU. Returns bytes written to
 * `out`, or -1 if `out_cap` is too small. nal_length_size is 1..4 (usually 4). */
int basis_avcc_to_annexb(const uint8_t* in, int in_len, int nal_length_size,
                         uint8_t* out, int out_cap);

/* Worst-case output size for basis_avcc_to_annexb: each NAL's length prefix
 * (nal_length_size bytes) becomes a 4-byte start code, and a NAL occupies at
 * least nal_length_size + 1 input bytes, which bounds the per-NAL growth.
 * Includes a small constant of extra headroom. */
int basis_avcc_annexb_cap(int in_len, int nal_length_size);

/* Builds an Annex B extradata blob from an avcC/hvcC config record (as carried
 * in MP4 stsd or FLV AVCDecoderConfigurationRecord). Writes the contained
 * SPS/PPS (and VPS for HEVC) as start-code-prefixed NALUs. Returns bytes written
 * or -1. Sets *nal_length_size to the record's length size. */
int basis_avcc_extradata_to_annexb(const uint8_t* cfg, int cfg_len, int hevc,
                                   uint8_t* out, int out_cap, int* nal_length_size);

/* ---- AAC ---------------------------------------------------------------- */

typedef struct basis_adts {
    int sample_rate;
    int channels;     /* raw channel_configuration (use basis_aac_channels_from_config for a count) */
    int profile;      /* MPEG-4 audio object type - 1 (ADTS profile field) */
    int frame_len;    /* full frame length incl. header */
    int header_len;   /* 7 or 9 */
} basis_adts_t;

/* Parses an ADTS header at `p`. Returns 0 on success. */
int basis_adts_parse(const uint8_t* p, int len, basis_adts_t* out);

/* Maps an AAC channelConfiguration to a channel count. Configs 1-6 equal their
 * count; config 7 is 7.1 (8 channels). */
int basis_aac_channels_from_config(int config);

/* Builds a 2-byte AudioSpecificConfig from object type (=profile+1), sample rate
 * and channelConfiguration. Returns 2. */
int basis_aac_build_asc(int object_type, int sample_rate, int channels, uint8_t out[2]);

/* Builds a 7-byte ADTS header for a raw AAC frame of `aac_len` bytes. Returns 7. */
int basis_aac_build_adts(uint8_t out[7], int object_type, int sample_rate,
                         int channels, int aac_len);

int basis_aac_sample_rate_index(int sample_rate);
int basis_aac_sample_rate_from_index(int index);

/* ---- VP9 ----------------------------------------------------------------- */

/* 1 if a raw VP9 sample is a keyframe. Reads the uncompressed-header head bits
 * of the frame at offset 0 — valid for superframes too (the index sits at the
 * buffer end; the first sub-frame's header starts the buffer). Truncated or
 * malformed input returns 0. */
int basis_vp9_is_keyframe(const uint8_t* sample, int len);

/* ---- AV1 ----------------------------------------------------------------- */

/* 1 if a raw AV1 sample (one temporal unit of low-overhead OBUs, as stored in
 * MP4/WebM) is a keyframe: it carries an OBU_SEQUENCE_HEADER (the ISOBMFF
 * sync-sample shape), or its first frame(-header) OBU codes a KEY_FRAME.
 * Bounded, allocation-free; truncated or malformed input returns 0. */
int basis_av1_is_keyframe(const uint8_t* sample, int len);

/* ---- H.264 SPS dimensions (best-effort) --------------------------------- */

/* Parses width/height from an H.264 SPS NAL (payload without start code).
 * Returns 0 on success. Useful to report size before the decoder produces a
 * frame; the decoder remains the source of truth. */
int basis_h264_sps_dimensions(const uint8_t* sps, int len, int* width, int* height);

#ifdef __cplusplus
}
#endif
#endif
