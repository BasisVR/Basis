#include "basis_io.h"

#include <string.h>
#include <stdlib.h>
#include <stdio.h>
#include <time.h>

#if defined(_WIN32)
  #include <winsock2.h>
  #include <ws2tcpip.h>
  typedef SOCKET sock_t;
  #define BASIS_INVALID_SOCK INVALID_SOCKET
  #define closesock closesocket
  #define sock_errno WSAGetLastError()
#else
  #include <sys/types.h>
  #include <sys/socket.h>
  #include <netinet/in.h>
  #include <netinet/tcp.h>
  #include <netdb.h>
  #include <unistd.h>
  #include <fcntl.h>
  #include <errno.h>
  #include <sys/time.h>
  #include <poll.h>
  typedef int sock_t;
  #define BASIS_INVALID_SOCK (-1)
  #define closesock close
  #define sock_errno errno
#endif

struct basis_io {
    sock_t fd;
};

void basis_io_global_init(void) {
#if defined(_WIN32)
    WSADATA wsa;
    WSAStartup(MAKEWORD(2, 2), &wsa);
#endif
}

void basis_io_global_shutdown(void) {
#if defined(_WIN32)
    WSACleanup();
#endif
}

static void set_blocking(sock_t fd, int blocking) {
#if defined(_WIN32)
    u_long mode = blocking ? 0 : 1;
    ioctlsocket(fd, FIONBIO, &mode);
#else
    int flags = fcntl(fd, F_GETFL, 0);
    if (flags < 0) return;
    fcntl(fd, F_SETFL, blocking ? (flags & ~O_NONBLOCK) : (flags | O_NONBLOCK));
#endif
}

void basis_io_set_read_timeout(basis_io_t* io, int timeout_ms) {
    if (!io || io->fd == BASIS_INVALID_SOCK) return;
#if defined(_WIN32)
    DWORD tv = (DWORD)timeout_ms;
    setsockopt(io->fd, SOL_SOCKET, SO_RCVTIMEO, (const char*)&tv, sizeof(tv));
#else
    struct timeval tv;
    tv.tv_sec = timeout_ms / 1000;
    tv.tv_usec = (timeout_ms % 1000) * 1000;
    setsockopt(io->fd, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof(tv));
#endif
}

static int local_allowed(void) {
    const char* v = getenv("BASIS_MEDIA_ALLOW_LOCAL");
    return v && v[0];
}

/* The IANA IPv4 Special-Purpose Address Registry entries that are "Globally
 * Reachable: False" — i.e. everything that is not a public unicast destination.
 * Kept in lockstep with the managed guard (BasisMediaPlayerSecurity.IsBlockedAddress)
 * — change both together. See
 * https://www.iana.org/assignments/iana-ipv4-special-registry/ */
static int ipv4_octets_blocked(uint8_t b0, uint8_t b1, uint8_t b2) {
    if (b0 == 0) return 1;                              /* 0/8 "this network" */
    if (b0 == 10) return 1;                             /* 10/8 private */
    if (b0 == 127) return 1;                            /* 127/8 loopback */
    if (b0 == 100 && (b1 & 0xC0) == 64) return 1;       /* 100.64/10 CGNAT */
    if (b0 == 169 && b1 == 254) return 1;               /* 169.254/16 link-local (metadata) */
    if (b0 == 172 && b1 >= 16 && b1 <= 31) return 1;    /* 172.16/12 private */
    if (b0 == 192 && b1 == 0 && b2 == 0) return 1;      /* 192.0.0/24 IETF protocol assignments */
    if (b0 == 192 && b1 == 0 && b2 == 2) return 1;      /* 192.0.2/24 TEST-NET-1 */
    if (b0 == 192 && b1 == 88 && b2 == 99) return 1;    /* 192.88.99/24 6to4 relay anycast (deprecated) */
    if (b0 == 192 && b1 == 168) return 1;               /* 192.168/16 private */
    if (b0 == 198 && (b1 & 0xFE) == 18) return 1;       /* 198.18/15 benchmarking */
    if (b0 == 198 && b1 == 51 && b2 == 100) return 1;   /* 198.51.100/24 TEST-NET-2 */
    if (b0 == 203 && b1 == 0 && b2 == 113) return 1;    /* 203.0.113/24 TEST-NET-3 */
    if (b0 >= 224) return 1;                            /* 224/4 multicast + 240/4 reserved (incl. 255.255.255.255) */
    return 0;
}

/* SSRF guard: reject connecting to a non-global-unicast target. Checks the ACTUAL
 * resolved address, so a public name pointed at a private IP (and DNS rebinding) is
 * caught here at connect time, not just at the URL string. */
