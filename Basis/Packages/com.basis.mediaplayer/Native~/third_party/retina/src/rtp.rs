// Copyright (C) The Retina Authors
// SPDX-License-Identifier: MIT OR Apache-2.0

//! Handles RTP data as described in
//! [RFC 3550 section 5.1](https://datatracker.ietf.org/doc/html/rfc3550#section-5.1).

use std::convert::TryFrom;
use std::ops::Range;

use bytes::{Buf, Bytes};

use crate::{PacketContext, Timestamp};

/// The minimum length of an RTP header (no CSRCs or extensions).
const MIN_HEADER_LEN: u16 = 12;

/// Raw packet without state-specific interpretation or metadata.
///
/// This design is inspired by [`rtp-rs`](https://crates.io/crates/rtp-rs) in
/// that it primarily validates a raw buffer then provides accessors for it.
/// Some differences though:
///
/// *   allows keeping around the payload range (determined during
///     construction/validation) as a `Range<u16>` , rather than reconstructing
///     on later accesses.
/// *   currently owns the `Bytes`, although this design may change when/if we
///     [redo the buffering
///     model](https://github.com/scottlamb/retina/issues/6).
/// *   directly exposes the sequence number as a `u16`, rather than having an
///     extra type that I find awkward to work with.
#[derive(Eq, PartialEq)]
pub(crate) struct RawPacket(
    /// Full packet data, including headers.
    ///
    /// ```text
    ///  0                   1                   2                   3
    ///  0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
    /// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
    /// |V=2|P|X|  CC   |M|     PT      |       sequence number         |
    /// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
    /// |                           timestamp                           |
    /// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
    /// |           synchronization source (SSRC) identifier            |
    /// +=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+
    /// |            contributing source (CSRC) identifiers             |
    /// |                             ....                              |
    /// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
    /// ```
    pub Bytes,
);

impl RawPacket {
    /// Validates an RTP packet, returning a wrapper and the payload range.
    ///
    /// The payload range is not part of the `RawPacket` to avoid extra padding
    /// bytes within the containing `ReceivedPacket`.
    pub fn new(data: Bytes) -> Result<(Self, Range<u16>), RawPacketError> {
        // RTP doesn't have a defined maximum size but it's implied by the transport:
        // * UDP packets (even with fragmentation) are at most 65,536 (minus IP/UDP headers).
        // * interleaved RTSP data messages have at most 65,536 bytes of data.
        let len = match u16::try_from(data.len()) {
            Ok(l) => l,
            Err(_) => {
                return Err(RawPacketError {
                    reason: "too long",
                    data,
                });
            }
        };
        if len < MIN_HEADER_LEN {
            return Err(RawPacketError {
                reason: "too short",
                data,
            });
        }
        if (data[0] & 0b1100_0000) != 2 << 6 {
            return Err(RawPacketError {
                reason: "must be version 2",
                data,
            });
        }
        let has_padding = (data[0] & 0b0010_0000) != 0;
        let has_extension = (data[0] & 0b0001_0000) != 0;
        let csrc_count = data[0] & 0b0000_1111;
        let csrc_end = MIN_HEADER_LEN + (4 * u16::from(csrc_count));
        let payload_start = if has_extension {
            // https://datatracker.ietf.org/doc/html/rfc3550#section-5.3.1
            //  0                   1                   2                   3
            //  0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
            // +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
            // |      defined by profile       |           length              |
            // +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
            // |                        header extension                       |
            // |                             ....                              |
            if data.len() < usize::from(csrc_end + 4) {
                return Err(RawPacketError {
                    reason: "extension is after end of packet",
                    data,
                });
            }

            // The header extension contains a 16-bit length field that counts the number of 32-bit
            // words in the extension, excluding the four-octet extension header (therefore zero is
            // a valid length).
            let extension_len = u16::from_be_bytes([
                data[usize::from(csrc_end) + 2],
                data[usize::from(csrc_end) + 3],
            ]);
            match extension_len
                .checked_mul(4)
                .and_then(|e| e.checked_add(csrc_end + 4))
            {
                Some(s) => s,
                None => {
                    return Err(RawPacketError {
                        reason: "extension extends beyond maximum packet size",
                        data,
                    });
                }
            }
        } else {
            csrc_end
        };
        if len < payload_start {
            return Err(RawPacketError {
                reason: "payload start is after end of packet",
                data,
            });
        }
        let payload_end = if has_padding {
            if len == payload_start {
                return Err(RawPacketError {
                    reason: "missing padding",
                    data,
                });
            }
            let padding_len = u16::from(data[data.len() - 1]);
            if padding_len == 0 {
                return Err(RawPacketError {
                    reason: "invalid padding length 0",
                    data,
                });
            }
            let payload_end = match len.checked_sub(padding_len) {
                Some(e) => e,
                None => {
                    return Err(RawPacketError {
                        reason: "padding larger than packet",
                        data,
                    });
                }
            };
            if payload_end < payload_start {
                return Err(RawPacketError {
                    reason: "bad padding",
                    data,
                });
            }
            payload_end
        } else {
            len
        };
        Ok((Self(data), payload_start..payload_end))
    }

