use byteorder::{BigEndian, ReadBytesExt as _};
use serde::Serialize;
use std::io::{Read, Seek};

use crate::mp4box::{
    box_start, read_box_header_ext, skip_bytes_to, BoxType, Mp4Box, ReadBox, Result,
    HEADER_EXT_SIZE, HEADER_SIZE,
};

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Default)]
pub struct TfhdBox {
    pub version: u8,
    pub flags: u32,
    pub track_id: u32,
    pub base_data_offset: Option<u64>,
    pub sample_description_index: Option<u32>,
    pub default_sample_duration: Option<u32>,
    pub default_sample_size: Option<u32>,
    pub default_sample_flags: Option<u32>,
}

impl TfhdBox {
    pub const FLAG_BASE_DATA_OFFSET: u32 = 0x01;
    pub const FLAG_SAMPLE_DESCRIPTION_INDEX: u32 = 0x02;
    pub const FLAG_DEFAULT_SAMPLE_DURATION: u32 = 0x08;
    pub const FLAG_DEFAULT_SAMPLE_SIZE: u32 = 0x10;
    pub const FLAG_DEFAULT_SAMPLE_FLAGS: u32 = 0x20;
    pub const FLAG_DURATION_IS_EMPTY: u32 = 0x10000;
    pub const FLAG_DEFAULT_BASE_IS_MOOF: u32 = 0x20000;

    pub fn get_type() -> BoxType {
        BoxType::TfhdBox
    }

    pub fn get_size(&self) -> u64 {
        let mut sum = HEADER_SIZE + HEADER_EXT_SIZE + 4;
        if Self::FLAG_BASE_DATA_OFFSET & self.flags > 0 {
            sum += 8;
        }
        if Self::FLAG_SAMPLE_DESCRIPTION_INDEX & self.flags > 0 {
            sum += 4;
        }
        if Self::FLAG_DEFAULT_SAMPLE_DURATION & self.flags > 0 {
            sum += 4;
        }
        if Self::FLAG_DEFAULT_SAMPLE_SIZE & self.flags > 0 {
            sum += 4;
        }
        if Self::FLAG_DEFAULT_SAMPLE_FLAGS & self.flags > 0 {
            sum += 4;
        }
        sum
    }
}

impl Mp4Box for TfhdBox {
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
        let s = format!("track_id={}", self.track_id);
        Ok(s)
    }
}

impl<R: Read + Seek> ReadBox<&mut R> for TfhdBox {
    fn read_box(reader: &mut R, size: u64) -> Result<Self> {
        let start = box_start(reader)?;

        let (version, flags) = read_box_header_ext(reader)?;
        let track_id = reader.read_u32::<BigEndian>()?;
        let base_data_offset = if Self::FLAG_BASE_DATA_OFFSET & flags > 0 {
            Some(reader.read_u64::<BigEndian>()?)
        } else {
            None
        };
        let sample_description_index = if Self::FLAG_SAMPLE_DESCRIPTION_INDEX & flags > 0 {
            Some(reader.read_u32::<BigEndian>()?)
        } else {
            None
        };
        let default_sample_duration = if Self::FLAG_DEFAULT_SAMPLE_DURATION & flags > 0 {
            Some(reader.read_u32::<BigEndian>()?)
        } else {
            None
        };
        let default_sample_size = if Self::FLAG_DEFAULT_SAMPLE_SIZE & flags > 0 {
            Some(reader.read_u32::<BigEndian>()?)
        } else {
            None
        };
        let default_sample_flags = if Self::FLAG_DEFAULT_SAMPLE_FLAGS & flags > 0 {
            Some(reader.read_u32::<BigEndian>()?)
        } else {
            None
        };

        skip_bytes_to(reader, start + size)?;

        Ok(Self {
            version,
            flags,
            track_id,
            base_data_offset,
            sample_description_index,
            default_sample_duration,
            default_sample_size,
            default_sample_flags,
        })
    }
}
