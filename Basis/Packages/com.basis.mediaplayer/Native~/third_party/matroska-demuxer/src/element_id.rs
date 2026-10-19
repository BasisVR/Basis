//! Element IDs defines by the EBML and Matroska specifications.

use std::collections::HashMap;
use std::sync::OnceLock;

/// The IDs of the supported elements.
#[derive(Clone, Copy, Debug, Eq, PartialEq, Hash)]
#[allow(missing_docs)]
pub enum ElementId {
    Unknown,
    Ebml,
    EbmlVersion,
    EbmlReadVersion,
    EbmlMaxIdLength,
    EbmlMaxSizeLength,
    DocType,
    DocTypeVersion,
    DocTypeReadVersion,
    Crc32,
    Void,
    Segment,
    SeekHead,
    Seek,
    SeekId,
    SeekPosition,
    Info,
    TimestampScale,
    Duration,
    DateUtc,
    Title,
    MuxingApp,
    WritingApp,
    Cluster,
    Timestamp,
    PrevSize,
    SimpleBlock,
    BlockGroup,
    Block,
    BlockAdditions,
    BlockMore,
    BlockAddId,
    BlockAdditional,
    BlockDuration,
    ReferenceBlock,
    DiscardPadding,
    Tracks,
    TrackEntry,
    TrackNumber,
    TrackUid,
    TrackType,
    FlagEnabled,
    FlagDefault,
    FlagForced,
    FlagHearingImpaired,
    FlagVisualImpaired,
    FlagTextDescriptions,
    FlagOriginal,
    FlagCommentary,
    FlagLacing,
    DefaultDuration,
    Name,
    Language,
    LanguageBcp47,
    CodecId,
    CodecPrivate,
    CodecName,
    CodecDelay,
    SeekPreRoll,
    Video,
    FlagInterlaced,
    StereoMode,
    AlphaMode,
    PixelWidth,
    PixelHeight,
    PixelCropBottom,
    PixelCropTop,
    PixelCropLeft,
    PixelCropRight,
    DisplayWidth,
    DisplayHeight,
    DisplayUnit,
    AspectRatioType,
    Audio,
    SamplingFrequency,
    OutputSamplingFrequency,
    Channels,
    BitDepth,
    ContentEncodings,
    ContentEncoding,
    ContentEncodingOrder,
    ContentEncodingScope,
    ContentEncodingType,
    ContentCompression,
    ContentCompAlgo,
    ContentCompSettings,
    ContentEncryption,
    ContentEncAlgo,
    ContentEncKeyId,
    ContentEncAesSettings,
    AesSettingsCipherMode,
    Colour,
    MatrixCoefficients,
    BitsPerChannel,
    ChromaSubsamplingHorz,
    ChromaSubsamplingVert,
    CbSubsamplingHorz,
    CbSubsamplingVert,
    ChromaSitingHorz,
    ChromaSitingVert,
    Range,
    TransferCharacteristics,
    Primaries,
    MaxCll,
    MaxFall,
    MasteringMetadata,
    PrimaryRChromaticityX,
    PrimaryRChromaticityY,
    PrimaryGChromaticityX,
    PrimaryGChromaticityY,
    PrimaryBChromaticityX,
    PrimaryBChromaticityY,
    WhitePointChromaticityX,
    WhitePointChromaticityY,
    LuminanceMax,
    LuminanceMin,
    Cues,
    CuePoint,
    CueTime,
    CueTrackPositions,
    CueTrack,
    CueClusterPosition,
    CueRelativePosition,
    CueDuration,
    CueBlockNumber,
    Chapters,
    EditionEntry,
    ChapterAtom,
    ChapterUid,
    ChapterStringUid,
    ChapterTimeStart,
    ChapterTimeEnd,
    ChapterDisplay,
    ChapString,
    ChapLanguage,
    ChapLanguageIetf,
    ChapCountry,
    Tags,
    Tag,
    Targets,
    TargetTypeValue,
    TargetType,
    TagTrackUid,
    SimpleTag,
    TagName,
    TagLanguage,
    TagDefault,
    TagString,
    TagBinary,
}

static ELEMENT_ID_TO_TYPE: OnceLock<HashMap<ElementId, ElementType>> = OnceLock::new();

