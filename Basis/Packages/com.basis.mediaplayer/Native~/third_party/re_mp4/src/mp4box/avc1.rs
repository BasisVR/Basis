use byteorder::{BigEndian, ReadBytesExt as _};
use serde::Serialize;
use std::io::{Read, Seek};

use crate::mp4box::{
    box_start, skip_bytes, skip_bytes_to, value_u32, BoxHeader, BoxType, Error, FixedPointU16,
    Mp4Box, RawBox, ReadBox, Result, HEADER_SIZE,
};

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct Avc1Box {
    pub data_reference_index: u16,
    pub width: u16,
    pub height: u16,

    #[serde(with = "value_u32")]
    pub horizresolution: FixedPointU16,

    #[serde(with = "value_u32")]
    pub vertresolution: FixedPointU16,
    pub frame_count: u16,
    pub depth: u16, // This is usually 24, even for HDR with bit_depth=10
    pub avcc: RawBox<AvcCBox>,
}

impl Default for Avc1Box {
    fn default() -> Self {
        Self {
            data_reference_index: 0,
            width: 0,
            height: 0,
            horizresolution: FixedPointU16::new(0x48),
            vertresolution: FixedPointU16::new(0x48),
            frame_count: 1,
            depth: 0x0018,
            avcc: RawBox::default(),
        }
    }
}

impl Avc1Box {
    pub fn get_type() -> BoxType {
        BoxType::Avc1Box
    }

    pub fn get_size(&self) -> u64 {
        HEADER_SIZE + 8 + 70 + self.avcc.box_size()
    }
}

impl Mp4Box for Avc1Box {
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
        let s = format!(
            "data_reference_index={} width={} height={} frame_count={}",
            self.data_reference_index, self.width, self.height, self.frame_count
        );
        Ok(s)
    }
}

impl<R: Read + Seek> ReadBox<&mut R> for Avc1Box {
    fn read_box(reader: &mut R, size: u64) -> Result<Self> {
        let start = box_start(reader)?;

        reader.read_u32::<BigEndian>()?; // reserved
        reader.read_u16::<BigEndian>()?; // reserved
        let data_reference_index = reader.read_u16::<BigEndian>()?;

        reader.read_u32::<BigEndian>()?; // pre-defined, reserved
        reader.read_u64::<BigEndian>()?; // pre-defined
        reader.read_u32::<BigEndian>()?; // pre-defined
        let width = reader.read_u16::<BigEndian>()?;
        let height = reader.read_u16::<BigEndian>()?;
        let horizresolution = FixedPointU16::new_raw(reader.read_u32::<BigEndian>()?);
        let vertresolution = FixedPointU16::new_raw(reader.read_u32::<BigEndian>()?);
        reader.read_u32::<BigEndian>()?; // reserved
        let frame_count = reader.read_u16::<BigEndian>()?;
        skip_bytes(reader, 32)?; // compressorname
        let depth = reader.read_u16::<BigEndian>()?;
        reader.read_i16::<BigEndian>()?; // pre-defined

        let end = start + size;
        loop {
            let current = reader.stream_position()?;
            if current >= end {
                return Err(Error::InvalidData("avcc not found"));
            }
            let header = BoxHeader::read(reader)?;
            let BoxHeader { name, size: s } = header;
            if s > size {
                return Err(Error::InvalidData(
                    "avc1 box contains a box with a larger size than it",
                ));
            }
            if name == BoxType::AvcCBox {
                let avcc = RawBox::<AvcCBox>::read_box(reader, s)?;

                skip_bytes_to(reader, start + size)?;

                return Ok(Self {
                    data_reference_index,
                    width,
                    height,
                    horizresolution,
                    vertresolution,
                    frame_count,
                    depth,
                    avcc,
                });
            } else {
                skip_bytes_to(reader, current + s)?;
            }
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Default, Serialize)]
pub struct AvcCBox {
    pub configuration_version: u8,
    pub avc_profile_indication: u8,
    pub profile_compatibility: u8,
    pub avc_level_indication: u8,
    pub length_size_minus_one: u8,
    pub sequence_parameter_sets: Vec<NalUnit>,
    pub picture_parameter_sets: Vec<NalUnit>,
    pub ext: Vec<u8>,
}

impl AvcCBox {
    pub fn new(sps: &[u8], pps: &[u8]) -> Self {
        Self {
            configuration_version: 1,
            avc_profile_indication: sps[1],
            profile_compatibility: sps[2],
            avc_level_indication: sps[3],
            length_size_minus_one: 0xff, // length_size = 4
            sequence_parameter_sets: vec![NalUnit::from(sps)],
            picture_parameter_sets: vec![NalUnit::from(pps)],
            ext: Vec::new(),
        }
    }
}

impl Mp4Box for AvcCBox {
    fn box_type(&self) -> BoxType {
        BoxType::AvcCBox
    }

    fn box_size(&self) -> u64 {
        let mut size = HEADER_SIZE + 7;
        for sps in &self.sequence_parameter_sets {
            size += sps.size() as u64;
        }
        for pps in &self.picture_parameter_sets {
            size += pps.size() as u64;
        }
        size
    }

    fn to_json(&self) -> Result<String> {
        Ok(serde_json::to_string(&self).expect("Failed to convert to JSON"))
    }

    fn summary(&self) -> Result<String> {
        let s = format!("avc_profile_indication={}", self.avc_profile_indication);
        Ok(s)
    }
}

impl<R: Read + Seek> ReadBox<&mut R> for AvcCBox {
    fn read_box(reader: &mut R, size: u64) -> Result<Self> {
        let start = box_start(reader)?;
        let content_start = reader.stream_position()?;

        let configuration_version = reader.read_u8()?;
        let avc_profile_indication = reader.read_u8()?;
        let profile_compatibility = reader.read_u8()?;
        let avc_level_indication = reader.read_u8()?;
        let length_size_minus_one = reader.read_u8()? & 0x3;
        let num_of_spss = reader.read_u8()? & 0x1F;
        let mut sequence_parameter_sets = Vec::with_capacity(num_of_spss as usize);
        for _ in 0..num_of_spss {
            let nal_unit = NalUnit::read(reader)?;
            sequence_parameter_sets.push(nal_unit);
        }
        let num_of_ppss = reader.read_u8()?;
        let mut picture_parameter_sets = Vec::with_capacity(num_of_ppss as usize);
        for _ in 0..num_of_ppss {
            let nal_unit = NalUnit::read(reader)?;
            picture_parameter_sets.push(nal_unit);
        }

        let content_end = reader.stream_position()?;
        let remainder = size - HEADER_SIZE - (content_end - content_start);
        let mut ext = vec![0u8; remainder as usize];
        reader.read_exact(&mut ext)?;

        skip_bytes_to(reader, start + size)?;

        Ok(Self {
            configuration_version,
            avc_profile_indication,
            profile_compatibility,
            avc_level_indication,
            length_size_minus_one,
            sequence_parameter_sets,
            picture_parameter_sets,
            ext,
        })
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Default, Serialize)]
pub struct NalUnit {
    pub bytes: Vec<u8>,
}

impl From<&[u8]> for NalUnit {
    fn from(bytes: &[u8]) -> Self {
        Self {
            bytes: bytes.to_vec(),
        }
    }
}

impl NalUnit {
    fn size(&self) -> usize {
        2 + self.bytes.len()
    }

    fn read<R: Read + Seek>(reader: &mut R) -> Result<Self> {
        let length = reader.read_u16::<BigEndian>()? as usize;
        let mut bytes = vec![0u8; length];
        reader.read_exact(&mut bytes)?;
        Ok(Self { bytes })
    }
}
