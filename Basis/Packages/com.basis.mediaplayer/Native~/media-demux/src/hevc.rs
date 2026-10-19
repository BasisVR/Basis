//! HEVC `hvcC` decoder-configuration parsing (ISO 14496-15 §8.3.3.1),
//! reusing the AVC conversion machinery: the parameter sets land in one
//! flat list ([`AvcConfig::sps`], VPS/SPS/PPS in stored order, `pps`
//! empty) because length-prefixed → Annex-B conversion is identical:
//! prepend the sets on keyframes, walk NALs by the stated length size.

use crate::DemuxError;
use crate::avc::AvcConfig;

pub(crate) fn parse_hvcc(hvcc: &[u8]) -> Result<AvcConfig, DemuxError> {
    let err = || DemuxError::Parse("truncated hvcC record".into());
    if hvcc.len() < 23 || hvcc[0] != 1 {
        return Err(DemuxError::Parse("not an hvcC record".into()));
    }
    let nal_length_size = (hvcc[21] & 0x3) as usize + 1;
    let num_arrays = hvcc[22] as usize;
    let mut pos = 23usize;
    let mut param_sets = Vec::new();
    for _ in 0..num_arrays {
        // array_completeness(1) + reserved(1) + NAL_unit_type(6), then the
        // NAL count. The type byte is not needed: sets are prepended in
        // stored order (VPS, SPS, PPS per the record's required layout).
        pos = pos.checked_add(1).ok_or_else(err)?;
        let count = u16::from_be_bytes([
            *hvcc.get(pos).ok_or_else(err)?,
            *hvcc.get(pos + 1).ok_or_else(err)?,
        ]) as usize;
        pos += 2;
        for _ in 0..count {
            let len = u16::from_be_bytes([
                *hvcc.get(pos).ok_or_else(err)?,
                *hvcc.get(pos + 1).ok_or_else(err)?,
            ]) as usize;
            pos += 2;
            param_sets.push(hvcc.get(pos..pos + len).ok_or_else(err)?.to_vec());
            pos += len;
        }
    }
    if param_sets.is_empty() {
        return Err(DemuxError::Parse(
            "hvcC record carries no parameter sets".into(),
        ));
    }
    Ok(AvcConfig {
        sps: param_sets,
        pps: Vec::new(),
        nal_length_size,
    })
}