static int sockaddr_is_blocked(const struct sockaddr* sa) {
    if (!sa) return 1;
    if (sa->sa_family == AF_INET) {
        const struct sockaddr_in* s = (const struct sockaddr_in*)sa;
        const uint8_t* b = (const uint8_t*)&s->sin_addr.s_addr; /* network order = octets in order */
        return ipv4_octets_blocked(b[0], b[1], b[2]);
    }
    if (sa->sa_family == AF_INET6) {
        const struct sockaddr_in6* s = (const struct sockaddr_in6*)sa;
        const uint8_t* b = (const uint8_t*)s->sin6_addr.s6_addr;
        int i, nz = 0, loop = (b[15] == 1);
        for (i = 0; i < 16; i++) if (b[i]) { nz = 1; break; }
        if (!nz) return 1;                                 /* :: unspecified */
        for (i = 0; i < 15; i++) if (b[i]) { loop = 0; break; }
        if (loop) return 1;                                /* ::1 loopback */
        if ((b[0] & 0xFE) == 0xFC) return 1;               /* fc00::/7 ULA */
        if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) return 1; /* fe80::/10 link-local */
        if (b[0] == 0xFF) return 1;                        /* ff00::/8 multicast */
        {
            int mapped = 1;
            for (i = 0; i < 10; i++) if (b[i]) { mapped = 0; break; }
            if (mapped && b[10] == 0xFF && b[11] == 0xFF)  /* ::ffff:a.b.c.d */
                return ipv4_octets_blocked(b[12], b[13], b[14]);
        }
        if (b[0] == 0x20 && b[1] == 0x02)                  /* 2002::/16 6to4 */
            return ipv4_octets_blocked(b[2], b[3], b[4]);
        return 0;
    }
    return 1;
}

int basis_io_host_is_blocked(const char* host) {
    if (!host || !host[0]) return 1;
    if (local_allowed()) return 0;

    struct addrinfo hints, *res = NULL, *ai;
    memset(&hints, 0, sizeof(hints));
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    /* Fail closed: a name we can't resolve here could resolve to a private
     * address at the platform stack's own lookup a moment later. */
    if (getaddrinfo(host, NULL, &hints, &res) != 0 || !res) return 1;

    /* Block if ANY resolved address is non-global — a host with both a public
     * and a private record must not be usable to reach the private one. */
    int blocked = 0;
    for (ai = res; ai; ai = ai->ai_next)
        if (sockaddr_is_blocked(ai->ai_addr)) { blocked = 1; break; }
    freeaddrinfo(res);
    return blocked;
}

basis_io_t* basis_io_connect(const char* host, int port, int timeout_ms) {
    if (!host || port <= 0) return NULL;

    char portstr[16];
    snprintf(portstr, sizeof(portstr), "%d", port);

    struct addrinfo hints, *res = NULL, *ai;
    memset(&hints, 0, sizeof(hints));
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    hints.ai_protocol = IPPROTO_TCP;
    if (getaddrinfo(host, portstr, &hints, &res) != 0 || !res) return NULL;

    sock_t fd = BASIS_INVALID_SOCK;
    int allow_local = local_allowed();
    for (ai = res; ai; ai = ai->ai_next) {
        if (!allow_local && sockaddr_is_blocked(ai->ai_addr)) continue;
        fd = socket(ai->ai_family, ai->ai_socktype, ai->ai_protocol);
        if (fd == BASIS_INVALID_SOCK) continue;

        /* non-blocking connect with select() timeout */
        set_blocking(fd, 0);
        int rc = connect(fd, ai->ai_addr, (int)ai->ai_addrlen);
        int inprogress = 0;
#if defined(_WIN32)
        inprogress = (rc != 0 && sock_errno == WSAEWOULDBLOCK);
#else
        inprogress = (rc != 0 && (errno == EINPROGRESS || errno == EWOULDBLOCK));
#endif
        if (rc == 0) {
            set_blocking(fd, 1);
            break;
        }
        if (inprogress) {
            fd_set wf;
            FD_ZERO(&wf);
            FD_SET(fd, &wf);
            struct timeval tv;
            tv.tv_sec = timeout_ms / 1000;
            tv.tv_usec = (timeout_ms % 1000) * 1000;
            int sel = select((int)fd + 1, NULL, &wf, NULL, timeout_ms > 0 ? &tv : NULL);
            if (sel > 0) {
                int err = 0;
                socklen_t elen = sizeof(err);
                getsockopt(fd, SOL_SOCKET, SO_ERROR, (char*)&err, &elen);
                if (err == 0) {
                    set_blocking(fd, 1);
                    break;
                }
            }
        }
        closesock(fd);
        fd = BASIS_INVALID_SOCK;
    }
    freeaddrinfo(res);

    if (fd == BASIS_INVALID_SOCK) return NULL;

    int one = 1;
    setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, (const char*)&one, sizeof(one));

    basis_io_t* io = (basis_io_t*)calloc(1, sizeof(*io));
    if (!io) { closesock(fd); return NULL; }
    io->fd = fd;
    basis_io_set_read_timeout(io, timeout_ms > 0 ? timeout_ms : 15000);
    return io;
}

