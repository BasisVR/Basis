use serde::Serialize;
use std::borrow::Cow;
use std::convert::TryFrom;
use std::fmt;

use crate::mp4box::BoxType;
use crate::{Error, Result};

pub use bytes::Bytes;
pub use num_rational::Ratio;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
pub struct FixedPointU8(Ratio<u16>);

impl FixedPointU8 {
    pub fn new(val: u8) -> Self {
        Self(Ratio::new_raw(val as u16 * 0x100, 0x100))
    }

    pub fn new_raw(val: u16) -> Self {
        Self(Ratio::new_raw(val, 0x100))
    }

    pub fn value(&self) -> u8 {
        self.0.to_integer() as u8
    }

    pub fn raw_value(&self) -> u16 {
        *self.0.numer()
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
pub struct FixedPointI8(Ratio<i16>);

impl FixedPointI8 {
    pub fn new(val: i8) -> Self {
        Self(Ratio::new_raw(val as i16 * 0x100, 0x100))
    }

    pub fn new_raw(val: i16) -> Self {
        Self(Ratio::new_raw(val, 0x100))
    }

    pub fn value(&self) -> i8 {
        self.0.to_integer() as i8
    }

    pub fn raw_value(&self) -> i16 {
        *self.0.numer()
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
pub struct FixedPointU16(Ratio<u32>);

impl FixedPointU16 {
    pub fn new(val: u16) -> Self {
        Self(Ratio::new_raw(val as u32 * 0x10000, 0x10000))
    }

    pub fn new_raw(val: u32) -> Self {
        Self(Ratio::new_raw(val, 0x10000))
    }

    pub fn value(&self) -> u16 {
        self.0.to_integer() as u16
    }

    pub fn raw_value(&self) -> u32 {
        *self.0.numer()
    }
}

impl fmt::Debug for BoxType {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        let fourcc: FourCC = From::from(*self);
        write!(f, "{fourcc}")
    }
}

impl fmt::Display for BoxType {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        let fourcc: FourCC = From::from(*self);
        write!(f, "{fourcc}")
    }
}

#[derive(Default, PartialEq, Eq, Clone, Copy, Serialize)]
pub struct FourCC {
    pub value: [u8; 4],
}

impl std::str::FromStr for FourCC {
    type Err = Error;

    fn from_str(s: &str) -> Result<Self> {
        if let [a, b, c, d] = s.as_bytes() {
            Ok(Self {
                value: [*a, *b, *c, *d],
            })
        } else {
            Err(Error::InvalidData("expected exactly four bytes in string"))
        }
    }
}

impl From<u32> for FourCC {
    fn from(number: u32) -> Self {
        Self {
            value: number.to_be_bytes(),
        }
    }
}

impl From<FourCC> for u32 {
    fn from(fourcc: FourCC) -> Self {
        (&fourcc).into()
    }
}

impl From<&FourCC> for u32 {
    fn from(fourcc: &FourCC) -> Self {
        Self::from_be_bytes(fourcc.value)
    }
}

impl From<[u8; 4]> for FourCC {
    fn from(value: [u8; 4]) -> Self {
        Self { value }
    }
}

impl From<BoxType> for FourCC {
    fn from(t: BoxType) -> Self {
        let box_num: u32 = Into::into(t);
        From::from(box_num)
    }
}

impl fmt::Debug for FourCC {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        let code: u32 = self.into();
        let string = String::from_utf8_lossy(&self.value[..]);
        write!(f, "{string} / {code:#010X}")
    }
}

impl fmt::Display for FourCC {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}", String::from_utf8_lossy(&self.value[..]))
    }
}

const DISPLAY_TYPE_VIDEO: &str = "Video";
const DISPLAY_TYPE_AUDIO: &str = "Audio";
const DISPLAY_TYPE_SUBTITLE: &str = "Subtitle";

const HANDLER_TYPE_VIDEO: &str = "vide";
const HANDLER_TYPE_VIDEO_FOURCC: [u8; 4] = [b'v', b'i', b'd', b'e'];

const HANDLER_TYPE_AUDIO: &str = "soun";
const HANDLER_TYPE_AUDIO_FOURCC: [u8; 4] = [b's', b'o', b'u', b'n'];

const HANDLER_TYPE_SUBTITLE: &str = "sbtl";
const HANDLER_TYPE_SUBTITLE_FOURCC: [u8; 4] = [b's', b'b', b't', b'l'];

pub type TrackId = u32;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TrackKind {
    Video,
    Audio,
    Subtitle,
}

