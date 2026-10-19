/*
 * Copyright © 2020, VideoLAN and librist authors
 * Copyright © 2019-2020 SipRadius LLC
 * All rights reserved.
 *
 * SPDX-License-Identifier: BSD-2-Clause
 */

#ifndef LIBRIST_URL_PARAM_H
#define LIBRIST_URL_PARAM_H

/* Rist URL parameter names for peer config */
#define RIST_URL_PARAM_BUFFER_SIZE "buffer"
#define RIST_URL_PARAM_SECRET "secret"
#define RIST_URL_PARAM_AES_TYPE "aes-type"
#define RIST_URL_PARAM_BANDWIDTH "bandwidth"
#define RIST_URL_PARAM_RET_BANDWIDTH "return-bandwidth"
#define RIST_URL_PARAM_REORDER_BUFFER "reorder-buffer"
#define RIST_URL_PARAM_RTT "rtt"
#define RIST_URL_PARAM_COMPRESSION "compression"
#define RIST_URL_PARAM_CNAME "cname"
#define RIST_URL_PARAM_VIRT_DST_PORT "virt-dst-port"
#define RIST_URL_PARAM_WEIGHT "weight"
#define RIST_URL_PARAM_MIFACE "miface"
#define RIST_URL_PARAM_SESSION_TIMEOUT "session-timeout"
#define RIST_URL_PARAM_KEEPALIVE_INT "keepalive-interval"
#define RIST_URL_PARAM_SRP_USERNAME "username"
#define RIST_URL_PARAM_SRP_PASSWORD "password"
/* Less common URL parameters */
#define RIST_URL_PARAM_BUFFER_SIZE_MIN "buffer-min"
#define RIST_URL_PARAM_BUFFER_SIZE_MAX "buffer-max"
#define RIST_URL_PARAM_RTT_MIN "rtt-min"
#define RIST_URL_PARAM_RTT_MAX "rtt-max"
#define RIST_URL_PARAM_AES_KEY_ROTATION "key-rotation"
#define RIST_URL_PARAM_CONGESTION_CONTROL "congestion-control"
#define RIST_URL_PARAM_MIN_RETRIES "min-retries"
#define RIST_URL_PARAM_MAX_RETRIES "max-retries"
#define RIST_URL_PARAM_TIMING_MODE "timing-mode"
/* udp specific parameters */
#define RIST_URL_PARAM_STREAM_ID "stream-id"
#define RIST_URL_PARAM_RTP_TIMESTAMP "rtp-timestamp"
#define RIST_URL_PARAM_RTP_SEQUENCE "rtp-sequence"
#define RIST_URL_PARAM_RTP_OUTPUT_PTYPE "rtp-ptype"
#define RIST_URL_PARAM_MULTIPLEX_MODE "multiplex-mode"
#define RIST_URL_PARAM_MULTIPLEX_FILTER "multiplex-filter"
/* Rist additional parameter names */
#define RIST_URL_PARAM_VIRT_SRC_PORT "virt-src-port"
#define RIST_URL_PARAM_PROFILE "profile"
#define RIST_URL_PARAM_VERBOSE_LEVEL "verbose-level"

#endif /* LIBRIST_URL_PARAM_H */