    #[inline]
    pub fn mark(&self) -> bool {
        (self.0[1] & 0b1000_0000) != 0
    }

    #[inline]
    pub fn sequence_number(&self) -> u16 {
        assert!(self.0.len() >= usize::from(MIN_HEADER_LEN));
        u16::from_be_bytes([self.0[2], self.0[3]])
    }

    #[inline]
    pub fn ssrc(&self) -> u32 {
        assert!(self.0.len() >= usize::from(MIN_HEADER_LEN));
        u32::from_be_bytes([self.0[8], self.0[9], self.0[10], self.0[11]])
    }

    #[inline]
    pub fn payload_type(&self) -> u8 {
        self.0[1] & 0b0111_1111
    }

    #[inline]
    pub fn timestamp(&self) -> u32 {
        assert!(self.0.len() >= usize::from(MIN_HEADER_LEN));
        u32::from_be_bytes([self.0[4], self.0[5], self.0[6], self.0[7]])
    }
}

#[derive(Debug)]
#[doc(hidden)]
pub struct RawPacketError {
    pub reason: &'static str,
    pub data: Bytes,
}

pub(crate) struct RawPacketBuilder {
    pub sequence_number: u16,
    pub timestamp: u32,
    pub payload_type: u8,
    pub ssrc: u32,
    pub mark: bool,
}

impl RawPacketBuilder {
    pub(crate) fn build<P: IntoIterator<Item = u8>>(
        self,
        payload: P,
    ) -> Result<(RawPacket, Range<u16>), &'static str> {
        if self.payload_type >= 0x80 {
            return Err("payload type too large");
        }
        let data: Bytes = [
            2 << 6, // version=2, no padding, no extensions, no CSRCs.
            if self.mark { 0b1000_0000 } else { 0 } | self.payload_type,
        ]
        .into_iter()
        .chain(self.sequence_number.to_be_bytes())
        .chain(self.timestamp.to_be_bytes())
        .chain(self.ssrc.to_be_bytes())
        .chain(payload)
        .collect();
        let len = u16::try_from(data.len()).map_err(|_| "payload too long")?;
        Ok((RawPacket(data), MIN_HEADER_LEN..len))
    }
}

/// A received RTP packet.
///
/// This holds more information than the packet itself: also a
/// [`PacketContext`], the stream, and extended timestamp.
#[derive(Eq, PartialEq)]
pub struct ReceivedPacket {
    // Currently this is constructed from crate::client::rtp, so everything here
    // is pub(crate).
    pub(crate) ctx: PacketContext,
    pub(crate) stream_id: usize,
    pub(crate) timestamp: crate::Timestamp,
    pub(crate) raw: RawPacket,
    pub(crate) payload_range: Range<u16>,

