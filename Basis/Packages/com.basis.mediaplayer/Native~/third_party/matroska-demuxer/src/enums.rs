//! Enums exposed in the API.

/// The Matrix Coefficients of the video used to derive luma and chroma values
/// from red, green, and blue color primaries.
///
/// For clarity, the value and meanings for `MatrixCoefficients` are adopted from
/// Table 4 of ISO/IEC 23001-8:2016 or ITU-T H.273.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum MatrixCoefficients {
    /// Unknown,
    Unknown,
    /// Identity.
    Identity,
    /// ITU-R BT.709.
    Bt709,
    /// US FCC 73.682.
    Fcc73682,
    /// ITU-R BT.470BG.
    Bt470bg,
    /// SMPTE 170M.
    Smpte170,
    /// SMPTE 240M.
    Smpte240,
    /// YCoCg.
    YCoCg,
    /// BT2020 Non-constant Luminance.
    Bt2020Ncl,
    /// BT2020 Constant Luminance.
    Bt2020Cl,
    /// SMPTE ST 2085.
    SmpteSt2085,
    /// Chroma-derived Non-constant Luminance.
    ChromaDerivedNcl,
    /// Chroma-derived Constant Luminance.
    ChromaDerivedCl,
    /// ITU-R BT.2100-0.
    Bt2100,
}

impl From<u64> for MatrixCoefficients {
    fn from(d: u64) -> Self {
        match d {
            0 => MatrixCoefficients::Identity,
            1 => MatrixCoefficients::Bt709,
            4 => MatrixCoefficients::Fcc73682,
            5 => MatrixCoefficients::Bt470bg,
            6 => MatrixCoefficients::Smpte170,
            7 => MatrixCoefficients::Smpte240,
            8 => MatrixCoefficients::YCoCg,
            9 => MatrixCoefficients::Bt2020Ncl,
            10 => MatrixCoefficients::Bt2020Cl,
            11 => MatrixCoefficients::SmpteSt2085,
            12 => MatrixCoefficients::ChromaDerivedNcl,
            13 => MatrixCoefficients::ChromaDerivedCl,
            14 => MatrixCoefficients::Bt2100,
            _ => MatrixCoefficients::Unknown,
        }
    }
}

/// How `DisplayWidth` & `DisplayHeight` are interpreted.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum DisplayUnit {
    /// In pixels.
    Pixels,
    /// In centimeters.
    Centimeters,
    /// In inches.
    Inches,
    /// By using the aspect ratio.
    DisplayAspectRatio,
    /// Unknown.
    Unknown,
}

impl From<u64> for DisplayUnit {
    fn from(d: u64) -> Self {
        match d {
            0 => DisplayUnit::Pixels,
            1 => DisplayUnit::Centimeters,
            2 => DisplayUnit::Inches,
            3 => DisplayUnit::DisplayAspectRatio,
            _ => DisplayUnit::Unknown,
        }
    }
}

/// Specify the possible modifications to the aspect ratio.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum AspectRatioType {
    /// Unknown.
    Unknown,
    /// Allow free resizing.
    FreeResizing,
    /// Keep the aspect ratio.
    KeepAspectRatio,
    /// Fixed size.
    Fixed,
}

impl From<u64> for AspectRatioType {
    fn from(d: u64) -> Self {
        match d {
            0 => AspectRatioType::FreeResizing,
            1 => AspectRatioType::KeepAspectRatio,
            2 => AspectRatioType::Fixed,
            _ => AspectRatioType::Unknown,
        }
    }
}

/// Type of the track.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TrackType {
    /// Unknown.
    Unknown,
    /// Video track.
    Video,
    /// Audio track.
    Audio,
    /// A complex track.
    Complex,
    /// A logo.
    Logo,
    /// Subtitles.
    Subtitle,
    /// Buttons.
    Buttons,
    /// Controls.
    Control,
    /// Metadata.
    Metadata,
}

