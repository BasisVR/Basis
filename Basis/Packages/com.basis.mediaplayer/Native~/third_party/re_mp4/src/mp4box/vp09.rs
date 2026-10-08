use crate::mp4box::vpcc::VpccBox;
use crate::mp4box::{
    box_start, read_box_header_ext, skip_bytes_to, BigEndian, BoxHeader, BoxType, Error, RawBox,
    Read, ReadBox, ReadBytesExt as _, Result, Seek,
};
use crate::Mp4Box;
use serde::Serialize;

/// Note: `Vp08Box` is identical to `Vp09Box`
#[derive(Debug, Clone, PartialEq, Eq, Default, Serialize)]
pub struct Vp09Box {
    pub version: u8,
    pub flags: u32,
    pub start_code: u16,
    pub data_reference_index: u16,
    pub reserved0: [u8; 16],
    pub width: u16,
    pub height: u16,
    pub horizresolution: (u16, u16),
    pub vertresolution: (u16, u16),
    pub reserved1: [u8; 4],
    pub frame_count: u16,
    pub compressorname: [u8; 32],
    pub depth: u16, // This is usually 24, even for HDR with bit_depth=10
    pub end_code: u16,
    pub vpcc: RawBox<VpccBox>,
}

impl Mp4Box for Vp09Box {
    fn box_type(&self) -> BoxType {
        BoxType::Vp09Box
    }

    fn box_size(&self) -> u64 {
        0x6A
    }

    fn to_json(&self) -> Result<String> {
        Ok(serde_json::to_string(&self).expect("Failed to convert to JSON"))
    }

    fn summary(&self) -> Result<String> {
        Ok(format!("{self:?}"))
    }
}

impl<R: Read + Seek> ReadBox<&mut R> for Vp09Box {
    fn read_box(reader: &mut R, size: u64) -> Result<Self> {
        let start = box_start(reader)?;
        let (version, flags) = read_box_header_ext(reader)?;

        let start_code: u16 = reader.read_u16::<BigEndian>()?;
        let data_reference_index: u16 = reader.read_u16::<BigEndian>()?;
        let reserved0: [u8; 16] = {
            let mut buf = [0u8; 16];
            reader.read_exact(&mut buf)?;
            buf
        };
        let width: u16 = reader.read_u16::<BigEndian>()?;
        let height: u16 = reader.read_u16::<BigEndian>()?;
        let horizresolution: (u16, u16) = (
            reader.read_u16::<BigEndian>()?,
            reader.read_u16::<BigEndian>()?,
        );
        let vertresolution: (u16, u16) = (
            reader.read_u16::<BigEndian>()?,
            reader.read_u16::<BigEndian>()?,
        );
        let reserved1: [u8; 4] = {
            let mut buf = [0u8; 4];
            reader.read_exact(&mut buf)?;
            buf
        };
        let frame_count: u16 = reader.read_u16::<BigEndian>()?;
        let compressorname: [u8; 32] = {
            let mut buf = [0u8; 32];
            reader.read_exact(&mut buf)?;
            buf
        };
        let depth: u16 = reader.read_u16::<BigEndian>()?;
        let end_code: u16 = reader.read_u16::<BigEndian>()?;

        let vpcc = {
            let header = BoxHeader::read(reader)?;
            if header.size > size {
                return Err(Error::InvalidData(
                    "vp09 box contains a box with a larger size than it",
                ));
            }
            RawBox::<VpccBox>::read_box(reader, header.size)?
        };

        skip_bytes_to(reader, start + size)?;

        Ok(Self {
            version,
            flags,
            start_code,
            data_reference_index,
            reserved0,
            width,
            height,
            horizresolution,
            vertresolution,
            reserved1,
            frame_count,
            compressorname,
            depth,
            end_code,
            vpcc,
        })
    }
}
