use serde::Serialize;
use std::io::{Read, Seek};

use crate::mp4box::{
    box_start, skip_box, skip_bytes_to, BoxHeader, BoxType, Error, Mp4Box, ReadBox, Result,
    HEADER_SIZE,
};
use crate::mp4box::{
    co64::Co64Box, ctts::CttsBox, stco::StcoBox, stsc::StscBox, stsd::StsdBox, stss::StssBox,
    stsz::StszBox, stts::SttsBox,
};

#[derive(Debug, Clone, PartialEq, Eq, Default, Serialize)]
pub struct StblBox {
    pub stsd: StsdBox,
    pub stts: SttsBox,

    #[serde(skip_serializing_if = "Option::is_none")]
    pub ctts: Option<CttsBox>,

    #[serde(skip_serializing_if = "Option::is_none")]
    pub stss: Option<StssBox>,
    pub stsc: StscBox,
    pub stsz: StszBox,

    #[serde(skip_serializing_if = "Option::is_none")]
    pub stco: Option<StcoBox>,

    #[serde(skip_serializing_if = "Option::is_none")]
    pub co64: Option<Co64Box>,
}

impl StblBox {
    pub fn get_type() -> BoxType {
        BoxType::StblBox
    }

    pub fn get_size(&self) -> u64 {
        let mut size = HEADER_SIZE;
        size += self.stsd.box_size();
        size += self.stts.box_size();
        if let Some(ref ctts) = self.ctts {
            size += ctts.box_size();
        }
        if let Some(ref stss) = self.stss {
            size += stss.box_size();
        }
        size += self.stsc.box_size();
        size += self.stsz.box_size();
        if let Some(ref stco) = self.stco {
            size += stco.box_size();
        }
        if let Some(ref co64) = self.co64 {
            size += co64.box_size();
        }
        size
    }
}

impl Mp4Box for StblBox {
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

impl<R: Read + Seek> ReadBox<&mut R> for StblBox {
    fn read_box(reader: &mut R, size: u64) -> Result<Self> {
        let start = box_start(reader)?;

        let mut stsd = None;
        let mut stts = None;
        let mut ctts = None;
        let mut stss = None;
        let mut stsc = None;
        let mut stsz = None;
        let mut stco = None;
        let mut co64 = None;

        let mut current = reader.stream_position()?;
        let end = start + size;
        while current < end {
            // Get box header.
            let header = BoxHeader::read(reader)?;
            let BoxHeader { name, size: s } = header;
            if s > size {
                return Err(Error::InvalidData(
                    "stbl box contains a box with a larger size than it",
                ));
            }

            match name {
                BoxType::StsdBox => {
                    stsd = Some(StsdBox::read_box(reader, s)?);
                }
                BoxType::SttsBox => {
                    stts = Some(SttsBox::read_box(reader, s)?);
                }
                BoxType::CttsBox => {
                    ctts = Some(CttsBox::read_box(reader, s)?);
                }
                BoxType::StssBox => {
                    stss = Some(StssBox::read_box(reader, s)?);
                }
                BoxType::StscBox => {
                    stsc = Some(StscBox::read_box(reader, s)?);
                }
                BoxType::StszBox => {
                    stsz = Some(StszBox::read_box(reader, s)?);
                }
                BoxType::StcoBox => {
                    stco = Some(StcoBox::read_box(reader, s)?);
                }
                BoxType::Co64Box => {
                    co64 = Some(Co64Box::read_box(reader, s)?);
                }
                _ => {
                    // XXX warn!()
                    skip_box(reader, s)?;
                }
            }
            current = reader.stream_position()?;
        }

        let Some(stsd) = stsd else {
            return Err(Error::BoxNotFound(BoxType::StsdBox));
        };
        let Some(stts) = stts else {
            return Err(Error::BoxNotFound(BoxType::SttsBox));
        };
        let Some(stsc) = stsc else {
            return Err(Error::BoxNotFound(BoxType::StscBox));
        };
        let Some(stsz) = stsz else {
            return Err(Error::BoxNotFound(BoxType::StszBox));
        };
        if stco.is_none() && co64.is_none() {
            return Err(Error::Box2NotFound(BoxType::StcoBox, BoxType::Co64Box));
        }

        skip_bytes_to(reader, start + size)?;

        Ok(Self {
            stsd,
            stts,
            ctts,
            stss,
            stsc,
            stsz,
            stco,
            co64,
        })
    }
}