int basis_io_read(basis_io_t* io, uint8_t* buf, int len) {
    if (!io || io->fd == BASIS_INVALID_SOCK || !buf || len <= 0) return -1;
    int n = (int)recv(io->fd, (char*)buf, len, 0);
    return n;
}

int basis_io_read_full(basis_io_t* io, uint8_t* buf, int len) {
    int got = 0;
    while (got < len) {
        int n = basis_io_read(io, buf + got, len - got);
        if (n <= 0) return got;
        got += n;
    }
    return got;
}

int basis_io_write_full(basis_io_t* io, const uint8_t* buf, int len) {
    if (!io || io->fd == BASIS_INVALID_SOCK || !buf) return -1;
    int sent = 0;
    while (sent < len) {
        int n = (int)send(io->fd, (const char*)buf + sent, len - sent, 0);
        if (n <= 0) return -1;
        sent += n;
    }
    return sent;
}

void basis_io_close(basis_io_t* io) {
    if (!io) return;
    if (io->fd != BASIS_INVALID_SOCK) closesock(io->fd);
    free(io);
}

int basis_io_peer_addr(basis_io_t* io, char* buf, int cap) {
    if (!io || io->fd == BASIS_INVALID_SOCK || !buf || cap <= 0) return -1;
    struct sockaddr_storage ss;
    socklen_t slen = sizeof(ss);
    if (getpeername(io->fd, (struct sockaddr*)&ss, &slen) != 0) return -1;
    return getnameinfo((struct sockaddr*)&ss, slen, buf, (socklen_t)cap,
                       NULL, 0, NI_NUMERICHOST) == 0 ? 0 : -1;
}

/* One UDP socket bound to local_port in the given family. Fails (returns
 * invalid) on bind conflicts so the caller can try another local pair. */
static sock_t udp_bind_one(int family, int local_port) {
    sock_t fd = socket(family, SOCK_DGRAM, IPPROTO_UDP);
    if (fd == BASIS_INVALID_SOCK) return BASIS_INVALID_SOCK;

    struct sockaddr_storage local;
    memset(&local, 0, sizeof(local));
    socklen_t llen;
    if (family == AF_INET) {
        struct sockaddr_in* l4 = (struct sockaddr_in*)&local;
        l4->sin_family = AF_INET;
        l4->sin_port = htons((unsigned short)local_port);
        llen = sizeof(*l4);
    } else {
        struct sockaddr_in6* l6 = (struct sockaddr_in6*)&local;
        l6->sin6_family = AF_INET6;
        l6->sin6_port = htons((unsigned short)local_port);
        llen = sizeof(*l6);
    }
    if (bind(fd, (const struct sockaddr*)&local, llen) != 0) { closesock(fd); return BASIS_INVALID_SOCK; }
    return fd;
}

int basis_io_udp_open_pair(const char* host, basis_io_t** rtp, basis_io_t** rtcp,
                           int* local_rtp_port) {
    if (!host || !rtp || !rtcp) return -1;
    *rtp = *rtcp = NULL;

    /* Resolve only to pick the address family the connect target will need. */
    struct addrinfo hints, *res = NULL;
    memset(&hints, 0, sizeof(hints));
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_DGRAM;
    hints.ai_protocol = IPPROTO_UDP;
    if (getaddrinfo(host, NULL, &hints, &res) != 0 || !res) return -1;
    int family = res->ai_family;
    freeaddrinfo(res);
    if (family != AF_INET && family != AF_INET6) return -1;

    sock_t frtp = BASIS_INVALID_SOCK, frtcp = BASIS_INVALID_SOCK;
    int chosen_port = 0;

    /* Even/odd local pair from a 1024-pair window, probed from a randomised
     * start so concurrent sessions and processes don't all contend from the
     * same base; a failed bind steps to the next pair (UDP has no TIME_WAIT —
     * a conflict means the port is genuinely in use). SO_REUSEADDR is
     * deliberately absent: sharing a bound port would route another session's
     * datagrams here. */
    enum { PORT_BASE = 46000, PORT_PAIRS = 1024 };
    /* The salt is shared across demux threads (split-stream sessions negotiate
     * on two); increment atomically so the shared counter stays defined. */
#if defined(_WIN32)
    static volatile LONG probe_salt;
    unsigned salt = (unsigned)InterlockedIncrement(&probe_salt);
#else
    static unsigned probe_salt;
    unsigned salt = __atomic_add_fetch(&probe_salt, 1u, __ATOMIC_RELAXED);
#endif
    unsigned start = ((unsigned)(uintptr_t)&hints ^ (unsigned)time(NULL) ^ salt) % PORT_PAIRS;
    for (int i = 0; i < PORT_PAIRS; ++i) {
        int base = PORT_BASE + 2 * (int)((start + (unsigned)i) % PORT_PAIRS);
        frtp = udp_bind_one(family, base);
        if (frtp == BASIS_INVALID_SOCK) continue;
        frtcp = udp_bind_one(family, base + 1);
        if (frtcp == BASIS_INVALID_SOCK) { closesock(frtp); frtp = BASIS_INVALID_SOCK; continue; }
        chosen_port = base;
        break;
    }
    if (frtp == BASIS_INVALID_SOCK) return -1;

    basis_io_t* a = (basis_io_t*)calloc(1, sizeof(*a));
    basis_io_t* b = (basis_io_t*)calloc(1, sizeof(*b));
    if (!a || !b) { free(a); free(b); closesock(frtp); closesock(frtcp); return -1; }
    a->fd = frtp; b->fd = frtcp;
    *rtp = a; *rtcp = b;
    if (local_rtp_port) *local_rtp_port = chosen_port;
    return 0;
}

