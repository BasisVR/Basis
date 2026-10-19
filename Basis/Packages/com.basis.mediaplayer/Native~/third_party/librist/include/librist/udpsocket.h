/*
 * Copyright © 2020, VideoLAN and librist authors
 * Copyright © 2019-2020 SipRadius LLC
 * All rights reserved.
 *
 * SPDX-License-Identifier: BSD-2-Clause
 */

#ifndef UDPSOCKET_H
#define UDPSOCKET_H

#include <stdlib.h>
#include <stdint.h>
#include <string.h>
#include <stdio.h>
#include "common.h"

/* TODO: check errno on windows */
#include <errno.h>

#define UDPSOCKET_MAX_HOPS 32

/* Windows */
#ifdef _WIN32
#include <winsock2.h>
#define _WINSOCKAPI_
#include <windows.h>
#include <ws2tcpip.h>
#define AF_LOCAL AF_UNSPEC
typedef int socklen_t;

/* POSIX */
#else 
#include <sys/types.h>
#include <sys/socket.h>
#include <netdb.h>
#include <unistd.h>
#include <arpa/inet.h>
#include <netinet/in.h>
#include <netinet/ip.h>
#include <netinet/ip6.h>
#include <net/if.h>
#include <poll.h>
#endif /* Windows / POSIX */

