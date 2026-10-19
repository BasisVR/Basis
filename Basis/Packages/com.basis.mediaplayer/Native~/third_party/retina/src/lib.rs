// Copyright (C) The Retina Authors
// SPDX-License-Identifier: MIT OR Apache-2.0

//! High-level RTSP library.
//!
//! Currently this is useful for clients; it will be extended to support
//! servers and proxies.

#![forbid(clippy::print_stderr, clippy::print_stdout)]
// Compiled with the engine but outside its workspace, so the workspace lint
// table does not reach it: each `unsafe` here carries its own allow.
#![deny(unsafe_code, unsafe_op_in_unsafe_fn)]
// I prefer to use from_str_radix(..., 10) to explicitly note the base.
#![allow(clippy::from_str_radix_10)]

use bytes::Bytes;
use log::trace;
use rand::Rng;
use rtsp::msg::Message;
use std::fmt::{Debug, Display};
use std::net::{IpAddr, SocketAddr, UdpSocket};
use std::num::NonZeroU32;
use std::ops::Range;
use std::time::{Instant, SystemTime};

mod error;

mod hex;
mod mostly_ascii;
pub mod rtcp;
pub mod rtp;

/// This is exposed for the fuzz tests. It is not a stable interface.
#[doc(hidden)]
pub mod testutil;

pub use error::Error;

/// Wraps the supplied `ErrorInt` and returns it as an `Err`.
macro_rules! bail {
    ($e:expr) => {
        return Err(crate::error::Error(std::sync::Arc::new($e)))
    };
}

macro_rules! wrap {
    ($e:expr) => {
        crate::error::Error(std::sync::Arc::new($e))
    };
}

pub mod client;
pub mod codec;
//mod error;
#[doc(hidden)]
pub mod rtsp;
mod tokio;

use error::ErrorInt;

/// A received RTSP message.
#[derive(Debug)]
struct ReceivedMessage {
    ctx: RtspMessageContext,
    msg: Message,
    body: Bytes,
}

/// An annotated RTP timestamp.
///
/// This couples together three pieces of information:
///
/// *   The stream's starting time. In client use, this is often as received in the RTSP
///     `RTP-Info` header but may be controlled via [`crate::client::InitialTimestampPolicy`].
///     According to [RFC 3550 section 5.1](https://datatracker.ietf.org/doc/html/rfc3550#section-5.1), "the initial
///     value of the timestamp SHOULD be random".
///
/// *   The codec-specific clock rate.
///
/// *   The timestamp as an `i64`. In client use, its top bits should be inferred from wraparounds
///     of 32-bit RTP timestamps. The Retina client's policy is that timestamps that differ by more
///     than `i32::MAX` from previous timestamps are treated as backwards jumps. It's allowed for
///     a timestamp to indicate a time *before* the stream's starting point.
///
/// In combination, these allow conversion to "normal play time" (NPT): seconds since start of
/// the stream.
///
/// According to [RFC 3550 section 5.1](https://datatracker.ietf.org/doc/html/rfc3550#section-5.1),
/// RTP timestamps "MUST be derived from a clock that increments monotonically". In practice,
/// many RTP servers violate this. The Retina client allows such violations unless
/// [`crate::client::PlayOptions::enforce_timestamps_with_max_jump_secs`] says otherwise.
///
/// [`Timestamp`] can't represent timestamps which overflow/underflow `i64` can't be constructed or
/// elapsed times (`elapsed = timestamp - start`) which underflow `i64`. The client will return
/// error in these cases. This should rarely cause problems. It'd take ~2^32 packets (~4 billion)
/// to advance the time this far forward or backward even with a hostile server.
///
/// The [`Display`] and [`Debug`] implementations currently display:
/// *   the bottom 32 bits, as seen in RTP packet headers. This advances at a
///     codec-specified clock rate.
/// *   the full timestamp.
/// *   NPT
#[derive(Copy, Clone, PartialEq, Eq)]
pub struct Timestamp {
    /// A timestamp which must be compared to `start`.
    timestamp: i64,

    /// The codec-specified clock rate, in Hz. Must be non-zero.
    clock_rate: NonZeroU32,

    /// The stream's starting time, as specified in the RTSP `RTP-Info` header.
    start: u32,
}

impl Timestamp {
    /// Creates a new timestamp unless `timestamp - start` underflows.
    #[inline]
    pub fn new(timestamp: i64, clock_rate: NonZeroU32, start: u32) -> Option<Self> {
        timestamp.checked_sub(i64::from(start)).map(|_| Timestamp {
            timestamp,
            clock_rate,
            start,
        })
    }