impl fmt::Display for TrackKind {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        let s = match self {
            Self::Video => DISPLAY_TYPE_VIDEO,
            Self::Audio => DISPLAY_TYPE_AUDIO,
            Self::Subtitle => DISPLAY_TYPE_SUBTITLE,
        };
        write!(f, "{s}")
    }
}

impl TryFrom<&str> for TrackKind {
    type Error = Error;
    fn try_from(handler: &str) -> Result<Self> {
        match handler {
            HANDLER_TYPE_VIDEO => Ok(Self::Video),
            HANDLER_TYPE_AUDIO => Ok(Self::Audio),
            HANDLER_TYPE_SUBTITLE => Ok(Self::Subtitle),
            _ => Err(Error::InvalidData("unsupported handler type")),
        }
    }
}

impl TryFrom<&FourCC> for TrackKind {
    type Error = Error;
    fn try_from(fourcc: &FourCC) -> Result<Self> {
        match fourcc.value {
            HANDLER_TYPE_VIDEO_FOURCC => Ok(Self::Video),
            HANDLER_TYPE_AUDIO_FOURCC => Ok(Self::Audio),
            HANDLER_TYPE_SUBTITLE_FOURCC => Ok(Self::Subtitle),
            _ => Err(Error::InvalidData("unsupported handler type")),
        }
    }
}

impl From<TrackKind> for FourCC {
    fn from(t: TrackKind) -> Self {
        match t {
            TrackKind::Video => HANDLER_TYPE_VIDEO_FOURCC.into(),
            TrackKind::Audio => HANDLER_TYPE_AUDIO_FOURCC.into(),
            TrackKind::Subtitle => HANDLER_TYPE_SUBTITLE_FOURCC.into(),
        }
    }
}

const MEDIA_TYPE_H264: &str = "h264";
const MEDIA_TYPE_H265: &str = "h265";
const MEDIA_TYPE_VP9: &str = "vp9";
const MEDIA_TYPE_AAC: &str = "aac";
const MEDIA_TYPE_TTXT: &str = "ttxt";

#[expect(
    clippy::upper_case_acronyms,
    reason = "Media type names match codec names/API"
)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MediaType {
    H264,
    H265,
    VP9,
    AAC,
    TTXT,
}

impl fmt::Display for MediaType {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        let s: &str = self.into();
        write!(f, "{s}")
    }
}

impl TryFrom<&str> for MediaType {
    type Error = Error;
    fn try_from(media: &str) -> Result<Self> {
        match media {
            MEDIA_TYPE_H264 => Ok(Self::H264),
            MEDIA_TYPE_H265 => Ok(Self::H265),
            MEDIA_TYPE_VP9 => Ok(Self::VP9),
            MEDIA_TYPE_AAC => Ok(Self::AAC),
            MEDIA_TYPE_TTXT => Ok(Self::TTXT),
            _ => Err(Error::InvalidData("unsupported media type")),
        }
    }
}

impl From<MediaType> for &str {
    fn from(t: MediaType) -> &'static str {
        match t {
            MediaType::H264 => MEDIA_TYPE_H264,
            MediaType::H265 => MEDIA_TYPE_H265,
            MediaType::VP9 => MEDIA_TYPE_VP9,
            MediaType::AAC => MEDIA_TYPE_AAC,
            MediaType::TTXT => MEDIA_TYPE_TTXT,
        }
    }
}

impl From<&MediaType> for &str {
    fn from(t: &MediaType) -> &'static str {
        match t {
            MediaType::H264 => MEDIA_TYPE_H264,
            MediaType::H265 => MEDIA_TYPE_H265,
            MediaType::VP9 => MEDIA_TYPE_VP9,
            MediaType::AAC => MEDIA_TYPE_AAC,
            MediaType::TTXT => MEDIA_TYPE_TTXT,
        }
    }
}

#[expect(
    clippy::enum_variant_names,
    reason = "Variant names match existing public API"
)]
#[derive(Debug, PartialEq, Eq, Clone, Copy)]
pub enum AvcProfile {
    AvcConstrainedBaseline, // 66 with constraint set 1
    AvcBaseline,            // 66,
    AvcMain,                // 77,
    AvcExtended,            // 88,
    AvcHigh,                // 100
                            // TODO Progressive High Profile, Constrained High Profile, ...
}

