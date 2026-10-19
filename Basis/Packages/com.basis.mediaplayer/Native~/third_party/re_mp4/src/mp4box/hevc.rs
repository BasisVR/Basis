use byteorder::{BigEndian, ReadBytesExt as _};
use serde::Serialize;
use std::io::{Read, Seek};

use crate::mp4box::{
    box_start, skip_bytes, skip_bytes_to, value_u32, BoxHeader, BoxType, Error, FixedPointU16,
    Mp4Box, RawBox, ReadBox, Result, HEADER_SIZE,
};

/// HEVC/H.265 box found for both `avc1` and `hvc1`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct HevcBox {
    pub data_reference_index: u16,
    pub width: u16,
    pub height: u16,

    #[serde(with = "value_u32")]
    pub horizresolution: FixedPointU16,

    #[serde(with = "value_u32")]
    pub vertresolution: FixedPointU16,
    pub frame_count: u16,
    pub depth: u16, // This is usually 24, even for HDR with bit_depth=10
    pub hvcc: RawBox<HevcDecoderConfigurationRecord>,
}

impl Default for HevcBox {
    fn default() -> Self {
        Self {
            data_reference_index: 0,
            width: 0,
            height: 0,
            horizresolution: FixedPointU16::new(0x48),
            vertresolution: FixedPointU16::new(0x48),
            frame_count: 1,
            depth: 0x0018,
            hvcc: RawBox::default(),
        }
    }
}

impl HevcBox {
    pub fn get_type() -> BoxType {
        BoxType::Hvc1Box
    }

    pub fn get_size(&self) -> u64 {
        HEADER_SIZE + 8 + 70 + self.hvcc.box_size()
    }
}

impl Mp4Box for HevcBox {
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

impl<R: Read + Seek> ReadBox<&mut R> for HevcBox {
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

        let header = BoxHeader::read(reader)?;
        let BoxHeader { name, size: s } = header;
        if s > size {
            return Err(Error::InvalidData(
                "hvc1 box contains a box with a larger size than it",
            ));
        }
        if name == BoxType::HvcCBox {
            let hvcc = RawBox::<HevcDecoderConfigurationRecord>::read_box(reader, s)?;

            skip_bytes_to(reader, start + size)?;

            Ok(Self {
                data_reference_index,
                width,
                height,
                horizresolution,
                vertresolution,
                frame_count,
                depth,
                hvcc,
            })
        } else {
            Err(Error::InvalidData("hvcc not found"))
        }
    }
}

// Naming this box is a bit of a mess. It sometimes gets referred to as
// Hvcc box but in fact it is shared by 'hvc1' and 'hev1'.
// Going with what ffmpeg does here and be more explicit.
// https://ffmpeg.org/doxygen/6.0/structHEVCDecoderConfigurationRecord.html
#[derive(Default, Debug, Clone, PartialEq, Eq, Serialize)]
pub struct HevcDecoderConfigurationRecord {
    pub configuration_version: u8,
    pub general_profile_space: u8,
    pub general_tier_flag: bool,
    pub general_profile_idc: u8,
    pub general_profile_compatibility_flags: u32,
    pub general_constraint_indicator_flag: u64,
    pub general_level_idc: u8,
    pub min_spatial_segmentation_idc: u16,
    pub parallelism_type: u8,
    pub chroma_format_idc: u8,
    pub bit_depth_luma_minus8: u8,
    pub bit_depth_chroma_minus8: u8,
    pub avg_frame_rate: u16,
    pub constant_frame_rate: u8,
    pub num_temporal_layers: u8,
    pub temporal_id_nested: bool,
    pub length_size_minus_one: u8,
    pub arrays: Vec<HvcCArray>,
}

impl HevcDecoderConfigurationRecord {
    pub fn new() -> Self {
        Self {
            configuration_version: 1,
            ..Default::default()
        }
    }
}

impl Mp4Box for HevcDecoderConfigurationRecord {
    fn box_type(&self) -> BoxType {
        BoxType::HvcCBox
    }

    fn box_size(&self) -> u64 {
        HEADER_SIZE
            + 23
            + self
                .arrays
                .iter()
                .map(|a| 3 + a.nalus.iter().map(|x| 2 + x.data.len() as u64).sum::<u64>())
                .sum::<u64>()
    }

