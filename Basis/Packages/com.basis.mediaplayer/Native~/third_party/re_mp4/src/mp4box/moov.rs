use serde::Serialize;
use std::io::{Read, Seek};

use crate::meta::MetaBox;
use crate::mp4box::{
    box_start, skip_box, skip_bytes_to, BoxHeader, BoxType, Error, Mp4Box, ReadBox, Result,
    HEADER_SIZE,
};
use crate::mp4box::{mvex::MvexBox, mvhd::MvhdBox, trak::TrakBox, udta::UdtaBox};

#[derive(Debug, Clone, PartialEq, Eq, Default, Serialize)]
pub struct MoovBox {
    pub mvhd: MvhdBox,

    #[serde(skip_serializing_if = "Option::is_none")]
    pub meta: Option<MetaBox>,

    #[serde(skip_serializing_if = "Option::is_none")]
    pub mvex: Option<MvexBox>,

    #[serde(rename = "trak")]
    pub traks: Vec<TrakBox>,

    #[serde(skip_serializing_if = "Option::is_none")]
    pub udta: Option<UdtaBox>,
}

impl MoovBox {
    pub fn get_type() -> BoxType {
        BoxType::MoovBox
    }

    pub fn get_size(&self) -> u64 {
        let mut size = HEADER_SIZE + self.mvhd.box_size();
        for trak in &self.traks {
            size += trak.box_size();
        }
        if let Some(meta) = &self.meta {
            size += meta.box_size();
        }
        if let Some(udta) = &self.udta {
            size += udta.box_size();
        }
        size
    }
}

impl Mp4Box for MoovBox {
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
        let s = format!("traks={}", self.traks.len());
        Ok(s)
    }
}

impl<R: Read + Seek> ReadBox<&mut R> for MoovBox {
    fn read_box(reader: &mut R, size: u64) -> Result<Self> {
        let start = box_start(reader)?;

        let mut mvhd = None;
        let mut meta = None;
        let mut udta = None;
        let mut mvex = None;
        let mut traks = Vec::new();

        let mut current = reader.stream_position()?;
        let end = start + size;
        while current < end {
            // Get box header.
            let header = BoxHeader::read(reader)?;
            let BoxHeader { name, size: s } = header;
            if s > size {
                return Err(Error::InvalidData(
                    "moov box contains a box with a larger size than it",
                ));
            }

            match name {
                BoxType::MvhdBox => {
                    mvhd = Some(MvhdBox::read_box(reader, s)?);
                }
                BoxType::MetaBox => {
                    meta = Some(MetaBox::read_box(reader, s)?);
                }
                BoxType::MvexBox => {
                    mvex = Some(MvexBox::read_box(reader, s)?);
                }
                BoxType::TrakBox => {
                    let trak = TrakBox::read_box(reader, s)?;
                    traks.push(trak);
                }
                BoxType::UdtaBox => {
                    udta = Some(UdtaBox::read_box(reader, s)?);
                }
                _ => {
                    // XXX warn!()
                    skip_box(reader, s)?;
                }
            }

            current = reader.stream_position()?;
        }

        let Some(mvhd) = mvhd else {
            return Err(Error::BoxNotFound(BoxType::MvhdBox));
        };

        skip_bytes_to(reader, start + size)?;

        Ok(Self {
            mvhd,
            meta,
            mvex,
            traks,
            udta,
        })
    }
}
