use std::io::{Read, Seek};

use serde::Serialize;

use crate::mp4box::meta::MetaBox;
use crate::mp4box::{
    box_start, skip_box, skip_bytes_to, BoxHeader, BoxType, Error, Mp4Box, ReadBox, Result,
    HEADER_SIZE,
};

#[derive(Debug, Clone, PartialEq, Eq, Default, Serialize)]
pub struct UdtaBox {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub meta: Option<MetaBox>,
}

impl UdtaBox {
    pub fn get_type() -> BoxType {
        BoxType::UdtaBox
    }

    pub fn get_size(&self) -> u64 {
        let mut size = HEADER_SIZE;
        if let Some(meta) = &self.meta {
            size += meta.box_size();
        }
        size
    }
}

impl Mp4Box for UdtaBox {
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
        Ok(String::new())
    }
}

impl<R: Read + Seek> ReadBox<&mut R> for UdtaBox {
    fn read_box(reader: &mut R, size: u64) -> Result<Self> {
        let start = box_start(reader)?;

        let mut meta = None;

        let mut current = reader.stream_position()?;
        let end = start + size;
        while current < end {
            // Get box header.
            let header = BoxHeader::read(reader)?;
            let BoxHeader { name, size: s } = header;
            if s > size {
                return Err(Error::InvalidData(
                    "udta box contains a box with a larger size than it",
                ));
            }

            match name {
                BoxType::MetaBox => {
                    meta = Some(MetaBox::read_box(reader, s)?);
                }
                _ => {
                    // XXX warn!()
                    skip_box(reader, s)?;
                }
            }

            current = reader.stream_position()?;
        }

        skip_bytes_to(reader, start + size)?;

        Ok(Self { meta })
    }
}
