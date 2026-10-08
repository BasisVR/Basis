use byteorder::{BigEndian, ReadBytesExt as _};
use serde::Serialize;
use std::io::{Read, Seek};

use crate::mp4box::{
    box_start, read_box_header_ext, skip_bytes_to, Av01Box, Avc1Box, BoxHeader, BoxType, Error,
    FourCC, HevcBox, Mp4Box, Mp4aBox, ReadBox, Result, TrackKind, Tx3gBox, Vp08Box, Vp09Box,
    HEADER_EXT_SIZE, HEADER_SIZE,
};

/// Codec dependent contents of the stsd box.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub enum StsdBoxContent {
    /// AV1 video codec
    Av01(Av01Box),

    /// AVC video codec (h.264)
    Avc1(Avc1Box),

    /// HVC1 video codec (h.265)
    ///
    /// hvc1 parameter sets are stored out-of-band in the sample entry
    /// (i.e. below the Sample Description Box (stsd) box)
    Hvc1(HevcBox),

    /// HEV1 video codec (h.265)
    ///
    /// hev1 parameter sets are stored out-of-band in the sample entry and/or in-band in the samples
    /// (i.e. SPS/PPS/VPS NAL units in the bitstream/ mdat box)
    Hev1(HevcBox),

    /// VP8 video codec
    Vp08(Vp08Box),

    /// VP9 video codec
    Vp09(Vp09Box),

    /// AAC audio codec
    Mp4a(Mp4aBox),

    /// TTXT subtitle codec
    Tx3g(Tx3gBox),

    /// Unrecognized codecs
    Unknown(FourCC),
}

impl Default for StsdBoxContent {
    fn default() -> Self {
        Self::Unknown(FourCC::default())
    }
}

impl StsdBoxContent {
    /// Per color component bit depth.
    ///
    /// Usually 8, but 10 for HDR (for example).
    pub fn bit_depth(&self) -> Option<u8> {
        #[expect(clippy::match_same_arms)]
        match self {
            Self::Av01(bx) => Some(bx.av1c.bit_depth),

            Self::Avc1(_) => None, // TODO(emilk): figure out bit depth

            Self::Hvc1(_) => None, // TODO(emilk): figure out bit depth

            Self::Hev1(_) => None, // TODO(emilk): figure out bit depth

            Self::Vp08(bx) => Some(bx.vpcc.bit_depth),

            Self::Vp09(bx) => Some(bx.vpcc.bit_depth),

            Self::Mp4a(_) | Self::Tx3g(_) | Self::Unknown(_) => None, // Not applicable
        }
    }

    pub fn codec_string(&self) -> Option<String> {
        Some(match self {
            Self::Av01(Av01Box { av1c, .. }) => {
                let profile = av1c.profile;
                let level = av1c.level;
                let tier = if av1c.tier == 0 { "M" } else { "H" };
                let bit_depth = av1c.bit_depth;

                format!("av01.{profile}.{level:02}{tier}.{bit_depth:02}")
            }

            Self::Avc1(Avc1Box { avcc, .. }) => {
                let profile = avcc.avc_profile_indication;
                let constraint = avcc.profile_compatibility;
                let level = avcc.avc_level_indication;

                // https://aomediacodec.github.io/av1-isobmff/#codecsparam
                format!("avc1.{profile:02X}{constraint:02X}{level:02X}")
            }

            Self::Hvc1(HevcBox { hvcc, .. }) => {
                format!("hvc1{}", hevc_codec_details(hvcc))
            }

            Self::Hev1(HevcBox { hvcc, .. }) => {
                format!("hev1{}", hevc_codec_details(hvcc))
            }

            Self::Vp08(_) => {
                // https://www.w3.org/TR/webcodecs-vp8-codec-registration/#fully-qualified-codec-strings
                String::from("vp8")
            }

            Self::Vp09(Vp09Box { vpcc, .. }) => {
                let profile = vpcc.profile;
                let level = vpcc.level;
                let bit_depth = vpcc.bit_depth;

                format!("vp09.{profile:02}.{level:02}.{bit_depth:02}")
            }

            Self::Mp4a(_) | Self::Tx3g(_) | Self::Unknown(_) => return None,
        })
    }
}

