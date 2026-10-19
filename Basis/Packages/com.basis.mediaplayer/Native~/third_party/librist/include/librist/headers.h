/*
 * Copyright © 2020, VideoLAN and librist authors
 * Copyright © 2019-2020 SipRadius LLC
 * All rights reserved.
 *
 * SPDX-License-Identifier: BSD-2-Clause
 */

#ifndef LIBRIST_HEADERS_H
#define LIBRIST_HEADERS_H

#include <stdint.h>
#include <stdlib.h>
#include <stdbool.h>
#include "urlparam.h"

#ifdef __cplusplus
extern "C" {
#endif

/* Used for cname, miface and shared secret */
#define RIST_MAX_STRING_SHORT 128
/* Used for url/address */
#define RIST_MAX_STRING_LONG 256

/* Track PROTOCOL and API changes */
#define RIST_PEER_UDPSOCKET_VERSION (0)
#define RIST_UDP_CONFIG_VERSION (1)

/* Error Codes */
#define RIST_ERR_MALLOC -1
#define RIST_ERR_NULL_PEER -2
#define RIST_ERR_INVALID_STRING_LENGTH -3
#define RIST_ERR_INVALID_PROFILE -4
#define RIST_ERR_MISSING_CALLBACK_FUNCTION -5
#define RIST_ERR_NULL_CREDENTIALS -6

enum rist_profile
{
	RIST_PROFILE_SIMPLE = 0,
	RIST_PROFILE_MAIN = 1,
	RIST_PROFILE_ADVANCED = 2,
};

enum rist_data_block_sender_flags
{
	RIST_DATA_FLAGS_USE_SEQ = 1,
	RIST_DATA_FLAGS_NEED_FREE = 2
};

enum rist_data_block_receiver_flags
{
	RIST_DATA_FLAGS_DISCONTINUITY = 1 << 0,
	RIST_DATA_FLAGS_FLOW_BUFFER_START = 1 << 1,
	RIST_DATA_FLAGS_OVERFLOW = 1 << 2
};


enum librist_multiplex_mode
{
	LIBRIST_MULTIPLEX_MODE_AUTO = -1, //Autoselect between multiplex modes below.
	LIBRIST_MULTIPLEX_MODE_VIRT_DESTINATION_PORT = 0,//Multiplexed flows are selected based on their GRE destination port (data packets)
	LIBRIST_MULTIPLEX_MODE_VIRT_SOURCE_PORT = 1, // Multiplexed flows are selected based on their GRE source port (data packets). libRIST ONLY
	LIBRIST_MULTIPLEX_MODE_IPV4 = 2, // Multiple IP flows are multiplexed into a single RIST flow, to be output via a TUN device. libRIST ONLY
};

struct rist_ctx;

struct rist_data_block
{
	const void *payload;
	size_t payload_len;
	uint64_t ts_ntp;
	/* The virtual source and destination ports are not used for simple profile */
	uint16_t virt_src_port;
	/* These next fields are not needed/used by rist_sender_data_write */
	uint16_t virt_dst_port;
	struct rist_peer *peer;
	uint32_t flow_id;
	/* Get's populated by librist with the rtp_seq on output, can be used on input to tell librist which rtp_seq to use */
	uint64_t seq;
	uint32_t flags;
	struct rist_ref *ref;
};

struct rist_udp_config
{
	int version;

	/* Communication parameters */
	// If a value of 0 is specified for address family, the library
	// will parse the address and populate all communication parameters.
	// Alternatively, use either AF_INET or AF_INET6 and address will be
	// treated like an IP address or hostname
	int address_family;
	int initiate_conn;
	char address[RIST_MAX_STRING_LONG];
	char miface[RIST_MAX_STRING_SHORT];
	uint16_t physical_port;
	char prefix[16];
	int rtp_timestamp;
	int rtp_sequence;
	int rtp;
	uint8_t rtp_ptype;
	uint16_t stream_id;
	enum librist_multiplex_mode multiplex_mode;
	char multiplex_filter[RIST_MAX_STRING_SHORT];//Future usage
};

#ifdef __cplusplus
}
#endif

#endif
