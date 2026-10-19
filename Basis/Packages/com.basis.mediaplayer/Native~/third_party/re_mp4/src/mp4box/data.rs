use std::{
    convert::TryFrom as _,
    io::{Read, Seek},
};

use serde::Serialize;

use crate::mp4box::{
    box_start, BigEndian, BoxType, DataType, Mp4Box, ReadBox, ReadBytesExt as _, Result,
    HEADER_SIZE,
};

#[derive(Debug, Clone, PartialEq, Eq, Default, Serialize)]
pub struct DataBox {
    pub data: Vec<u8>,
    pub data_type: DataType,
}

impl DataBox {
    pub fn get_type() -> BoxType {
        BoxType::DataBox
    }

    pub fn get_size(&self) -> u64 {
        let mut size = HEADER_SIZE;
        size += 4; // data_type
        size += 4; // reserved
        size += self.data.len() as u64;
        size
    }
}

impl Mp4Box for DataBox {
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
        let s = format!("type={:?} len={}", self.data_type, self.data.len());
        Ok(s)
    }
}

impl<R: Read + Seek> ReadBox<&mut R> for DataBox {
    fn read_box(reader: &mut R, size: u64) -> Result<Self> {
        let start = box_start(reader)?;

        let data_type = DataType::try_from(reader.read_u32::<BigEndian>()?)?;

        reader.read_u32::<BigEndian>()?; // reserved = 0

        let current = reader.stream_position()?;
        let mut data = vec![0u8; (start + size - current) as usize];
        reader.read_exact(&mut data)?;

        Ok(Self { data, data_type })
    }
}
