/*
 * Copyright © 2020, VideoLAN and librist authors
 * Copyright © 2019-2020 SipRadius LLC
 * All rights reserved.
 *
 * SPDX-License-Identifier: BSD-2-Clause
 */

#ifndef LIBRIST_PEER_H
#define LIBRIST_PEER_H

#include "common.h"
#include "headers.h"
#include "librist_config.h"
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

struct rist_peer;

/* Default peer config values */
#define RIST_DEFAULT_VIRT_SRC_PORT (1971)
#define RIST_DEFAULT_VIRT_DST_PORT (1968)
#define RIST_DEFAULT_RECOVERY_MODE RIST_RECOVERY_MODE_TIME
#define RIST_DEFAULT_RECOVERY_MAXBITRATE (100000)
#define RIST_DEFAULT_RECOVERY_MAXBITRATE_RETURN (0)
#define RIST_DEFAULT_RECOVERY_LENGTH_MIN (1000)
#define RIST_DEFAULT_RECOVERY_LENGHT_MIN _Pragma ("GCC warning \"'RIST_DEFAULT_RECOVERY_LENGHT_MIN' is deprecated, use RIST_DEFAULT_RECOVERY_LENGTH_MIN\"")  RIST_DEFAULT_RECOVERY_LENGTH_MIN
#define RIST_DEFAULT_RECOVERY_LENGTH_MAX (1000)
#define RIST_DEFAULT_RECOVERY_LENGHT_MAX _Pragma ("GCC warning \"'RIST_DEFAULT_RECOVERY_LENGHT_MAX' is deprecated, use RIST_DEFAULT_RECOVERY_LENGTH_MAX\"")  RIST_DEFAULT_RECOVERY_LENGTH_MAX
#define RIST_DEFAULT_RECOVERY_REORDER_BUFFER (15)
#define RIST_DEFAULT_RECOVERY_RTT_MIN (5)
#define RIST_DEFAULT_RECOVERY_RTT_MAX (500)
#define RIST_DEFAULT_CONGESTION_CONTROL_MODE RIST_CONGESTION_CONTROL_MODE_NORMAL
#define RIST_DEFAULT_MIN_RETRIES (6)
#define RIST_DEFAULT_MAX_RETRIES (20)
#define RIST_DEFAULT_VERBOSE_LEVEL RIST_LOG_INFO
#define RIST_DEFAULT_PROFILE RIST_PROFILE_MAIN
#define RIST_DEFAULT_SESSION_TIMEOUT (2000)
#define RIST_DEFAULT_KEEPALIVE_INTERVAL (1000)
#define RIST_DEFAULT_TIMING_MODE RIST_TIMING_MODE_SOURCE

enum rist_timing_mode
{
	RIST_TIMING_MODE_SOURCE = 0,
	RIST_TIMING_MODE_ARRIVAL = 1,
	RIST_TIMING_MODE_RTC = 2
};

enum rist_recovery_mode
{
	RIST_RECOVERY_MODE_UNCONFIGURED = 0,
	RIST_RECOVERY_MODE_DISABLED = 1,
	RIST_RECOVERY_MODE_TIME = 2,
};

enum rist_congestion_control_mode
{
	RIST_CONGESTION_CONTROL_MODE_OFF = 0,
	RIST_CONGESTION_CONTROL_MODE_NORMAL = 1,
	RIST_CONGESTION_CONTROL_MODE_AGGRESSIVE = 2
};

#define RIST_PEER_CONFIG_VERSION (0)

struct rist_peer_config
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

	/* The virtual destination port is not used for simple profile */
	uint16_t virt_dst_port;

	/* Recovery options */
	enum rist_recovery_mode recovery_mode;
	uint32_t recovery_maxbitrate; /* kbps */
	uint32_t recovery_maxbitrate_return; /* kbps */
	uint32_t recovery_length_min; /* ms */
	uint32_t recovery_length_max; /* ms */
	uint32_t recovery_reorder_buffer; /* ms */
	uint32_t recovery_rtt_min; /* ms */
	uint32_t recovery_rtt_max; /* ms */

	/* Load balancing weight (use 0 for duplication) */
	uint32_t weight;

	/* Encryption */
	char secret[RIST_MAX_STRING_SHORT];
	int key_size;
	uint32_t key_rotation;

	/* Compression (sender only as receiver is auto detect) */
	int compression;

	/* cname identifier for rtcp packets */
	char cname[RIST_MAX_STRING_SHORT];

	/* Congestion control */
	enum rist_congestion_control_mode congestion_control_mode;
	uint32_t min_retries;
	uint32_t max_retries;

	/* Connection options */
	uint32_t session_timeout;
	uint32_t keepalive_interval;
	enum rist_timing_mode timing_mode;
	char srp_username[RIST_MAX_STRING_LONG];
	char srp_password[RIST_MAX_STRING_LONG];
};

/**
 * @brief Populate a preallocated peer_config structure with library default values
 *
 * @return 0 on success or non-zero on error.
 */
RIST_API int rist_peer_config_defaults_set(struct rist_peer_config *peer_config);

