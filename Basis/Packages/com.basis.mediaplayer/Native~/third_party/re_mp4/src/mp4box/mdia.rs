use serde::Serialize;
use std::io::{Read, Seek};

use crate::mp4box::{
    box_start, skip_box, skip_bytes_to, BoxHeader, BoxType, Error, Mp4Box, ReadBox, Result,
    HEADER_SIZE,
};
use crate::mp4box::{hdlr::HdlrBox, mdhd::MdhdBox, minf::MinfBox};

#[derive(Debug, Clone, PartialEq, Eq, Default, Serialize)]
pub struct MdiaBox {
    pub mdhd: MdhdBox,
    pub hdlr: HdlrBox,
    pub minf: MinfBox,
}

impl MdiaBox {
    pub fn get_type() -> BoxType {
        BoxType::MdiaBox
    }

    pub fn get_size(&self) -> u64 {
        HEADER_SIZE + self.mdhd.box_size() + self.hdlr.box_size() + self.minf.box_size()
    }
}

impl Mp4Box for MdiaBox {
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
        let s = String::new();
        Ok(s)
    }
}

impl<R: Read + Seek> ReadBox<&mut R> for MdiaBox {
    fn read_box(reader: &mut R, size: u64) -> Result<Self> {
        let start = box_start(reader)?;

        let mut mdhd = None;
        let mut hdlr = None;
        let mut minf = None;

        let mut current = reader.stream_position()?;
        let end = start + size;
        while current < end {
            // Get box header.
            let header = BoxHeader::read(reader)?;
            let BoxHeader { name, size: s } = header;
            if s > size {
                return Err(Error::InvalidData(
                    "mdia box contains a box with a larger size than it",
                ));
            }

            match name {
                BoxType::MdhdBox => {
                    mdhd = Some(MdhdBox::read_box(reader, s)?);
                }
                BoxType::HdlrBox => {
                    hdlr = Some(HdlrBox::read_box(reader, s)?);
                }
                BoxType::MinfBox => {
                    minf = Some(MinfBox::read_box(reader, s)?);
                }
                _ => {
                    // XXX warn!()
                    skip_box(reader, s)?;
                }
            }

            current = reader.stream_position()?;
        }

        let Some(mdhd) = mdhd else {
            return Err(Error::BoxNotFound(BoxType::MdhdBox));
        };
        let Some(hdlr) = hdlr else {
            return Err(Error::BoxNotFound(BoxType::HdlrBox));
        };
        let Some(minf) = minf else {
            return Err(Error::BoxNotFound(BoxType::MinfBox));
        };

        skip_bytes_to(reader, start + size)?;

        Ok(Self { mdhd, hdlr, minf })
    }
}
