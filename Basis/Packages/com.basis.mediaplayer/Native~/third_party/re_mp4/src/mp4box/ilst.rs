use std::borrow::Cow;
use std::collections::HashMap;
use std::io::{Read, Seek};

use byteorder::ByteOrder as _;
use serde::Serialize;

use crate::mp4box::data::DataBox;
use crate::mp4box::{
    box_start, skip_box, skip_bytes_to, BigEndian, BoxHeader, BoxType, DataType, Error, Metadata,
    MetadataKey, Mp4Box, ReadBox, Result, HEADER_SIZE,
};

#[derive(Debug, Clone, PartialEq, Eq, Default, Serialize)]
pub struct IlstBox {
    pub items: HashMap<MetadataKey, IlstItemBox>,
}

impl IlstBox {
    pub fn get_type() -> BoxType {
        BoxType::IlstBox
    }

    pub fn get_size(&self) -> u64 {
        HEADER_SIZE + self.items.values().map(|item| item.get_size()).sum::<u64>()
    }
}

impl Mp4Box for IlstBox {
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
        let s = format!("item_count={}", self.items.len());
        Ok(s)
    }
}

impl<R: Read + Seek> ReadBox<&mut R> for IlstBox {
    fn read_box(reader: &mut R, size: u64) -> Result<Self> {
        let start = box_start(reader)?;

        let mut items = HashMap::new();

        let mut current = reader.stream_position()?;
        let end = start + size;
        while current < end {
            // Get box header.
            let header = BoxHeader::read(reader)?;
            let BoxHeader { name, size: s } = header;
            if s > size {
                return Err(Error::InvalidData(
                    "ilst box contains a box with a larger size than it",
                ));
            }

            match name {
                BoxType::NameBox => {
                    items.insert(MetadataKey::Title, IlstItemBox::read_box(reader, s)?);
                }
                BoxType::DayBox => {
                    items.insert(MetadataKey::Year, IlstItemBox::read_box(reader, s)?);
                }
                BoxType::CovrBox => {
                    items.insert(MetadataKey::Poster, IlstItemBox::read_box(reader, s)?);
                }
                BoxType::DescBox => {
                    items.insert(MetadataKey::Summary, IlstItemBox::read_box(reader, s)?);
                }
                _ => {
                    // XXX warn!()
                    skip_box(reader, s)?;
                }
            }

            current = reader.stream_position()?;
        }

        skip_bytes_to(reader, start + size)?;

        Ok(Self { items })
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Default, Serialize)]
pub struct IlstItemBox {
    pub data: DataBox,
}

impl IlstItemBox {
    fn get_size(&self) -> u64 {
        HEADER_SIZE + self.data.box_size()
    }
}

impl<R: Read + Seek> ReadBox<&mut R> for IlstItemBox {
    fn read_box(reader: &mut R, size: u64) -> Result<Self> {
        let start = box_start(reader)?;

        let mut data = None;

        let mut current = reader.stream_position()?;
        let end = start + size;
        while current < end {
            // Get box header.
            let header = BoxHeader::read(reader)?;
            let BoxHeader { name, size: s } = header;
            if s > size {
                return Err(Error::InvalidData(
                    "ilst item box contains a box with a larger size than it",
                ));
            }

            match name {
                BoxType::DataBox => {
                    data = Some(DataBox::read_box(reader, s)?);
                }
                _ => {
                    // XXX warn!()
                    skip_box(reader, s)?;
                }
            }

            current = reader.stream_position()?;
        }

        let Some(data) = data else {
            return Err(Error::BoxNotFound(BoxType::DataBox));
        };

        skip_bytes_to(reader, start + size)?;

        Ok(Self { data })
    }
}

impl Metadata<'_> for IlstBox {
    fn title(&self) -> Option<Cow<'_, str>> {
        self.items.get(&MetadataKey::Title).map(item_to_str)
    }

    fn year(&self) -> Option<u32> {
        self.items.get(&MetadataKey::Year).and_then(item_to_u32)
    }

    fn poster(&self) -> Option<&[u8]> {
        self.items.get(&MetadataKey::Poster).map(item_to_bytes)
    }

    fn summary(&self) -> Option<Cow<'_, str>> {
        self.items.get(&MetadataKey::Summary).map(item_to_str)
    }
}

fn item_to_bytes(item: &IlstItemBox) -> &[u8] {
    &item.data.data
}

fn item_to_str(item: &IlstItemBox) -> Cow<'_, str> {
    String::from_utf8_lossy(&item.data.data)
}

fn item_to_u32(item: &IlstItemBox) -> Option<u32> {
    match item.data.data_type {
        DataType::Binary if item.data.data.len() == 4 => Some(BigEndian::read_u32(&item.data.data)),
        DataType::Text => String::from_utf8_lossy(&item.data.data).parse::<u32>().ok(),
        _ => None,
    }
}