/**
 * @brief Parses rist url for peer config data (encryption, compression, etc)
 *
 * Use this API to parse a generic URL string and turn it into a meaninful peer_config structure
 *
 * @param url a pointer to a url to be parsed, i.e. rist://myserver.net:1234?buffer=100&cname=hello
 * @param[out] peer_config a pointer to a the rist_peer_config structure (NULL is allowed).
 * When passing NULL, the library will allocate a new rist_peer_config structure with the latest
 * default values and it expects the application to free it when it is done using it.
 * @return 0 on success or non-zero on error. The value returned is actually the number
 * of parameters that are valid
 */
RIST_DEPRECATED RIST_API int rist_parse_address(const char *url, const struct rist_peer_config **peer_config);
RIST_API int rist_parse_address2(const char *url, struct rist_peer_config **peer_config);

/**
 * @brief Free the rist_peer_config structure memory allocation
 *
 * @return 0 on success or non-zero on error.
 */
RIST_DEPRECATED RIST_API int rist_peer_config_free(const struct rist_peer_config **peer_config);
RIST_API int rist_peer_config_free2(struct rist_peer_config **peer_config);

/**
 * @brief Add a peer to the RIST session
 *
 * One sender can send data to multiple peers.
 *
 * @param ctx RIST context
 * @param[out] peer Store the new peer pointer
 * @param config a pointer to the struct rist_peer_config, which contains
 *        the configuration parameters for the peer endpoint.
 * @return 0 on success, -1 in case of error.
 */
RIST_API int rist_peer_create(struct rist_ctx *ctx,
		struct rist_peer **peer, const struct rist_peer_config *config);

/**
 * @brief Removes a peer from the RIST session.
 *
 * @param ctx RIST context
 * @param peer a pointer to the struct rist_peer, which
 *        points to the peer endpoint.
 * @return 0 on success, -1 in case of error.
 */
RIST_API int rist_peer_destroy(struct rist_ctx *ctx,
		struct rist_peer *peer);

/**
 * @brief Set the weight of a given peer.
 *
 * @param ctx RIST context
 * @param peer The peer to set the weight for
 * @param weight The weight to assign to the peer
 * @return 0 on success, -1 in case of error.
 */
RIST_API int rist_peer_weight_set(struct rist_ctx *ctx, struct rist_peer *peer, const uint32_t weight);

RIST_API int rist_peer_get_socket(struct rist_peer *peer, int *socket, int *socket_extra);

enum rist_connection_status
{
	RIST_CONNECTION_ESTABLISHED = 0,
	RIST_CONNECTION_TIMED_OUT = 1,
	RIST_CLIENT_CONNECTED = 2,
	RIST_CLIENT_TIMED_OUT = 3
};

/**
 * @brief Connection status callback function
 *
 * Optional calling application provided function for receiving connection status changes for peers.
 *
 * @param arg optional user data set via rist_connection_status_callback_set
 * @param peer peer associated with the event
 * @param rist_peer_connection_status status value
 * @return void.
 */
typedef void (*connection_status_callback_t)(void *arg, struct rist_peer *peer, enum rist_connection_status peer_connection_status);

/**
 * @brief Set callback for receiving connection status change events
 *
 * @param ctx RIST context
 * @param connection_status_callback_t Callback function that will be called.
 * @param arg extra arguments for callback function
 */
RIST_API int rist_connection_status_callback_set(struct rist_ctx *ctx, connection_status_callback_t, void *arg);

typedef int (*rist_auth_handler_connect_cb)(void *arg, const char* conn_ip, uint16_t conn_port, const char* local_ip, uint16_t local_port,struct rist_peer *peer);
typedef int (*rist_auth_handler_disconnect_cb)(void *arg, struct rist_peer *peer);

/**
 * @brief Assign dynamic authentication handler
 *
 * Whenever a new peer is connected, @a connect_cb is called.
 * Whenever a new peer is disconnected, @a disconn_cb is called.
 *
 * @param ctx RIST context
 * @param connect_cb A pointer to the function that will be called when a new peer
 * connects. Return 0 or -1 to authorize or decline (NULL function pointer is valid)
 * @param disconn_cb A pointer to the function that will be called when a new peer
 * is marked as dead (NULL function pointer is valid)
 * @param arg is an the extra argument passed to the `conn_cb` and `disconn_cb`
 */
RIST_API int rist_auth_handler_set(struct rist_ctx *ctx,
		rist_auth_handler_connect_cb connect_cb,
		rist_auth_handler_disconnect_cb disconnect_cb,
		void *arg);


RIST_API uint32_t rist_peer_get_id(const struct rist_peer *peer);

/**
 * @brief Retrieve the cname associated to a peer (if any)
 *
 * @param peer The peer to extract the cname from
 * @param[out] cname a pointer to the string containing the cname
 * @return the length of the cname string
 */
RIST_API uint32_t rist_peer_get_cname(const struct rist_peer *peer, const char **cname);

#if HAVE_SRP_SUPPORT
/*
	@brief Update the shared passphrase for the peer
*/
RIST_API int rist_peer_update_secret(struct rist_peer *peer, const char* password);
#endif
#ifdef __cplusplus
}
#endif

#endif /* LIBRIST_PEER_H */
