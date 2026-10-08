use byteorder::{BigEndian, ReadBytesExt as _};
use serde::Serialize;
use std::io::{Read, Seek};

use crate::mp4box::{box_start, skip_bytes_to, BoxType, Mp4Box, ReadBox, Result, HEADER_SIZE};

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct Tx3gBox {
    pub data_reference_index: u16,
    pub display_flags: u32,
    pub horizontal_justification: i8,
    pub vertical_justification: i8,
    pub bg_color_rgba: RgbaColor,
    pub box_record: [i16; 4],
    pub style_record: [u8; 12],
}

#[derive(Debug, Clone, PartialEq, Eq, Default, Serialize)]
pub struct RgbaColor {
    pub red: u8,
    pub green: u8,
    pub blue: u8,
    pub alpha: u8,
}

impl Default for Tx3gBox {
    fn default() -> Self {
        Self {
            data_reference_index: 0,
            display_flags: 0,
            horizontal_justification: 1,
            vertical_justification: -1,
            bg_color_rgba: RgbaColor {
                red: 0,
                green: 0,
                blue: 0,
                alpha: 255,
            },
            box_record: [0, 0, 0, 0],
            style_record: [0, 0, 0, 0, 0, 1, 0, 16, 255, 255, 255, 255],
        }
    }
}

impl Tx3gBox {
    pub fn get_type() -> BoxType {
        BoxType::Tx3gBox
    }

    pub fn get_size() -> u64 {
        HEADER_SIZE + 6 + 32
    }
}

impl Mp4Box for Tx3gBox {
    fn box_type(&self) -> BoxType {
        Self::get_type()
    }

    fn box_size(&self) -> u64 {
        Self::get_size()
    }

    fn to_json(&self) -> Result<String> {
        Ok(serde_json::to_string(&self).expect("Failed to convert to JSON"))
    }

    fn summary(&self) -> Result<String> {
        let s = format!("data_reference_index={} horizontal_justification={} vertical_justification={} rgba={}{}{}{}",
            self.data_reference_index, self.horizontal_justification,
            self.vertical_justification, self.bg_color_rgba.red,
            self.bg_color_rgba.green, self.bg_color_rgba.blue, self.bg_color_rgba.alpha);
        Ok(s)
    }
}

impl<R: Read + Seek> ReadBox<&mut R> for Tx3gBox {
    fn read_box(reader: &mut R, size: u64) -> Result<Self> {
        let start = box_start(reader)?;

        reader.read_u32::<BigEndian>()?; // reserved
        reader.read_u16::<BigEndian>()?; // reserved
        let data_reference_index = reader.read_u16::<BigEndian>()?;

        let display_flags = reader.read_u32::<BigEndian>()?;
        let horizontal_justification = reader.read_i8()?;
        let vertical_justification = reader.read_i8()?;
        let bg_color_rgba = RgbaColor {
            red: reader.read_u8()?,
            green: reader.read_u8()?,
            blue: reader.read_u8()?,
            alpha: reader.read_u8()?,
        };
        let box_record: [i16; 4] = [
            reader.read_i16::<BigEndian>()?,
            reader.read_i16::<BigEndian>()?,
            reader.read_i16::<BigEndian>()?,
            reader.read_i16::<BigEndian>()?,
        ];
        let style_record: [u8; 12] = [
            reader.read_u8()?,
            reader.read_u8()?,
            reader.read_u8()?,
            reader.read_u8()?,
            reader.read_u8()?,
            reader.read_u8()?,
            reader.read_u8()?,
            reader.read_u8()?,
            reader.read_u8()?,
            reader.read_u8()?,
            reader.read_u8()?,
            reader.read_u8()?,
        ];

        skip_bytes_to(reader, start + size)?;

        Ok(Self {
            data_reference_index,
            display_flags,
            horizontal_justification,
            vertical_justification,
            bg_color_rgba,
            box_record,
            style_record,
        })
    }
}
