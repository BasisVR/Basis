/*
 * Copyright © 2020, VideoLAN and librist authors
 * Copyright © 2019-2020 SipRadius LLC
 * All rights reserved.
 *
 * SPDX-License-Identifier: BSD-2-Clause
 */

#ifndef LIBRIST_OOB_H
#define LIBRIST_OOB_H

#include "common.h"
#include "headers.h"

#ifdef __cplusplus
extern "C" {
#endif

struct rist_oob_block
{
	struct rist_peer *peer;
	const void *payload;
	size_t payload_len;
	uint64_t ts_ntp;
};

/* OOB Specific functions, send and receive IP traffic inband in RIST Main Profile */
/**
 * @brief Write data directly to a remote peer.
 *
 * This API is used to transmit out-of-band data to a remote peer
 *
 * @param ctx RIST context
 * @param oob_block a pointer to the struct rist_oob_block
 * @return number of written bytes on success, -1 in case of error.
 */
RIST_API int rist_oob_write(struct rist_ctx *ctx, const struct rist_oob_block *oob_block);

/**
 * @brief Reads out-of-band data
 *
 * Use this API to read out-of-band data from an internal fifo queue instead of the callback
 *
 * @param ctx RIST context
 * @param[out] oob_block pointer to the rist_oob_block structure
 * @return 0 on success, -1 in case of error.
 */
RIST_API int rist_oob_read(struct rist_ctx *ctx, const struct rist_oob_block **oob_block);

typedef int (*oob_callback_func_t)(void *arg, const struct rist_oob_block *oob_block);

/**
 * @brief Enable out-of-band data channel
 *
 * Call after receiver initialization to enable out-of-band data.
 *
 * @param ctx RIST context
 * @param oob_callback A pointer to the function that will be called when out-of-band data
 * comes in (NULL function pointer is valid)
 * @param arg is an the extra argument passed to the `oob_callback`
 * @return 0 on success, -1 on error
 */
RIST_API int rist_oob_callback_set(struct rist_ctx *ctx, oob_callback_func_t callback_func, void *arg);

#ifdef __cplusplus
}
#endif

#endif /* LIBRIST_OOB_H */
