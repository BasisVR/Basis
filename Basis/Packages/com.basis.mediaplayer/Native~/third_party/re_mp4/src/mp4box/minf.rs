use serde::Serialize;
use std::io::{Read, Seek};

use crate::mp4box::{
    box_start, skip_box, skip_bytes_to, BoxHeader, BoxType, Error, Mp4Box, ReadBox, Result,
    HEADER_SIZE,
};
use crate::mp4box::{dinf::DinfBox, smhd::SmhdBox, stbl::StblBox, vmhd::VmhdBox};

#[derive(Debug, Clone, PartialEq, Eq, Default, Serialize)]
pub struct MinfBox {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub vmhd: Option<VmhdBox>,

    #[serde(skip_serializing_if = "Option::is_none")]
    pub smhd: Option<SmhdBox>,

    pub dinf: DinfBox,
    pub stbl: StblBox,
}

impl MinfBox {
    pub fn get_type() -> BoxType {
        BoxType::MinfBox
    }

    pub fn get_size(&self) -> u64 {
        let mut size = HEADER_SIZE;
        if let Some(ref vmhd) = self.vmhd {
            size += vmhd.box_size();
        }
        if let Some(ref smhd) = self.smhd {
            size += smhd.box_size();
        }
        size += self.dinf.box_size();
        size += self.stbl.box_size();
        size
    }
}

impl Mp4Box for MinfBox {
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

impl<R: Read + Seek> ReadBox<&mut R> for MinfBox {
    fn read_box(reader: &mut R, size: u64) -> Result<Self> {
        let start = box_start(reader)?;

        let mut vmhd = None;
        let mut smhd = None;
        let mut dinf = None;
        let mut stbl = None;

        let mut current = reader.stream_position()?;
        let end = start + size;
        while current < end {
            // Get box header.
            let header = BoxHeader::read(reader)?;
            let BoxHeader { name, size: s } = header;
            if s > size {
                return Err(Error::InvalidData(
                    "minf box contains a box with a larger size than it",
                ));
            }

            match name {
                BoxType::VmhdBox => {
                    vmhd = Some(VmhdBox::read_box(reader, s)?);
                }
                BoxType::SmhdBox => {
                    smhd = Some(SmhdBox::read_box(reader, s)?);
                }
                BoxType::DinfBox => {
                    dinf = Some(DinfBox::read_box(reader, s)?);
                }
                BoxType::StblBox => {
                    stbl = Some(StblBox::read_box(reader, s)?);
                }
                _ => {
                    // XXX warn!()
                    skip_box(reader, s)?;
                }
            }

            current = reader.stream_position()?;
        }

        let Some(dinf) = dinf else {
            return Err(Error::BoxNotFound(BoxType::DinfBox));
        };
        let Some(stbl) = stbl else {
            return Err(Error::BoxNotFound(BoxType::StblBox));
        };

        skip_bytes_to(reader, start + size)?;

        Ok(Self {
            vmhd,
            smhd,
            dinf,
            stbl,
        })
    }
}
