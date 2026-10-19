use byteorder::{BigEndian, ReadBytesExt as _};
use serde::Serialize;
use std::io::{Read, Seek};
use std::mem::size_of;

use crate::mp4box::{
    box_start, co64, read_box_header_ext, skip_bytes_to, BoxType, Error, Mp4Box, ReadBox, Result,
    HEADER_EXT_SIZE, HEADER_SIZE,
};

#[derive(Debug, Clone, PartialEq, Eq, Default, Serialize)]
pub struct StcoBox {
    pub version: u8,
    pub flags: u32,

    #[serde(skip_serializing)]
    pub entries: Vec<u32>,
}

impl StcoBox {
    pub fn get_type() -> BoxType {
        BoxType::StcoBox
    }

    pub fn get_size(&self) -> u64 {
        HEADER_SIZE + HEADER_EXT_SIZE + 4 + (4 * self.entries.len() as u64)
    }
}

impl Mp4Box for StcoBox {
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
        let s = format!("entries={}", self.entries.len());
        Ok(s)
    }
}

impl<R: Read + Seek> ReadBox<&mut R> for StcoBox {
    fn read_box(reader: &mut R, size: u64) -> Result<Self> {
        let start = box_start(reader)?;

        let (version, flags) = read_box_header_ext(reader)?;

        let header_size = HEADER_SIZE + HEADER_EXT_SIZE;
        let other_size = size_of::<u32>(); // entry_count
        let entry_size = size_of::<u32>(); // chunk_offset
        let entry_count = reader.read_u32::<BigEndian>()?;
        if u64::from(entry_count)
            > size
                .saturating_sub(header_size)
                .saturating_sub(other_size as u64)
                / entry_size as u64
        {
            return Err(Error::InvalidData(
                "stco entry_count indicates more entries than could fit in the box",
            ));
        }
        let mut entries = Vec::with_capacity(entry_count as usize);
        for _i in 0..entry_count {
            let chunk_offset = reader.read_u32::<BigEndian>()?;
            entries.push(chunk_offset);
        }

        skip_bytes_to(reader, start + size)?;

        Ok(Self {
            version,
            flags,
            entries,
        })
    }
}

impl std::convert::TryFrom<&co64::Co64Box> for StcoBox {
    type Error = std::num::TryFromIntError;

    fn try_from(co64: &co64::Co64Box) -> std::result::Result<Self, Self::Error> {
        let entries = co64
            .entries
            .iter()
            .copied()
            .map(u32::try_from)
            .collect::<std::result::Result<Vec<_>, _>>()?;
        Ok(Self {
            version: 0,
            flags: 0,
            entries,
        })
    }
}