impl From<u64> for TrackType {
    fn from(d: u64) -> Self {
        match d {
            1 => TrackType::Video,
            2 => TrackType::Audio,
            3 => TrackType::Complex,
            16 => TrackType::Logo,
            17 => TrackType::Subtitle,
            18 => TrackType::Buttons,
            32 => TrackType::Control,
            33 => TrackType::Metadata,
            _ => TrackType::Unknown,
        }
    }
}

/// A flag to declare if the video is known to be progressive or interlaced.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum FlagInterlaced {
    /// Unknown.
    Unknown,
    /// Interlaced.
    Interlaced,
    /// Progressive.
    Progressive,
}

impl From<u64> for FlagInterlaced {
    fn from(d: u64) -> Self {
        match d {
            1 => FlagInterlaced::Interlaced,
            2 => FlagInterlaced::Progressive,
            _ => FlagInterlaced::Unknown,
        }
    }
}

/// Declare the field ordering of the video.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum FieldOrder {
    /// Unknown.
    Unknown,
    /// Progressive.
    Progressive,
    /// Top Field First.
    Tff,
    /// Bottom Field First.
    Bff,
    /// Top Field First (swapped).
    BffSwapped,
    /// Bottom Field First (swapped).
    TffSwapped,
}

impl From<u64> for FieldOrder {
    fn from(d: u64) -> Self {
        match d {
            0 => FieldOrder::Progressive,
            1 => FieldOrder::Tff,
            6 => FieldOrder::Bff,
            9 => FieldOrder::BffSwapped,
            14 => FieldOrder::TffSwapped,
            _ => FieldOrder::Unknown,
        }
    }
}

/// Stereo-3D video mode.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum StereoMode {
    /// Unknown.
    Unknown,
    /// Mono.
    Mono,
    /// Side by side (left eye first).
    SideBySideLeftEyeFirst,
    /// Top - bottom (right eye is first).
    TopBottomRightEyeFirst,
    /// Top - bottom (left eye is first).
    TopBottomLeftEyeFirst,
    /// Checkboard (right eye is first).
    CheckboardRightEyeFirst,
    /// Checkboard (left eye is first).
    CheckboardLeftEyeFirst,
    /// Row interleaved (right eye is first).
    RowInterleavedRightEyeFirst,
    /// Row interleaved (left eye is first).
    RowInterleavedLeftEyeFirst,
    /// Column interleaved (right eye is first).
    ColumnInterleavedRightEyeFirst,
    /// Column interleaved (left eye is first).
    ColumnInterleavedLeftEyeFirst,
    /// Anaglyph (cyan/red).
    AnaglyphCyanRed,
    /// Side by side (right eye first).
    SideBySideRightEyeFirst,
    /// Anaglyph (green/magenta).
    AnaglyphGreenMagenta,
    /// Both eyes laced in one Block (left eye is first).
    LacedLeftEyeFirst,
    /// Both eyes laced in one Block (right eye is first).
    LacedRightEyeFirst,
}

impl From<u64> for StereoMode {
    fn from(d: u64) -> Self {
        match d {
            0 => StereoMode::Mono,
            1 => StereoMode::SideBySideLeftEyeFirst,
            2 => StereoMode::TopBottomRightEyeFirst,
            3 => StereoMode::TopBottomLeftEyeFirst,
            4 => StereoMode::CheckboardRightEyeFirst,
            5 => StereoMode::CheckboardLeftEyeFirst,
            6 => StereoMode::RowInterleavedRightEyeFirst,
            7 => StereoMode::RowInterleavedLeftEyeFirst,
            8 => StereoMode::ColumnInterleavedRightEyeFirst,
            9 => StereoMode::ColumnInterleavedLeftEyeFirst,
            10 => StereoMode::AnaglyphCyanRed,
            11 => StereoMode::SideBySideRightEyeFirst,
            12 => StereoMode::AnaglyphGreenMagenta,
            13 => StereoMode::LacedLeftEyeFirst,
            14 => StereoMode::LacedRightEyeFirst,
            _ => StereoMode::Unknown,
        }
    }
}

/// How chroma is sub sampled horizontally.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ChromaSitingHorz {
    /// Unknown.
    Unknown,
    /// Left collocated.
    LeftCollated,
    /// Half.
    Half,
}