    /// Returns time since some arbitrary point before the stream started.
    #[inline]
    pub fn timestamp(&self) -> i64 {
        self.timestamp
    }

    /// Returns timestamp of the start of the stream.
    #[inline]
    pub fn start(&self) -> u32 {
        self.start
    }

    /// Returns codec-specified clock rate, in Hz.
    #[inline]
    pub fn clock_rate(&self) -> NonZeroU32 {
        self.clock_rate
    }

    /// Returns elapsed time since the stream start in clock rate units.
    #[inline]
    pub fn elapsed(&self) -> i64 {
        self.timestamp - i64::from(self.start)
    }

    /// Returns elapsed time since the stream start in seconds, aka "normal play
    /// time" (NPT).
    #[inline]
    pub fn elapsed_secs(&self) -> f64 {
        (self.elapsed() as f64) / (self.clock_rate.get() as f64)
    }

    /// Returns `self + delta` unless it would overflow.
    pub fn try_add(&self, delta: u32) -> Option<Self> {
        // Check for `timestamp` overflow only. We don't need to check for
        // `timestamp - start` underflow because delta is non-negative.
        self.timestamp
            .checked_add(i64::from(delta))
            .map(|timestamp| Timestamp {
                timestamp,
                clock_rate: self.clock_rate,
                start: self.start,
            })
    }
}

impl Display for Timestamp {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(
            f,
            "{} (mod-2^32: {}), npt {:.03}",
            self.timestamp,
            self.timestamp as u32,
            self.elapsed_secs()
        )
    }
}

impl Debug for Timestamp {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        Display::fmt(self, f)
    }
}

/// The Unix epoch as an [`NtpTimestamp`].
pub const UNIX_EPOCH: NtpTimestamp = NtpTimestamp((2_208_988_800) << 32);

/// A wallclock time represented using the format of the Network Time Protocol.
///
/// NTP timestamps are in a fixed-point representation of seconds since
/// 0h UTC on 1 January 1900. The top 32 bits represent the integer part
/// (wrapping around every 68 years) and the bottom 32 bits represent the
/// fractional part.
///
/// This is a simple wrapper around a `u64` in that format, with a `Display`
/// impl that writes the timestamp as a human-readable string. Currently this
/// assumes the time is within 68 years of 1970; the string will be incorrect
/// after `2038-01-19T03:14:07Z`.
///
/// An `NtpTimestamp` isn't necessarily gathered from a real NTP server.
/// Reported NTP timestamps are allowed to jump backwards and/or be complete
/// nonsense.
///
/// The NTP timestamp of the Unix epoch is available via the constant [`UNIX_EPOCH`].
#[derive(Copy, Clone, PartialEq, PartialOrd, Eq, Ord)]
pub struct NtpTimestamp(pub u64);

impl std::fmt::Display for NtpTimestamp {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        let since_epoch = self.0.wrapping_sub(UNIX_EPOCH.0);
        let sec_since_epoch = (since_epoch >> 32) as u32;
        let ns = i32::try_from(((since_epoch & 0xFFFF_FFFF) * 1_000_000_000) >> 32)
            .expect("should be < 1_000_000_000");
        let tm = jiff::Timestamp::new(i64::from(sec_since_epoch), ns)
            .expect("u32 sec should be valid Timestamp");
        std::fmt::Display::fmt(&tm, f)
    }
}

impl std::fmt::Debug for NtpTimestamp {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        // Write both the raw and display forms.
        write!(f, "{} /* {} */", self.0, self)
    }
}

/// A wall time taken from the local machine's realtime clock, used in error reporting.
///
/// This allows formatting via `Debug` and `Display` and conversion to [`SystemTime`].
#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub struct WallTime(jiff::Timestamp);

impl WallTime {
    #[inline]
    fn now() -> Self {
        Self(jiff::Timestamp::now())
    }
}

impl Display for WallTime {
    #[inline]
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        std::fmt::Display::fmt(&self.0, f)
    }
}

impl From<WallTime> for SystemTime {
    #[inline]
    fn from(wall_time: WallTime) -> Self {
        wall_time.0.into()
    }
}

/// RTSP connection context.
///
/// This gives enough information to pick out the flow in a packet capture.
#[derive(Copy, Clone, Debug)]
pub struct ConnectionContext {
    local_addr: std::net::SocketAddr,
    peer_addr: std::net::SocketAddr,
    established_wall: WallTime,
}