pub(crate) fn element_id_to_type(id: ElementId) -> ElementType {
    let mapping = ELEMENT_ID_TO_TYPE.get_or_init(|| {
        let mut m = HashMap::with_capacity(144);
        m.insert(ElementId::Ebml, ElementType::Master);
        m.insert(ElementId::EbmlVersion, ElementType::Unsigned);
        m.insert(ElementId::EbmlReadVersion, ElementType::Unsigned);
        m.insert(ElementId::EbmlMaxIdLength, ElementType::Unsigned);
        m.insert(ElementId::EbmlMaxSizeLength, ElementType::Unsigned);
        m.insert(ElementId::DocType, ElementType::String);
        m.insert(ElementId::DocTypeVersion, ElementType::Unsigned);
        m.insert(ElementId::DocTypeReadVersion, ElementType::Unsigned);
        m.insert(ElementId::Crc32, ElementType::Binary);
        m.insert(ElementId::Void, ElementType::Binary);
        m.insert(ElementId::Segment, ElementType::Master);
        m.insert(ElementId::SeekHead, ElementType::Master);
        m.insert(ElementId::Seek, ElementType::Master);
        // This is a binary in the spec, but we convert the IDs to u32.
        m.insert(ElementId::SeekId, ElementType::Unsigned);
        m.insert(ElementId::SeekPosition, ElementType::Unsigned);
        m.insert(ElementId::Info, ElementType::Master);
        m.insert(ElementId::TimestampScale, ElementType::Unsigned);
        m.insert(ElementId::Duration, ElementType::Float);
        m.insert(ElementId::DateUtc, ElementType::Date);
        m.insert(ElementId::Title, ElementType::String);
        m.insert(ElementId::MuxingApp, ElementType::String);
        m.insert(ElementId::WritingApp, ElementType::String);
        m.insert(ElementId::Cluster, ElementType::Master);
        m.insert(ElementId::Timestamp, ElementType::Unsigned);
        m.insert(ElementId::PrevSize, ElementType::Unsigned);
        m.insert(ElementId::SimpleBlock, ElementType::Binary);
        m.insert(ElementId::BlockGroup, ElementType::Master);
        m.insert(ElementId::Block, ElementType::Binary);
        m.insert(ElementId::BlockAdditions, ElementType::Master);
        m.insert(ElementId::BlockMore, ElementType::Master);
        m.insert(ElementId::BlockAddId, ElementType::Unsigned);
        m.insert(ElementId::BlockAdditional, ElementType::Binary);
        m.insert(ElementId::BlockDuration, ElementType::Unsigned);
        m.insert(ElementId::ReferenceBlock, ElementType::Signed);
        m.insert(ElementId::DiscardPadding, ElementType::Signed);
        m.insert(ElementId::Tracks, ElementType::Master);
        m.insert(ElementId::TrackEntry, ElementType::Master);
        m.insert(ElementId::TrackNumber, ElementType::Unsigned);
        m.insert(ElementId::TrackUid, ElementType::Unsigned);
        m.insert(ElementId::TrackType, ElementType::Unsigned);
        m.insert(ElementId::FlagEnabled, ElementType::Unsigned);
        m.insert(ElementId::FlagDefault, ElementType::Unsigned);
        m.insert(ElementId::FlagForced, ElementType::Unsigned);
        m.insert(ElementId::FlagHearingImpaired, ElementType::Unsigned);
        m.insert(ElementId::FlagVisualImpaired, ElementType::Unsigned);
        m.insert(ElementId::FlagTextDescriptions, ElementType::Unsigned);
        m.insert(ElementId::FlagOriginal, ElementType::Unsigned);
        m.insert(ElementId::FlagCommentary, ElementType::Unsigned);
        m.insert(ElementId::FlagLacing, ElementType::Unsigned);
        m.insert(ElementId::DefaultDuration, ElementType::Unsigned);
        m.insert(ElementId::Name, ElementType::String);
        m.insert(ElementId::Language, ElementType::String);
        m.insert(ElementId::LanguageBcp47, ElementType::String);
        m.insert(ElementId::CodecId, ElementType::String);
        m.insert(ElementId::CodecPrivate, ElementType::Binary);
        m.insert(ElementId::CodecName, ElementType::String);
        m.insert(ElementId::CodecDelay, ElementType::Unsigned);
        m.insert(ElementId::SeekPreRoll, ElementType::Unsigned);
        m.insert(ElementId::Video, ElementType::Master);
        m.insert(ElementId::FlagInterlaced, ElementType::Unsigned);
        m.insert(ElementId::StereoMode, ElementType::Unsigned);
        m.insert(ElementId::AlphaMode, ElementType::Unsigned);
        m.insert(ElementId::PixelWidth, ElementType::Unsigned);
        m.insert(ElementId::PixelHeight, ElementType::Unsigned);
        m.insert(ElementId::PixelCropBottom, ElementType::Unsigned);
        m.insert(ElementId::PixelCropTop, ElementType::Unsigned);
        m.insert(ElementId::PixelCropLeft, ElementType::Unsigned);
        m.insert(ElementId::PixelCropRight, ElementType::Unsigned);
        m.insert(ElementId::DisplayWidth, ElementType::Unsigned);
        m.insert(ElementId::DisplayHeight, ElementType::Unsigned);
        m.insert(ElementId::DisplayUnit, ElementType::Unsigned);
        m.insert(ElementId::AspectRatioType, ElementType::Unsigned);
        m.insert(ElementId::Audio, ElementType::Master);
        m.insert(ElementId::SamplingFrequency, ElementType::Float);
        m.insert(ElementId::OutputSamplingFrequency, ElementType::Float);
        m.insert(ElementId::Channels, ElementType::Unsigned);
        m.insert(ElementId::BitDepth, ElementType::Unsigned);
        m.insert(ElementId::ContentEncodings, ElementType::Master);
        m.insert(ElementId::ContentEncoding, ElementType::Master);
        m.insert(ElementId::ContentEncodingOrder, ElementType::Unsigned);
        m.insert(ElementId::ContentEncodingScope, ElementType::Unsigned);
        m.insert(ElementId::ContentEncodingType, ElementType::Unsigned);
        m.insert(ElementId::ContentCompression, ElementType::Master);
        m.insert(ElementId::ContentCompAlgo, ElementType::Unsigned);
        m.insert(ElementId::ContentCompSettings, ElementType::Binary);
        m.insert(ElementId::ContentEncryption, ElementType::Master);
        m.insert(ElementId::ContentEncAlgo, ElementType::Unsigned);
        m.insert(ElementId::ContentEncKeyId, ElementType::Unsigned);
        m.insert(ElementId::ContentEncAesSettings, ElementType::Master);
        m.insert(ElementId::AesSettingsCipherMode, ElementType::Unsigned);
        m.insert(ElementId::Colour, ElementType::Master);
        m.insert(ElementId::MatrixCoefficients, ElementType::Unsigned);
        m.insert(ElementId::BitsPerChannel, ElementType::Unsigned);
        m.insert(ElementId::ChromaSubsamplingHorz, ElementType::Unsigned);
        m.insert(ElementId::ChromaSubsamplingVert, ElementType::Unsigned);
        m.insert(ElementId::CbSubsamplingHorz, ElementType::Unsigned);
        m.insert(ElementId::CbSubsamplingVert, ElementType::Unsigned);
        m.insert(ElementId::ChromaSitingHorz, ElementType::Unsigned);
        m.insert(ElementId::ChromaSitingVert, ElementType::Unsigned);
        m.insert(ElementId::Range, ElementType::Unsigned);
        m.insert(ElementId::TransferCharacteristics, ElementType::Unsigned);
        m.insert(ElementId::Primaries, ElementType::Unsigned);
        m.insert(ElementId::MaxCll, ElementType::Unsigned);
        m.insert(ElementId::MaxFall, ElementType::Unsigned);
        m.insert(ElementId::MasteringMetadata, ElementType::Master);
        m.insert(ElementId::PrimaryRChromaticityX, ElementType::Float);
        m.insert(ElementId::PrimaryRChromaticityY, ElementType::Float);
        m.insert(ElementId::PrimaryGChromaticityX, ElementType::Float);
        m.insert(ElementId::PrimaryGChromaticityY, ElementType::Float);
        m.insert(ElementId::PrimaryBChromaticityX, ElementType::Float);
        m.insert(ElementId::PrimaryBChromaticityY, ElementType::Float);
        m.insert(ElementId::WhitePointChromaticityX, ElementType::Float);
        m.insert(ElementId::WhitePointChromaticityY, ElementType::Float);
        m.insert(ElementId::LuminanceMax, ElementType::Float);
        m.insert(ElementId::LuminanceMin, ElementType::Float);
        m.insert(ElementId::Cues, ElementType::Master);
        m.insert(ElementId::CuePoint, ElementType::Master);
        m.insert(ElementId::CueTime, ElementType::Unsigned);
        m.insert(ElementId::CueTrackPositions, ElementType::Master);
        m.insert(ElementId::CueTrack, ElementType::Unsigned);
        m.insert(ElementId::CueClusterPosition, ElementType::Unsigned);
        m.insert(ElementId::CueRelativePosition, ElementType::Unsigned);
        m.insert(ElementId::CueDuration, ElementType::Unsigned);
        m.insert(ElementId::CueBlockNumber, ElementType::Unsigned);
        m.insert(ElementId::Chapters, ElementType::Master);
        m.insert(ElementId::EditionEntry, ElementType::Master);
        m.insert(ElementId::ChapterAtom, ElementType::Master);
        m.insert(ElementId::ChapterUid, ElementType::Unsigned);
        m.insert(ElementId::ChapterStringUid, ElementType::String);
        m.insert(ElementId::ChapterTimeStart, ElementType::Unsigned);
        m.insert(ElementId::ChapterTimeEnd, ElementType::Unsigned);
        m.insert(ElementId::ChapterDisplay, ElementType::Master);
        m.insert(ElementId::ChapString, ElementType::String);
        m.insert(ElementId::ChapLanguage, ElementType::String);
        m.insert(ElementId::ChapLanguageIetf, ElementType::String);
        m.insert(ElementId::ChapCountry, ElementType::String);
        m.insert(ElementId::Tags, ElementType::Master);
        m.insert(ElementId::Tag, ElementType::Master);
        m.insert(ElementId::Targets, ElementType::Master);
        m.insert(ElementId::TargetTypeValue, ElementType::Unsigned);
        m.insert(ElementId::TargetType, ElementType::String);
        m.insert(ElementId::TagTrackUid, ElementType::Unsigned);
        m.insert(ElementId::SimpleTag, ElementType::Master);
        m.insert(ElementId::TagName, ElementType::String);
        m.insert(ElementId::TagLanguage, ElementType::String);
        m.insert(ElementId::TagDefault, ElementType::Unsigned);
        m.insert(ElementId::TagString, ElementType::String);
        m.insert(ElementId::TagBinary, ElementType::Binary);
        m
    });
    mapping.get(&id).copied().unwrap_or(ElementType::Unknown)
}