impl From<u64> for ChromaSitingHorz {
    fn from(d: u64) -> Self {
        match d {
            1 => ChromaSitingHorz::LeftCollated,
            2 => ChromaSitingHorz::Half,
            _ => ChromaSitingHorz::Unknown,
        }
    }
}

/// How chroma is sub sampled vertically.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ChromaSitingVert {
    /// Unknown.
    Unknown,
    /// Left collocated.
    LeftCollated,
    /// Half.
    Half,
}

impl From<u64> for ChromaSitingVert {
    fn from(d: u64) -> Self {
        match d {
            1 => ChromaSitingVert::LeftCollated,
            2 => ChromaSitingVert::Half,
            _ => ChromaSitingVert::Unknown,
        }
    }
}

/// Clipping of the color ranges.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Range {
    /// Unknown.
    Unknown,
    /// Broadcast range.
    Broadcast,
    /// Full range (no clipping).
    Full,
    /// Defined by MatrixCoefficients / TransferCharacteristics.
    Defined,
}

impl From<u64> for Range {
    fn from(d: u64) -> Self {
        match d {
            1 => Range::Broadcast,
            2 => Range::Full,
            3 => Range::Defined,
            _ => Range::Unknown,
        }
    }
}

/// The transfer characteristics of the video.
///
/// For clarity, the value and meanings for `TransferCharacteristics` are adopted
/// from Table 3 of ISO/IEC 23091-4 or ITU-T H.273.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TransferCharacteristics {
    /// Unknown.
    Unknown,
    /// ITU-R BT.709.
    Bt709,
    /// Gamma 2.2 curve - BT.470M.
    Bt407m,
    /// Gamma 2.8 curve - BT.470BG.
    Bt407bg,
    /// SMPTE 170M.
    Smpte170,
    /// SMPTE 240M.
    Smpte240,
    /// Linear.
    Linear,
    /// Log.
    Log,
    /// Log Sqrt,
    LogSqrt,
    /// IEC 61966-2-4.
    Iec61966_2_4,
    /// ITU-R BT.1361 Extended Colour Gamut.
    Bt1361,
    /// IEC 61966-2-1.
    Iec61966_2_1,
    /// ITU-R BT.2020 10 bit.
    Bt220_10,
    /// ITU-R BT.2020 12 bit.
    Bt220_12,
    /// ITU-R BT.2100 Perceptual Quantization.
    Bt2100,
    /// SMPTE ST 428-1.
    SmpteSt428_1,
    /// ARIB STD-B67 (HLG).
    Hlg,
}

impl From<u64> for TransferCharacteristics {
    fn from(d: u64) -> Self {
        match d {
            1 => TransferCharacteristics::Bt709,
            4 => TransferCharacteristics::Bt407m,
            5 => TransferCharacteristics::Bt407bg,
            6 => TransferCharacteristics::Smpte170,
            7 => TransferCharacteristics::Smpte240,
            8 => TransferCharacteristics::Linear,
            9 => TransferCharacteristics::Log,
            10 => TransferCharacteristics::LogSqrt,
            11 => TransferCharacteristics::Iec61966_2_4,
            12 => TransferCharacteristics::Bt1361,
            13 => TransferCharacteristics::Iec61966_2_1,
            14 => TransferCharacteristics::Bt220_10,
            15 => TransferCharacteristics::Bt220_12,
            16 => TransferCharacteristics::Bt2100,
            17 => TransferCharacteristics::SmpteSt428_1,
            18 => TransferCharacteristics::Hlg,
            _ => TransferCharacteristics::Unknown,
        }
    }
}

/// The colour primaries of the video.
///
/// For clarity, the value and meanings for `Primaries` are adopted
/// from Table 2 of ISO/IEC 23091-4 or ITU-T H.273.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Primaries {
    /// Unknown.
    Unknown,
    /// ITU-R BT.709.
    Bt709,
    /// ITU-R BT.470M.
    Bt470m,
    /// ITU-R BT.470BG - BT.601 625.
    Bt601,
    /// ITU-R BT.601 525 - SMPTE 170M.
    Smpte170,
    /// SMPTE 240M.
    Smpte240,
    /// FILM.
    Film,
    /// ITU-R BT.2020.
    Bt2020,
    /// SMPTE ST 428-1.
    SmpteSt428_1,
    /// SMPTE RP 432-2.
    SmpteRp432_2,
    /// SMPTE EG 432-2.
    SmpteEg432_2,
    /// EBU Tech. 3213-E - JEDEC P22 phosphors.
    JedecP22,
}

