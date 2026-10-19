// Copyright (C) The Retina Authors
// SPDX-License-Identifier: MIT OR Apache-2.0

//! Creates a `HEVCDecoderConfigurationRecord`.

use std::ops::Range;

use bytes::Bytes;

use super::nal::{Pps, Sps, UnitType};

/// A constructed record and parameter sets, all sharing the same underlying
/// allocation by reference count.
pub struct Out {
    pub record: Bytes,
    pub sps: Bytes,
    pub pps: Bytes,
    pub vps: Bytes,
}

/// Creates a `HEVCDecoderConfigurationRecord` for the active PPS, SPS, and VPS.
///
/// Only a single of each of may be active at a time according to H.265, so this
/// should be sufficient. If the active parameter set changes,
/// `retina::VideoFrame::has_new_parameters` will return true.
///
/// Always declares `lengthSizeMinusOne` of 3, meaning that NAL units are
/// prefixed with a 4-byte length.
pub(crate) fn decoder_configuration_record(
    raw_pps: &[u8],
    pps: &Pps,
    raw_sps: &[u8],
    sps: &Sps,
    raw_vps: &[u8],
) -> Out {
    let mut record = Vec::new();

    // unsigned int(8) configurationVersion = 1;
    record.push(1);

    // All 11 bytes of Profile:
    // unsigned int(2) general_profile_space;
    // unsigned int(1) general_tier_flag;
    // unsigned int(5) general_profile_idc;
    // unsigned int(32) general_profile_compatibility_flags;
    // unsigned int(48) general_constraint_indicator_flags;
    let profile = sps.profile();
    record.extend(&profile.0[..]);

    // unsigned int(8) general_level_idc;
    record.push(sps.general_level_idc());

    // bit(4) reserved = ‘1111’b;
    // unsigned int(12) min_spatial_segmentation_idc;
    let min_spatial_segmentation_idc = sps
        .vui()
        .and_then(|v| v.min_spatial_segmentation_idc())
        .unwrap_or(0);
    record.extend(&(0b1111_0000_0000_0000 | min_spatial_segmentation_idc).to_be_bytes()[..]);
    let parallelism_type: u8 = if min_spatial_segmentation_idc == 0 {
        0
    } else {
        match (
            pps.entropy_coding_sync_enabled_flag(),
            pps.tiles_enabled_flag(),
        ) {
            (true, true) => 0,
            (true, false) => 3,
            (false, true) => 2,
            (false, false) => 1,
        }
    };

    // bit(6) reserved = ‘111111’b;
    // unsigned int(2) parallelismType;
    record.push(0b1111_1100 | parallelism_type);

    // bit(6) reserved = ‘111111’b;
    // unsigned int(2) chromaFormat;
    record.push(0b1111_1100 | sps.chroma_format_idc());

    // bit(5) reserved = ‘11111’b;
    // unsigned int(3) bitDepthLumaMinus8;
    // bit(5) reserved = ‘11111’b;
    // unsigned int(3) bitDepthChromaMinus8;
    record.push(0b1111_1000 | sps.bit_depth_luma_minus8());
    record.push(0b1111_1000 | sps.bit_depth_chroma_minus8());

    // bit(16) avgFrameRate;
    record.extend([0, 0]);

    // bit(2) constantFrameRate;
    // bit(3) numTemporalLayers;
    // bit(1) temporalIdNested;
    // unsigned int(2) lengthSizeMinusOne;

    // Note: H.265 section 7.4.3.2.1 states
    // `sps_max_sub_layers_minus1 <= vps_max_sub_layers_minus1`. Declare
    // the more constrained value.
    record.push(
        (sps.max_sub_layers() << 3) | (u8::from(sps.temporal_id_nesting_flag()) << 2) | 0b0011,
    );

    // unsigned int(8) numOfArrays;
    // for (j=0; j < numOfArrays; j++) {
    //   bit(1) array_completeness;
    //   unsigned int(1) reserved = 0;
    //   unsigned int(6) NAL_unit_type;
    //   unsigned int(16) numNalus;
    //   for (i=0; i< numNalus; i++) {
    //     unsigned int(16) nalUnitLength;
    //     bit(8*nalUnitLength) nalUnit;
    //   }
    // }
    record.push(3); // 3 arrays: VPS, SPS, PPS
    let vps_range = append_array(raw_vps, UnitType::VpsNut, &mut record);
    let sps_range = append_array(raw_sps, UnitType::SpsNut, &mut record);
    let pps_range = append_array(raw_pps, UnitType::PpsNut, &mut record);
    let record = Bytes::from(record);
    let vps = record.slice(vps_range);
    let sps = record.slice(sps_range);
    let pps = record.slice(pps_range);

    Out {
        record,
        vps,
        sps,
        pps,
    }
}

fn append_array(nal: &[u8], unit_type: UnitType, record: &mut Vec<u8>) -> Range<usize> {
    record.extend([0b1000_0000 | u8::from(unit_type), 0, 1]);
    record.extend(
        &u16::try_from(nal.len())
            .expect("nalUnitLength must fit in u16")
            .to_be_bytes()[..],
    );
    let start = record.len();
    record.extend_from_slice(nal);
    start..record.len()
}

#[cfg(test)]
mod tests {
    use base64::Engine;

    use super::super::nal;
    use super::*;
    use crate::testutil::{assert_eq_hex, init_logging};

