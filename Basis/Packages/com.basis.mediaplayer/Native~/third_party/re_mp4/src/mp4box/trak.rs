use serde::Serialize;
use std::io::{Read, Seek};

use crate::meta::MetaBox;
use crate::mp4box::{
    box_start, skip_box, skip_bytes_to, BoxHeader, BoxType, Error, Mp4Box, ReadBox, Result,
    HEADER_SIZE,
};
use crate::mp4box::{edts::EdtsBox, mdia::MdiaBox, tkhd::TkhdBox};

#[derive(Debug, Clone, PartialEq, Eq, Default, Serialize)]
pub struct TrakBox {
    pub tkhd: TkhdBox,

    #[serde(skip_serializing_if = "Option::is_none")]
    pub edts: Option<EdtsBox>,

    #[serde(skip_serializing_if = "Option::is_none")]
    pub meta: Option<MetaBox>,

    pub mdia: MdiaBox,
}

impl TrakBox {
    pub fn get_type() -> BoxType {
        BoxType::TrakBox
    }

    pub fn get_size(&self) -> u64 {
        let mut size = HEADER_SIZE;
        size += self.tkhd.box_size();
        if let Some(ref edts) = self.edts {
            size += edts.box_size();
        }
        size += self.mdia.box_size();
        size
    }
}

impl Mp4Box for TrakBox {
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

impl<R: Read + Seek> ReadBox<&mut R> for TrakBox {
    fn read_box(reader: &mut R, size: u64) -> Result<Self> {
        let start = box_start(reader)?;

        let mut tkhd = None;
        let mut edts = None;
        let mut meta = None;
        let mut mdia = None;

        let mut current = reader.stream_position()?;
        let end = start + size;
        while current < end {
            // Get box header.
            let header = BoxHeader::read(reader)?;
            let BoxHeader { name, size: s } = header;
            if s > size {
                return Err(Error::InvalidData(
                    "trak box contains a box with a larger size than it",
                ));
            }

            match name {
                BoxType::TkhdBox => {
                    tkhd = Some(TkhdBox::read_box(reader, s)?);
                }
                BoxType::EdtsBox => {
                    edts = Some(EdtsBox::read_box(reader, s)?);
                }
                BoxType::MetaBox => {
                    meta = Some(MetaBox::read_box(reader, s)?);
                }
                BoxType::MdiaBox => {
                    mdia = Some(MdiaBox::read_box(reader, s)?);
                }
                _ => {
                    // XXX warn!()
                    skip_box(reader, s)?;
                }
            }

            current = reader.stream_position()?;
        }

        let Some(tkhd) = tkhd else {
            return Err(Error::BoxNotFound(BoxType::TkhdBox));
        };
        let Some(mdia) = mdia else {
            return Err(Error::BoxNotFound(BoxType::MdiaBox));
        };

        skip_bytes_to(reader, start + size)?;

        Ok(Self {
            tkhd,
            edts,
            meta,
            mdia,
        })
    }
}
