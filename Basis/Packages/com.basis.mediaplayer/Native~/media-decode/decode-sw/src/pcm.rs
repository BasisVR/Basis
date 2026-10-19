//! Raw integer PCM: widen to the ring's float samples and put the channels
//! in WAVE order. No OS decoder is involved, so a chunk is ready the moment
//! it is submitted.
//!
//! Serves both PCM lanes. RIFF/WAVE arrives little-endian and already in
//! WAVE order; Blu-ray HDMV LPCM arrives big-endian and, for the layouts
//! that carry an LFE or side pair, in its own order.

use media_decode::{AudioDecoder, DecodeError, PcmChunk, SubmitOutcome};

/// Format announce private data: `[channel_assignment, bits_code, flags]`.
/// Assignment 0 means the channels already arrive in WAVE order; otherwise
/// it is the Blu-ray `channel_assignment`. Bits code 1 = 16-bit, 3 = 24-bit
/// (the Blu-ray coding, reused for WAV). Flags bit 0 marks little-endian
/// samples.
const CONFIG_LEN: usize = 3;

/// Stream order to WAVE order for the Blu-ray assignments whose order
/// differs: Blu-ray puts the LFE last and the side pair ahead of the rears.
/// Each entry maps a source channel to its WAVE output slot. `None` means
/// identity: mono, stereo, 3.0, 4.0 and 5.0 already arrive in WAVE order.
fn bluray_remap(assignment: u8) -> Option<&'static [usize]> {
    const K51: [usize; 6] = [0, 1, 2, 4, 5, 3];
    const K70: [usize; 7] = [0, 1, 2, 5, 3, 4, 6];
    const K71: [usize; 8] = [0, 1, 2, 6, 4, 5, 7, 3];
    match assignment {
        9 => Some(&K51),
        10 => Some(&K70),
        11 => Some(&K71),
        _ => None,
    }
}

pub struct PcmDecoder {
    sample_rate: u32,
    channels: usize,
    bytes_per_sample: usize,
    little_endian: bool,
    remap: Option<&'static [usize]>,
    pending: Option<PcmChunk>,
}

impl PcmDecoder {
    pub fn new(sample_rate: u32, channels: u32, codec_private: &[u8]) -> Result<Self, DecodeError> {
        if codec_private.len() < CONFIG_LEN {
            return Err(DecodeError(
                "PCM format announce is missing its assignment/bits/flags bytes".into(),
            ));
        }
        let assignment = codec_private[0];
        let bytes_per_sample = match codec_private[1] {
            1 => 2,
            3 => 3,
            other => {
                return Err(DecodeError(format!(
                    "PCM bits code {other} unsupported (16- and 24-bit only)"
                )));
            }
        };
        let channels = channels as usize;
        if !(1..=8).contains(&channels) {
            return Err(DecodeError(format!(
                "PCM channel count {channels} unsupported"
            )));
        }
        let remap = bluray_remap(assignment);
        // A remap table that disagrees with the announced channel count
        // would index out of the frame; refuse rather than truncate.
        if let Some(map) = remap
            && map.len() != channels
        {
            return Err(DecodeError(format!(
                "PCM channel assignment {assignment} states {} channels, announce says {channels}",
                map.len()
            )));
        }
        Ok(Self {
            sample_rate,
            channels,
            bytes_per_sample,
            little_endian: codec_private[2] & 1 != 0,
            remap,
            pending: None,
        })
    }

    fn sample(&self, s: &[u8]) -> f32 {
        if self.bytes_per_sample == 2 {
            let v = if self.little_endian {
                i16::from_le_bytes([s[0], s[1]])
            } else {
                i16::from_be_bytes([s[0], s[1]])
            };
            f32::from(v) / 32768.0
        } else {
            let (hi, mid, lo) = if self.little_endian {
                (s[2], s[1], s[0])
            } else {
                (s[0], s[1], s[2])
            };
            let mut v = (i32::from(hi) << 16) | (i32::from(mid) << 8) | i32::from(lo);
            if v & 0x0080_0000 != 0 {
                v -= 0x0100_0000;
            }
            v as f32 / 8_388_608.0
        }
    }
}

impl AudioDecoder for PcmDecoder {
    fn output_format(&self) -> (u32, u32) {
        (self.sample_rate, self.channels as u32)
    }

