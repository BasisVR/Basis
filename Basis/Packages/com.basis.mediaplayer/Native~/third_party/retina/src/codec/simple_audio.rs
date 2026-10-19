// Copyright (C) The Retina Authors
// SPDX-License-Identifier: MIT OR Apache-2.0

//! Fixed-size audio sample codecs as defined in
//! [RFC 3551 section 4.5](https://datatracker.ietf.org/doc/html/rfc3551#section-4.5).

use std::num::{NonZeroU16, NonZeroU32};

use super::{AudioParameters, CodecItem};

#[derive(Debug)]
pub(crate) struct Depacketizer {
    parameters: AudioParameters,
    pending: Option<super::AudioFrame>,
    bits_per_sample: u32,
}

impl Depacketizer {
    /// Creates a new Depacketizer.
    pub(super) fn new(clock_rate: u32, bits_per_sample: u32, channels: Option<NonZeroU16>) -> Self {
        Self {
            parameters: AudioParameters {
                rfc6381_codec: None,
                frame_length: None, // variable
                clock_rate,
                channels: channels.map_or(const { NonZeroU16::new(1).unwrap() }, |c| c),
                extra_data: Vec::new(),
                codec: super::AudioParametersCodec::Other,
            },
            bits_per_sample,
            pending: None,
        }
    }

    pub(super) fn parameters(&self) -> Option<super::ParametersRef<'_>> {
        Some(super::ParametersRef::Audio(&self.parameters))
    }

    fn frame_length(&self, payload_len: usize) -> Option<NonZeroU32> {
        // This calculation could be strength-reduced but it's just once per frame anyway.
        // Let's do it in a straightforward way.
        assert!(payload_len < usize::from(u16::MAX));
        let bits = (payload_len) as u32 * 8;
        match bits.is_multiple_of(self.bits_per_sample) {
            false => None,
            true => NonZeroU32::new(bits / self.bits_per_sample),
        }
    }

    pub(super) fn push(&mut self, pkt: crate::rtp::ReceivedPacket) -> Result<(), String> {
        assert!(self.pending.is_none());
        let payload = pkt.payload();
        let frame_length = self.frame_length(payload.len()).ok_or_else(|| {
            format!(
                "invalid length {} for payload of {}-bit audio samples",
                payload.len(),
                self.bits_per_sample
            )
        })?;
        self.pending = Some(super::AudioFrame {
            loss: pkt.loss(),
            ctx: *pkt.ctx(),
            stream_id: pkt.stream_id(),
            timestamp: pkt.timestamp(),
            frame_length,
            data: pkt.into_payload_bytes(),
        });
        Ok(())
    }

    pub(super) fn pull(&mut self) -> Option<Result<super::CodecItem, super::DepacketizeError>> {
        self.pending.take().map(|f| Ok(CodecItem::AudioFrame(f)))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn depacketizer_8bit() -> Depacketizer {
        Depacketizer::new(8000, 8, None)
    }

    fn depacketizer_16bit() -> Depacketizer {
        Depacketizer::new(16000, 16, None)
    }

    #[test]
    fn frame_length_valid_8bit() {
        // 384 bytes of 8-bit samples = 384 samples (G.711 PCMA/PCMU)
        let d = depacketizer_8bit();
        assert_eq!(d.frame_length(384), Some(NonZeroU32::new(384).unwrap()));
    }

    #[test]
    fn frame_length_invalid_8bit() {
        // Not applicable for 8-bit: any byte count is divisible by 8 bits.
        // But 0 bytes should return None (NonZeroU32::new(0) is None).
        let d = depacketizer_8bit();
        assert_eq!(d.frame_length(0), None);
    }

    #[test]
    fn frame_length_valid_16bit() {
        // 320 bytes = 160 16-bit samples
        let d = depacketizer_16bit();
        assert_eq!(d.frame_length(320), Some(NonZeroU32::new(160).unwrap()));
    }

    #[test]
    fn frame_length_invalid_16bit() {
        // 321 bytes is not divisible by 2 bytes per sample
        let d = depacketizer_16bit();
        assert_eq!(d.frame_length(321), None);
    }
}
