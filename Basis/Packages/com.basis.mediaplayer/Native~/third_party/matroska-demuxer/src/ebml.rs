//! Implement the parsing of EBML coded files.

use std::{
    convert::{TryFrom, TryInto},
    io::{Read, Seek, SeekFrom},
    num::NonZeroU64,
};

use crate::element_id::{element_id_to_type, id_to_element_id};
use crate::{
    element_id::{ElementId, ElementType},
    DemuxError, Result,
};

/// The most a string or binary element may hold. Strings are names, codec
/// ids and languages, binary elements codec setup data, and none comes near
/// it. The stream-length check alone does not bound the allocation: the
/// length is whatever the reader reports, which over a network is the
/// server's word.
pub(crate) const MAX_ELEMENT_SIZE: u64 = 16 * 1024 * 1024;

/// Refuses a declared size above `limit` before anything is allocated for it.
pub(crate) fn check_size_limit(declared: u64, limit: u64) -> Result<()> {
    if declared > limit {
        return Err(DemuxError::ElementSizeExceedsLimit { declared, limit });
    }
    Ok(())
}

/// The data an element can contain.
#[derive(Clone, Debug, PartialEq)]
pub enum ElementData {
    /// Returns the offset and size of the data.
    Location { offset: u64, size: u64 },
    /// Unsigned integer.
    Unsigned(u64),
    /// Signed integer.
    Signed(i64),
    /// Float.
    Float(f64),
    /// Date.
    Date(i64),
    /// String.
    String(String),
}

pub(crate) trait ParsableElement<R: Read + Seek> {
    type Output;

    fn new(r: &mut R, fields: &[(ElementId, ElementData)]) -> Result<Self::Output>;
}

/// Tries to parse an element with the given Element ID that returns a master element at the current location of the reader. Leaves the reader at the first byte after the master entry.
pub(crate) fn expect_master<R: Read + Seek>(
    r: &mut R,
    expected_id: ElementId,
    from: Option<u64>,
) -> Result<(u64, u64)> {
    let (element_id, size) = parse_element_header(r, from)?;

    if element_id != expected_id {
        return Err(DemuxError::UnexpectedElement((expected_id, element_id)));
    }

    let offset = r.stream_position()?;
    Ok((offset, size))
}

/// Collects the children of a master element.
pub(crate) fn collect_children<R: Read + Seek>(
    r: &mut R,
    offset: u64,
    size: u64,
) -> Result<Vec<(ElementId, ElementData)>> {
    let mut children = Vec::with_capacity(16);
    r.seek(SeekFrom::Start(offset))?;
    let available = remaining_len(r)?;

    // An unknown size means that the master element extends to the end of the stream.
    let end = if size == u64::MAX {
        offset.saturating_add(available)
    } else {
        if size > available {
            return Err(DemuxError::ElementSizeExceedsStream {
                declared: size,
                available,
            });
        }
        offset.saturating_add(size)
    };

    while r.stream_position()? < end {
        let (element_id, element_data) = next_element(r)?;

        if let ElementData::Location { offset, size } = element_data {
            if size == u64::MAX {
                break;
            }
            r.seek(SeekFrom::Start(offset.saturating_add(size)))?;
        }

        if element_id != ElementId::Unknown {
            children.push((element_id, element_data))
        }
    }
    Ok(children)
}

/// Tries to parses children of the same kind for the given master element.
pub(crate) fn try_parse_children<R, T>(
    r: &mut R,
    fields: &[(ElementId, ElementData)],
    parent_id: ElementId,
    child_id: ElementId,
) -> Result<Option<Vec<T::Output>>>
where
    R: Read + Seek,
    T: ParsableElement<R>,
{
    let children = if let Some((_, ElementData::Location { offset, size })) =
        fields.iter().find(|(id, _)| *id == parent_id)
    {
        let content_encodings = parse_children_inner::<_, T>(r, *offset, *size, child_id)?;
        Some(content_encodings)
    } else {
        None
    };
    Ok(children)
}

