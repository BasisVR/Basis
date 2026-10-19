/*
 * opus_shim.c - Non-variadic wrappers for Opus CTL functions.
 *
 * On ARM64 Apple Silicon (macOS/iOS), the C ABI passes variadic arguments
 * on the stack while fixed arguments go in registers. The opus _ctl functions
 * (opus_encoder_ctl, opus_decoder_ctl, etc.) are variadic, so calling them
 * via P/Invoke (which treats all parameters as fixed) causes arguments to be
 * passed in registers instead of on the stack, resulting in OPUS_BAD_ARG.
 *
 * These thin non-variadic wrappers correctly forward calls to the variadic
 * opus CTL functions. Since the wrapper itself is compiled natively, the
 * compiler handles the variadic calling convention correctly.
 *
 * Built into the opus shared/static library for all platforms to avoid
 * conditional compilation in C#.
 */

#include <opus.h>
#include <opus_multistream.h>

#ifdef _WIN32
#  define SHIM_EXPORT __declspec(dllexport)
#else
#  define SHIM_EXPORT
#endif

/* === Encoder CTL === */

SHIM_EXPORT SHIM_EXPORT int opussharp_encoder_ctl(OpusEncoder *st, int request)
{
    return opus_encoder_ctl(st, request);
}

SHIM_EXPORT int opussharp_encoder_ctl_i(OpusEncoder *st, int request, int value)
{
    return opus_encoder_ctl(st, request, value);
}

SHIM_EXPORT int opussharp_encoder_ctl_p(OpusEncoder *st, int request, void *value)
{
    return opus_encoder_ctl(st, request, value);
}

SHIM_EXPORT int opussharp_encoder_ctl_pi(OpusEncoder *st, int request, void *data, int data2)
{
    return opus_encoder_ctl(st, request, data, data2);
}

SHIM_EXPORT int opussharp_encoder_ctl_ip(OpusEncoder *st, int request, int data, void *data2)
{
    return opus_encoder_ctl(st, request, data, data2);
}

SHIM_EXPORT int opussharp_encoder_ctl_pp(OpusEncoder *st, int request, void *data, void *data2)
{
    return opus_encoder_ctl(st, request, data, data2);
}

/* === Decoder CTL === */

SHIM_EXPORT int opussharp_decoder_ctl(OpusDecoder *st, int request)
{
    return opus_decoder_ctl(st, request);
}

SHIM_EXPORT int opussharp_decoder_ctl_i(OpusDecoder *st, int request, int value)
{
    return opus_decoder_ctl(st, request, value);
}

SHIM_EXPORT int opussharp_decoder_ctl_p(OpusDecoder *st, int request, void *value)
{
    return opus_decoder_ctl(st, request, value);
}

/* === DRED Decoder CTL === */

SHIM_EXPORT int opussharp_dred_decoder_ctl(OpusDREDDecoder *dred_dec, int request)
{
    return opus_dred_decoder_ctl(dred_dec, request);
}

SHIM_EXPORT int opussharp_dred_decoder_ctl_p(OpusDREDDecoder *dred_dec, int request, void *value)
{
    return opus_dred_decoder_ctl(dred_dec, request, value);
}

/* === Multistream Encoder CTL === */

SHIM_EXPORT int opussharp_ms_encoder_ctl(OpusMSEncoder *st, int request)
{
    return opus_multistream_encoder_ctl(st, request);
}

SHIM_EXPORT int opussharp_ms_encoder_ctl_i(OpusMSEncoder *st, int request, int value)
{
    return opus_multistream_encoder_ctl(st, request, value);
}

SHIM_EXPORT int opussharp_ms_encoder_ctl_p(OpusMSEncoder *st, int request, void *value)
{
    return opus_multistream_encoder_ctl(st, request, value);
}

SHIM_EXPORT int opussharp_ms_encoder_ctl_pi(OpusMSEncoder *st, int request, void *data, int data2)
{
    return opus_multistream_encoder_ctl(st, request, data, data2);
}

SHIM_EXPORT int opussharp_ms_encoder_ctl_ip(OpusMSEncoder *st, int request, int data, void *data2)
{
    return opus_multistream_encoder_ctl(st, request, data, data2);
}

SHIM_EXPORT int opussharp_ms_encoder_ctl_pp(OpusMSEncoder *st, int request, void *data, void *data2)
{
    return opus_multistream_encoder_ctl(st, request, data, data2);
}

/* === Multistream Decoder CTL === */

SHIM_EXPORT int opussharp_ms_decoder_ctl(OpusMSDecoder *st, int request)
{
    return opus_multistream_decoder_ctl(st, request);
}

SHIM_EXPORT int opussharp_ms_decoder_ctl_i(OpusMSDecoder *st, int request, int value)
{
    return opus_multistream_decoder_ctl(st, request, value);
}

SHIM_EXPORT int opussharp_ms_decoder_ctl_p(OpusMSDecoder *st, int request, void *value)
{
    return opus_multistream_decoder_ctl(st, request, value);
}