impl TryFrom<(u8, u8)> for AvcProfile {
    type Error = Error;
    fn try_from(value: (u8, u8)) -> Result<Self> {
        let profile = value.0;
        let constraint_set1_flag = (value.1 & 0x40) >> 7;
        match (profile, constraint_set1_flag) {
            (66, 1) => Ok(Self::AvcConstrainedBaseline),
            (66, 0) => Ok(Self::AvcBaseline),
            (77, _) => Ok(Self::AvcMain),
            (88, _) => Ok(Self::AvcExtended),
            (100, _) => Ok(Self::AvcHigh),
            _ => Err(Error::InvalidData("unsupported avc profile")),
        }
    }
}

impl fmt::Display for AvcProfile {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        let profile = match self {
            Self::AvcConstrainedBaseline => "Constrained Baseline",
            Self::AvcBaseline => "Baseline",
            Self::AvcMain => "Main",
            Self::AvcExtended => "Extended",
            Self::AvcHigh => "High",
        };
        write!(f, "{profile}")
    }
}

#[derive(Debug, PartialEq, Eq, Clone, Copy)]
pub enum AudioObjectType {
    AacMain = 1,                                       // AAC Main Profile
    AacLowComplexity = 2,                              // AAC Low Complexity
    AacScalableSampleRate = 3,                         // AAC Scalable Sample Rate
    AacLongTermPrediction = 4,                         // AAC Long Term Predictor
    SpectralBandReplication = 5,                       // Spectral band Replication
    AACScalable = 6,                                   // AAC Scalable
    TwinVQ = 7,                                        // Twin VQ
    CodeExcitedLinearPrediction = 8,                   // CELP
    HarmonicVectorExcitationCoding = 9,                // HVXC
    TextToSpeechtInterface = 12,                       // TTSI
    MainSynthetic = 13,                                // Main Synthetic
    WavetableSynthesis = 14,                           // Wavetable Synthesis
    GeneralMIDI = 15,                                  // General MIDI
    AlgorithmicSynthesis = 16,                         // Algorithmic Synthesis
    ErrorResilientAacLowComplexity = 17,               // ER AAC LC
    ErrorResilientAacLongTermPrediction = 19,          // ER AAC LTP
    ErrorResilientAacScalable = 20,                    // ER AAC Scalable
    ErrorResilientAacTwinVQ = 21,                      // ER AAC TwinVQ
    ErrorResilientAacBitSlicedArithmeticCoding = 22,   // ER Bit Sliced Arithmetic Coding
    ErrorResilientAacLowDelay = 23,                    // ER AAC Low Delay
    ErrorResilientCodeExcitedLinearPrediction = 24,    // ER CELP
    ErrorResilientHarmonicVectorExcitationCoding = 25, // ER HVXC
    ErrorResilientHarmonicIndividualLinesNoise = 26,   // ER HILN
    ErrorResilientParametric = 27,                     // ER Parametric
    SinuSoidalCoding = 28,                             // SSC
    ParametricStereo = 29,                             // PS
    MpegSurround = 30,                                 // MPEG Surround
    MpegLayer1 = 32,                                   // MPEG Layer 1
    MpegLayer2 = 33,                                   // MPEG Layer 2
    MpegLayer3 = 34,                                   // MPEG Layer 3
    DirectStreamTransfer = 35,                         // DST Direct Stream Transfer
    AudioLosslessCoding = 36,                          // ALS Audio Lossless Coding
    ScalableLosslessCoding = 37,                       // SLC Scalable Lossless Coding
    ScalableLosslessCodingNoneCore = 38,               // SLC non-core
    ErrorResilientAacEnhancedLowDelay = 39,            // ER AAC ELD
    SymbolicMusicRepresentationSimple = 40,            // SMR Simple
    SymbolicMusicRepresentationMain = 41,              // SMR Main
    UnifiedSpeechAudioCoding = 42,                     // USAC
    SpatialAudioObjectCoding = 43,                     // SAOC
    LowDelayMpegSurround = 44,                         // LD MPEG Surround
    SpatialAudioObjectCodingDialogueEnhancement = 45,  // SAOC-DE
    AudioSync = 46,                                    // Audio Sync
}

