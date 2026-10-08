use serde::Serialize;
use std::io::{Read, Seek};

use crate::mp4box::{
    box_start, skip_box, skip_bytes_to, BoxHeader, BoxType, Error, Mp4Box, ReadBox, Result,
    HEADER_SIZE,
};
use crate::mp4box::{mfhd::MfhdBox, traf::TrafBox};

#[derive(Debug, Clone, PartialEq, Eq, Default, Serialize)]
pub struct MoofBox {
    /// The start position of the box in the stream.
    pub start: u64,

    pub mfhd: MfhdBox,

    #[serde(rename = "traf")]
    pub trafs: Vec<TrafBox>,
}

impl MoofBox {
    pub fn get_type() -> BoxType {
        BoxType::MoofBox
    }

    pub fn get_size(&self) -> u64 {
        let mut size = HEADER_SIZE + self.mfhd.box_size();
        for traf in &self.trafs {
            size += traf.box_size();
        }
        size
    }
}

impl Mp4Box for MoofBox {
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
        let s = format!("trafs={}", self.trafs.len());
        Ok(s)
    }
}

impl<R: Read + Seek> ReadBox<&mut R> for MoofBox {
    fn read_box(reader: &mut R, size: u64) -> Result<Self> {
        let start = box_start(reader)?;

        let mut mfhd = None;
        let mut trafs = Vec::new();

        let mut current = reader.stream_position()?;
        let end = start + size;
        while current < end {
            // Get box header.
            let header = BoxHeader::read(reader)?;
            let BoxHeader { name, size: s } = header;
            if s > size {
                return Err(Error::InvalidData(
                    "moof box contains a box with a larger size than it",
                ));
            }

            match name {
                BoxType::MfhdBox => {
                    mfhd = Some(MfhdBox::read_box(reader, s)?);
                }
                BoxType::TrafBox => {
                    let traf = TrafBox::read_box(reader, s)?;
                    trafs.push(traf);
                }
                _ => {
                    // XXX warn!()
                    skip_box(reader, s)?;
                }
            }
            current = reader.stream_position()?;
        }

        let Some(mfhd) = mfhd else {
            return Err(Error::BoxNotFound(BoxType::MfhdBox));
        };

        skip_bytes_to(reader, start + size)?;

        Ok(Self { start, mfhd, trafs })
    }
}
