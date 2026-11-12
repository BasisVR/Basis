//! Vorbis decode on lewton, in-process. No platform route carries Vorbis
//! everywhere (Media Foundation has none without a store extension), so
//! one software decoder gives the same behaviour on every platform.
//!
//! Codec private data is the three header packets in Xiph lacing, the
//! Matroska `A_VORBIS` CodecPrivate layout, which the Ogg demuxer builds
//! too. Each AU is one audio packet. The first packet after a reset
//! decodes to nothing: Vorbis overlaps each packet with the one before,
//! and the demuxer's timeline already counts that packet as zero long.
//!
//! Matroska laces several packets into one block under one timestamp, and
//! rounds timestamps to the millisecond, so once the first chunk anchors
//! the timeline the decoder counts samples itself and only re-anchors on a
//! real gap.
//!
//! lewton is pure Rust, but it indexes setup tables with values read from
//! the stream; a hostile packet that trips one becomes a typed error here
//! rather than a panic on the decode thread.

use std::collections::VecDeque;
use std::panic::{AssertUnwindSafe, catch_unwind};

use lewton::audio::{PreviousWindowRight, read_audio_packet_generic};
use lewton::header::{IdentHeader, SetupHeader, read_header_ident, read_header_setup};
use lewton::samples::InterleavedSamples;
use media_decode::{AudioDecoder, DecodeError, PcmChunk, SubmitOutcome};

use crate::opus::VORBIS_TO_WAVE;

const READY_CAP: usize = 64;
const MAX_CHANNELS: u8 = 8;
/// How far a packet's stated time may sit past the counted one before it
/// is taken as a gap rather than rounding.
const GAP_US: i64 = 50_000;

/// Split Xiph-laced header packets: a count less one, the lace-coded
/// sizes of all but the last, then the packets end to end.
pub fn split_xiph_headers(private: &[u8]) -> Option<[&[u8]; 3]> {
    let (&count, mut rest) = private.split_first()?;
    if count != 2 {
        return None;
    }
    let mut sizes = [0usize; 2];
    for size in &mut sizes {
        loop {
            let (&b, tail) = rest.split_first()?;
            rest = tail;
            *size = size.checked_add(usize::from(b))?;
            if b != 255 {
                break;
            }
        }
    }
    if sizes[0].checked_add(sizes[1])? > rest.len() {
        return None;
    }
    let (ident, rest) = rest.split_at(sizes[0]);
    let (comment, setup) = rest.split_at(sizes[1]);
    Some([ident, comment, setup])
}

/// The identification and setup headers from Xiph-laced codec private
/// data, parsed. Shared with the demuxer, which needs the setup header's
/// modes to time each packet.
pub fn parse_headers(private: &[u8]) -> Result<(IdentHeader, SetupHeader), DecodeError> {
    let [ident, _, setup] = split_xiph_headers(private)
        .ok_or_else(|| DecodeError("Vorbis codec private is not three laced headers".into()))?;
    catch_unwind(|| {
        let ident = read_header_ident(ident)
            .map_err(|e| DecodeError(format!("Vorbis identification header: {e:?}")))?;
        if ident.audio_channels == 0 || ident.audio_channels > MAX_CHANNELS {
            return Err(DecodeError(format!(
                "Vorbis channel count {} unsupported",
                ident.audio_channels
            )));
        }
        if ident.audio_sample_rate == 0 {
            return Err(DecodeError("Vorbis sample rate 0".into()));
        }
        let setup = read_header_setup(
            setup,
            ident.audio_channels,
            (ident.blocksize_0, ident.blocksize_1),
        )
        .map_err(|e| DecodeError(format!("Vorbis setup header: {e:?}")))?;
        Ok((ident, setup))
    })
    .unwrap_or_else(|_| Err(DecodeError("Vorbis headers are malformed".into())))
}

pub struct VorbisDecoder {
    ident: IdentHeader,
    setup: SetupHeader,
    pwr: PreviousWindowRight,
    channels: usize,
    ready: VecDeque<PcmChunk>,
    /// Timeline anchor and the sample frames output since it.
    clock: Option<(i64, u64)>,
}

impl VorbisDecoder {
    pub fn new(codec_private: &[u8]) -> Result<Self, DecodeError> {
        let (ident, setup) = parse_headers(codec_private)?;
        Ok(Self {
            channels: usize::from(ident.audio_channels),
            ident,
            setup,
            pwr: PreviousWindowRight::new(),
            ready: VecDeque::new(),
            clock: None,
        })
    }
}

impl AudioDecoder for VorbisDecoder {
    fn output_format(&self) -> (u32, u32) {
        (self.ident.audio_sample_rate, self.channels as u32)
    }