    // TODO: consider dropping this field in favor of a PacketItem::Loss.
    // https://github.com/scottlamb/retina/issues/47
    pub(crate) loss: u16,
}

impl std::fmt::Debug for ReceivedPacket {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ReceivedPacket")
            .field("ctx", &self.ctx)
            .field("stream_id", &self.stream_id)
            .field("timestamp", &self.timestamp)
            .field("ssrc", &self.raw.ssrc())
            .field("sequence_number", &self.raw.sequence_number())
            .field("mark", &self.raw.mark())
            .field("payload", &crate::hex::LimitedHex::new(self.payload(), 64))
            .finish()
    }
}

impl ReceivedPacket {
    #[inline]
    pub fn timestamp(&self) -> crate::Timestamp {
        self.timestamp
    }

    #[inline]
    pub fn mark(&self) -> bool {
        self.raw.mark()
    }

    #[inline]
    pub fn ctx(&self) -> &PacketContext {
        &self.ctx
    }

    #[inline]
    pub fn stream_id(&self) -> usize {
        self.stream_id
    }

    #[inline]
    pub fn ssrc(&self) -> u32 {
        self.raw.ssrc()
    }

    #[inline]
    pub fn sequence_number(&self) -> u16 {
        self.raw.sequence_number()
    }

    /// Returns the raw bytes, including the RTP headers.
    #[inline]
    pub fn raw(&self) -> &[u8] {
        &self.raw.0[..]
    }

    /// Returns only the payload bytes.
    #[inline]
    pub fn payload(&self) -> &[u8] {
        &self.raw.0[usize::from(self.payload_range.start)..usize::from(self.payload_range.end)]
    }

    #[inline]
    pub fn loss(&self) -> u16 {
        self.loss
    }

    /// Consumes the `ReceivedPacket` and returns the `Payload` as a [`Bytes`].
    ///
    /// This is currently is very efficient (no copying or reference-counting),
    /// although that is not an API guarantee.
    #[inline]
    pub fn into_payload_bytes(self) -> Bytes {
        let mut data = self.raw.0;
        data.truncate(usize::from(self.payload_range.end));
        data.advance(usize::from(self.payload_range.start));
        data
    }
}

/// Testing API; exposed for fuzz tests.
#[doc(hidden)]
pub struct ReceivedPacketBuilder {
    pub ctx: PacketContext,
    pub stream_id: usize,
    pub sequence_number: u16,
    pub timestamp: Timestamp,
    pub payload_type: u8,
    pub ssrc: u32,
    pub mark: bool,
    pub loss: u16,
}

impl ReceivedPacketBuilder {
    pub fn build<P: IntoIterator<Item = u8>>(
        self,
        payload: P,
    ) -> Result<ReceivedPacket, &'static str> {
        let (raw, payload_range) = RawPacketBuilder {
            sequence_number: self.sequence_number,
            timestamp: self.timestamp.timestamp as u32,
            payload_type: self.payload_type,
            ssrc: self.ssrc,
            mark: self.mark,
        }
        .build(payload)?;
        Ok(ReceivedPacket {
            ctx: self.ctx,
            stream_id: self.stream_id,
            timestamp: self.timestamp,
            raw,
            payload_range,
            loss: self.loss,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::testutil::init_logging;

    #[test]
    pub fn pkt_with_extension() {
        init_logging();
        let data = b"\x90\x60\x4c\x62\x01\xbb\x3c\xb5\x1c\x04\x15\xb1\xab\xac\x00\x03\
                     \x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x01\x67\x64\x00\x32\
                     \xac\x3c\x6b\x81\x7c\x05\x46\x9b\x82\x80\x82\xa0\x00\x00\x03\x00\
                     \x20\x00\x00\x07\x90\x80\x00";
        let (pkt, payload_range) = RawPacket::new(Bytes::from_static(data)).unwrap();
        let payload = &pkt.0[payload_range.start as usize..payload_range.end as usize];
        assert_eq!(payload_range, 28..55);
        assert_eq!(payload[0], 0x67);
    }
}
