//! AIFF and AIFF-C demuxer: walks the `FORM` chunk list for `COMM` and
//! `SSND`, then hands the sample data to the WAV block server, which
//! serves any interleaved PCM once its layout is known.
//!
//! Supported: plain AIFF (big-endian two's complement, 8 to 32 bits) and
//! the uncompressed AIFF-C types: `NONE`/`twos`, `sowt` (little-endian),
//! `in24`/`in32`, `raw ` (offset binary 8-bit), `fl32`/`fl64`. Compressed
//! AIFF-C (µ-law, A-law, IMA and the rest) refuses typed.

use media_clock::Generation;

use crate::source::SeqReader;
use crate::wav::{PCM_16, PCM_24, PCM_32, PCM_F32, PCM_F64, PCM_RATES, PCM_S8, PCM_U8, PcmLayout};
use crate::{ByteSource, DemuxError, DemuxLimits, WavDemuxer};

fn be16(p: &[u8]) -> u16 {
    u16::from_be_bytes([p[0], p[1]])
}

fn be32(p: &[u8]) -> u32 {
    u32::from_be_bytes([p[0], p[1], p[2], p[3]])
}

/// The 80-bit IEEE 754 extended sample rate `COMM` states, rounded to
/// whole hertz. `None` for anything that is not a plausible positive rate.
fn extended_rate(p: &[u8]) -> Option<u32> {
    let sign_exp = be16(p);
    let mantissa = u64::from_be_bytes(p[2..10].try_into().ok()?);
    if sign_exp & 0x8000 != 0 || mantissa == 0 {
        return None;
    }
    // Value = mantissa * 2^(exp - 16383 - 63), the mantissa carrying its
    // integer bit explicitly.
    let shift = i32::from(sign_exp & 0x7FFF) - 16383 - 63;
    if !(-63..=0).contains(&shift) {
        return None;
    }
    let whole = mantissa >> (-shift);
    let half = if shift < 0 {
        (mantissa >> (-shift - 1)) & 1
    } else {
        0
    };
    u32::try_from(whole + half).ok()
}

/// The PCM layout a `COMM` body states, or the refusal.
fn layout(comm: &[u8], aifc: bool) -> Result<PcmLayout, DemuxError> {
    if comm.len() < 18 || (aifc && comm.len() < 22) {
        return Err(DemuxError::Parse("AIFF COMM chunk truncated".into()));
    }
    let channels = u32::from(be16(comm));
    let bits = be16(&comm[6..]);
    let sample_rate = extended_rate(&comm[8..18])
        .ok_or_else(|| DemuxError::Parse("AIFF sample rate is not a usable number".into()))?;
    let kind: [u8; 4] = if aifc {
        comm[18..22].try_into().expect("sliced 4")
    } else {
        *b"NONE"
    };
    // Integer samples sit left-justified in whole bytes, so a 12- or
    // 20-bit file reads as the container width it occupies.
    let container = bits.div_ceil(8) * 8;
    let (code, little_endian, width) = match (&kind, container) {
        (b"NONE" | b"twos", 8) => (PCM_S8, false, 1),
        (b"NONE" | b"twos", 16) => (PCM_16, false, 2),
        (b"NONE" | b"twos" | b"in24", 24) => (PCM_24, false, 3),
        (b"NONE" | b"twos" | b"in32", 32) => (PCM_32, false, 4),
        (b"sowt", 8) => (PCM_S8, true, 1),
        (b"sowt", 16) => (PCM_16, true, 2),
        (b"sowt", 24) => (PCM_24, true, 3),
        (b"sowt", 32) => (PCM_32, true, 4),
        (b"raw ", 8) => (PCM_U8, false, 1),
        // Float types state their own width; the sample size field is
        // often left at 0 or 16 by writers.
        (b"fl32" | b"FL32", _) => (PCM_F32, false, 4),
        (b"fl64" | b"FL64", _) => (PCM_F64, false, 8),
        (b"NONE" | b"twos" | b"sowt" | b"raw " | b"in24" | b"in32", _) => {
            return Err(DemuxError::Unsupported(
                "AIFF sample width unsupported (need 8 to 32-bit integer)",
            ));
        }
        _ => {
            return Err(DemuxError::Unsupported(
                "AIFF-C compression unsupported (need uncompressed integer or float PCM)",
            ));
        }
    };
    if !(1..=8).contains(&channels) || !PCM_RATES.contains(&sample_rate) {
        return Err(DemuxError::Unsupported(
            "AIFF format unsupported (need 1-8 channels at 8-192 kHz)",
        ));
    }
    Ok(PcmLayout {
        sample_rate,
        channels,
        code,
        little_endian,
        frame_bytes: channels as usize * width,
    })
}

pub struct AiffDemuxer;