    fn submit(&mut self, au: &[u8], pts_us: i64) -> Result<SubmitOutcome, DecodeError> {
        if self.ready.len() >= READY_CAP {
            return Ok(SubmitOutcome::NotAccepting);
        }
        // A zero-length packet is legal and carries nothing.
        if au.is_empty() {
            return Ok(SubmitOutcome::Accepted);
        }
        let (ident, setup, pwr) = (&self.ident, &self.setup, &mut self.pwr);
        let decoded = catch_unwind(AssertUnwindSafe(|| {
            read_audio_packet_generic::<InterleavedSamples<f32>>(ident, setup, au, pwr)
        }));
        let samples = match decoded {
            Ok(Ok(samples)) => samples,
            // Headers repeated mid-stream (a chained file's second
            // stream) are not audio; skip rather than fail.
            Ok(Err(lewton::audio::AudioReadError::AudioIsHeader)) => {
                return Ok(SubmitOutcome::Accepted);
            }
            Ok(Err(e)) => return Err(DecodeError(format!("Vorbis packet: {e:?}"))),
            Err(_) => {
                self.pwr = PreviousWindowRight::new();
                return Err(DecodeError("Vorbis packet is malformed".into()));
            }
        };
        if samples.samples.is_empty() {
            return Ok(SubmitOutcome::Accepted);
        }
        if samples.channel_count != self.channels {
            return Err(DecodeError(format!(
                "Vorbis packet channel count {} != stream {}",
                samples.channel_count, self.channels
            )));
        }
        let map = VORBIS_TO_WAVE[self.channels];
        let data = if map.iter().enumerate().all(|(w, &v)| w == v) {
            samples.samples
        } else {
            let mut out = vec![0f32; samples.samples.len()];
            for (src, dst) in samples
                .samples
                .chunks_exact(self.channels)
                .zip(out.chunks_exact_mut(self.channels))
            {
                for (w, &v) in map.iter().enumerate() {
                    dst[w] = src[v];
                }
            }
            out
        };
        let rate = i64::from(self.ident.audio_sample_rate);
        let frames = (data.len() / self.channels) as u64;
        let pts_us = match self.clock {
            Some((anchor, counted)) => {
                let at = anchor.saturating_add((counted as i64).saturating_mul(1_000_000) / rate);
                if pts_us > at.saturating_add(GAP_US) {
                    self.clock = Some((pts_us, frames));
                    pts_us
                } else {
                    self.clock = Some((anchor, counted + frames));
                    at
                }
            }
            None => {
                self.clock = Some((pts_us, frames));
                pts_us
            }
        };
        self.ready.push_back(PcmChunk {
            sample_rate: self.ident.audio_sample_rate,
            channels: self.channels as u32,
            pts_us,
            data,
        });
        Ok(SubmitOutcome::Accepted)
    }

    fn try_output(&mut self) -> Result<Option<PcmChunk>, DecodeError> {
        Ok(self.ready.pop_front())
    }

    fn begin_drain(&mut self) -> Result<(), DecodeError> {
        Ok(())
    }

    fn reset(&mut self) -> Result<(), DecodeError> {
        self.ready.clear();
        self.pwr = PreviousWindowRight::new();
        self.clock = None;
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn join_xiph_headers(ident: &[u8], comment: &[u8], setup: &[u8]) -> Vec<u8> {
        let mut out = vec![2];
        for len in [ident.len(), comment.len()] {
            out.extend(std::iter::repeat_n(255u8, len / 255));
            out.push((len % 255) as u8);
        }
        out.extend_from_slice(ident);
        out.extend_from_slice(comment);
        out.extend_from_slice(setup);
        out
    }

    #[test]
    fn lacing_round_trips_across_the_255_boundary() {
        let ident = vec![1u8; 30];
        let comment = vec![3u8; 510];
        let setup = vec![5u8; 7];
        let laced = join_xiph_headers(&ident, &comment, &setup);
        let [a, b, c] = split_xiph_headers(&laced).expect("split");
        assert_eq!((a, b, c), (&ident[..], &comment[..], &setup[..]));
    }

    #[test]
    fn rubbish_private_data_is_refused_not_panicked_on() {
        assert!(split_xiph_headers(&[]).is_none());
        assert!(split_xiph_headers(&[1, 0]).is_none());
        assert!(split_xiph_headers(&[2, 255, 255]).is_none());
        assert!(split_xiph_headers(&[2, 200, 5, 1, 2]).is_none());
        for len in 0..64usize {
            let bytes: Vec<u8> = (0..len).map(|i| (i * 7 + 2) as u8).collect();
            let _ = VorbisDecoder::new(&bytes);
        }
        let laced = join_xiph_headers(b"\x01vorbis-not-really", b"\x03vorbis", b"\x05vorbis");
        assert!(VorbisDecoder::new(&laced).is_err());
    }
}