impl TryFrom<u8> for AudioObjectType {
    type Error = Error;
    fn try_from(value: u8) -> Result<Self> {
        match value {
            1 => Ok(Self::AacMain),
            2 => Ok(Self::AacLowComplexity),
            3 => Ok(Self::AacScalableSampleRate),
            4 => Ok(Self::AacLongTermPrediction),
            5 => Ok(Self::SpectralBandReplication),
            6 => Ok(Self::AACScalable),
            7 => Ok(Self::TwinVQ),
            8 => Ok(Self::CodeExcitedLinearPrediction),
            9 => Ok(Self::HarmonicVectorExcitationCoding),
            12 => Ok(Self::TextToSpeechtInterface),
            13 => Ok(Self::MainSynthetic),
            14 => Ok(Self::WavetableSynthesis),
            15 => Ok(Self::GeneralMIDI),
            16 => Ok(Self::AlgorithmicSynthesis),
            17 => Ok(Self::ErrorResilientAacLowComplexity),
            19 => Ok(Self::ErrorResilientAacLongTermPrediction),
            20 => Ok(Self::ErrorResilientAacScalable),
            21 => Ok(Self::ErrorResilientAacTwinVQ),
            22 => Ok(Self::ErrorResilientAacBitSlicedArithmeticCoding),
            23 => Ok(Self::ErrorResilientAacLowDelay),
            24 => Ok(Self::ErrorResilientCodeExcitedLinearPrediction),
            25 => Ok(Self::ErrorResilientHarmonicVectorExcitationCoding),
            26 => Ok(Self::ErrorResilientHarmonicIndividualLinesNoise),
            27 => Ok(Self::ErrorResilientParametric),
            28 => Ok(Self::SinuSoidalCoding),
            29 => Ok(Self::ParametricStereo),
            30 => Ok(Self::MpegSurround),
            32 => Ok(Self::MpegLayer1),
            33 => Ok(Self::MpegLayer2),
            34 => Ok(Self::MpegLayer3),
            35 => Ok(Self::DirectStreamTransfer),
            36 => Ok(Self::AudioLosslessCoding),
            37 => Ok(Self::ScalableLosslessCoding),
            38 => Ok(Self::ScalableLosslessCodingNoneCore),
            39 => Ok(Self::ErrorResilientAacEnhancedLowDelay),
            40 => Ok(Self::SymbolicMusicRepresentationSimple),
            41 => Ok(Self::SymbolicMusicRepresentationMain),
            42 => Ok(Self::UnifiedSpeechAudioCoding),
            43 => Ok(Self::SpatialAudioObjectCoding),
            44 => Ok(Self::LowDelayMpegSurround),
            45 => Ok(Self::SpatialAudioObjectCodingDialogueEnhancement),
            46 => Ok(Self::AudioSync),
            _ => Err(Error::InvalidData("invalid audio object type")),
        }
    }
}

impl fmt::Display for AudioObjectType {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        let type_str = match self {
            Self::AacMain => "AAC Main",
            Self::AacLowComplexity => "LC",
            Self::AacScalableSampleRate => "SSR",
            Self::AacLongTermPrediction => "LTP",
            Self::SpectralBandReplication => "SBR",
            Self::AACScalable => "Scalable",
            Self::TwinVQ => "TwinVQ",
            Self::CodeExcitedLinearPrediction => "CELP",
            Self::HarmonicVectorExcitationCoding => "HVXC",
            Self::TextToSpeechtInterface => "TTSI",
            Self::MainSynthetic => "Main Synthetic",
            Self::WavetableSynthesis => "Wavetable Synthesis",
            Self::GeneralMIDI => "General MIDI",
            Self::AlgorithmicSynthesis => "Algorithmic Synthesis",
            Self::ErrorResilientAacLowComplexity => "ER AAC LC",
            Self::ErrorResilientAacLongTermPrediction => "ER AAC LTP",
            Self::ErrorResilientAacScalable => "ER AAC scalable",
            Self::ErrorResilientAacTwinVQ => "ER AAC TwinVQ",
            Self::ErrorResilientAacBitSlicedArithmeticCoding => "ER AAC BSAC",
            Self::ErrorResilientAacLowDelay => "ER AAC LD",
            Self::ErrorResilientCodeExcitedLinearPrediction => "ER CELP",
            Self::ErrorResilientHarmonicVectorExcitationCoding => "ER HVXC",
            Self::ErrorResilientHarmonicIndividualLinesNoise => "ER HILN",
            Self::ErrorResilientParametric => "ER Parametric",
            Self::SinuSoidalCoding => "SSC",
            Self::ParametricStereo => "Parametric Stereo",
            Self::MpegSurround => "MPEG surround",
            Self::MpegLayer1 => "MPEG Layer 1",
            Self::MpegLayer2 => "MPEG Layer 2",
            Self::MpegLayer3 => "MPEG Layer 3",
            Self::DirectStreamTransfer => "DST",
            Self::AudioLosslessCoding => "ALS",
            Self::ScalableLosslessCoding => "SLS",
            Self::ScalableLosslessCodingNoneCore => "SLS Non-core",
            Self::ErrorResilientAacEnhancedLowDelay => "ER AAC ELD",
            Self::SymbolicMusicRepresentationSimple => "SMR Simple",
            Self::SymbolicMusicRepresentationMain => "SMR Main",
            Self::UnifiedSpeechAudioCoding => "USAC",
            Self::SpatialAudioObjectCoding => "SAOC",
            Self::LowDelayMpegSurround => "LD MPEG Surround",
            Self::SpatialAudioObjectCodingDialogueEnhancement => "SAOC-DE",
            Self::AudioSync => "Audio Sync",
        };
        write!(f, "{type_str}")
    }
}

