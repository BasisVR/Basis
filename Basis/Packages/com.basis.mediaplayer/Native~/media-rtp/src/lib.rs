#![forbid(unsafe_code)]

//! Sans-IO RTP receive substrate: sequence unwrap, bounded
//! reorder, RFC 3550 jitter/loss accounting, sender-report tracking and
//! receiver-report composition. Shared by the RTSP-UDP and WHEP lanes,
//! so nothing here knows about sockets, retina or any transport: the
//! caller feeds datagrams and an explicit `now` (monotonic microseconds
//! as [`MediaTime`]) and polls for in-order packets, outbound RTCP and
//! the next wake deadline.
//!
//! One [`RtpReceiver`] per RTP stream (one media sender per stream, as
//! RTSP and WHEP both have it). The remote source is pinned to the first
//! RTP packet's SSRC, not to any advertised value: advertised SSRCs are
//! placeholder-prone (all-zero on some edges) while the stream itself
//! is authoritative.

mod receiver;
mod reports;

use std::num::NonZeroU32;

pub use receiver::{
    OrderedPacket, PacketRejected, ReceiverConfig, ReceiverStats, RtpFields, RtpReceiver,
};
pub use reports::SenderInfo;

/// Convert a span of RTP clock units to microseconds.
///
/// The unit count is an *extended* timestamp difference, so it carries no
/// ceiling of its own: a sender chooses the 32-bit wire values, each one
/// can move the unwrapped total by up to 2^31, and nothing bounds where
/// the running total ends up. Scaling by 1 000 000 in i64 would overflow
/// after a few thousand hostile packets, silently in release builds,
/// leaving an arbitrary value as a presentation timestamp or alignment
/// anchor. Widening makes the multiply exact, and clamping keeps the
/// narrowing from truncating.
pub fn units_to_us(units: i64, clock_rate: NonZeroU32) -> i64 {
    (i128::from(units) * 1_000_000 / i128::from(clock_rate.get()))
        .clamp(i128::from(i64::MIN), i128::from(i64::MAX)) as i64
}

/// The NTP 32.32 timestamp a stream's elapsed-zero sits at, from a sender
/// report's own NTP and how far into the stream that report was taken.
///
/// `elapsed_us` comes from [`units_to_us`], which saturates rather than
/// wrapping, so its extremes are exactly the values that a float
/// conversion rounds and an `as u64` truncates. The offset is built
/// widened and clamped instead, from the magnitude, so the sign is a
/// branch rather than something a cast has to carry (negation would
/// itself overflow at `i64::MIN`).
pub fn ntp_at_zero(sr_ntp: u64, elapsed_us: i64) -> u64 {
    let ticks = (i128::from(elapsed_us.unsigned_abs()) << 32) / 1_000_000;
    let offset = ticks.min(i128::from(u64::MAX)) as u64;
    if elapsed_us >= 0 {
        sr_ntp.wrapping_sub(offset)
    } else {
        sr_ntp.wrapping_add(offset)
    }
}
