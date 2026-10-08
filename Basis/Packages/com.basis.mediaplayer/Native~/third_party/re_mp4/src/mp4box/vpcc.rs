use crate::mp4box::{
    box_start, read_box_header_ext, skip_bytes_to, BigEndian, BoxType, Read, ReadBox,
    ReadBytesExt as _, Result, Seek, HEADER_EXT_SIZE, HEADER_SIZE,
};
use crate::Mp4Box;
use serde::Serialize;

#[derive(Debug, Clone, PartialEq, Eq, Default, Serialize)]
pub struct VpccBox {
    pub version: u8,
    pub flags: u32,
    pub profile: u8,
    pub level: u8,
    pub bit_depth: u8,
    pub chroma_subsampling: u8,
    pub video_full_range_flag: bool,
    pub color_primaries: u8,
    pub transfer_characteristics: u8,
    pub matrix_coefficients: u8,
    pub codec_initialization_data_size: u16,
}

impl VpccBox {
    pub const DEFAULT_VERSION: u8 = 1;
    pub const DEFAULT_BIT_DEPTH: u8 = 8;
}

impl Mp4Box for VpccBox {
    fn box_type(&self) -> BoxType {
        BoxType::VpccBox
    }

    fn box_size(&self) -> u64 {
        HEADER_SIZE + HEADER_EXT_SIZE + 8
    }

    fn to_json(&self) -> Result<String> {
        Ok(serde_json::to_string(&self).expect("Failed to convert to JSON"))
    }

    fn summary(&self) -> Result<String> {
        Ok(format!("{self:?}"))
    }
}

impl<R: Read + Seek> ReadBox<&mut R> for VpccBox {
    fn read_box(reader: &mut R, size: u64) -> Result<Self> {
        let start = box_start(reader)?;
        let (version, flags) = read_box_header_ext(reader)?;

        let profile: u8 = reader.read_u8()?;
        let level: u8 = reader.read_u8()?;
        let (bit_depth, chroma_subsampling, video_full_range_flag) = {
            let b = reader.read_u8()?;
            (b >> 4, b << 4 >> 5, b & 0x01 == 1)
        };
        let transfer_characteristics: u8 = reader.read_u8()?;
        let matrix_coefficients: u8 = reader.read_u8()?;
        let codec_initialization_data_size: u16 = reader.read_u16::<BigEndian>()?;

        skip_bytes_to(reader, start + size)?;

        Ok(Self {
            version,
            flags,
            profile,
            level,
            bit_depth,
            chroma_subsampling,
            video_full_range_flag,
            color_primaries: 0,
            transfer_characteristics,
            matrix_coefficients,
            codec_initialization_data_size,
        })
    }
}