#[derive(Debug, PartialEq, Eq, Clone, Copy)]
pub enum SampleFreqIndex {
    Freq96000 = 0x0,
    Freq88200 = 0x1,
    Freq64000 = 0x2,
    Freq48000 = 0x3,
    Freq44100 = 0x4,
    Freq32000 = 0x5,
    Freq24000 = 0x6,
    Freq22050 = 0x7,
    Freq16000 = 0x8,
    Freq12000 = 0x9,
    Freq11025 = 0xa,
    Freq8000 = 0xb,
    Freq7350 = 0xc,
}

impl TryFrom<u8> for SampleFreqIndex {
    type Error = Error;
    fn try_from(value: u8) -> Result<Self> {
        match value {
            0x0 => Ok(Self::Freq96000),
            0x1 => Ok(Self::Freq88200),
            0x2 => Ok(Self::Freq64000),
            0x3 => Ok(Self::Freq48000),
            0x4 => Ok(Self::Freq44100),
            0x5 => Ok(Self::Freq32000),
            0x6 => Ok(Self::Freq24000),
            0x7 => Ok(Self::Freq22050),
            0x8 => Ok(Self::Freq16000),
            0x9 => Ok(Self::Freq12000),
            0xa => Ok(Self::Freq11025),
            0xb => Ok(Self::Freq8000),
            0xc => Ok(Self::Freq7350),
            _ => Err(Error::InvalidData("invalid sampling frequency index")),
        }
    }
}

impl SampleFreqIndex {
    pub fn freq(&self) -> u32 {
        match *self {
            Self::Freq96000 => 96000,
            Self::Freq88200 => 88200,
            Self::Freq64000 => 64000,
            Self::Freq48000 => 48000,
            Self::Freq44100 => 44100,
            Self::Freq32000 => 32000,
            Self::Freq24000 => 24000,
            Self::Freq22050 => 22050,
            Self::Freq16000 => 16000,
            Self::Freq12000 => 12000,
            Self::Freq11025 => 11025,
            Self::Freq8000 => 8000,
            Self::Freq7350 => 7350,
        }
    }
}

#[derive(Debug, PartialEq, Eq, Clone, Copy)]
pub enum ChannelConfig {
    Mono = 0x1,
    Stereo = 0x2,
    Three = 0x3,
    Four = 0x4,
    Five = 0x5,
    FiveOne = 0x6,
    SevenOne = 0x7,
}

impl TryFrom<u8> for ChannelConfig {
    type Error = Error;
    fn try_from(value: u8) -> Result<Self> {
        match value {
            0x1 => Ok(Self::Mono),
            0x2 => Ok(Self::Stereo),
            0x3 => Ok(Self::Three),
            0x4 => Ok(Self::Four),
            0x5 => Ok(Self::Five),
            0x6 => Ok(Self::FiveOne),
            0x7 => Ok(Self::SevenOne),
            _ => Err(Error::InvalidData("invalid channel configuration")),
        }
    }
}

impl fmt::Display for ChannelConfig {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        let s = match self {
            Self::Mono => "mono",
            Self::Stereo => "stereo",
            Self::Three => "three",
            Self::Four => "four",
            Self::Five => "five",
            Self::FiveOne => "five.one",
            Self::SevenOne => "seven.one",
        };
        write!(f, "{s}")
    }
}

#[derive(Debug, PartialEq, Eq, Clone, Default)]
pub struct AvcConfig {
    pub width: u16,
    pub height: u16,
    pub seq_param_set: Vec<u8>,
    pub pic_param_set: Vec<u8>,
}

#[derive(Debug, PartialEq, Eq, Clone, Default)]
pub struct HevcConfig {
    pub width: u16,
    pub height: u16,
}

