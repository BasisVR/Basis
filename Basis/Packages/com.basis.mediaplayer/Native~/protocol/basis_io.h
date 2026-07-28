/* Blocking TCP client sockets (Winsock / BSD). No TLS — plaintext rtsp/rtmp and
 * plaintext http only. TLS streams use the platform stacks (WinHTTP on Windows,
 * AMediaExtractor on Android). */
#ifndef BASIS_IO_H
#define BASIS_IO_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct basis_io basis_io_t;

/* Connect with a timeout (ms). Returns NULL on failure. */
basis_io_t* basis_io_connect(const char* host, int port, int timeout_ms);

/* Returns bytes read (>0), 0 on orderly close, <0 on error. */
int basis_io_read(basis_io_t* io, uint8_t* buf, int len);

/* Reads exactly `len` bytes unless the connection closes/errors first.
 * Returns len on success, <len on close/error. */
int basis_io_read_full(basis_io_t* io, uint8_t* buf, int len);

/* Writes exactly `len` bytes. Returns len on success, <0 on error. */
int basis_io_write_full(basis_io_t* io, const uint8_t* buf, int len);

/* Sets the per-recv timeout in ms (0 = block forever). */
void basis_io_set_read_timeout(basis_io_t* io, int timeout_ms);

void basis_io_close(basis_io_t* io);

/* Numeric peer address of a connected socket (e.g. "203.0.113.7"), for reusing
 * one validated resolution across further connections. Returns 0 on success. */
int basis_io_peer_addr(basis_io_t* io, char* buf, int cap);

/* Binds a UDP socket pair for RTP/RTCP on two adjacent local ports (RTP even,
 * RTCP odd, per RFC 3550 §11). `host` selects the address family and must
 * later be the connect target. The sockets receive nothing useful until
 * basis_io_udp_connect. Returns 0 and fills the rtp/rtcp outputs (+ the
 * chosen local RTP port); -1 with nothing to free. */
int basis_io_udp_open_pair(const char* host, basis_io_t** rtp, basis_io_t** rtcp,
                           int* local_rtp_port);

/* Connects a UDP socket to host:port. Connecting makes the kernel drop
 * datagrams from any other source and surfaces ICMP port-unreachable as a
 * socket error. The same non-global-unicast guard as basis_io_connect applies
 * to the resolved address. Returns 0 on success. */
int basis_io_udp_connect(basis_io_t* io, const char* host, int port);

/* Sends one datagram (UDP sockets). Returns len on success, <0 on error. */
int basis_io_send(basis_io_t* io, const uint8_t* buf, int len);

/* Waits up to timeout_ms for any of `ios[0..n-1]` (n <= 8) to become readable.
 * Returns a bitmask of readable entries (bit i = ios[i]), 0 on timeout, -1 on
 * error. A socket with a pending error reports readable; the next read fails. */
int basis_io_poll_read(basis_io_t** ios, int n, int timeout_ms);

/* SSRF pre-check for a host about to be fetched through a platform HTTP stack
 * (WinHTTP / JNI HttpsURLConnection) that does not itself apply the
 * non-global-unicast guard basis_io_connect enforces. Resolves the name and
 * returns 1 if it is empty, unresolvable (fail-closed), or resolves to any
 * non-global-unicast address (loopback / RFC1918 / link-local / ULA /
 * multicast). Honours the same BASIS_MEDIA_ALLOW_LOCAL escape hatch. */
int basis_io_host_is_blocked(const char* host);

/* Process-wide one-time init/teardown (WSAStartup on Windows; no-op elsewhere). */
void basis_io_global_init(void);
void basis_io_global_shutdown(void);

#ifdef __cplusplus
}
#endif
#endif
