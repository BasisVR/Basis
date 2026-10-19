//! All ISO-MP4 boxes (atoms) and operations.
//!
//! * [ISO/IEC 14496-12](https://en.wikipedia.org/wiki/MPEG-4_Part_14) - ISO Base Media File Format (`QuickTime`, MPEG-4, etc)
//! * [ISO/IEC 14496-14](https://en.wikipedia.org/wiki/MPEG-4_Part_14) - MP4 file format
//! * ISO/IEC 14496-17 - Streaming text format
//! * [ISO 23009-1](https://www.iso.org/standard/79329.html) -Dynamic adaptive streaming over HTTP (DASH)
//!
//! * <http://mp4ra.org/#/atoms>
//!
//! Supported Atoms:
//! ```text
//! ftyp
//! moov
//!     mvhd
//!     udta
//!         meta
//!             ilst
//!                 data
//!     trak
//!         tkhd
//!         mdia
//!             mdhd
//!             hdlr
//!             minf
//!                 stbl
//!                     stsd
//!                         avc1
//!                         hev1
//!                         mp4a
//!                         tx3g
//!                     stts
//!                     stsc
//!                     stsz
//!                     stss
//!                     stco
//!                     co64
//!                     ctts
//!                 dinf
//!                     dref
//!                 smhd
//!                 vmhd
//!         edts
//!             elst
//!     mvex
//!         mehd
//!         trex
//! emsg
//! moof
//!     mfhd
//!     traf
//!         tfhd
//!         tfdt
//!         trun
//! mdat
//! free
//! ```

use byteorder::{BigEndian, ReadBytesExt};
use serde::Serialize;
use std::convert::TryInto as _;
use std::io::{Read, Seek, SeekFrom};

use crate::{
    AacConfig, DataType, Error, FixedPointI8, FixedPointU16, FixedPointU8, FourCC, Metadata,
    MetadataKey, Result, TrackKind,
};

pub(crate) mod av01;
pub(crate) mod avc1;
pub(crate) mod co64;
pub(crate) mod ctts;
pub(crate) mod data;
pub(crate) mod dinf;
pub(crate) mod edts;
pub(crate) mod elst;
pub(crate) mod emsg;
pub(crate) mod ftyp;
pub(crate) mod hdlr;
pub(crate) mod hevc;
pub(crate) mod ilst;
pub(crate) mod mdhd;
pub(crate) mod mdia;
pub(crate) mod mehd;
pub(crate) mod meta;
pub(crate) mod mfhd;
pub(crate) mod minf;
pub(crate) mod moof;
pub(crate) mod moov;
pub(crate) mod mp4a;
pub(crate) mod mvex;
pub(crate) mod mvhd;
pub(crate) mod smhd;
pub(crate) mod stbl;
pub(crate) mod stco;
pub(crate) mod stsc;
pub(crate) mod stsd;
pub(crate) mod stss;
pub(crate) mod stsz;
pub(crate) mod stts;
pub(crate) mod tfdt;
pub(crate) mod tfhd;
pub(crate) mod tkhd;
pub(crate) mod traf;
pub(crate) mod trak;
pub(crate) mod trex;
pub(crate) mod trun;
pub(crate) mod tx3g;
pub(crate) mod udta;
pub(crate) mod vmhd;
pub(crate) mod vp08;
pub(crate) mod vp09;
pub(crate) mod vpcc;

pub use av01::Av01Box;
pub use avc1::Avc1Box;
pub use co64::Co64Box;
pub use ctts::CttsBox;
pub use data::DataBox;
pub use dinf::DinfBox;
pub use edts::EdtsBox;
pub use elst::ElstBox;
pub use emsg::EmsgBox;
pub use ftyp::FtypBox;
pub use hdlr::HdlrBox;
pub use hevc::HevcBox;
pub use ilst::IlstBox;
pub use mdhd::MdhdBox;
pub use mdia::MdiaBox;
pub use mehd::MehdBox;
pub use meta::MetaBox;
pub use mfhd::MfhdBox;
pub use minf::MinfBox;
pub use moof::MoofBox;
pub use moov::MoovBox;
pub use mp4a::Mp4aBox;
pub use mvex::MvexBox;
pub use mvhd::MvhdBox;
pub use smhd::SmhdBox;
pub use stbl::StblBox;
pub use stco::StcoBox;
pub use stsc::StscBox;
pub use stsd::{StsdBox, StsdBoxContent};
pub use stss::StssBox;
pub use stsz::StszBox;
pub use stts::SttsBox;
pub use tfdt::TfdtBox;
pub use tfhd::TfhdBox;
pub use tkhd::TkhdBox;
pub use traf::TrafBox;
pub use trak::TrakBox;
pub use trex::TrexBox;
pub use trun::TrunBox;
pub use tx3g::Tx3gBox;
pub use udta::UdtaBox;
pub use vmhd::VmhdBox;
pub use vp08::Vp08Box;
pub use vp09::Vp09Box;
pub use vpcc::VpccBox;

