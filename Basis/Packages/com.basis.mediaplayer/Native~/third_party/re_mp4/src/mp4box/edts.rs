use serde::Serialize;
use std::io::{Read, Seek};

use crate::mp4box::elst::ElstBox;
use crate::mp4box::{
    box_start, skip_bytes_to, BoxHeader, BoxType, Error, Mp4Box, ReadBox, Result, HEADER_SIZE,
};

#[derive(Debug, Clone, PartialEq, Eq, Default, Serialize)]
pub struct EdtsBox {
    pub elst: Option<ElstBox>,
}

impl EdtsBox {
    pub(crate) fn new() -> Self {
        Default::default()
    }

    pub fn get_type() -> BoxType {
        BoxType::EdtsBox
    }

    pub fn get_size(&self) -> u64 {
        let mut size = HEADER_SIZE;
        if let Some(ref elst) = self.elst {
            size += elst.box_size();
        }
        size
    }
}

impl Mp4Box for EdtsBox {
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

impl<R: Read + Seek> ReadBox<&mut R> for EdtsBox {
    fn read_box(reader: &mut R, size: u64) -> Result<Self> {
        let start = box_start(reader)?;

        let mut edts = Self::new();

        let header = BoxHeader::read(reader)?;
        let BoxHeader { name, size: s } = header;
        if s > size {
            return Err(Error::InvalidData(
                "edts box contains a box with a larger size than it",
            ));
        }

        if name == BoxType::ElstBox {
            let elst = ElstBox::read_box(reader, s)?;
            edts.elst = Some(elst);
        }

        skip_bytes_to(reader, start + size)?;

        Ok(edts)
    }
}
