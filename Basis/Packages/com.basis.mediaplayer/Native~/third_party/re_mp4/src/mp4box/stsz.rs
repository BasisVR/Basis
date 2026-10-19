use byteorder::{BigEndian, ReadBytesExt as _};
use serde::Serialize;
use std::io::{Read, Seek};
use std::mem::size_of;

use crate::mp4box::{
    box_start, read_box_header_ext, skip_bytes_to, BoxType, Error, Mp4Box, ReadBox, Result,
    HEADER_EXT_SIZE, HEADER_SIZE,
};

#[derive(Debug, Clone, PartialEq, Eq, Default, Serialize)]
pub struct StszBox {
    pub version: u8,
    pub flags: u32,
    pub sample_size: u32,
    pub sample_count: u32,

    #[serde(skip_serializing)]
    pub sample_sizes: Vec<u32>,
}

impl StszBox {
    pub fn get_type() -> BoxType {
        BoxType::StszBox
    }

    pub fn get_size(&self) -> u64 {
        HEADER_SIZE + HEADER_EXT_SIZE + 8 + (4 * self.sample_sizes.len() as u64)
    }
}

impl Mp4Box for StszBox {
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
        let s = format!(
            "sample_size={} sample_count={} sample_sizes={}",
            self.sample_size,
            self.sample_count,
            self.sample_sizes.len()
        );
        Ok(s)
    }
}

impl<R: Read + Seek> ReadBox<&mut R> for StszBox {
    fn read_box(reader: &mut R, size: u64) -> Result<Self> {
        let start = box_start(reader)?;

        let (version, flags) = read_box_header_ext(reader)?;

        let header_size = HEADER_SIZE + HEADER_EXT_SIZE;
        let other_size = size_of::<u32>() + size_of::<u32>(); // sample_size + sample_count
        let sample_size = reader.read_u32::<BigEndian>()?;
        let stsz_item_size = if sample_size == 0 {
            size_of::<u32>() // entry_size
        } else {
            0
        };
        let sample_count = reader.read_u32::<BigEndian>()?;
        let mut sample_sizes = Vec::new();
        if sample_size == 0 {
            if u64::from(sample_count)
                > size
                    .saturating_sub(header_size)
                    .saturating_sub(other_size as u64)
                    / stsz_item_size as u64
            {
                return Err(Error::InvalidData(
                    "stsz sample_count indicates more values than could fit in the box",
                ));
            }
            sample_sizes.reserve(sample_count as usize);
            for _ in 0..sample_count {
                let sample_number = reader.read_u32::<BigEndian>()?;
                sample_sizes.push(sample_number);
            }
        }

        skip_bytes_to(reader, start + size)?;

        Ok(Self {
            version,
            flags,
            sample_size,
            sample_count,
            sample_sizes,
        })
    }
}