/// Parses children of the same kind for the given master element at the given offset.
pub(crate) fn parse_children_at_offset<R, T>(
    r: &mut R,
    offset: u64,
    master_id: ElementId,
    child_id: ElementId,
) -> Result<Vec<T::Output>>
where
    R: Read + Seek,
    T: ParsableElement<R>,
{
    let (data_offset, data_size) = expect_master(r, master_id, Some(offset))?;
    let children = parse_children_inner::<_, T>(r, data_offset, data_size, child_id)?;
    Ok(children)
}

fn parse_children_inner<R, T>(
    r: &mut R,
    offset: u64,
    size: u64,
    child_id: ElementId,
) -> Result<Vec<T::Output>>
where
    R: Read + Seek,
    T: ParsableElement<R>,
{
    let mut children = vec![];
    let master_fields = collect_children(r, offset, size)?;
    for (_, element_data) in master_fields.iter().filter(|(id, _)| *id == child_id) {
        if let ElementData::Location { offset, size } = element_data {
            let child_fields = collect_children(r, *offset, *size)?;
            let track_entry = T::new(r, &child_fields)?;
            children.push(track_entry)
        }
    }
    Ok(children)
}

/// Expects to find the child with the given Element ID from the given fields and reader.
pub(crate) fn parse_child<R, T>(
    r: &mut R,
    fields: &[(ElementId, ElementData)],
    element_id: ElementId,
) -> Result<T::Output>
where
    R: Read + Seek,
    T: ParsableElement<R>,
{
    let child = try_parse_child::<_, T>(r, fields, element_id)?
        .ok_or(DemuxError::ElementNotFound(element_id))?;
    Ok(child)
}

/// Tries to parse the child with the given Element ID from the given fields and reader.
pub(crate) fn try_parse_child<R, T>(
    r: &mut R,
    fields: &[(ElementId, ElementData)],
    element_id: ElementId,
) -> Result<Option<T::Output>>
where
    R: Read + Seek,
    T: ParsableElement<R>,
{
    let child = if let Some((_, element_data)) = fields.iter().find(|(id, _)| *id == element_id) {
        if let ElementData::Location { offset, size } = element_data {
            let child_fields = collect_children(r, *offset, *size)?;
            let child = T::new(r, &child_fields)?;
            Some(child)
        } else {
            return Err(DemuxError::UnexpectedDataType);
        }
    } else {
        None
    };
    Ok(child)
}

/// Expects to find element with the an Element ID for an unsigned integer inside a list of children.
pub(crate) fn find_unsigned(
    fields: &[(ElementId, ElementData)],
    element_id: ElementId,
) -> Result<u64> {
    let value =
        try_find_unsigned(fields, element_id)?.ok_or(DemuxError::ElementNotFound(element_id))?;
    Ok(value)
}

/// Expects to find element with the an Element ID for an unsigned integer inside a list of children, otherwise sets the default value.
pub(crate) fn find_unsigned_or(
    fields: &[(ElementId, ElementData)],
    element_id: ElementId,
    default: u64,
) -> Result<u64> {
    let value = try_find_unsigned(fields, element_id)?.unwrap_or(default);
    Ok(value)
}

/// Tries to find an element with the Element ID for an unsigned integer inside a list of children.
pub(crate) fn try_find_unsigned(
    fields: &[(ElementId, ElementData)],
    element_id: ElementId,
) -> Result<Option<u64>> {
    if let Some((_, data)) = fields.iter().find(|(id, _)| *id == element_id) {
        if let ElementData::Unsigned(value) = data {
            Ok(Some(*value))
        } else {
            Err(DemuxError::UnexpectedDataType)
        }
    } else {
        Ok(None)
    }
}

/// Tries to find an element with the Element ID for a custom type inside a list of children, otherwise sets the default value.
pub(crate) fn try_find_custom_type_or<T: From<u64>>(
    fields: &[(ElementId, ElementData)],
    element_id: ElementId,
    default: T,
) -> Result<T> {
    let value = match try_find_unsigned(fields, element_id)? {
        None => default,
        Some(value) => {
            let value: T = value.into();
            value
        }
    };
    Ok(value)
}

