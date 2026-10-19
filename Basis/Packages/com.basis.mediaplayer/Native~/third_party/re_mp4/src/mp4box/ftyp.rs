use byteorder::{BigEndian, ReadBytesExt as _};
use serde::Serialize;
use std::io::{Read, Seek};

use crate::mp4box::{
    box_start, skip_bytes_to, BoxType, Error, FourCC, Mp4Box, ReadBox, Result, HEADER_SIZE,
};

#[derive(Debug, Clone, PartialEq, Eq, Default, Serialize)]
pub struct FtypBox {
    pub major_brand: FourCC,
    pub minor_version: u32,
    pub compatible_brands: Vec<FourCC>,
}

impl FtypBox {
    pub fn get_type() -> BoxType {
        BoxType::FtypBox
    }

    pub fn get_size(&self) -> u64 {
        HEADER_SIZE + 8 + (4 * self.compatible_brands.len() as u64)
    }
}

impl Mp4Box for FtypBox {
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
        let mut compatible_brands = Vec::new();
        for brand in &self.compatible_brands {
            compatible_brands.push(brand.to_string());
        }
        let s = format!(
            "major_brand={} minor_version={} compatible_brands={}",
            self.major_brand,
            self.minor_version,
            compatible_brands.join("-")
        );
        Ok(s)
    }
}

impl<R: Read + Seek> ReadBox<&mut R> for FtypBox {
    fn read_box(reader: &mut R, size: u64) -> Result<Self> {
        let start = box_start(reader)?;

        if size < 16 || !size.is_multiple_of(4) {
            return Err(Error::InvalidData("ftyp size too small or not aligned"));
        }
        let brand_count = (size - 16) / 4; // header + major + minor
        let major = reader.read_u32::<BigEndian>()?;
        let minor = reader.read_u32::<BigEndian>()?;

        let mut brands = Vec::new();
        for _ in 0..brand_count {
            let b = reader.read_u32::<BigEndian>()?;
            brands.push(From::from(b));
        }

        skip_bytes_to(reader, start + size)?;

        Ok(Self {
            major_brand: From::from(major),
            minor_version: minor,
            compatible_brands: brands,
        })
    }
}