int basis_io_udp_connect(basis_io_t* io, const char* host, int port) {
    if (!io || io->fd == BASIS_INVALID_SOCK || !host || port <= 0) return -1;

    char portstr[16];
    snprintf(portstr, sizeof(portstr), "%d", port);

    struct addrinfo hints, *res = NULL, *ai;
    memset(&hints, 0, sizeof(hints));
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_DGRAM;
    hints.ai_protocol = IPPROTO_UDP;
    if (getaddrinfo(host, portstr, &hints, &res) != 0 || !res) return -1;

    int allow_local = local_allowed();
    int rc = -1;
    for (ai = res; ai; ai = ai->ai_next) {
        if (!allow_local && sockaddr_is_blocked(ai->ai_addr)) continue;
        if (connect(io->fd, ai->ai_addr, (int)ai->ai_addrlen) == 0) { rc = 0; break; }
    }
    freeaddrinfo(res);
    return rc;
}

int basis_io_send(basis_io_t* io, const uint8_t* buf, int len) {
    if (!io || io->fd == BASIS_INVALID_SOCK || !buf || len < 0) return -1;
    return (int)send(io->fd, (const char*)buf, len, 0);
}

int basis_io_poll_read(basis_io_t** ios, int n, int timeout_ms) {
    if (!ios || n <= 0 || n > 8) return -1;
#if defined(_WIN32)
    /* Winsock reports a pending socket error (e.g. ICMP port-unreachable on a
     * connected UDP socket) in the exception set, not readfds — fold it into
     * the readable mask so the next read surfaces the error promptly, matching
     * the POSIX POLLERR handling below and the contract in basis_io.h. */
    fd_set rf, ef;
    FD_ZERO(&rf);
    FD_ZERO(&ef);
    sock_t maxfd = 0;
    for (int i = 0; i < n; ++i) {
        if (!ios[i] || ios[i]->fd == BASIS_INVALID_SOCK) continue;
        FD_SET(ios[i]->fd, &rf);
        FD_SET(ios[i]->fd, &ef);
        if (ios[i]->fd > maxfd) maxfd = ios[i]->fd;
    }
    struct timeval tv;
    tv.tv_sec = timeout_ms / 1000;
    tv.tv_usec = (timeout_ms % 1000) * 1000;
    int rc = select((int)maxfd + 1, &rf, NULL, &ef, &tv);
    if (rc < 0) return -1;
    if (rc == 0) return 0;
    int mask = 0;
    for (int i = 0; i < n; ++i)
        if (ios[i] && ios[i]->fd != BASIS_INVALID_SOCK &&
            (FD_ISSET(ios[i]->fd, &rf) || FD_ISSET(ios[i]->fd, &ef))) mask |= 1 << i;
    return mask;
#else
    struct pollfd pf[8];
    int map[8];
    int np = 0;
    for (int i = 0; i < n; ++i) {
        if (!ios[i] || ios[i]->fd == BASIS_INVALID_SOCK) continue;
        pf[np].fd = ios[i]->fd;
        pf[np].events = POLLIN;
        pf[np].revents = 0;
        map[np] = i;
        np++;
    }
    if (np == 0) return 0;
    int rc = poll(pf, (nfds_t)np, timeout_ms);
    if (rc < 0) return -1;
    if (rc == 0) return 0;
    int mask = 0;
    for (int i = 0; i < np; ++i)
        if (pf[i].revents & (POLLIN | POLLERR | POLLHUP)) mask |= 1 << map[i];
    return mask;
#endif
}