/// Tries to find an element with the Element ID for a custom type inside a list of children.
pub(crate) fn try_find_custom_type<T: From<u64>>(
    fields: &[(ElementId, ElementData)],
    element_id: ElementId,
) -> Result<Option<T>> {
    let value = match try_find_unsigned(fields, element_id)? {
        None => None,
        Some(value) => {
            let value = value.into();
            Some(value)
        }
    };
    Ok(value)
}

/// Expects to find element with the an Element ID for a custom type inside a list of children and converts it into a custom type.
pub(crate) fn find_custom_type<T: From<u64>>(
    fields: &[(ElementId, ElementData)],
    element_id: ElementId,
) -> Result<T> {
    let value =
        try_find_unsigned(fields, element_id)?.ok_or(DemuxError::ElementNotFound(element_id))?;
    Ok(value.into())
}

/// Tries to find an element with the Element ID for a boolean inside a list of children.
pub(crate) fn try_find_bool(
    fields: &[(ElementId, ElementData)],
    element_id: ElementId,
) -> Result<Option<bool>> {
    let value = try_find_unsigned(fields, element_id)?;
    match value {
        None => Ok(None),
        Some(value) => match value {
            0 => Ok(Some(false)),
            _ => Ok(Some(true)),
        },
    }
}

/// Tries to find an element with the Element ID for a boolean inside a list of children, otherwise sets the default value.
pub(crate) fn find_bool_or(
    fields: &[(ElementId, ElementData)],
    element_id: ElementId,
    default: bool,
) -> Result<bool> {
    let value = try_find_unsigned(fields, element_id)?;
    match value {
        None => Ok(default),
        Some(value) => match value {
            0 => Ok(false),
            _ => Ok(true),
        },
    }
}

/// Expects to find an element with the Element ID for a non zero unsigned integer inside a list of children.
pub(crate) fn find_nonzero(
    fields: &[(ElementId, ElementData)],
    element_id: ElementId,
) -> Result<NonZeroU64> {
    let value =
        try_find_unsigned(fields, element_id)?.ok_or(DemuxError::ElementNotFound(element_id))?;
    NonZeroU64::new(value).ok_or(DemuxError::NonZeroValueIsZero(element_id))
}

/// Tries to find an element with the Element ID for an non zero unsigned integer inside a list of children, otherwise sets the default value.
pub(crate) fn find_nonzero_or(
    fields: &[(ElementId, ElementData)],
    element_id: ElementId,
    default: u64,
) -> Result<NonZeroU64> {
    let value = try_find_unsigned(fields, element_id)?.unwrap_or(default);
    NonZeroU64::new(value).ok_or(DemuxError::NonZeroValueIsZero(element_id))
}

/// Tries to find an element with the Element ID for an non zero unsigned integer inside a list of children.
pub(crate) fn try_find_nonzero(
    fields: &[(ElementId, ElementData)],
    element_id: ElementId,
) -> Result<Option<NonZeroU64>> {
    match try_find_unsigned(fields, element_id)? {
        None => Ok(None),
        Some(value) => Ok(Some(
            NonZeroU64::new(value).ok_or(DemuxError::NonZeroValueIsZero(element_id))?,
        )),
    }
}

/// Expects to find an element with the Element ID for a float inside a list of children, otherwise sets the default value.
pub(crate) fn find_float_or(
    fields: &[(ElementId, ElementData)],
    element_id: ElementId,
    default: f64,
) -> Result<f64> {
    let value = try_find_float(fields, element_id)?.unwrap_or(default);
    Ok(value)
}

/// Tries to find an element with the Element ID for a float inside a list of children.
pub(crate) fn try_find_float(
    fields: &[(ElementId, ElementData)],
    element_id: ElementId,
) -> Result<Option<f64>> {
    if let Some((_, data)) = fields.iter().find(|(id, _)| *id == element_id) {
        if let ElementData::Float(value) = data {
            Ok(Some(*value))
        } else {
            Err(DemuxError::UnexpectedDataType)
        }
    } else {
        Ok(None)
    }
}

/// Expects to find an element with the Element ID for a string inside a list of children.
pub(crate) fn find_string(
    fields: &[(ElementId, ElementData)],
    element_id: ElementId,
) -> Result<String> {
    let value =
        try_find_string(fields, element_id)?.ok_or(DemuxError::ElementNotFound(element_id))?;
    Ok(value)
}