impl AiffDemuxer {
    /// Open an AIFF stream. The result is the shared PCM block server.
    #[allow(clippy::new_ret_no_self)]
    pub fn open(
        mut src: Box<dyn ByteSource>,
        limits: DemuxLimits,
        generation: Generation,
    ) -> Result<WavDemuxer, DemuxError> {
        let seekable = src.size().map_err(DemuxError::Source)?.is_some();
        let mut reader = SeqReader::new(src);

        let head = reader.peek(12).map_err(DemuxError::Source)?;
        if head.len() < 12 || &head[..4] != b"FORM" {
            return Err(DemuxError::Parse("not an AIFF stream".into()));
        }
        let aifc = match &head[8..12] {
            b"AIFF" => false,
            b"AIFC" => true,
            _ => return Err(DemuxError::Parse("FORM is not AIFF or AIFF-C".into())),
        };
        reader.consume(12);

        let mut comm: Option<PcmLayout> = None;
        // Where the samples start and how many bytes they hold, when SSND
        // came before COMM and the walk had to go on past it.
        let mut ssnd: Option<(u64, Option<u64>)> = None;
        let mut metadata = 0u64;
        loop {
            if let (Some(_), Some((start, len))) = (&comm, ssnd) {
                reader.reposition(start);
                let layout = comm.take().expect("checked");
                return Ok(WavDemuxer::serve(reader, generation, layout, len, seekable));
            }
            metadata = metadata.saturating_add(8);
            if metadata > limits.max_metadata_bytes {
                return Err(DemuxError::Cap("AIFF chunk walk"));
            }
            let header = reader.peek(8).map_err(DemuxError::Source)?;
            if header.len() < 8 {
                return Err(DemuxError::Parse(
                    "AIFF ended before its COMM and SSND chunks".into(),
                ));
            }
            let id: [u8; 4] = header[..4].try_into().expect("sliced 4");
            let size = u64::from(be32(&header[4..8]));
            reader.consume(8);
            // Chunks pad to an even length.
            let padded = size + (size & 1);
            let body_start = reader.pos();

            match &id {
                b"COMM" => {
                    let take = size.min(64) as usize;
                    let body = reader.peek(take).map_err(DemuxError::Source)?;
                    if body.len() < take {
                        return Err(DemuxError::Parse("AIFF COMM chunk truncated".into()));
                    }
                    comm = Some(layout(&body[..take], aifc)?);
                }
                b"SSND" => {
                    let body = reader.peek(8).map_err(DemuxError::Source)?;
                    if body.len() < 8 || size < 8 {
                        return Err(DemuxError::Parse("AIFF SSND chunk truncated".into()));
                    }
                    let offset = u64::from(be32(body));
                    let start = body_start + 8 + offset;
                    // A streaming writer leaves the size at 0 or all ones;
                    // a truncated file overstates it. EOF ends either.
                    let unbounded = size == 0 || size == u64::from(u32::MAX);
                    let len = (!unbounded).then(|| size.saturating_sub(8 + offset));
                    if comm.is_some() {
                        reader.consume(8);
                        reader.seek_to(start).map_err(DemuxError::Source)?;
                        let layout = comm.take().expect("checked");
                        return Ok(WavDemuxer::serve(reader, generation, layout, len, seekable));
                    }
                    if !seekable || unbounded {
                        return Err(DemuxError::Unsupported(
                            "AIFF sample data before its COMM chunk on a stream that cannot seek back",
                        ));
                    }
                    ssnd = Some((start, len));
                }
                _ => {
                    metadata = metadata.saturating_add(padded);
                    if metadata > limits.max_metadata_bytes {
                        return Err(DemuxError::Cap("AIFF chunk walk"));
                    }
                }
            }
            reader
                .seek_to(body_start + padded)
                .map_err(DemuxError::Source)?;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 80-bit extended encodings of common rates.
    fn ext(rate: u32) -> [u8; 10] {
        let exp = 16383 + 31 - rate.leading_zeros() as u16;
        let mantissa = u64::from(rate) << (63 - (31 - rate.leading_zeros()));
        let mut out = [0u8; 10];
        out[..2].copy_from_slice(&exp.to_be_bytes());
        out[2..].copy_from_slice(&mantissa.to_be_bytes());
        out
    }

    #[test]
    fn extended_rates_decode_exactly() {
        for rate in [8_000, 11_025, 22_050, 44_100, 48_000, 96_000, 192_000] {
            assert_eq!(extended_rate(&ext(rate)), Some(rate));
        }
        // 44100 as most writers store it.
        let written = [0x40, 0x0E, 0xAC, 0x44, 0, 0, 0, 0, 0, 0];
        assert_eq!(extended_rate(&written), Some(44_100));
        assert_eq!(extended_rate(&[0; 10]), None);
        assert_eq!(
            extended_rate(&[0xC0, 0x0E, 0xAC, 0x44, 0, 0, 0, 0, 0, 0]),
            None
        );
        assert_eq!(
            extended_rate(&[0x7F, 0xFF, 0x80, 0, 0, 0, 0, 0, 0, 0]),
            None
        );
    }

    fn comm(channels: u16, bits: u16, rate: u32, kind: Option<&[u8; 4]>) -> Vec<u8> {
        let mut c = channels.to_be_bytes().to_vec();
        c.extend_from_slice(&1000u32.to_be_bytes());
        c.extend_from_slice(&bits.to_be_bytes());
        c.extend_from_slice(&ext(rate));
        if let Some(kind) = kind {
            c.extend_from_slice(kind);
            c.push(0);
            c.push(0);
        }
        c
    }

    #[test]
    fn layouts_cover_the_uncompressed_types() {
        let l = layout(&comm(2, 16, 44_100, None), false).expect("plain");
        assert_eq!(
            (l.code, l.little_endian, l.frame_bytes, l.sample_rate),
            (PCM_16, false, 4, 44_100)
        );
        let l = layout(&comm(2, 16, 48_000, Some(b"sowt")), true).expect("sowt");
        assert_eq!((l.code, l.little_endian), (PCM_16, true));
        let l = layout(&comm(1, 20, 48_000, None), false).expect("20-bit");
        assert_eq!((l.code, l.frame_bytes), (PCM_24, 3));
        let l = layout(&comm(2, 0, 48_000, Some(b"fl32")), true).expect("float");
        assert_eq!((l.code, l.frame_bytes), (PCM_F32, 8));
        assert!(matches!(
            layout(&comm(2, 16, 48_000, Some(b"ulaw")), true),
            Err(DemuxError::Unsupported(_))
        ));
        assert!(matches!(
            layout(&comm(9, 16, 48_000, None), false),
            Err(DemuxError::Unsupported(_))
        ));
    }
}
