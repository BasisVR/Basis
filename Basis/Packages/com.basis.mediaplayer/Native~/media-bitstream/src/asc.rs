//! The AudioSpecificConfig as a container carries it (ISO/IEC 14496-3
//! 1.6.2.1): the fields a decoder is configured from, and the core
//! a decoder that rejects the backward-compatible SBR signalling is given.

use crate::adts::{AAC_RATES, aac_channels_from_config};

/// SBR and parametric stereo, signalled explicitly by the object type.
const AOT_SBR: u8 = 5;
const AOT_PS: u8 = 29;
const AOT_ESCAPE: u32 = 31;
const RATE_ESCAPE: u32 = 15;
/// Backward-compatible signalling after the core config: SBR, and within
/// it parametric stereo.
const SYNC_EXTENSION: u32 = 0x2b7;
const PS_SYNC_EXTENSION: u32 = 0x548;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct AudioSpecificConfig {
    /// The core's object type: 2 (AAC-LC) for HE-AAC, which signals
    /// SBR or parametric stereo ahead of it.
    pub object_type: u8,
    /// The rate the core is coded at.
    pub core_rate: u32,
    /// The rate the decoder puts out: the SBR rate where the config
    /// signals SBR, the core rate otherwise.
    pub output_rate: u32,
    /// Raw channel_configuration; 0 means a PCE states the layout.
    pub channel_config: u8,
    pub sbr: bool,
    pub ps: bool,
}

impl AudioSpecificConfig {
    /// Channels the decoder puts out, where the config says: parametric
    /// stereo codes a mono core that decodes to two.
    pub fn channels(&self) -> u8 {
        if self.ps && self.channel_config == 1 {
            2
        } else {
            aac_channels_from_config(self.channel_config)
        }
    }
}

struct Bits<'a> {
    data: &'a [u8],
    pos: usize,
}

impl Bits<'_> {
    fn u(&mut self, n: usize) -> Option<u32> {
        if self.pos + n > self.data.len() * 8 {
            return None;
        }
        let mut v = 0u32;
        for _ in 0..n {
            let bit = (self.data[self.pos >> 3] >> (7 - (self.pos & 7))) & 1;
            v = (v << 1) | u32::from(bit);
            self.pos += 1;
        }
        Some(v)
    }

    fn object_type(&mut self) -> Option<u8> {
        let aot = self.u(5)?;
        let aot = if aot == AOT_ESCAPE {
            32 + self.u(6)?
        } else {
            aot
        };
        Some(aot as u8)
    }

    fn rate(&mut self) -> Option<u32> {
        let index = self.u(4)?;
        let rate = if index == RATE_ESCAPE {
            self.u(24)?
        } else {
            *AAC_RATES.get(index as usize)?
        };
        (rate > 0).then_some(rate)
    }

    /// GASpecificConfig for a core with its channels stated:
    /// frameLengthFlag, dependsOnCoreCoder with its delay, and
    /// extensionFlag, which is 0 for every core that carries SBR.
    fn ga_specific(&mut self) -> Option<()> {
        self.u(1)?;
        if self.u(1)? == 1 {
            self.u(14)?;
        }
        (self.u(1)? == 0).then_some(())
    }
}

/// The config's leading fields, or `None` when it is truncated or names
/// a reserved rate.
pub fn parse_asc(asc: &[u8]) -> Option<AudioSpecificConfig> {
    let mut bits = Bits { data: asc, pos: 0 };
    let mut object_type = bits.object_type()?;
    let core_rate = bits.rate()?;
    let channel_config = bits.u(4)? as u8;
    let mut output_rate = core_rate;
    let (mut sbr, mut ps) = (
        object_type == AOT_SBR || object_type == AOT_PS,
        object_type == AOT_PS,
    );
    if sbr {
        output_rate = bits.rate()?;
        object_type = bits.object_type()?;
    } else if (1..=4).contains(&object_type)
        && channel_config != 0
        && bits.ga_specific().is_some()
        && bits.u(11) == Some(SYNC_EXTENSION)
        && bits.u(5) == Some(u32::from(AOT_SBR))
        && bits.u(1) == Some(1)
        && let Some(rate) = bits.rate()
    {
        // Backward-compatible signalling after the core. A tail that is
        // absent or cut short leaves the core as stated.
        sbr = true;
        output_rate = rate;
        ps = bits.u(11) == Some(PS_SYNC_EXTENSION) && bits.u(1) == Some(1);
    }
    Some(AudioSpecificConfig {
        object_type,
        core_rate,
        output_rate,
        channel_config,
        sbr,
        ps,
    })
}