/// Tries to find an element with the Element ID for a string inside a list of children.
pub(crate) fn try_find_string(
    fields: &[(ElementId, ElementData)],
    element_id: ElementId,
) -> Result<Option<String>> {
    if let Some((_, data)) = fields.iter().find(|(id, _)| *id == element_id) {
        if let ElementData::String(value) = data {
            Ok(Some(value.clone()))
        } else {
            Err(DemuxError::UnexpectedDataType)
        }
    } else {
        Ok(None)
    }
}

/// Tries to find an element with the Element ID for binary inside a list of children.
pub(crate) fn try_find_binary<R: Read + Seek>(
    r: &mut R,
    fields: &[(ElementId, ElementData)],
    element_id: ElementId,
) -> Result<Option<Vec<u8>>> {
    if let Some((_, data)) = fields.iter().find(|(id, _)| *id == element_id) {
        if let ElementData::Location { offset, size } = data {
            check_size_limit(*size, MAX_ELEMENT_SIZE)?;
            r.seek(SeekFrom::Start(*offset))?;
            let available = remaining_len(r)?;
            if *size > available {
                return Err(DemuxError::ElementSizeExceedsStream {
                    declared: *size,
                    available,
                });
            }
            let size = usize::try_from(*size)?;
            let mut data = vec![0_u8; size];
            r.read_exact(&mut data)?;
            Ok(Some(data))
        } else {
            Err(DemuxError::UnexpectedDataType)
        }
    } else {
        Ok(None)
    }
}

/// Tries to find an element with the Element ID for a date inside a list of children.
pub(crate) fn try_find_date(
    fields: &[(ElementId, ElementData)],
    element_id: ElementId,
) -> Result<Option<i64>> {
    if let Some((_, data)) = fields.iter().find(|(id, _)| *id == element_id) {
        if let ElementData::Date(value) = data {
            Ok(Some(*value))
        } else {
            Err(DemuxError::UnexpectedDataType)
        }
    } else {
        Ok(None)
    }
}

/// Parses the next Element at the current location of the reader and returns it's data.
pub(crate) fn next_element<R: Read + Seek>(r: &mut R) -> Result<(ElementId, ElementData)> {
    let (element_id, size) = parse_element_header(r, None)?;

    let element_data = match element_id_to_type(element_id) {
        ElementType::Master | ElementType::Binary | ElementType::Unknown => {
            let (offset, size) = parse_location(r, size)?;
            ElementData::Location { offset, size }
        }
        ElementType::Unsigned => {
            let value = parse_unsigned(r, size)?;
            ElementData::Unsigned(value)
        }
        ElementType::Signed => {
            let value = parse_signed(r, size)?;
            ElementData::Signed(value)
        }
        ElementType::Float => {
            let value = parse_float(r, size)?;
            ElementData::Float(value)
        }
        ElementType::Date => {
            let value = parse_date(r, size)?;
            ElementData::Date(value)
        }
        ElementType::String => {
            let value = parse_string(r, size)?;
            ElementData::String(value)
        }
    };

    Ok((element_id, element_data))
}

/// Parses the next element from the given location inside the reader. Returns the Element ID and the size of the data.
pub(crate) fn parse_element_header<R: Read + Seek>(
    r: &mut R,
    from: Option<u64>,
) -> Result<(ElementId, u64)> {
    if let Some(from) = from {
        r.seek(SeekFrom::Start(from))?;
    }

    let id = parse_variable_u32(r)?;
    let element_id = id_to_element_id(id);

    let size = parse_variable_u64(r)?;
    Ok((element_id, size))
}