#ifdef __cplusplus
extern "C" {
#endif

/*** Public API ***/
#define UDPSOCKET_SOCK_BUFSIZE (1048576)

typedef struct udpsocket_url_param {
	char *key;
	char *val;
} udpsocket_url_param_t;

/* Open a udp socket of family [af]
 *
 * Returns: socket descriptor, -1 for error (errno is set)
 *
 */
RIST_API int udpsocket_open(uint16_t af);

/* Open a udp socket and binds it to local [host] + [port].
 *
 * binds to multicast interface [mciface], (if not NULL).
 *
 * Returns: socket descriptor, -1 for error (errno is set)
 * (In case of gai_error, -1 is returned, errno is not set, check gai_error)
 *
 */
RIST_API int udpsocket_open_bind(const char *host, uint16_t port, const char *mciface);

/*
 * Try to set RX buffer to 1Mbyte and fallback to 256Kbytes if that fails
 * Returns -1 on error, 0 on success.
 */
RIST_API int udpsocket_set_optimal_buffer_size(int sd);

/*
 * Try to set TX buffer to 1Mbyte and fallback to 256Kbytes if that fails
 * Returns -1 on error, 0 on success.
 */
RIST_API int udpsocket_set_optimal_buffer_send_size(int sd);

/*
 * Explicitly set RX buffer size for [sd] to [bufsize], in bytes.
 * Returns -1 on error, 0 on success.
 */
RIST_API int udpsocket_set_buffer_size(int sd, uint32_t bufsize);

/*
 * Explicitly set TX buffer size for [sd] to [bufsize], in bytes.
 * Returns -1 on error, 0 on success.
 */
RIST_API int udpsocket_set_buffer_send_size(int sd, uint32_t bufsize);

/*
 *
 * Retrieve current RX buffer size for [sd].
 * Returns 0 on error, current RX bufsize on success.
 */
RIST_API uint32_t udpsocket_get_buffer_size(int sd);

/*
 *
 * Retrieve current TX buffer size for [sd].
 * Returns 0 on error, current TX bufsize on success.
 */
RIST_API uint32_t udpsocket_get_buffer_send_size(int sd);

/*
 * Explicitly set the mcast interface for the socket [sd] to [mciface] for address
 * family [family].
 * Returns 0 on success, -1 on error (errno is set accordingly).
 */
RIST_API int udpsocket_set_mcast_iface(int sd, const char *mciface, uint16_t family);

/* Open a udp socket and connect it to remote [host] + [port].
 *
 * binds to multicast interface [mciface], (if not NULL).
 *
 * Returns: socket descriptor, -1 for error (errno is set)
 * (In case of gai_error, -1 is returned, errno is not set, check gai_error)
 *
 */
RIST_API int udpsocket_open_connect(const char *host, uint16_t port, const char *mciface);

RIST_API int udpsocket_resolve_host(const char *host, uint16_t port, struct sockaddr *addr);

RIST_API int udpsocket_set_nonblocking(int sd);
RIST_API int udpsocket_send(int sd, const void *buf, size_t size);
RIST_API int udpsocket_send_nonblocking(int sd, const void *buf, size_t size);
RIST_API int udpsocket_sendto(int sd, const void *buf, size_t size, const char *host, uint16_t port);
RIST_API int udpsocket_recv(int sd, void *buf, size_t size);
RIST_API int udpsocket_recvfrom(int sd, void *buf, size_t size, int flags, struct sockaddr *addr, socklen_t *addr_len);
RIST_API int udpsocket_close(int sd);
RIST_API int udpsocket_parse_url(char *url, char *address, int address_maxlen, uint16_t *port, int *local);
RIST_API int udpsocket_parse_url_parameters(const char *url, udpsocket_url_param_t *params,
         int max_params, uint32_t *clean_url_len);


/* evsocket related functions */

#define EVSOCKET_EV_READ POLLIN
#define EVSOCKET_EV_WRITE POLLOUT

struct evsocket_event;
struct evsocket_ctx;

/**
 * @brief Initialize the evsocket context.
 *
 * Use this API to initialize the evsocket context.
 *
 * @return the evsocket context, NULL for OOM error
 */
RIST_API struct evsocket_ctx *evsocket_create(void);

/**
 * @brief Start the master socket event processing loop
 *
 * Use this API to start the main loop for socket event processing
 *
 * @param ctx evsocket context
 * @param timeout How long to wait for socket events (ms), 0 for no wait, -1 for infinite
 * @return void
 */
RIST_API void evsocket_loop(struct evsocket_ctx *ctx, int timeout);

/**
 * @brief Signal the master socket event processing loop to stop
 *
 * Use this API to stop the main loop for socket event processing
 *
 * @param ctx evsocket context
 * @return void
 */
RIST_API void evsocket_loop_stop(struct evsocket_ctx *ctx);

/**
 * @brief Process pending socket events
 *
 * Use this API to process pending socket events
 *
 * @param ctx evsocket context
 * @param timeout How long to wait for socket events (ms), 0 for no wait, -1 for infinite
 * @param max_events Maximum number of events to process
 * @return void
 */
RIST_API int evsocket_loop_single(struct evsocket_ctx *ctx, int timeout, int max_events);

/**
 * @brief Destroy the evsocket context.
 *
 * Use this API to destroy the evsocket context (full cleanup is performed).
 *
 * @return void
 */
RIST_API void evsocket_destroy(struct evsocket_ctx *ctx);

/**
 * @brief Add a new socket event handler
 *
 * Use this API to add a new socket event handler
 *
 * @param ctx evsocket context
 * @param callback pointer to the callback function to process the event
 * @param err_callback pointer to the err_callback function to process poll errors
 * @param arg the extra argument passed to the `callback` or `err_callback` functions
 * @return void
 */
RIST_API struct evsocket_event *evsocket_addevent(struct evsocket_ctx *ctx, int fd, short events,
			void (*callback)(struct evsocket_ctx *ctx, int fd, short revents, void *arg),
			void (*err_callback)(struct evsocket_ctx *ctx, int fd, short revents, void *arg),
			void *arg);

/**
 * @brief Delete a socket event handler
 *
 * Use this API to delete an existing socket event handler
 *
 * @param ctx evsocket context
 * @param e evsocket_event object pointer for the event handler to delete
 * @return void
 */
RIST_API void evsocket_delevent(struct evsocket_ctx *ctx, struct evsocket_event *e);

/**
 * @brief Retrieve the evsocket event handler count.
 *
 * Use this API to retrieve the number of active evsocket event handlers.
 *
 * @param ctx evsocket context
 * @return number of active event handlers
 */
RIST_API int evsocket_geteventcount(struct evsocket_ctx *ctx);

#ifdef __cplusplus
}
#endif

#endif /* ifndef UDPSOCKET_H */
