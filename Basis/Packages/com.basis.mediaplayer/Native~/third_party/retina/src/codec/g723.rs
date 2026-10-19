// Copyright (C) The Retina Authors
// SPDX-License-Identifier: MIT OR Apache-2.0

//! G.723.1 audio as specified in [RFC 3551 section 4.5.3](https://datatracker.ietf.org/doc/html/rfc3551#section-4.5.3).

use std::num::{NonZeroU16, NonZeroU32};

use crate::codec::DepacketizeError;

use super::AudioParameters;

const FIXED_CLOCK_RATE: u32 = 8_000;

#[derive(Debug)]
pub(crate) struct Depacketizer {
    pending: Option<super::AudioFrame>,
    parameters: AudioParameters,
}

impl Depacketizer {
    /// Creates a new Depacketizer.
    pub(super) fn new(clock_rate: u32) -> Result<Self, String> {
        if clock_rate != FIXED_CLOCK_RATE {
            return Err(format!(
                "Expected clock rate of {FIXED_CLOCK_RATE} for G.723, got {clock_rate}"
            ));
        }
        Ok(Self {
            pending: None,
            parameters: AudioParameters {
                rfc6381_codec: None,
                frame_length: NonZeroU32::new(240),
                clock_rate: FIXED_CLOCK_RATE,
                channels: const { NonZeroU16::new(1).unwrap() },
                extra_data: Vec::new(),
                codec: super::AudioParametersCodec::Other,
            },
        })
    }

    pub(super) fn parameters(&self) -> Option<super::ParametersRef<'_>> {
        Some(super::ParametersRef::Audio(&self.parameters))
    }

    fn validate(pkt: &crate::rtp::ReceivedPacket) -> bool {
        let payload = pkt.payload();
        let expected_hdr_bits = match payload.len() {
            24 => 0b00,
            20 => 0b01,
            4 => 0b10,
            _ => return false,
        };
        let actual_hdr_bits = payload[0] & 0b11;
        actual_hdr_bits == expected_hdr_bits
    }

    pub(super) fn push(&mut self, pkt: crate::rtp::ReceivedPacket) -> Result<(), String> {
        assert!(self.pending.is_none());
        if !Self::validate(&pkt) {
            return Err(format!(
                "Invalid G.723 packet: {:#?}",
                crate::hex::LimitedHex::new(pkt.payload(), 64),
            ));
        }
        self.pending = Some(super::AudioFrame {
            ctx: *pkt.ctx(),
            loss: pkt.loss(),
            stream_id: pkt.stream_id(),
            timestamp: pkt.timestamp(),
            frame_length: NonZeroU32::new(240).unwrap(),
            data: pkt.into_payload_bytes(),
        });
        Ok(())
    }

    pub(super) fn pull(&mut self) -> Option<Result<super::CodecItem, DepacketizeError>> {
        self.pending
            .take()
            .map(|f| Ok(super::CodecItem::AudioFrame(f)))
    }
}