/// Parses a variable length EBML u32 (as used for the Element ID).
fn parse_variable_u32<R: Read>(r: &mut R) -> Result<u32> {
    loop {
        let mut bytes = [0u8];
        r.read_exact(&mut bytes)?;
        let element_id = match bytes[0] {
            // We keep reading bytes until we find a valid variable.
            byte if (byte & 0xF0) == 0x00 => continue,
            byte if (byte & 0x80) == 0x80 => byte.into(),
            byte if (byte & 0xC0) == 0x40 => parse_variable_u32_data(r, byte, 1)?,
            byte if (byte & 0xE0) == 0x20 => parse_variable_u32_data(r, byte, 2)?,
            byte if (byte & 0xF0) == 0x10 => parse_variable_u32_data(r, byte, 3)?,
            _ => return Err(DemuxError::InvalidEbmlElementId),
        };
        return Ok(element_id);
    }
}

/// Parses a variable length EBML i64 as found in EBML laving frame sizes (as used in EBML frame lacing).
pub(crate) fn parse_variable_i64<R: Read>(r: &mut R) -> Result<i64> {
    loop {
        let mut bytes = [0u8];
        r.read_exact(&mut bytes)?;
        let element_id = match bytes[0] {
            byte if (byte & 0xF0) == 0x00 => continue,
            byte if (byte & 0x80) == 0x80 => i64::from(0x7F & byte).saturating_sub(63),
            byte if (byte & 0xC0) == 0x40 => {
                i64::from(parse_variable_u32_data(r, 0x3F & byte, 1)?).saturating_sub(8191)
            }
            byte if (byte & 0xE0) == 0x20 => {
                i64::from(parse_variable_u32_data(r, 0x1F & byte, 2)?).saturating_sub(1048575)
            }
            byte if (byte & 0xF0) == 0x10 => {
                i64::from(parse_variable_u32_data(r, 0x0F & byte, 3)?).saturating_sub(134217727)
            }
            _ => return Err(DemuxError::InvalidEbmlElementId),
        };
        return Ok(element_id);
    }
}

/// Parses a variable length EBML u64 (as used for the data size).
pub(crate) fn parse_variable_u64<R: Read>(r: &mut R) -> Result<u64> {
    let mut bytes = [0u8];
    r.read_exact(&mut bytes)?;
    let size = match bytes[0] {
        0xFF => u64::MAX,
        byte if (byte & 0x80) == 0x80 => (0x7F & byte).into(),
        byte if (byte & 0xC0) == 0x40 => parse_variable_u64_data(r, 0x3F & byte, 1)?,
        byte if (byte & 0xE0) == 0x20 => parse_variable_u64_data(r, 0x1F & byte, 2)?,
        byte if (byte & 0xF0) == 0x10 => parse_variable_u64_data(r, 0x0F & byte, 3)?,
        byte if (byte & 0xF8) == 0x08 => parse_variable_u64_data(r, 0x07 & byte, 4)?,
        byte if (byte & 0xFC) == 0x04 => parse_variable_u64_data(r, 0x03 & byte, 5)?,
        byte if (byte & 0xFE) == 0x02 => parse_variable_u64_data(r, 0x01 & byte, 6)?,
        0x01 => parse_variable_u64_data(r, 0, 7)?,
        _ => return Err(DemuxError::InvalidEbmlDataSize),
    };
    Ok(size)
}

fn parse_variable_u32_data<R: Read>(r: &mut R, byte: u8, left: u8) -> Result<u32> {
    let shift: usize = (8 * (3 - left)).into();
    let mut bytes = [byte, 0, 0, 0];
    r.read_exact(&mut bytes[1..=left.into()])?;
    Ok(u32::from_be_bytes(bytes) >> shift)
}

fn parse_variable_u64_data<R: Read>(r: &mut R, byte: u8, left: u8) -> Result<u64> {
    let shift: usize = (8 * (7 - left)).into();
    let mut bytes = [byte, 0, 0, 0, 0, 0, 0, 0];
    r.read_exact(&mut bytes[1..=left.into()])?;
    Ok(u64::from_be_bytes(bytes) >> shift)
}

/// Returns the number of bytes remaining from the current position to the end of the stream.
pub(crate) fn remaining_len<R: Seek>(r: &mut R) -> Result<u64> {
    let pos = r.stream_position()?;
    let end = r.seek(SeekFrom::End(0))?;
    r.seek(SeekFrom::Start(pos))?;
    Ok(end.saturating_sub(pos))
}