#[derive(Debug, PartialEq, Eq, Clone, Default)]
pub struct Vp9Config {
    pub width: u16,
    pub height: u16,
}

#[derive(Debug, PartialEq, Eq, Clone)]
pub struct AacConfig {
    pub bitrate: u32,
    pub profile: AudioObjectType,
    pub freq_index: SampleFreqIndex,
    pub chan_conf: ChannelConfig,
}

impl Default for AacConfig {
    fn default() -> Self {
        Self {
            bitrate: 0,
            profile: AudioObjectType::AacLowComplexity,
            freq_index: SampleFreqIndex::Freq48000,
            chan_conf: ChannelConfig::Stereo,
        }
    }
}

#[derive(Debug, PartialEq, Eq, Clone, Default)]
pub struct TtxtConfig {}

#[expect(
    clippy::enum_variant_names,
    reason = "Variant names match wrapped config types"
)]
#[derive(Debug, PartialEq, Eq, Clone)]
pub enum MediaConfig {
    AvcConfig(AvcConfig),
    HevcConfig(HevcConfig),
    Vp9Config(Vp9Config),
    AacConfig(AacConfig),
    TtxtConfig(TtxtConfig),
}

#[derive(Debug)]
pub struct Mp4Sample {
    pub start_time: u64,
    pub duration: u32,
    pub rendering_offset: i32,
    pub is_sync: bool,
    pub bytes: Bytes,
}

impl PartialEq for Mp4Sample {
    fn eq(&self, other: &Self) -> bool {
        self.start_time == other.start_time
            && self.duration == other.duration
            && self.rendering_offset == other.rendering_offset
            && self.is_sync == other.is_sync
            && self.bytes.len() == other.bytes.len() // XXX for easy check
    }
}

impl fmt::Display for Mp4Sample {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            f,
            "start_time {}, duration {}, rendering_offset {}, is_sync {}, length {}",
            self.start_time,
            self.duration,
            self.rendering_offset,
            self.is_sync,
            self.bytes.len()
        )
    }
}

pub fn creation_time(creation_time: u64) -> u64 {
    // convert from MP4 epoch (1904-01-01) to Unix epoch (1970-01-01)
    if creation_time >= 2082844800 {
        creation_time - 2082844800
    } else {
        creation_time
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub enum DataType {
    Binary = 0x000000,
    Text = 0x000001,
    Image = 0x00000D,
    TempoCpil = 0x000015,
}

#[expect(clippy::derivable_impls)]
impl std::default::Default for DataType {
    fn default() -> Self {
        Self::Binary
    }
}

impl TryFrom<u32> for DataType {
    type Error = Error;
    fn try_from(value: u32) -> Result<Self> {
        match value {
            0x000000 => Ok(Self::Binary),
            0x000001 => Ok(Self::Text),
            0x00000D => Ok(Self::Image),
            0x000015 => Ok(Self::TempoCpil),
            _ => Err(Error::InvalidData("invalid data type")),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Hash, Serialize)]
pub enum MetadataKey {
    Title,
    Year,
    Poster,
    Summary,
}

pub trait Metadata<'a> {
    /// The video's title
    fn title(&self) -> Option<Cow<'_, str>>;
    /// The video's release year
    fn year(&self) -> Option<u32>;
    /// The video's poster (cover art)
    fn poster(&self) -> Option<&[u8]>;
    /// The video's summary
    fn summary(&self) -> Option<Cow<'_, str>>;
}

impl<'a, T: Metadata<'a>> Metadata<'a> for &'a T {
    fn title(&self) -> Option<Cow<'_, str>> {
        (**self).title()
    }

    fn year(&self) -> Option<u32> {
        (**self).year()
    }

    fn poster(&self) -> Option<&[u8]> {
        (**self).poster()
    }

    fn summary(&self) -> Option<Cow<'_, str>> {
        (**self).summary()
    }
}

impl<'a, T: Metadata<'a>> Metadata<'a> for Option<T> {
    fn title(&self) -> Option<Cow<'_, str>> {
        let t = self.as_ref()?;
        t.title()
    }

    fn year(&self) -> Option<u32> {
        let t = self.as_ref()?;
        t.year()
    }

    fn poster(&self) -> Option<&[u8]> {
        let t = self.as_ref()?;
        t.poster()
    }

    fn summary(&self) -> Option<Cow<'_, str>> {
        let t = self.as_ref()?;
        t.summary()
    }
}