    fn submit(&mut self, au: &[u8], pts_us: i64) -> Result<SubmitOutcome, DecodeError> {
        if self.pending.is_some() {
            return Ok(SubmitOutcome::NotAccepting);
        }
        let frame_bytes = self.channels * self.bytes_per_sample;
        let frames = au.len() / frame_bytes;
        if frames == 0 {
            return Ok(SubmitOutcome::Accepted);
        }
        let mut data = vec![0f32; frames * self.channels];
        for f in 0..frames {
            let src = &au[f * frame_bytes..];
            let out = &mut data[f * self.channels..(f + 1) * self.channels];
            for c in 0..self.channels {
                let slot = match self.remap {
                    Some(map) => map[c],
                    None => c,
                };
                out[slot] = self.sample(&src[c * self.bytes_per_sample..]);
            }
        }
        self.pending = Some(PcmChunk {
            sample_rate: self.sample_rate,
            channels: self.channels as u32,
            pts_us,
            data,
        });
        Ok(SubmitOutcome::Accepted)
    }

    fn try_output(&mut self) -> Result<Option<PcmChunk>, DecodeError> {
        Ok(self.pending.take())
    }

    fn begin_drain(&mut self) -> Result<(), DecodeError> {
        Ok(())
    }

    fn reset(&mut self) -> Result<(), DecodeError> {
        self.pending = None;
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// One frame of `channels` samples, produced then read back.
    fn decode(codec_private: &[u8], channels: u32, au: &[u8]) -> Vec<f32> {
        let mut d = PcmDecoder::new(48_000, channels, codec_private).expect("adapter");
        assert_eq!(d.submit(au, 0).expect("submit"), SubmitOutcome::Accepted);
        d.try_output().expect("output").expect("a chunk").data
    }

    #[test]
    fn reads_both_endiannesses_at_both_depths() {
        // Full-scale negative and +1: the sign extension is what 24-bit
        // gets wrong when the top byte is treated as unsigned.
        let le16 = decode(&[0, 1, 1], 2, &[0x00, 0x80, 0x01, 0x00]);
        let be16 = decode(&[0, 1, 0], 2, &[0x80, 0x00, 0x00, 0x01]);
        assert_eq!(le16, be16);
        assert_eq!(le16, vec![-1.0, 1.0 / 32768.0]);

        let le24 = decode(&[0, 3, 1], 2, &[0x00, 0x00, 0x80, 0x01, 0x00, 0x00]);
        let be24 = decode(&[0, 3, 0], 2, &[0x80, 0x00, 0x00, 0x00, 0x00, 0x01]);
        assert_eq!(le24, be24);
        assert_eq!(le24, vec![-1.0, 1.0 / 8_388_608.0]);
    }

    #[test]
    fn blu_ray_five_one_moves_the_lfe_off_the_end() {
        // Blu-ray order is L R C Ls Rs LFE; WAVE wants the LFE at slot 3.
        // Each channel carries its own source index as a sample value.
        let mut au = Vec::new();
        for c in 0..6i16 {
            au.extend_from_slice(&((c + 1) * 256).to_be_bytes());
        }
        let out = decode(&[9, 1, 0], 6, &au);
        let slot = |v: f32| (v * 32768.0 / 256.0).round() as i32;
        assert_eq!(
            out.iter().map(|v| slot(*v)).collect::<Vec<_>>(),
            vec![1, 2, 3, 6, 4, 5],
            "L R C LFE Ls Rs"
        );
    }

    #[test]
    fn wav_order_passes_straight_through() {
        let mut au = Vec::new();
        for c in 0..6i16 {
            au.extend_from_slice(&((c + 1) * 256).to_le_bytes());
        }
        let out = decode(&[0, 1, 1], 6, &au);
        let slot = |v: f32| (v * 32768.0 / 256.0).round() as i32;
        assert_eq!(
            out.iter().map(|v| slot(*v)).collect::<Vec<_>>(),
            vec![1, 2, 3, 4, 5, 6]
        );
    }

    #[test]
    fn refuses_configs_it_cannot_serve() {
        // 20-bit Blu-ray, and a remap table that disagrees with the
        // announced channel count: both would index out of a frame.
        assert!(PcmDecoder::new(48_000, 2, &[0, 2, 0]).is_err());
        assert!(PcmDecoder::new(48_000, 2, &[9, 1, 0]).is_err());
        assert!(PcmDecoder::new(48_000, 2, &[0, 1]).is_err());
    }

    #[test]
    fn a_torn_tail_frame_is_not_a_frame() {
        let mut d = PcmDecoder::new(48_000, 2, &[0, 1, 1]).expect("adapter");
        // Three bytes is short of one stereo 16-bit frame.
        assert_eq!(
            d.submit(&[0, 0, 0], 0).expect("submit"),
            SubmitOutcome::Accepted
        );
        assert!(d.try_output().expect("output").is_none());
    }
}