fn parse_location<R: Read + Seek>(r: &mut R, size: u64) -> Result<(u64, u64)> {
    let offset = r.stream_position()?;
    // We skip the data and set the reader to the next element, if the size is known.
    if size != u64::MAX {
        r.seek(SeekFrom::Start(offset.saturating_add(size)))?;
    }
    Ok((offset, size))
}

#[allow(clippy::as_conversions)]
fn parse_unsigned<R: Read>(r: &mut R, size: u64) -> Result<u64> {
    if size == 0 {
        return Ok(0);
    }
    if size > 8 {
        return Err(DemuxError::WrongIntegerSize(size));
    }
    let shift = (8 * (8 - size)) as i64;
    let mut bytes = [0u8; 8];
    r.read_exact(&mut bytes[0..size as usize])?;
    Ok(u64::from_be_bytes(bytes) >> shift)
}

#[allow(clippy::as_conversions)]
fn parse_signed<R: Read>(r: &mut R, size: u64) -> Result<i64> {
    if size == 0 {
        return Ok(0);
    }
    if size > 8 {
        return Err(DemuxError::WrongIntegerSize(size));
    }
    let shift = (8 * (8 - size)) as i64;
    let mut bytes = [0u8; 8];
    r.read_exact(&mut bytes[0..size as usize])?;
    Ok(i64::from_be_bytes(bytes) >> shift)
}

fn parse_float<R: Read>(r: &mut R, size: u64) -> Result<f64> {
    match size {
        0 => Ok(0.0),
        4 => {
            let mut bytes = [0u8; 4];
            r.read_exact(&mut bytes)?;
            Ok(f32::from_be_bytes(bytes).into())
        }
        8 => {
            let mut bytes = [0u8; 8];
            r.read_exact(&mut bytes)?;
            Ok(f64::from_be_bytes(bytes))
        }
        _ => Err(DemuxError::WrongFloatSize(size)),
    }
}

#[allow(clippy::as_conversions)]
fn parse_date<R: Read>(r: &mut R, size: u64) -> Result<i64> {
    if size == 0 {
        return Ok(0);
    }
    if size > 8 {
        return Err(DemuxError::WrongDateSize(size));
    }
    let shift = (8 * (8 - size)) as i64;
    let mut bytes = [0u8; 8];
    r.read_exact(&mut bytes[0..size as usize])?;
    Ok(i64::from_be_bytes(bytes) >> shift)
}

fn parse_string<R: Read + Seek>(r: &mut R, size: u64) -> Result<String> {
    if size == 0 {
        return Ok(String::from(""));
    }
    check_size_limit(size, MAX_ELEMENT_SIZE)?;
    let available = remaining_len(r)?;
    if size > available {
        return Err(DemuxError::ElementSizeExceedsStream {
            declared: size,
            available,
        });
    }
    let size: usize = size.try_into()?;
    let mut bytes = vec![0u8; size];
    r.read_exact(&mut bytes[0..size])?;
    Ok(String::from_utf8(bytes)?)
}

#[cfg(test)]
mod tests {
    #![allow(clippy::panic)]

    use std::io::Cursor;

    use super::*;

    #[test]
    fn test_parse_master_element() -> Result<()> {
        let data: Vec<u8> = vec![0x1A, 0x45, 0xDF, 0xA3, 0xA2];
        let mut cursor = Cursor::new(data);
        let (element_id, element_data) = next_element(&mut cursor)?;
        assert_eq!(element_id, ElementId::Ebml);
        assert_eq!(
            element_data,
            ElementData::Location {
                offset: 5,
                size: 34,
            }
        );

        Ok(())
    }

    #[test]
    fn test_parse_unsigned() -> Result<()> {
        let data: Vec<u8> = vec![0x42, 0x86, 0x81, 0x01];
        let mut cursor = Cursor::new(data);
        let (element_id, element_data) = next_element(&mut cursor)?;
        assert_eq!(element_id, ElementId::EbmlVersion);
        assert_eq!(element_data, ElementData::Unsigned(1));

        Ok(())
    }