impl From<u64> for Primaries {
    fn from(d: u64) -> Self {
        match d {
            1 => Primaries::Bt709,
            4 => Primaries::Bt470m,
            5 => Primaries::Bt601,
            6 => Primaries::Smpte170,
            7 => Primaries::Smpte240,
            8 => Primaries::Film,
            9 => Primaries::Bt2020,
            10 => Primaries::SmpteSt428_1,
            11 => Primaries::SmpteRp432_2,
            12 => Primaries::SmpteEg432_2,
            22 => Primaries::JedecP22,
            _ => Primaries::Unknown,
        }
    }
}

/// Describing what kind of transformation is applied.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum ContentEncodingType {
    /// Unknown.
    Unknown,
    /// Transformation is a compression.
    Compression,
    /// Transformation is a encryption.
    Encryption,
}

impl From<u64> for ContentEncodingType {
    fn from(d: u64) -> Self {
        match d {
            0 => ContentEncodingType::Compression,
            1 => ContentEncodingType::Encryption,
            _ => ContentEncodingType::Unknown,
        }
    }
}

/// The compression algorithm used.
///
/// # Notes
/// The Matroska documentation indicate for [ContentCompAlgo](https://www.matroska.org/technical/elements.html#ContentCompAlgo) elements :
///
/// > Compression method "1" (bzlib) and "2" (lzo1x) are lacking proper documentation
/// > on the format which limits implementation possibilities. Due to licensing conflicts
/// > on commonly available libraries compression methods "2" (lzo1x) does not offer
/// > widespread interoperability.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ContentCompAlgo {
    /// Unknown.
    Unknown,
    /// Zlib
    Zlib,
    /// Bzlib
    Bzlib,
    /// Lzo1x
    Lzo1x,
    /// Header Stripping.
    Stripping,
}

impl From<u64> for ContentCompAlgo {
    fn from(d: u64) -> Self {
        match d {
            0 => Self::Zlib,
            1 => Self::Bzlib,
            2 => Self::Lzo1x,
            3 => Self::Stripping,
            _ => Self::Unknown,
        }
    }
}

/// The encryption algorithm used.
///
/// `NotEncrypted` means that the contents have not been encrypted but only signed.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ContentEncAlgo {
    /// Unknown.
    Unknown,
    /// Not encrypted.
    NotEncrypted,
    /// DES - FIPS 46-3.
    Des,
    /// Triple DES - RFC 1851.
    TripleDes,
    /// Twofish.
    Twofish,
    /// Blowfish.
    Blowfish,
    /// AES - FIPS 187.
    Aes,
}

impl From<u64> for ContentEncAlgo {
    fn from(d: u64) -> Self {
        match d {
            0 => ContentEncAlgo::NotEncrypted,
            1 => ContentEncAlgo::Des,
            2 => ContentEncAlgo::TripleDes,
            3 => ContentEncAlgo::Twofish,
            4 => ContentEncAlgo::Blowfish,
            5 => ContentEncAlgo::Aes,
            _ => ContentEncAlgo::Unknown,
        }
    }
}

/// The AES cipher mode used in the encryption.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum AesSettingsCipherMode {
    /// Unknown.
    Unknown,
    /// AES-CTR / Counter, NIST SP 800-38A.
    Ctr,
    /// AES-CBC / Cipher Block Chaining, NIST SP 800-38A.
    Cbc,
}

impl From<u64> for AesSettingsCipherMode {
    fn from(d: u64) -> Self {
        match d {
            0 => AesSettingsCipherMode::Ctr,
            1 => AesSettingsCipherMode::Cbc,
            _ => AesSettingsCipherMode::Unknown,
        }
    }
}