impl ConnectionContext {
    #[doc(hidden)]
    pub fn dummy() -> Self {
        let addr = SocketAddr::new(IpAddr::V4(std::net::Ipv4Addr::UNSPECIFIED), 0);
        Self {
            local_addr: addr,
            peer_addr: addr,
            established_wall: WallTime::now(),
        }
    }
}

impl Display for ConnectionContext {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        // TODO: this current hardcodes the assumption we are the client.
        // Change if/when adding server code.
        write!(
            f,
            "{}(me)->{}@{}",
            &self.local_addr, &self.peer_addr, &self.established_wall,
        )
    }
}

/// Context of a received message (or read error) within an RTSP connection.
///
/// When paired with a [`ConnectionContext`], this should allow picking the
/// message out of a packet capture.
#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub struct RtspMessageContext {
    /// The starting byte position within the input stream. The bottom 32 bits
    /// can be compared to the relative TCP sequence number.
    pos: u64,

    /// Time when the application parsed the message. Caveat: this may not
    /// closely match the time on a packet capture if the application is
    /// overloaded (or if `CLOCK_REALTIME` jumps).
    received_wall: WallTime,
    received: std::time::Instant,
}

impl RtspMessageContext {
    #[doc(hidden)]
    pub fn dummy() -> Self {
        Self {
            pos: 0,
            received_wall: WallTime::now(),
            received: std::time::Instant::now(),
        }
    }

    pub fn received(&self) -> std::time::Instant {
        self.received
    }

    pub fn pos(&self) -> u64 {
        self.pos
    }
}

impl Display for RtspMessageContext {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}@{}", self.pos, &self.received_wall)
    }
}

/// Context for an active stream (RTP+RTCP session), either TCP or UDP. Owned version.
#[derive(Copy, Clone, Debug)]
pub struct StreamContext(StreamContextInner);

impl StreamContext {
    #[doc(hidden)]
    pub fn dummy() -> Self {
        StreamContext(StreamContextInner::Dummy)
    }
}

impl Display for StreamContext {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match &self.0 {
            StreamContextInner::Tcp(tcp) => {
                write!(
                    f,
                    "TCP, interleaved channel ids {}-{}",
                    tcp.rtp_channel_id,
                    tcp.rtp_channel_id + 1
                )
            }
            StreamContextInner::Udp(udp) => Display::fmt(udp, f),
            StreamContextInner::Dummy => write!(f, "dummy"),
        }
    }
}

#[derive(Copy, Clone, Debug)]
enum StreamContextInner {
    Tcp(TcpStreamContext),
    Udp(UdpStreamContext),
    Dummy,
}

/// Context for a UDP stream (aka UDP-based RTP transport). Unstable/internal. Exposed for benchmarks.
///
/// This stores only the RTP addresses; the RTCP addresses are assumed to use
/// the same IP and one port higher.
#[doc(hidden)]
#[derive(Copy, Clone, Debug)]
pub struct UdpStreamContext {
    local_ip: IpAddr,
    peer_ip: IpAddr,
    local_rtp_port: u16,
    peer_rtp_port: u16,
}

/// Context for a TCP stream. Unstable/internal. Exposed for benchmarks.
///
/// This stores only the RTP channel id; the RTCP channel id is assumed to be one higher.
#[doc(hidden)]
#[derive(Copy, Clone, Debug)]
pub struct TcpStreamContext {
    rtp_channel_id: u8,
}

impl Display for UdpStreamContext {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        // TODO: this assumes we are the client. Revisit when adding server support.
        write!(
            f,
            "{}:{}-{}(me) -> {}:{}-{}",
            self.local_ip,
            self.local_rtp_port,
            self.local_rtp_port + 1,
            self.peer_ip,
            self.peer_rtp_port,
            self.peer_rtp_port + 1
        )
    }
}

/// Context for an RTP or RTCP packet, received either via RTSP interleaved data or UDP.
///
/// Should be paired with an [`ConnectionContext`] of the RTSP connection that started
/// the session. In the interleaved data case, it's assumed the packet was received over
/// that same connection.
#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub struct PacketContext(PacketContextInner);

impl PacketContext {
    #[inline]
    pub fn received(&self) -> Instant {
        match self.0 {
            PacketContextInner::Tcp { msg_ctx } => msg_ctx.received,
            PacketContextInner::Udp { received, .. } => received,
            PacketContextInner::Dummy => Instant::now(),
        }
    }

