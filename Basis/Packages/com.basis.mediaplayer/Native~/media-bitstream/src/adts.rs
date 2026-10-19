//! ADTS frame headers and the two-byte AudioSpecificConfig.

/// ISO/IEC 14496-3 sampling-frequency-index table (indices 13..=15 are
/// reserved/escape).
pub const AAC_RATES: [u32; 13] = [
    96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350,
];

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct AdtsHeader {
    /// ADTS profile field (AOT minus one): 0 = Main, 1 = LC, …
    pub profile: u8,
    /// 0 when the sampling-frequency index is reserved.
    pub sample_rate: u32,
    /// Raw channel_configuration field (see [`aac_channels_from_config`]).
    pub channel_config: u8,
    /// Whole frame including this header.
    pub frame_len: usize,
    /// 7, or 9 when a CRC is present.
    pub header_len: usize,
}

/// Parse an ADTS header at the start of `p`. The frame body may extend
/// beyond `p`; callers check `frame_len` against what they hold.
pub fn parse_adts(p: &[u8]) -> Option<AdtsHeader> {
    if p.len() < 7 || p[0] != 0xFF || (p[1] & 0xF0) != 0xF0 {
        return None;
    }
    let protection_absent = p[1] & 0x01;
    let sr_index = (p[2] >> 2) & 0x0F;
    let header = AdtsHeader {
        profile: (p[2] >> 6) & 0x3,
        sample_rate: AAC_RATES.get(sr_index as usize).copied().unwrap_or(0),
        channel_config: ((p[2] & 0x1) << 2) | ((p[3] >> 6) & 0x3),
        frame_len: usize::from(p[3] & 0x3) << 11 | usize::from(p[4]) << 3 | usize::from(p[5] >> 5),
        header_len: if protection_absent == 1 { 7 } else { 9 },
    };
    if header.frame_len < header.header_len {
        return None;
    }
    Some(header)
}

/// channel_configuration -> channel count (7 signals 7.1).
pub fn aac_channels_from_config(config: u8) -> u8 {
    if config == 7 { 8 } else { config }
}

/// Two-byte AudioSpecificConfig: 5 bits AOT, 4 bits sample-rate index,
/// 4 bits channel configuration.
pub fn build_asc(object_type: u8, sample_rate: u32, channel_config: u8) -> [u8; 2] {
    let sri = AAC_RATES
        .iter()
        .position(|&r| r == sample_rate)
        .unwrap_or(4) as u8; // 44100 fallback
    [
        (object_type << 3) | ((sri >> 1) & 0x7),
        ((sri & 0x1) << 7) | ((channel_config & 0xF) << 3),
    ]
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_a_typical_lc_header() {
        // 48 kHz (index 3), stereo (config 2), frame_len 256, no CRC.
        let mut h = [0u8; 7];
        h[0] = 0xFF;
        h[1] = 0xF1;
        h[2] = (1 << 6) | (3 << 2); // profile LC, sr index 3, private bit 0
        h[3] = 2 << 6 | ((256 >> 11) as u8 & 0x3);
        h[4] = ((256 >> 3) & 0xFF) as u8;
        h[5] = ((256 & 0x7) as u8) << 5 | 0x1F;
        h[6] = 0xFC;
        let parsed = parse_adts(&h).unwrap();
        assert_eq!(parsed.profile, 1);
        assert_eq!(parsed.sample_rate, 48000);
        assert_eq!(parsed.channel_config, 2);
        assert_eq!(parsed.frame_len, 256);
        assert_eq!(parsed.header_len, 7);
    }

    #[test]
    fn rejects_bad_sync_and_short_frames() {
        assert_eq!(parse_adts(&[0xFF, 0xE0, 0, 0, 0, 0, 0]), None);
        assert_eq!(parse_adts(&[0xFF, 0xF1, 0, 0]), None);
        // frame_len below the header length is malformed.
        let mut h = [0xFFu8, 0xF1, 1 << 6, 0, 0, 0, 0xFC];
        h[5] = 3 << 5; // frame_len = 3
        assert_eq!(parse_adts(&h), None);
    }

    #[test]
    fn asc_round_trips_adts_fields() {
        // LC at 44100 stereo: AOT 2, sri 4, config 2.
        assert_eq!(build_asc(2, 44100, 2), [0x12, 0x10]);
    }
}
