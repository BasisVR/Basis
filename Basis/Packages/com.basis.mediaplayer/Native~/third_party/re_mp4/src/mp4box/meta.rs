use std::io::{Read, Seek};

use serde::Serialize;

use crate::mp4box::hdlr::HdlrBox;
use crate::mp4box::ilst::IlstBox;
use crate::mp4box::{
    box_start, skip_box, BigEndian, BoxHeader, BoxType, Error, FourCC, Mp4Box, ReadBox,
    ReadBytesExt as _, Result, SeekFrom, HEADER_EXT_SIZE, HEADER_SIZE,
};

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(tag = "hdlr")]
#[serde(rename_all = "lowercase")]
pub enum MetaBox {
    Mdir {
        #[serde(skip_serializing_if = "Option::is_none")]
        ilst: Option<IlstBox>,
    },

    #[serde(skip)]
    Unknown {
        #[serde(skip)]
        hdlr: HdlrBox,

        #[serde(skip)]
        data: Vec<(BoxType, Vec<u8>)>,
    },
}

const MDIR: FourCC = FourCC { value: *b"mdir" };

impl MetaBox {
    pub fn get_type() -> BoxType {
        BoxType::MetaBox
    }

    pub fn get_size(&self) -> u64 {
        let mut size = HEADER_SIZE + HEADER_EXT_SIZE;
        match self {
            Self::Mdir { ilst } => {
                size += HdlrBox::default().box_size();
                if let Some(ilst) = ilst {
                    size += ilst.box_size();
                }
            }
            Self::Unknown { hdlr, data } => {
                size += hdlr.box_size()
                    + data
                        .iter()
                        .map(|(_, data)| data.len() as u64 + HEADER_SIZE)
                        .sum::<u64>();
            }
        }
        size
    }
}

impl Mp4Box for MetaBox {
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
        let s = match self {
            Self::Mdir { .. } => "hdlr=ilst".to_owned(),
            Self::Unknown { hdlr, data } => {
                format!("hdlr={} data_len={}", hdlr.handler_type, data.len())
            }
        };
        Ok(s)
    }
}

impl Default for MetaBox {
    fn default() -> Self {
        Self::Unknown {
            hdlr: Default::default(),
            data: Default::default(),
        }
    }
}

impl<R: Read + Seek> ReadBox<&mut R> for MetaBox {
    fn read_box(reader: &mut R, size: u64) -> Result<Self> {
        let start = box_start(reader)?;

        let extended_header = reader.read_u32::<BigEndian>()?;
        if extended_header != 0 {
            // ISO mp4 requires this header (version & flags) to be 0. Some
            // files skip the extended header and directly start the hdlr box.
            let possible_hdlr = BoxType::from(reader.read_u32::<BigEndian>()?);
            if possible_hdlr == BoxType::HdlrBox {
                // This file skipped the extended header! Go back to start.
                reader.seek(SeekFrom::Current(-8))?;
            } else {
                // Looks like we actually have a bad version number or flags.
                let v = (extended_header >> 24) as u8;
                return Err(Error::UnsupportedBoxVersion(BoxType::MetaBox, v));
            }
        }

        let mut current = reader.stream_position()?;
        let end = start + size;

        let content_start = current;

        // find the hdlr box
        let mut hdlr = None;
        while current < end {
            // Get box header.
            let header = BoxHeader::read(reader)?;
            let BoxHeader { name, size: s } = header;

            match name {
                BoxType::HdlrBox => {
                    hdlr = Some(HdlrBox::read_box(reader, s)?);
                }
                _ => {
                    // XXX warn!()
                    skip_box(reader, s)?;
                }
            }

            current = reader.stream_position()?;
        }

        let Some(hdlr) = hdlr else {
            return Err(Error::BoxNotFound(BoxType::HdlrBox));
        };

        // rewind and handle the other boxes
        reader.seek(SeekFrom::Start(content_start))?;
        current = reader.stream_position()?;

        let mut ilst = None;

        if hdlr.handler_type == MDIR {
            while current < end {
                // Get box header.
                let header = BoxHeader::read(reader)?;
                let BoxHeader { name, size: s } = header;

                match name {
                    BoxType::IlstBox => {
                        ilst = Some(IlstBox::read_box(reader, s)?);
                    }
                    _ => {
                        // XXX warn!()
                        skip_box(reader, s)?;
                    }
                }

                current = reader.stream_position()?;
            }

            Ok(Self::Mdir { ilst })
        } else {
            let mut data = Vec::new();

            while current < end {
                // Get box header.
                let header = BoxHeader::read(reader)?;
                let BoxHeader { name, size: s } = header;

                if name == BoxType::HdlrBox {
                    skip_box(reader, s)?;
                } else {
                    let mut box_data = vec![0; (s - HEADER_SIZE) as usize];
                    reader.read_exact(&mut box_data)?;

                    data.push((name, box_data));
                }

                current = reader.stream_position()?;
            }

            Ok(Self::Unknown { hdlr, data })
        }
    }
}