fn hevc_codec_details(hvcc: &crate::hevc::HevcDecoderConfigurationRecord) -> String {
    use std::fmt::Write as _;

    let mut codec = String::new();
    match hvcc.general_profile_space {
        1 => codec.push_str(".A"),
        2 => codec.push_str(".B"),
        3 => codec.push_str(".C"),
        _ => {}
    }
    write!(&mut codec, ".{}", hvcc.general_profile_idc).ok();

    let mut val = hvcc.general_profile_compatibility_flags;
    let mut reversed = 0;
    for i in 0..32 {
        reversed |= val & 1;
        if i == 31 {
            break;
        }
        reversed <<= 1;
        val >>= 1;
    }
    write!(&mut codec, ".{reversed:X}").ok();

    if hvcc.general_tier_flag {
        codec.push_str(".H");
    } else {
        codec.push_str(".L");
    }
    write!(&mut codec, "{}", hvcc.general_level_idc).ok();

    let mut constraint = [0u8; 6];
    constraint.copy_from_slice(&hvcc.general_constraint_indicator_flag.to_be_bytes()[2..]);
    let mut has_byte = false;
    let mut i = 5isize;
    while 0 <= i {
        let v = constraint[i as usize];
        if v > 0 || has_byte {
            write!(&mut codec, ".{v:00X}").ok();
            has_byte = true;
        }
        i -= 1;
    }

    codec
}

/// Information about the video codec.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Default)]
pub struct StsdBox {
    pub version: u8,
    pub flags: u32,
    pub contents: StsdBoxContent,
}

impl StsdBox {
    pub fn kind(&self) -> Option<TrackKind> {
        match &self.contents {
            StsdBoxContent::Av01(_)
            | StsdBoxContent::Avc1(_)
            | StsdBoxContent::Hev1(_)
            | StsdBoxContent::Hvc1(_)
            | StsdBoxContent::Vp08(_)
            | StsdBoxContent::Vp09(_) => Some(TrackKind::Video),
            StsdBoxContent::Mp4a(_) => Some(TrackKind::Audio),
            StsdBoxContent::Tx3g(_) => Some(TrackKind::Subtitle),
            StsdBoxContent::Unknown(_) => None,
        }
    }

    pub fn get_type() -> BoxType {
        BoxType::StsdBox
    }

    pub fn get_size(&self) -> u64 {
        HEADER_SIZE
            + HEADER_EXT_SIZE
            + 4
            + match &self.contents {
                StsdBoxContent::Av01(contents) => contents.box_size(),
                StsdBoxContent::Avc1(contents) => contents.box_size(),
                StsdBoxContent::Hev1(contents) | StsdBoxContent::Hvc1(contents) => {
                    contents.box_size()
                }
                StsdBoxContent::Vp08(contents) => contents.box_size(),
                StsdBoxContent::Vp09(contents) => contents.box_size(),
                StsdBoxContent::Mp4a(contents) => contents.box_size(),
                StsdBoxContent::Tx3g(contents) => contents.box_size(),
                StsdBoxContent::Unknown(_) => 0,
            }
    }
}

impl Mp4Box for StsdBox {
    fn box_type(&self) -> BoxType {
        Self::get_type()
    }

    fn box_size(&self) -> u64 {
        self.get_size()
    }

    fn to_json(&self) -> Result<String> {
        Ok(serde_json::to_string(&self).expect("Failed to convert to JSON"))
    }

    fn summary(&self) -> Result<String> {
        let s = String::new();
        Ok(s)
    }
}

impl<R: Read + Seek> ReadBox<&mut R> for StsdBox {
    fn read_box(reader: &mut R, size: u64) -> Result<Self> {
        let start = box_start(reader)?;

        let (version, flags) = read_box_header_ext(reader)?;

        reader.read_u32::<BigEndian>()?; // XXX entry_count

        // Get box header.
        let header = BoxHeader::read(reader)?;
        let BoxHeader { name, size: s } = header;
        if s > size {
            return Err(Error::InvalidData(
                "stsd box contains a box with a larger size than it",
            ));
        }

        let contents = match name {
            BoxType::Av01Box => StsdBoxContent::Av01(Av01Box::read_box(reader, s)?),
            // According to MPEG-4 part 15, sections 5.4.2.1.2 and 5.4.4 (or the whole 5.4 section in general),
            // the Avc1Box and Avc3Box are identical, but the Avc3Box is used in some cases.
            BoxType::Avc1Box => StsdBoxContent::Avc1(Avc1Box::read_box(reader, s)?),
            BoxType::Hvc1Box => StsdBoxContent::Hvc1(HevcBox::read_box(reader, s)?),
            BoxType::Hev1Box => StsdBoxContent::Hev1(HevcBox::read_box(reader, s)?),
            BoxType::Vp08Box => StsdBoxContent::Vp08(Vp08Box::read_box(reader, s)?),
            BoxType::Vp09Box => StsdBoxContent::Vp09(Vp09Box::read_box(reader, s)?),
            BoxType::Mp4aBox => StsdBoxContent::Mp4a(Mp4aBox::read_box(reader, s)?),
            BoxType::Tx3gBox => StsdBoxContent::Tx3g(Tx3gBox::read_box(reader, s)?),
            _ => StsdBoxContent::Unknown(name.into()),
        };

        skip_bytes_to(reader, start + size)?;

        Ok(Self {
            version,
            flags,
            contents,
        })
    }
}