    #[test]
    fn test_parse_signed() -> Result<()> {
        let data: Vec<u8> = vec![0xFB, 0x82, 0xFF, 0xFB];
        let mut cursor = Cursor::new(data);
        let (element_id, element_data) = next_element(&mut cursor)?;
        assert_eq!(element_id, ElementId::ReferenceBlock);
        assert_eq!(element_data, ElementData::Signed(-5));

        Ok(())
    }

    #[test]
    fn test_parse_date() -> Result<()> {
        let data: Vec<u8> = vec![0x44, 0x61, 0x84, 0xFF, 0xB3, 0xB4, 0xC0];
        let mut cursor = Cursor::new(data);
        let (element_id, element_data) = next_element(&mut cursor)?;
        assert_eq!(element_id, ElementId::DateUtc);
        assert_eq!(element_data, ElementData::Date(-5_000_000));

        Ok(())
    }

    #[test]
    fn test_parse_float_32() -> Result<()> {
        let data: Vec<u8> = vec![0x44, 0x89, 0x84, 0x43, 0x1C, 0x20, 0x07];
        let mut cursor = Cursor::new(data);
        let (element_id, element_data) = next_element(&mut cursor)?;
        assert_eq!(element_id, ElementId::Duration);
        if let ElementData::Float(x) = element_data {
            assert!((x - 156.1251).abs() < 0.00001)
        } else {
            panic!("parse_element returned the wrong element type");
        }

        Ok(())
    }

    #[test]
    fn test_parse_float_64() -> Result<()> {
        let data: Vec<u8> = vec![
            0x44, 0x89, 0x88, 0x40, 0xA9, 0xE0, 0x43, 0x30, 0xBC, 0x60, 0x6E,
        ];
        let mut cursor = Cursor::new(data);
        let (element_id, element_data) = next_element(&mut cursor)?;
        assert_eq!(element_id, ElementId::Duration);
        if let ElementData::Float(x) = element_data {
            assert!((x - 3312.1312312).abs() < 0.00001)
        } else {
            panic!("parse_element returned the wrong element type");
        }

        Ok(())
    }

    #[test]
    fn test_parse_ascii_string() -> Result<()> {
        let data: Vec<u8> = vec![
            0x42, 0x82, 0x88, 0x6D, 0x61, 0x74, 0x72, 0x6F, 0x73, 0x6B, 0x61,
        ];
        let mut cursor = Cursor::new(data);
        let (element_id, element_data) = next_element(&mut cursor)?;
        assert_eq!(element_id, ElementId::DocType);
        assert_eq!(element_data, ElementData::String("matroska".to_owned()));

        Ok(())
    }

    #[test]
    fn test_parse_utf8_string() -> Result<()> {
        let data: Vec<u8> = vec![
            0x4D, 0x80, 0x95, 0xE3, 0x82, 0x82, 0xE3, 0x81, 0x90, 0xE3, 0x82, 0x82, 0xE3, 0x81,
            0x90, 0xE3, 0x81, 0x8A, 0xE3, 0x81, 0x8B, 0xE3, 0x82, 0x86,
        ];
        let mut cursor = Cursor::new(data);
        let (element_id, element_data) = next_element(&mut cursor)?;
        assert_eq!(element_id, ElementId::MuxingApp);
        assert_eq!(
            element_data,
            ElementData::String("もぐもぐおかゆ".to_owned())
        );

        Ok(())
    }

    #[test]
    fn test_parse_binary() -> Result<()> {
        let data: Vec<u8> = vec![
            0x63, 0xA2, 0x95, 0xE3, 0x82, 0x82, 0xE3, 0x81, 0x90, 0xE3, 0x82, 0x82, 0xE3, 0x81,
            0x90, 0xE3, 0x81, 0x8A, 0xE3, 0x81, 0x8B, 0xE3, 0x82, 0x86,
        ];
        let mut cursor = Cursor::new(data);
        let (element_id, element_data) = next_element(&mut cursor)?;
        assert_eq!(element_id, ElementId::CodecPrivate);
        assert_eq!(
            element_data,
            ElementData::Location {
                offset: 3,
                size: 21,
            }
        );

        Ok(())
    }