pub const HEADER_SIZE: u64 = 8;
// const HEADER_LARGE_SIZE: u64 = 16;
pub const HEADER_EXT_SIZE: u64 = 4;

macro_rules! boxtype {
    ($( $name:ident => $value:expr ),*) => {
        #[expect(clippy::enum_variant_names, reason = "MP4 box type variants keep Box suffix for clarity/API")]
        #[derive(Clone, Copy, PartialEq, Eq)]
        pub enum BoxType {
            $( $name, )*
            UnknownBox(u32),
        }

        impl From<u32> for BoxType {
            fn from(t: u32) -> BoxType {
                match t {
                    $( $value => BoxType::$name, )*
                    _ => BoxType::UnknownBox(t),
                }
            }
        }

        impl From<BoxType> for u32 {
            fn from(b: BoxType) -> u32 {
                match b {
                    $( BoxType::$name => $value, )*
                    BoxType::UnknownBox(t) => t,
                }
            }
        }
    }
}

boxtype! {
    FtypBox => 0x66747970,
    MvhdBox => 0x6d766864,
    MfhdBox => 0x6d666864,
    FreeBox => 0x66726565,
    MdatBox => 0x6d646174,
    MoovBox => 0x6d6f6f76,
    MvexBox => 0x6d766578,
    MehdBox => 0x6d656864,
    TrexBox => 0x74726578,
    EmsgBox => 0x656d7367,
    MoofBox => 0x6d6f6f66,
    TkhdBox => 0x746b6864,
    TfhdBox => 0x74666864,
    TfdtBox => 0x74666474,
    EdtsBox => 0x65647473,
    MdiaBox => 0x6d646961,
    ElstBox => 0x656c7374,
    MdhdBox => 0x6d646864,
    HdlrBox => 0x68646c72,
    MinfBox => 0x6d696e66,
    VmhdBox => 0x766d6864,
    StblBox => 0x7374626c,
    StsdBox => 0x73747364,
    SttsBox => 0x73747473,
    CttsBox => 0x63747473,
    StssBox => 0x73747373,
    StscBox => 0x73747363,
    StszBox => 0x7374737A,
    StcoBox => 0x7374636F,
    Co64Box => 0x636F3634,
    TrakBox => 0x7472616b,
    TrafBox => 0x74726166,
    TrunBox => 0x7472756E,
    UdtaBox => 0x75647461,
    MetaBox => 0x6d657461,
    DinfBox => 0x64696e66,
    DrefBox => 0x64726566,
    UrlBox  => 0x75726C20,
    SmhdBox => 0x736d6864,
    Avc1Box => 0x61766331,
    // Avc3Box => 0x61766333,
    AvcCBox => 0x61766343,
    Av01Box => 0x61763031,
    Av1CBox => 0x61763143,
    Hev1Box => 0x68657631,
    Hvc1Box => 0x68766331,
    HvcCBox => 0x68766343,
    Mp4aBox => 0x6d703461,
    EsdsBox => 0x65736473,
    Tx3gBox => 0x74783367,
    VpccBox => 0x76706343,
    Vp08Box => 0x76703038,
    Vp09Box => 0x76703039,
    DataBox => 0x64617461,
    IlstBox => 0x696c7374,
    NameBox => 0xa96e616d,
    DayBox => 0xa9646179,
    CovrBox => 0x636f7672,
    DescBox => 0x64657363,
    WideBox => 0x77696465,
    WaveBox => 0x77617665
}

pub trait Mp4Box: Sized {
    fn box_type(&self) -> BoxType;
    fn box_size(&self) -> u64;
    fn to_json(&self) -> Result<String>;
    fn summary(&self) -> Result<String>;
}

pub trait ReadBox<T>: Sized {
    fn read_box(_: T, size: u64) -> Result<Self>;
}

#[derive(Debug, Clone, Copy)]
pub struct BoxHeader {
    pub name: BoxType,
    pub size: u64,
}

impl BoxHeader {
    pub fn new(name: BoxType, size: u64) -> Self {
        Self { name, size }
    }

    // TODO: if size is 0, then this box is the last one in the file
    pub fn read<R: Read>(reader: &mut R) -> Result<Self> {
        // Create and read to buf.
        let mut buf = [0u8; 8]; // 8 bytes for box header.
        reader.read_exact(&mut buf)?;

        // Get size.
        #[expect(clippy::unwrap_used)] // [u8; 4] from a slice that is 4 long cannot fail
        let size = u32::from_be_bytes(buf[0..4].try_into().unwrap());

        // Get box type string.
        #[expect(clippy::unwrap_used)] // [u8; 4] from a slice that is 4 long cannot fail
        let typ = u32::from_be_bytes(buf[4..8].try_into().unwrap());

        // Get largesize if size is 1
        if size == 1 {
            reader.read_exact(&mut buf)?;
            let largesize = u64::from_be_bytes(buf);

            Ok(Self {
                name: BoxType::from(typ),

                // Subtract the length of the serialized largesize, as callers assume `size - HEADER_SIZE` is the length
                // of the box data. Disallow `largesize < 16`, or else a largesize of 8 will result in a BoxHeader::size
                // of 0, incorrectly indicating that the box data extends to the end of the stream.
                size: match largesize {
                    0 => 0,
                    1..=15 => return Err(Error::InvalidData("64-bit box size too small")),
                    16..=u64::MAX => largesize - 8,
                },
            })
        } else {
            Ok(Self {
                name: BoxType::from(typ),
                size: size as u64,
            })
        }
    }
}