static ID_TO_ELEMENT_ID: OnceLock<HashMap<u32, ElementId>> = OnceLock::new();

pub(crate) fn id_to_element_id(id: u32) -> ElementId {
    let mapping = ID_TO_ELEMENT_ID.get_or_init(|| {
        let mut m = HashMap::with_capacity(144);
        m.insert(0x1A45DFA3, ElementId::Ebml);
        m.insert(0x4286, ElementId::EbmlVersion);
        m.insert(0x42F7, ElementId::EbmlReadVersion);
        m.insert(0x42F2, ElementId::EbmlMaxIdLength);
        m.insert(0x42F3, ElementId::EbmlMaxSizeLength);
        m.insert(0x4282, ElementId::DocType);
        m.insert(0x4287, ElementId::DocTypeVersion);
        m.insert(0x4285, ElementId::DocTypeReadVersion);
        m.insert(0xBF, ElementId::Crc32);
        m.insert(0xEC, ElementId::Void);
        m.insert(0x18538067, ElementId::Segment);
        m.insert(0x114D9B74, ElementId::SeekHead);
        m.insert(0x4DBB, ElementId::Seek);
        m.insert(0x53AB, ElementId::SeekId);
        m.insert(0x53AC, ElementId::SeekPosition);
        m.insert(0x1549A966, ElementId::Info);
        m.insert(0x2AD7B1, ElementId::TimestampScale);
        m.insert(0x4489, ElementId::Duration);
        m.insert(0x4461, ElementId::DateUtc);
        m.insert(0x7BA9, ElementId::Title);
        m.insert(0x4D80, ElementId::MuxingApp);
        m.insert(0x5741, ElementId::WritingApp);
        m.insert(0x1F43B675, ElementId::Cluster);
        m.insert(0xE7, ElementId::Timestamp);
        m.insert(0xAB, ElementId::PrevSize);
        m.insert(0xA3, ElementId::SimpleBlock);
        m.insert(0xA0, ElementId::BlockGroup);
        m.insert(0xA1, ElementId::Block);
        m.insert(0x75A1, ElementId::BlockAdditions);
        m.insert(0xA6, ElementId::BlockMore);
        m.insert(0xEE, ElementId::BlockAddId);
        m.insert(0xA5, ElementId::BlockAdditional);
        m.insert(0x9B, ElementId::BlockDuration);
        m.insert(0xFB, ElementId::ReferenceBlock);
        m.insert(0x75A2, ElementId::DiscardPadding);
        m.insert(0x1654AE6B, ElementId::Tracks);
        m.insert(0xAE, ElementId::TrackEntry);
        m.insert(0xD7, ElementId::TrackNumber);
        m.insert(0x73C5, ElementId::TrackUid);
        m.insert(0x83, ElementId::TrackType);
        m.insert(0xB9, ElementId::FlagEnabled);
        m.insert(0x88, ElementId::FlagDefault);
        m.insert(0x55AA, ElementId::FlagForced);
        m.insert(0x55AB, ElementId::FlagHearingImpaired);
        m.insert(0x55AC, ElementId::FlagVisualImpaired);
        m.insert(0x55AD, ElementId::FlagTextDescriptions);
        m.insert(0x55AE, ElementId::FlagOriginal);
        m.insert(0x55AF, ElementId::FlagCommentary);
        m.insert(0x9C, ElementId::FlagLacing);
        m.insert(0x23E383, ElementId::DefaultDuration);
        m.insert(0x536E, ElementId::Name);
        m.insert(0x22B59C, ElementId::Language);
        m.insert(0x22B59D, ElementId::LanguageBcp47);
        m.insert(0x86, ElementId::CodecId);
        m.insert(0x63A2, ElementId::CodecPrivate);
        m.insert(0x258688, ElementId::CodecName);
        m.insert(0x56AA, ElementId::CodecDelay);
        m.insert(0x56BB, ElementId::SeekPreRoll);
        m.insert(0xE0, ElementId::Video);
        m.insert(0x9A, ElementId::FlagInterlaced);
        m.insert(0x53B8, ElementId::StereoMode);
        m.insert(0x53C0, ElementId::AlphaMode);
        m.insert(0xB0, ElementId::PixelWidth);
        m.insert(0xBA, ElementId::PixelHeight);
        m.insert(0x54AA, ElementId::PixelCropBottom);
        m.insert(0x54BB, ElementId::PixelCropTop);
        m.insert(0x54CC, ElementId::PixelCropLeft);
        m.insert(0x54DD, ElementId::PixelCropRight);
        m.insert(0x54B0, ElementId::DisplayWidth);
        m.insert(0x54BA, ElementId::DisplayHeight);
        m.insert(0x54B2, ElementId::DisplayUnit);
        m.insert(0x54B3, ElementId::AspectRatioType);
        m.insert(0xE1, ElementId::Audio);
        m.insert(0xB5, ElementId::SamplingFrequency);
        m.insert(0x78B5, ElementId::OutputSamplingFrequency);
        m.insert(0x9F, ElementId::Channels);
        m.insert(0x6264, ElementId::BitDepth);
        m.insert(0x6D80, ElementId::ContentEncodings);
        m.insert(0x6240, ElementId::ContentEncoding);
        m.insert(0x5031, ElementId::ContentEncodingOrder);
        m.insert(0x5032, ElementId::ContentEncodingScope);
        m.insert(0x5033, ElementId::ContentEncodingType);
        m.insert(0x5034, ElementId::ContentCompression);
        m.insert(0x4254, ElementId::ContentCompAlgo);
        m.insert(0x4255, ElementId::ContentCompSettings);
        m.insert(0x5035, ElementId::ContentEncryption);
        m.insert(0x47E1, ElementId::ContentEncAlgo);
        m.insert(0x47E2, ElementId::ContentEncKeyId);
        m.insert(0x47E7, ElementId::ContentEncAesSettings);
        m.insert(0x47E8, ElementId::AesSettingsCipherMode);
        m.insert(0x55B0, ElementId::Colour);
        m.insert(0x55B1, ElementId::MatrixCoefficients);
        m.insert(0x55B2, ElementId::BitsPerChannel);
        m.insert(0x55B3, ElementId::ChromaSubsamplingHorz);
        m.insert(0x55B4, ElementId::ChromaSubsamplingVert);
        m.insert(0x55B5, ElementId::CbSubsamplingHorz);
        m.insert(0x55B6, ElementId::CbSubsamplingVert);
        m.insert(0x55B7, ElementId::ChromaSitingHorz);
        m.insert(0x55B8, ElementId::ChromaSitingVert);
        m.insert(0x55B9, ElementId::Range);
        m.insert(0x55BA, ElementId::TransferCharacteristics);
        m.insert(0x55BB, ElementId::Primaries);
        m.insert(0x55BC, ElementId::MaxCll);
        m.insert(0x55BD, ElementId::MaxFall);
        m.insert(0x55D0, ElementId::MasteringMetadata);
        m.insert(0x55D1, ElementId::PrimaryRChromaticityX);
        m.insert(0x55D2, ElementId::PrimaryRChromaticityY);
        m.insert(0x55D3, ElementId::PrimaryGChromaticityX);
        m.insert(0x55D4, ElementId::PrimaryGChromaticityY);
        m.insert(0x55D5, ElementId::PrimaryBChromaticityX);
        m.insert(0x55D6, ElementId::PrimaryBChromaticityY);
        m.insert(0x55D7, ElementId::WhitePointChromaticityX);
        m.insert(0x55D8, ElementId::WhitePointChromaticityY);
        m.insert(0x55D9, ElementId::LuminanceMax);
        m.insert(0x55DA, ElementId::LuminanceMin);
        m.insert(0x1C53BB6B, ElementId::Cues);
        m.insert(0xBB, ElementId::CuePoint);
        m.insert(0xB3, ElementId::CueTime);
        m.insert(0xB7, ElementId::CueTrackPositions);
        m.insert(0xF7, ElementId::CueTrack);
        m.insert(0xF1, ElementId::CueClusterPosition);
        m.insert(0xF0, ElementId::CueRelativePosition);
        m.insert(0xB2, ElementId::CueDuration);
        m.insert(0x5378, ElementId::CueBlockNumber);
        m.insert(0x1043A770, ElementId::Chapters);
        m.insert(0x45B9, ElementId::EditionEntry);
        m.insert(0xB6, ElementId::ChapterAtom);
        m.insert(0x73C4, ElementId::ChapterUid);
        m.insert(0x5654, ElementId::ChapterStringUid);
        m.insert(0x91, ElementId::ChapterTimeStart);
        m.insert(0x92, ElementId::ChapterTimeEnd);
        m.insert(0x80, ElementId::ChapterDisplay);
        m.insert(0x85, ElementId::ChapString);
        m.insert(0x437C, ElementId::ChapLanguage);
        m.insert(0x437D, ElementId::ChapLanguageIetf);
        m.insert(0x437E, ElementId::ChapCountry);
        m.insert(0x1254C367, ElementId::Tags);
        m.insert(0x7373, ElementId::Tag);
        m.insert(0x63C0, ElementId::Targets);
        m.insert(0x68CA, ElementId::TargetTypeValue);
        m.insert(0x63CA, ElementId::TargetType);
        m.insert(0x63C5, ElementId::TagTrackUid);
        m.insert(0x67C8, ElementId::SimpleTag);
        m.insert(0x45A3, ElementId::TagName);
        m.insert(0x447A, ElementId::TagLanguage);
        m.insert(0x4484, ElementId::TagDefault);
        m.insert(0x4487, ElementId::TagString);
        m.insert(0x4485, ElementId::TagBinary);
        m
    });
    mapping.get(&id).copied().unwrap_or(ElementId::Unknown)
}

/// The types of elements an EBML file can have.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum ElementType {
    /// Unknown element.
    Unknown,
    /// An element that contains other elements as children.
    Master,
    /// Unsigned integer.
    Unsigned,
    /// Signed integer.
    Signed,
    /// Float.
    Float,
    /// Date.
    Date,
    /// String.
    String,
    /// Binary.
    Binary,
}