    fn to_json(&self) -> Result<String> {
        Ok(serde_json::to_string(&self).expect("Failed to convert to JSON"))
    }

    fn summary(&self) -> Result<String> {
        Ok(format!("configuration_version={} general_profile_space={} general_tier_flag={} general_profile_idc={} general_profile_compatibility_flags={} general_constraint_indicator_flag={} general_level_idc={} min_spatial_segmentation_idc={} parallelism_type={} chroma_format_idc={} bit_depth_luma_minus8={} bit_depth_chroma_minus8={} avg_frame_rate={} constant_frame_rate={} num_temporal_layers={} temporal_id_nested={} length_size_minus_one={}",
            self.configuration_version,
            self.general_profile_space,
            self.general_tier_flag,
            self.general_profile_idc,
            self.general_profile_compatibility_flags,
            self.general_constraint_indicator_flag,
            self.general_level_idc,
            self.min_spatial_segmentation_idc,
            self.parallelism_type,
            self.chroma_format_idc,
            self.bit_depth_luma_minus8,
            self.bit_depth_chroma_minus8,
            self.avg_frame_rate,
            self.constant_frame_rate,
            self.num_temporal_layers,
            self.temporal_id_nested,
            self.length_size_minus_one
        ))
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Default, Serialize)]
pub struct HvcCArrayNalu {
    pub size: u16,
    pub data: Vec<u8>,
}

#[derive(Debug, Clone, PartialEq, Eq, Default, Serialize)]
pub struct HvcCArray {
    pub completeness: bool,
    pub nal_unit_type: u8,
    pub nalus: Vec<HvcCArrayNalu>,
}

impl<R: Read + Seek> ReadBox<&mut R> for HevcDecoderConfigurationRecord {
    fn read_box(reader: &mut R, _size: u64) -> Result<Self> {
        let configuration_version = reader.read_u8()?;
        let params = reader.read_u8()?;
        let general_profile_space = params >> 6;
        let general_tier_flag = ((params & 0b00100000) >> 5) > 0;
        let general_profile_idc = params & 0b00011111;

        let general_profile_compatibility_flags = reader.read_u32::<BigEndian>()?;
        let general_constraint_indicator_flag = reader.read_u48::<BigEndian>()?;
        let general_level_idc = reader.read_u8()?;
        let min_spatial_segmentation_idc = reader.read_u16::<BigEndian>()? & 0x0FFF;
        let parallelism_type = reader.read_u8()? & 0b11;
        let chroma_format_idc = reader.read_u8()? & 0b11;
        let bit_depth_luma_minus8 = reader.read_u8()? & 0b111;
        let bit_depth_chroma_minus8 = reader.read_u8()? & 0b111;
        let avg_frame_rate = reader.read_u16::<BigEndian>()?;

        let params = reader.read_u8()?;
        let constant_frame_rate = (params & 0b11000000) >> 6;
        let num_temporal_layers = (params & 0b00111000) >> 3;
        let temporal_id_nested = (params & 0b00000100) > 0;
        let length_size_minus_one = params & 0b00000011;

        let num_of_arrays = reader.read_u8()?;

        let mut arrays = Vec::with_capacity(num_of_arrays as _);
        for _ in 0..num_of_arrays {
            let params = reader.read_u8()?;
            let num_nalus = reader.read_u16::<BigEndian>()?;
            let mut nalus = Vec::with_capacity(num_nalus as usize);

            for _ in 0..num_nalus {
                let size = reader.read_u16::<BigEndian>()?;
                let mut data = vec![0; size as usize];

                reader.read_exact(&mut data)?;

                nalus.push(HvcCArrayNalu { size, data });
            }

            arrays.push(HvcCArray {
                completeness: (params & 0b10000000) > 0,
                nal_unit_type: params & 0b111111,
                nalus,
            });
        }

        Ok(Self {
            configuration_version,
            general_profile_space,
            general_tier_flag,
            general_profile_idc,
            general_profile_compatibility_flags,
            general_constraint_indicator_flag,
            general_level_idc,
            min_spatial_segmentation_idc,
            parallelism_type,
            chroma_format_idc,
            bit_depth_luma_minus8,
            bit_depth_chroma_minus8,
            avg_frame_rate,
            constant_frame_rate,
            num_temporal_layers,
            temporal_id_nested,
            length_size_minus_one,
            arrays,
        })
    }
}