pub fn read_box_header_ext<R: Read>(reader: &mut R) -> Result<(u8, u32)> {
    let version = reader.read_u8()?;
    let flags = reader.read_u24::<BigEndian>()?;
    Ok((version, flags))
}

pub fn box_start<R: Seek>(seeker: &mut R) -> Result<u64> {
    Ok(seeker.stream_position()? - HEADER_SIZE)
}

pub fn skip_bytes<S: Seek>(seeker: &mut S, size: u64) -> Result<()> {
    let size = i64::try_from(size).map_err(|_err| Error::InvalidData("skip size too large"))?;
    seeker.seek(SeekFrom::Current(size))?;
    Ok(())
}

pub fn skip_bytes_to<S: Seek>(seeker: &mut S, pos: u64) -> Result<()> {
    seeker.seek(SeekFrom::Start(pos))?;
    Ok(())
}

pub fn skip_box<S: Seek>(seeker: &mut S, size: u64) -> Result<()> {
    let start = box_start(seeker)?;
    skip_bytes_to(seeker, start + size)?;
    Ok(())
}

#[derive(Default, Debug, Clone, PartialEq, Eq, Serialize)]
pub struct RawBox<T> {
    pub contents: T,
    pub raw: Vec<u8>,
}

impl<R: Read + Seek, T: for<'a> ReadBox<&'a mut R>> ReadBox<&mut R> for RawBox<T> {
    fn read_box(reader: &mut R, size: u64) -> Result<Self> {
        let start = reader.stream_position()?;

        let contents = T::read_box(reader, size)?;

        let end = reader.stream_position()?;
        let mut raw = vec![0u8; (end - start) as usize];
        reader.seek(SeekFrom::Start(start))?;
        reader.read_exact(&mut raw[..])?;

        Ok(Self { contents, raw })
    }
}

impl<T> std::ops::Deref for RawBox<T> {
    type Target = T;

    fn deref(&self) -> &Self::Target {
        &self.contents
    }
}

impl<T> std::ops::DerefMut for RawBox<T> {
    fn deref_mut(&mut self) -> &mut Self::Target {
        &mut self.contents
    }
}

mod value_u32 {
    use crate::types::FixedPointU16;
    use serde::{self, Serializer};

    pub fn serialize<S>(fixed: &FixedPointU16, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        serializer.serialize_u16(fixed.value())
    }
}

mod value_i16 {
    use crate::types::FixedPointI8;
    use serde::{self, Serializer};

    pub fn serialize<S>(fixed: &FixedPointI8, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        serializer.serialize_i8(fixed.value())
    }
}

mod value_u8 {
    use crate::types::FixedPointU8;
    use serde::{self, Serializer};

    pub fn serialize<S>(fixed: &FixedPointU8, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: Serializer,
    {
        serializer.serialize_u8(fixed.value())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_fourcc() {
        let ftyp_fcc = 0x66747970;
        let ftyp_value = FourCC::from(ftyp_fcc);
        assert_eq!(&ftyp_value.value[..], b"ftyp");
        let ftyp_fcc2: u32 = ftyp_value.into();
        assert_eq!(ftyp_fcc, ftyp_fcc2);
    }

    #[test]
    fn test_largesize_too_small() {
        let error = BoxHeader::read(&mut &[0, 0, 0, 1, 1, 2, 3, 4, 0, 0, 0, 0, 0, 0, 0, 7][..]);
        assert!(matches!(error, Err(Error::InvalidData(_))));
    }

    #[test]
    fn test_zero_largesize() {
        let error = BoxHeader::read(&mut &[0, 0, 0, 1, 1, 2, 3, 4, 0, 0, 0, 0, 0, 0, 0, 8][..]);
        assert!(matches!(error, Err(Error::InvalidData(_))));
    }

    #[test]
    fn test_nonzero_largesize_too_small() {
        let error = BoxHeader::read(&mut &[0, 0, 0, 1, 1, 2, 3, 4, 0, 0, 0, 0, 0, 0, 0, 15][..]);
        assert!(matches!(error, Err(Error::InvalidData(_))));
    }

    #[test]
    fn test_valid_largesize() {
        let header = BoxHeader::read(&mut &[0, 0, 0, 1, 1, 2, 3, 4, 0, 0, 0, 0, 0, 0, 0, 16][..]);
        assert!(matches!(header, Ok(BoxHeader { size: 8, .. })));
    }
}
