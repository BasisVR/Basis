use std::ffi::CStr;
use std::io::{Read, Seek};

use byteorder::{BigEndian, ReadBytesExt as _};
use serde::Serialize;

use crate::mp4box::{
    box_start, read_box_header_ext, skip_bytes_to, BoxType, Error, Mp4Box, ReadBox, Result,
    HEADER_EXT_SIZE, HEADER_SIZE,
};

#[derive(Debug, Clone, PartialEq, Eq, Default, Serialize)]
pub struct EmsgBox {
    pub version: u8,
    pub flags: u32,
    pub timescale: u32,
    pub presentation_time: Option<u64>,
    pub presentation_time_delta: Option<u32>,
    pub event_duration: u32,
    pub id: u32,
    pub scheme_id_uri: String,
    pub value: String,
    pub message_data: Vec<u8>,
}

impl EmsgBox {
    fn size_without_message(version: u8, scheme_id_uri: &str, value: &str) -> u64 {
        HEADER_SIZE + HEADER_EXT_SIZE +
            4 + // id
            Self::time_size(version) +
            (scheme_id_uri.len() + 1) as u64 +
            (value.len() as u64 + 1)
    }

    fn time_size(version: u8) -> u64 {
        match version {
            0 => 12,
            1 => 16,
            _ => panic!("version must be 0 or 1"),
        }
    }
}

impl Mp4Box for EmsgBox {
    fn box_type(&self) -> BoxType {
        BoxType::EmsgBox
    }

    fn box_size(&self) -> u64 {
        Self::size_without_message(self.version, &self.scheme_id_uri, &self.value)
            + self.message_data.len() as u64
    }

    fn to_json(&self) -> Result<String> {
        Ok(serde_json::to_string(&self).expect("Failed to convert to JSON"))
    }

    fn summary(&self) -> Result<String> {
        let s = format!("id={} value={}", self.id, self.value);
        Ok(s)
    }
}

impl<R: Read + Seek> ReadBox<&mut R> for EmsgBox {
    fn read_box(reader: &mut R, size: u64) -> Result<Self> {
        let start = box_start(reader)?;
        let (version, flags) = read_box_header_ext(reader)?;

        let (
            timescale,
            presentation_time,
            presentation_time_delta,
            event_duration,
            id,
            scheme_id_uri,
            value,
        ) = match version {
            0 => {
                let scheme_id_uri = read_null_terminated_utf8_string(reader)?;
                let value = read_null_terminated_utf8_string(reader)?;
                (
                    reader.read_u32::<BigEndian>()?,
                    None,
                    Some(reader.read_u32::<BigEndian>()?),
                    reader.read_u32::<BigEndian>()?,
                    reader.read_u32::<BigEndian>()?,
                    scheme_id_uri,
                    value,
                )
            }
            1 => (
                reader.read_u32::<BigEndian>()?,
                Some(reader.read_u64::<BigEndian>()?),
                None,
                reader.read_u32::<BigEndian>()?,
                reader.read_u32::<BigEndian>()?,
                read_null_terminated_utf8_string(reader)?,
                read_null_terminated_utf8_string(reader)?,
            ),
            _ => return Err(Error::InvalidData("version must be 0 or 1")),
        };

        let message_size = size - Self::size_without_message(version, &scheme_id_uri, &value);
        let mut message_data = Vec::with_capacity(message_size as usize);
        for _ in 0..message_size {
            message_data.push(reader.read_u8()?);
        }

        skip_bytes_to(reader, start + size)?;

        Ok(Self {
            version,
            flags,
            timescale,
            presentation_time,
            presentation_time_delta,
            event_duration,
            id,
            scheme_id_uri,
            value,
            message_data,
        })
    }
}

fn read_null_terminated_utf8_string<R: Read + Seek>(reader: &mut R) -> Result<String> {
    let mut bytes = Vec::new();
    loop {
        let byte = reader.read_u8()?;
        bytes.push(byte);
        if byte == 0 {
            break;
        }
    }
    #[expect(unsafe_code)]
    // SAFETY: we ensure there is exactly one nul-byte at the end of the slice
    if let Ok(str) = unsafe { CStr::from_bytes_with_nul_unchecked(&bytes) }.to_str() {
        Ok(str.to_owned())
    } else {
        Err(Error::InvalidData("invalid utf8"))
    }
}
