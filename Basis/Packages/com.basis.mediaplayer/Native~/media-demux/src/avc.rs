//! Shared AVC helpers: `avcC` decoder-configuration parsing and
//! length-prefixed → Annex-B conversion, used by every container that
//! stores H.264 the MP4 way (MP4 itself, Matroska).

use crate::DemuxError;

pub(crate) const START_CODE: [u8; 4] = [0, 0, 0, 1];

/// Parameter sets + NAL length size from an `AVCDecoderConfigurationRecord`.
pub(crate) struct AvcConfig {
    pub sps: Vec<Vec<u8>>,
    pub pps: Vec<Vec<u8>>,
    pub nal_length_size: usize,
}

impl AvcConfig {
    /// Parse a raw `avcC` box payload (ISO 14496-15 §5.3.3.1).
    pub(crate) fn parse(avcc: &[u8]) -> Result<Self, DemuxError> {
        let err = || DemuxError::Parse("truncated avcC record".into());
        if avcc.len() < 7 || avcc[0] != 1 {
            return Err(DemuxError::Parse("not an avcC record".into()));
        }
        let nal_length_size = (avcc[4] & 0x3) as usize + 1;
        let mut pos = 5usize;
        let sps_count = (avcc.get(pos).ok_or_else(err)? & 0x1F) as usize;
        pos += 1;
        let mut sps = Vec::with_capacity(sps_count);
        for _ in 0..sps_count {
            let len = u16::from_be_bytes([
                *avcc.get(pos).ok_or_else(err)?,
                *avcc.get(pos + 1).ok_or_else(err)?,
            ]) as usize;
            pos += 2;
            sps.push(avcc.get(pos..pos + len).ok_or_else(err)?.to_vec());
            pos += len;
        }
        let pps_count = *avcc.get(pos).ok_or_else(err)? as usize;
        pos += 1;
        let mut pps = Vec::with_capacity(pps_count);
        for _ in 0..pps_count {
            let len = u16::from_be_bytes([
                *avcc.get(pos).ok_or_else(err)?,
                *avcc.get(pos + 1).ok_or_else(err)?,
            ]) as usize;
            pos += 2;
            pps.push(avcc.get(pos..pos + len).ok_or_else(err)?.to_vec());
            pos += len;
        }
        Ok(Self {
            sps,
            pps,
            nal_length_size,
        })
    }
}

/// Convert one length-prefixed sample to Annex B, prepending SPS/PPS on
/// keyframes so the stream stays decodable from any sync point.
pub(crate) fn to_annex_b(
    sps: &[Vec<u8>],
    pps: &[Vec<u8>],
    nal_length_size: usize,
    src: &[u8],
    keyframe: bool,
) -> Result<Vec<u8>, DemuxError> {
    let mut data = Vec::with_capacity(src.len() + 128);
    if keyframe {
        for ps in sps.iter().chain(pps.iter()) {
            data.extend_from_slice(&START_CODE);
            data.extend_from_slice(ps);
        }
    }
    let mut pos = 0usize;
    while pos + nal_length_size <= src.len() {
        let mut len = 0usize;
        for &b in &src[pos..pos + nal_length_size] {
            len = (len << 8) | b as usize;
        }
        pos += nal_length_size;
        let nal = src
            .get(pos..pos + len)
            .ok_or_else(|| DemuxError::Parse("NAL length overruns sample".into()))?;
        data.extend_from_slice(&START_CODE);
        data.extend_from_slice(nal);
        pos += len;
    }
    Ok(data)
}
