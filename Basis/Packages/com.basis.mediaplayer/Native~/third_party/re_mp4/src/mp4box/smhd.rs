use byteorder::{BigEndian, ReadBytesExt as _};
use serde::Serialize;
use std::io::{Read, Seek};

use crate::mp4box::{
    box_start, read_box_header_ext, skip_bytes_to, value_i16, BoxType, FixedPointI8, Mp4Box,
    ReadBox, Result, HEADER_EXT_SIZE, HEADER_SIZE,
};

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct SmhdBox {
    pub version: u8,
    pub flags: u32,

    #[serde(with = "value_i16")]
    pub balance: FixedPointI8,
}

impl SmhdBox {
    pub fn get_type() -> BoxType {
        BoxType::SmhdBox
    }

    pub fn get_size() -> u64 {
        HEADER_SIZE + HEADER_EXT_SIZE + 4
    }
}

impl Default for SmhdBox {
    fn default() -> Self {
        Self {
            version: 0,
            flags: 0,
            balance: FixedPointI8::new_raw(0),
        }
    }
}

impl Mp4Box for SmhdBox {
    fn box_type(&self) -> BoxType {
        Self::get_type()
    }

    fn box_size(&self) -> u64 {
        Self::get_size()
    }

    fn to_json(&self) -> Result<String> {
        Ok(serde_json::to_string(&self).expect("Failed to convert to JSON"))
    }

    fn summary(&self) -> Result<String> {
        let s = format!("balance={}", self.balance.value());
        Ok(s)
    }
}

impl<R: Read + Seek> ReadBox<&mut R> for SmhdBox {
    fn read_box(reader: &mut R, size: u64) -> Result<Self> {
        let start = box_start(reader)?;

        let (version, flags) = read_box_header_ext(reader)?;

        let balance = FixedPointI8::new_raw(reader.read_i16::<BigEndian>()?);

        skip_bytes_to(reader, start + size)?;

        Ok(Self {
            version,
            flags,
            balance,
        })
    }
}