    #[test]
    fn simple() {
        init_logging();
        let raw_pps = base64::engine::general_purpose::STANDARD
            .decode("RAHA8saNA7NA")
            .unwrap();
        let raw_sps = base64::engine::general_purpose::STANDARD
            .decode("QgEBAWAAAAMAsAAAAwAAAwBaoAWCAeFja5JFL83BQYFBAAADAAEAAAMADKE=")
            .unwrap();
        let raw_vps = base64::engine::general_purpose::STANDARD
            .decode("QAEMAf//AWAAAAMAsAAAAwAAAwBarAwAAAMABAAAAwAyqA==")
            .unwrap();
        let (pps_h, pps_bits) = nal::split(&raw_pps).unwrap();
        assert_eq!(pps_h.unit_type(), nal::UnitType::PpsNut);
        let pps = nal::Pps::from_bits(pps_bits).unwrap();
        let (sps_h, sps_bits) = nal::split(&raw_sps).unwrap();
        assert_eq!(sps_h.unit_type(), nal::UnitType::SpsNut);
        let sps = nal::Sps::from_bits(sps_bits).unwrap();
        let (vps_h, _vps_bits) = nal::split(&raw_vps).unwrap();
        assert_eq!(vps_h.unit_type(), nal::UnitType::VpsNut);
        let record = decoder_configuration_record(&raw_pps, &pps, &raw_sps, &sps, &raw_vps);
        assert_eq_hex!(record.pps, raw_pps);
        assert_eq_hex!(record.sps, raw_sps);
        assert_eq_hex!(record.vps, raw_vps);
        const EXPECTED: &[u8; 125] = b"\
            \x01\x01\x60\x00\x00\x00\xb0\x00\x00\x00\x00\x00\x5a\xf0\x00\xfc\
            \xfd\xf8\xf8\x00\x00\x0f\x03\xa0\x00\x01\x00\x22\x40\x01\x0c\x01\
            \xff\xff\x01\x60\x00\x00\x03\x00\xb0\x00\x00\x03\x00\x00\x03\x00\
            \x5a\xac\x0c\x00\x00\x03\x00\x04\x00\x00\x03\x00\x32\xa8\xa1\x00\
            \x01\x00\x2c\x42\x01\x01\x01\x60\x00\x00\x03\x00\xb0\x00\x00\x03\
            \x00\x00\x03\x00\x5a\xa0\x05\x82\x01\xe1\x63\x6b\x92\x45\x2f\xcd\
            \xc1\x41\x81\x41\x00\x00\x03\x00\x01\x00\x00\x03\x00\x0c\xa1\xa2\
            \x00\x01\x00\x09\x44\x01\xc0\xf2\xc6\x8d\x03\xb3\x40\
        ";
        assert_eq_hex!(&*record.record, EXPECTED);
    }

    #[test]
    fn geovision() {
        init_logging();
        let raw_vps = &b"\x40\x01\x0c\x01\xff\xff\x01\x40\x00\x00\x03\x00\x00\x03\x00\x00\x03\x00\x00\x03\x00\x99\xac\x09"[..];
        let raw_sps = &b"\x42\x01\x01\x01\x40\x00\x00\x03\x00\x00\x03\x00\x00\x03\x00\x00\x03\x00\x99\xa0\x01\x50\x20\x06\x01\xf1\x39\x6b\xb9\x1b\x06\xb9\x54\x4d\xc0\x40\x40\x41\x00\x00\x03\x00\x01\x00\x00\x03\x00\x1e\x08"[..];
        let raw_pps = &b"\x44\x01\xc0\x73\xc0\x4c\x90"[..];
        let (pps_h, pps_bits) = nal::split(raw_pps).unwrap();
        assert_eq!(pps_h.unit_type(), nal::UnitType::PpsNut);
        let pps = nal::Pps::from_bits(pps_bits).unwrap();
        let (sps_h, sps_bits) = nal::split(raw_sps).unwrap();
        assert_eq!(sps_h.unit_type(), nal::UnitType::SpsNut);
        let sps = nal::Sps::from_bits(sps_bits).unwrap();
        assert_eq!(sps.rfc6381_codec(), "hvc1.1.2.L153.00");
        let (vps_h, _vps_bits) = nal::split(raw_vps).unwrap();
        assert_eq!(vps_h.unit_type(), nal::UnitType::VpsNut);
        let record = decoder_configuration_record(raw_pps, &pps, raw_sps, &sps, raw_vps);
        assert_eq_hex!(record.pps, raw_pps);
        assert_eq_hex!(record.sps, raw_sps);
        assert_eq_hex!(record.vps, raw_vps);
        let expected = &b"\x01\x01\x40\x00\x00\x00\x00\x00\x00\x00\x00\x00\x99\xf0\x00\xfc\xfd\xf8\xf8\x00\x00\x0f\x03\xa0\x00\x01\x00\x18\x40\x01\x0c\x01\xff\xff\x01\x40\x00\x00\x03\x00\x00\x03\x00\x00\x03\x00\x00\x03\x00\x99\xac\x09\xa1\x00\x01\x00\x31\x42\x01\x01\x01\x40\x00\x00\x03\x00\x00\x03\x00\x00\x03\x00\x00\x03\x00\x99\xa0\x01\x50\x20\x06\x01\xf1\x39\x6b\xb9\x1b\x06\xb9\x54\x4d\xc0\x40\x40\x41\x00\x00\x03\x00\x01\x00\x00\x03\x00\x1e\x08\xa2\x00\x01\x00\x07\x44\x01\xc0\x73\xc0\x4c\x90"[..];
        assert_eq_hex!(&*record.record, expected);
    }
}