/// The config to hand a decoder that rejects an inert SBR sync
/// extension: an AAC-LC config followed by `0x2b7`, SBR, and
/// sbrPresentFlag 0, which says only that SBR is absent. Android's
/// C2SoftAacDec refuses a multichannel config carrying one, and every
/// config ffmpeg writes carries one. Anything else is returned whole.
pub fn strip_inert_sbr(asc: &[u8]) -> &[u8] {
    let mut bits = Bits { data: asc, pos: 0 };
    let core = (|| {
        if bits.u(5)? != 2 || bits.u(4)? == RATE_ESCAPE || bits.u(4)? == 0 {
            return None;
        }
        bits.ga_specific()?;
        // Only a byte-aligned core can be cut off cleanly.
        if bits.pos != 16 {
            return None;
        }
        let inert =
            bits.u(11)? == SYNC_EXTENSION && bits.u(5)? == u32::from(AOT_SBR) && bits.u(1)? == 0;
        inert.then_some(2)
    })();
    core.map_or(asc, |len| &asc[..len])
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_explicit_he_aac() {
        // AOT 5, core 22050, 5.1, SBR at 44100, core AAC-LC.
        let asc = parse_asc(&[0x2B, 0xB2, 0x08, 0x00]).unwrap();
        assert_eq!(asc.object_type, 2);
        assert_eq!(asc.core_rate, 22050);
        assert_eq!(asc.output_rate, 44100);
        assert_eq!(asc.channel_config, 6);
        assert!(asc.sbr && !asc.ps);
        assert_eq!(asc.channels(), 6);
    }

    #[test]
    fn parametric_stereo_decodes_a_mono_core_to_two_channels() {
        let asc = parse_asc(&[0xEB, 0x8A, 0x08, 0x00]).unwrap();
        assert!(asc.sbr && asc.ps);
        assert_eq!(asc.channel_config, 1);
        assert_eq!(asc.channels(), 2);
        assert_eq!(asc.output_rate, 44100);
    }

    #[test]
    fn reads_aac_lc_with_its_sync_extension() {
        let asc = parse_asc(&[0x11, 0xB0, 0x56, 0xE5, 0x00]).unwrap();
        assert_eq!(
            (asc.object_type, asc.core_rate, asc.output_rate),
            (2, 48000, 48000)
        );
        assert_eq!(asc.channels(), 6);
        assert!(!asc.sbr);
    }

    /// Fields of the given widths, most significant bit first, padded to
    /// a byte.
    fn pack(fields: &[(u32, usize)]) -> Vec<u8> {
        let mut bits = Vec::new();
        for &(value, width) in fields {
            bits.extend((0..width).rev().map(|i| (value >> i) & 1 == 1));
        }
        bits.chunks(8)
            .map(|byte| {
                byte.iter()
                    .enumerate()
                    .fold(0u8, |acc, (i, &b)| acc | (u8::from(b) << (7 - i)))
            })
            .collect()
    }

    /// AAC-LC at 22050 Hz with the channels given and GASpecificConfig
    /// all zero, then a sync extension naming SBR present at 44100 Hz.
    fn backward_compatible(channels: u32, ps: bool) -> Vec<u8> {
        let mut fields = vec![(2, 5), (7, 4), (channels, 4), (0, 3)];
        fields.extend([(SYNC_EXTENSION, 11), (5, 5), (1, 1), (4, 4)]);
        if ps {
            fields.extend([(PS_SYNC_EXTENSION, 11), (1, 1)]);
        }
        pack(&fields)
    }

    #[test]
    fn reads_backward_compatible_sbr() {
        let asc = parse_asc(&backward_compatible(2, false)).unwrap();
        assert_eq!(
            (asc.object_type, asc.core_rate, asc.output_rate),
            (2, 22050, 44100)
        );
        assert!(asc.sbr && !asc.ps);
        assert_eq!(asc.channels(), 2);
    }

    #[test]
    fn reads_backward_compatible_parametric_stereo() {
        let asc = parse_asc(&backward_compatible(1, true)).unwrap();
        assert_eq!(asc.output_rate, 44100);
        assert!(asc.sbr && asc.ps);
        assert_eq!(asc.channels(), 2);
    }

    #[test]
    fn a_cut_short_sync_extension_leaves_the_core() {
        let whole = backward_compatible(2, false);
        let asc = parse_asc(&whole[..3]).unwrap();
        assert_eq!((asc.core_rate, asc.output_rate), (22050, 22050));
        assert!(!asc.sbr);
    }

    #[test]
    fn reads_the_escapes() {
        // AOT 31 + 6 bits (42, USAC), then an explicit 24-bit rate of
        // 48000 and channel configuration 2.
        let bytes = pack(&[(31, 5), (10, 6), (15, 4), (48000, 24), (2, 4)]);
        let asc = parse_asc(&bytes).unwrap();
        assert_eq!(
            (asc.object_type, asc.core_rate, asc.channel_config),
            (42, 48000, 2)
        );
    }

    #[test]
    fn refuses_truncated_and_reserved() {
        assert_eq!(parse_asc(&[]), None);
        assert_eq!(parse_asc(&[0x2B]), None);
        // HE-AAC that stops after the channel configuration.
        assert_eq!(parse_asc(&[0x2B, 0xB0]), None);
        // Rate index 13 is reserved.
        assert_eq!(parse_asc(&[0x16, 0x90]), None);
    }

    #[test]
    fn strips_only_an_inert_sync_extension() {
        assert_eq!(
            strip_inert_sbr(&[0x11, 0xB0, 0x56, 0xE5, 0x00]),
            &[0x11, 0xB0]
        );
        assert_eq!(
            strip_inert_sbr(&[0x11, 0x90, 0x56, 0xE5, 0x00]),
            &[0x11, 0x90]
        );
        // sbrPresentFlag 1: real SBR, kept.
        let present = [0x11, 0x90, 0x56, 0xE5, 0x80];
        assert_eq!(strip_inert_sbr(&present), &present);
        // Explicit HE-AAC, a bare core, and a PCE layout are left alone.
        let he = [0x2B, 0xB2, 0x08, 0x00];
        assert_eq!(strip_inert_sbr(&he), &he);
        assert_eq!(strip_inert_sbr(&[0x11, 0x90]), &[0x11, 0x90]);
        let pce = [0x11, 0x80, 0x56, 0xE5, 0x00];
        assert_eq!(strip_inert_sbr(&pce), &pce);
    }
}
