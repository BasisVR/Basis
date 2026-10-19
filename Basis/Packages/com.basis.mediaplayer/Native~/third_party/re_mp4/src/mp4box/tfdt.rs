use byteorder::{BigEndian, ReadBytesExt as _};
use serde::Serialize;
use std::io::{Read, Seek};

use crate::mp4box::{
    box_start, read_box_header_ext, skip_bytes_to, BoxType, Error, Mp4Box, ReadBox, Result,
    HEADER_EXT_SIZE, HEADER_SIZE,
};

#[derive(Debug, Clone, PartialEq, Eq, Default, Serialize)]
pub struct TfdtBox {
    pub version: u8,
    pub flags: u32,
    pub base_media_decode_time: u64,
}

impl TfdtBox {
    pub fn get_type() -> BoxType {
        BoxType::TfdtBox
    }

    pub fn get_size(&self) -> u64 {
        let mut sum = HEADER_SIZE + HEADER_EXT_SIZE;
        if self.version == 1 {
            sum += 8;
        } else {
            sum += 4;
        }
        sum
    }
}

impl Mp4Box for TfdtBox {
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
        let s = format!("base_media_decode_time={}", self.base_media_decode_time);
        Ok(s)
    }
}

impl<R: Read + Seek> ReadBox<&mut R> for TfdtBox {
    fn read_box(reader: &mut R, size: u64) -> Result<Self> {
        let start = box_start(reader)?;

        let (version, flags) = read_box_header_ext(reader)?;

        let base_media_decode_time = if version == 1 {
            reader.read_u64::<BigEndian>()?
        } else if version == 0 {
            reader.read_u32::<BigEndian>()? as u64
        } else {
            return Err(Error::InvalidData("version must be 0 or 1"));
        };

        skip_bytes_to(reader, start + size)?;

        Ok(Self {
            version,
            flags,
            base_media_decode_time,
        })
    }
}