    #[test]
    fn test_parse_default_unsigned() -> Result<()> {
        let data: Vec<u8> = vec![0x42, 0x86, 0x80];
        let mut cursor = Cursor::new(data);
        let (element_id, element_data) = next_element(&mut cursor)?;
        assert_eq!(element_id, ElementId::EbmlVersion);
        assert_eq!(element_data, ElementData::Unsigned(0));

        Ok(())
    }

    #[test]
    fn test_parse_default_signed() -> Result<()> {
        let data: Vec<u8> = vec![0xFB, 0x80];
        let mut cursor = Cursor::new(data);
        let (element_id, element_data) = next_element(&mut cursor)?;
        assert_eq!(element_id, ElementId::ReferenceBlock);
        assert_eq!(element_data, ElementData::Signed(0));

        Ok(())
    }

    #[test]
    fn test_parse_default_date() -> Result<()> {
        let data: Vec<u8> = vec![0x44, 0x61, 0x80];
        let mut cursor = Cursor::new(data);
        let (element_id, element_data) = next_element(&mut cursor)?;
        assert_eq!(element_id, ElementId::DateUtc);
        assert_eq!(element_data, ElementData::Date(0));

        Ok(())
    }

    #[test]
    fn test_parse_default_float() -> Result<()> {
        let data: Vec<u8> = vec![0x44, 0x89, 0x80];
        let mut cursor = Cursor::new(data);
        let (element_id, element_data) = next_element(&mut cursor)?;
        assert_eq!(element_id, ElementId::Duration);
        if let ElementData::Float(x) = element_data {
            assert!(x.abs() < 0.00001)
        } else {
            panic!("parse_element returned the wrong element type");
        }

        Ok(())
    }

    #[test]
    fn test_parse_default_ascii_string() -> Result<()> {
        let data: Vec<u8> = vec![0x42, 0x82, 0x80];
        let mut cursor = Cursor::new(data);
        let (element_id, element_data) = next_element(&mut cursor)?;
        assert_eq!(element_id, ElementId::DocType);
        assert_eq!(element_data, ElementData::String("".to_owned()));

        Ok(())
    }

    #[test]
    fn test_parse_default_utf8_string() -> Result<()> {
        let data: Vec<u8> = vec![0x4D, 0x80, 0x80];
        let mut cursor = Cursor::new(data);
        let (element_id, element_data) = next_element(&mut cursor)?;
        assert_eq!(element_id, ElementId::MuxingApp);
        assert_eq!(element_data, ElementData::String("".to_owned()));

        Ok(())
    }

    #[test]
    fn reject_oversized_string() {
        let mut cursor = Cursor::new(vec![0u8; 4]);
        let result = parse_string(&mut cursor, 0x00FF_FFFF_FFFF_FFFE);
        assert!(result.is_err());
    }

    #[test]
    fn collect_children_of_unknown_size_master_element() -> Result<()> {
        let data: Vec<u8> = vec![0x1A, 0x45, 0xDF, 0xA3, 0xFF, 0x42, 0x86, 0x81, 0x01];
        let mut cursor = Cursor::new(data);
        let (offset, size) = expect_master(&mut cursor, ElementId::Ebml, None)?;
        assert_eq!(size, u64::MAX);

        let children = collect_children(&mut cursor, offset, size)?;
        assert_eq!(
            children,
            vec![(ElementId::EbmlVersion, ElementData::Unsigned(1))]
        );

        Ok(())
    }

    #[test]
    fn reject_master_element_exceeding_stream() {
        let mut cursor = Cursor::new(vec![0u8; 4]);
        let result = collect_children(&mut cursor, 0, 0x00FF_FFFF_FFFF_FFFE);
        assert!(result.is_err());
    }

    #[test]
    fn reject_oversized_binary() {
        let mut cursor = Cursor::new(vec![0u8; 8]);
        let fields = vec![(
            ElementId::CodecPrivate,
            ElementData::Location {
                offset: 0,
                size: 0x00FF_FFFF_FFFF_FFFE,
            },
        )];
        let result = try_find_binary(&mut cursor, &fields, ElementId::CodecPrivate);
        assert!(result.is_err());
    }
}