    #[inline]
    pub fn received_wall(&self) -> WallTime {
        match self.0 {
            PacketContextInner::Tcp { msg_ctx } => msg_ctx.received_wall,
            PacketContextInner::Udp { received_wall, .. } => received_wall,
            PacketContextInner::Dummy => WallTime::now(),
        }
    }

    #[doc(hidden)]
    pub fn dummy() -> PacketContext {
        Self(PacketContextInner::Dummy)
    }
}

#[derive(Copy, Clone, Debug, PartialEq, Eq)]
enum PacketContextInner {
    Tcp {
        msg_ctx: RtspMessageContext,
    },
    Udp {
        received: Instant,
        received_wall: WallTime,
    },
    Dummy,
}

impl Display for PacketContext {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self.0 {
            PacketContextInner::Udp { received_wall, .. } => {
                std::fmt::Display::fmt(&received_wall, f)
            }
            PacketContextInner::Tcp { msg_ctx } => std::fmt::Display::fmt(&msg_ctx, f),
            PacketContextInner::Dummy => write!(f, "dummy"),
        }
    }
}

/// A pair of local UDP sockets used for RTP and RTCP transmission.
///
/// The RTP port is always even, and the RTCP port is always the following (odd) integer.
struct UdpPair {
    rtp_port: u16,
    rtp_socket: UdpSocket,
    rtcp_socket: UdpSocket,
}

impl UdpPair {
    fn for_ip(ip_addr: IpAddr) -> Result<Self, std::io::Error> {
        const MAX_TRIES: usize = 10;
        const ALLOWED_RTP_RANGE: Range<u16> = 5000..65000; // stolen from ffmpeg's defaults.
        let mut rng = rand::thread_rng();
        for i in 0..MAX_TRIES {
            let rtp_port = rng.gen_range(ALLOWED_RTP_RANGE) & !0b1;
            debug_assert!(ALLOWED_RTP_RANGE.contains(&rtp_port));
            let rtp_addr = SocketAddr::new(ip_addr, rtp_port);
            // Windows reports ports inside its excluded ephemeral
            // ranges (Hyper-V/WinNAT reservations) as PermissionDenied
            // rather than AddrInUse; both mean "try another port".
            let rtp_socket = match UdpSocket::bind(rtp_addr) {
                Ok(s) => s,
                Err(e)
                    if matches!(
                        e.kind(),
                        std::io::ErrorKind::AddrInUse | std::io::ErrorKind::PermissionDenied
                    ) =>
                {
                    trace!(
                        "Try {}/{}: unable to bind RTP addr {:?}",
                        i, MAX_TRIES, rtp_addr
                    );
                    continue;
                }
                Err(e) => return Err(e),
            };
            let rtcp_addr = SocketAddr::new(ip_addr, rtp_port + 1);
            let rtcp_socket = match UdpSocket::bind(rtcp_addr) {
                Ok(s) => s,
                Err(e)
                    if matches!(
                        e.kind(),
                        std::io::ErrorKind::AddrInUse | std::io::ErrorKind::PermissionDenied
                    ) =>
                {
                    trace!(
                        "Try {}/{}: unable to bind RTCP addr {:?}",
                        i, MAX_TRIES, rtcp_addr
                    );
                    continue;
                }
                Err(e) => return Err(e),
            };
            return Ok(Self {
                rtp_port,
                rtp_socket,
                rtcp_socket,
            });
        }
        Err(std::io::Error::new(
            std::io::ErrorKind::AddrInUse,
            format!(
                "Unable to find even/odd pair in {}:{}..{} after {} tries",
                ip_addr, ALLOWED_RTP_RANGE.start, ALLOWED_RTP_RANGE.end, MAX_TRIES
            ),
        ))
    }
}

// Let's assume pointers are either 32-bit or 64-bit so we can do the following
// infallible conversions.
fn to_usize<V: Into<u32>>(v: V) -> usize {
    const {
        assert!(std::mem::size_of::<u32>() <= std::mem::size_of::<usize>());
    }
    v.into() as usize
}
fn to_u64(v: usize) -> u64 {
    const {
        assert!(std::mem::size_of::<usize>() <= std::mem::size_of::<u64>());
    }
    v as u64
}

#[cfg(test)]
mod test {
    use std::net::Ipv4Addr;

    use super::*;

    #[test]
    fn local_udp_pair() {
        // Just test that it succeeds.
        UdpPair::for_ip(IpAddr::V4(Ipv4Addr::LOCALHOST)).unwrap();
    }
}
