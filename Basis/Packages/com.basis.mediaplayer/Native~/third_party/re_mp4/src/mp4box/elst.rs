use byteorder::{BigEndian, ReadBytesExt as _};
use serde::Serialize;
use std::io::{Read, Seek};
use std::mem::size_of;

use crate::mp4box::{
    box_start, read_box_header_ext, skip_bytes_to, BoxType, Error, Mp4Box, ReadBox, Result,
    HEADER_EXT_SIZE, HEADER_SIZE,
};

#[derive(Debug, Clone, PartialEq, Eq, Default, Serialize)]
pub struct ElstBox {
    pub version: u8,
    pub flags: u32,

    #[serde(skip_serializing)]
    pub entries: Vec<ElstEntry>,
}

#[derive(Debug, Clone, PartialEq, Eq, Default, Serialize)]
pub struct ElstEntry {
    pub segment_duration: u64,
    pub media_time: u64,
    pub media_rate: u16,
    pub media_rate_fraction: u16,
}

impl ElstBox {
    pub fn get_type() -> BoxType {
        BoxType::ElstBox
    }

    pub fn get_size(&self) -> u64 {
        let mut size = HEADER_SIZE + HEADER_EXT_SIZE + 4;
        if self.version == 1 {
            size += self.entries.len() as u64 * 20;
        } else if self.version == 0 {
            size += self.entries.len() as u64 * 12;
        }
        size
    }
}

impl Mp4Box for ElstBox {
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
        let s = format!("elst_entries={}", self.entries.len());
        Ok(s)
    }
}

impl<R: Read + Seek> ReadBox<&mut R> for ElstBox {
    fn read_box(reader: &mut R, size: u64) -> Result<Self> {
        let start = box_start(reader)?;

        let (version, flags) = read_box_header_ext(reader)?;

        let header_size = HEADER_SIZE + HEADER_EXT_SIZE;
        let entry_count = reader.read_u32::<BigEndian>()?;
        let other_size = size_of::<i32>(); // entry_count
        let entry_size = {
            let mut entry_size = 0;
            entry_size += if version == 1 {
                size_of::<u64>() + size_of::<i64>() // segment_duration + media_time
            } else {
                size_of::<u32>() + size_of::<i32>() // segment_duration + media_time
            };
            entry_size += size_of::<i16>() + size_of::<i16>(); // media_rate_integer + media_rate_fraction
            entry_size
        };
        if u64::from(entry_count)
            > size
                .saturating_sub(header_size)
                .saturating_sub(other_size as u64)
                / entry_size as u64
        {
            return Err(Error::InvalidData(
                "elst entry_count indicates more entries than could fit in the box",
            ));
        }
        let mut entries = Vec::with_capacity(entry_count as usize);
        for _ in 0..entry_count {
            let (segment_duration, media_time) = if version == 1 {
                (
                    reader.read_u64::<BigEndian>()?,
                    reader.read_u64::<BigEndian>()?,
                )
            } else {
                (
                    reader.read_u32::<BigEndian>()? as u64,
                    reader.read_u32::<BigEndian>()? as u64,
                )
            };

            let entry = ElstEntry {
                segment_duration,
                media_time,
                media_rate: reader.read_u16::<BigEndian>()?,
                media_rate_fraction: reader.read_u16::<BigEndian>()?,
            };
            entries.push(entry);
        }

        skip_bytes_to(reader, start + size)?;

        Ok(Self {
            version,
            flags,
            entries,
        })
    }
}
