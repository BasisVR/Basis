#![warn(missing_docs)]
#![deny(clippy::as_conversions)]
#![deny(clippy::panic)]
#![forbid(unsafe_code)]
#![forbid(clippy::unwrap_used)]
//! A demuxer that can demux Matroska and WebM container files.
//!
//! # Example:
//! ```no_run
//! use std::fs::File;
//! use matroska_demuxer::*;
//!
//! let file = File::open("test.mkv").unwrap();
//! let mut mkv = MatroskaFile::open(file).unwrap();
//! let video_track = mkv
//!     .tracks()
//!     .iter()
//!     .find(|t| t.track_type() == TrackType::Video)
//!     .map(|t| t.track_number().get())
//!     .unwrap();
//!
//! let mut frame = Frame::default();
//! while mkv.next_frame(&mut frame).unwrap() {
//!     if frame.track == video_track {
//!         dbg!("video frame found");
//!     }
//! }
//! ```

use std::{
    collections::{HashMap, VecDeque},
    convert::TryInto,
    error::Error,
    io::{Read, Seek, SeekFrom},
    num::NonZeroU64,
};

use ebml::{
    check_size_limit, collect_children, expect_master, find_bool_or, find_custom_type,
    find_float_or, find_nonzero, find_nonzero_or, find_string, find_unsigned, find_unsigned_or,
    next_element, parse_children_at_offset, parse_element_header, remaining_len, try_find_binary,
    try_find_custom_type, try_find_custom_type_or, try_find_date, try_find_float, try_find_nonzero,
    try_find_string, try_find_unsigned, try_parse_child, try_parse_children, ElementData,
    ParsableElement,
};
pub use element_id::ElementId;
pub use enums::*;
pub use error::DemuxError;

use crate::element_id::id_to_element_id;
use crate::{
    block::{parse_laced_frames, probe_block_timestamp, LacedFrame},
    ebml::{parse_child, try_find_bool},
};

mod block;
mod ebml;
pub(crate) mod element_id;
mod enums;
mod error;

/// The doc type version this demuxer supports.
const DEMUXER_DOC_TYPE_VERSION: u64 = 4;

type Result<T> = std::result::Result<T, DemuxError>;

/// A data frame inside the Matroska container.
#[derive(Clone, Debug, Default)]
pub struct Frame {
    /// The ID of the track.
    pub track: u64,
    /// The timestamp of the frame.
    pub timestamp: u64,
    /// The data of the frame.
    pub data: Vec<u8>,
    /// Set when the codec should decode this frame but not display it.
    pub is_invisible: bool,
    /// Block marked this frame as a keyframe.
    ///
    /// Only set for files that use simple blocks.
    pub is_keyframe: Option<bool>,
    /// Set when the frame can be discarded during playing if needed.
    ///
    /// Only set for files that use simple blocks.
    pub is_discardable: Option<bool>,
    /// The duration associated with the frame.
    ///
    /// Used when there is a break in a track like for subtitle tracks.
    pub duration: Option<u64>,
}

impl From<Vec<u8>> for Frame {
    fn from(data: Vec<u8>) -> Self {
        Self {
            data,
            ..Frame::default()
        }
    }
}

/// The EBML header of the file.
#[derive(Clone, Debug)]
pub struct EbmlHeader {
    version: Option<u64>,
    read_version: Option<u64>,
    max_id_length: u64,
    max_size_length: u64,
    doc_type: String,
    doc_type_version: u64,
    doc_type_read_version: u64,
}

impl<R: Read + Seek> ParsableElement<R> for EbmlHeader {
    type Output = Self;

    fn new(_r: &mut R, fields: &[(ElementId, ElementData)]) -> Result<Self> {
        let version = try_find_unsigned(fields, ElementId::EbmlVersion)?;
        let read_version = try_find_unsigned(fields, ElementId::EbmlReadVersion)?;
        let max_id_length = find_unsigned_or(fields, ElementId::EbmlMaxIdLength, 4)?;
        let max_size_length = find_unsigned_or(fields, ElementId::EbmlMaxSizeLength, 8)?;
        let doc_type = find_string(fields, ElementId::DocType)?;
        let doc_type_version = find_unsigned(fields, ElementId::DocTypeVersion)?;
        let doc_type_read_version = find_unsigned(fields, ElementId::DocTypeReadVersion)?;

        // The spec allows Null-terminated strings.
        let trimmed_doc_type = doc_type.trim_end_matches('\0');

        if trimmed_doc_type != "matroska" && trimmed_doc_type != "webm" {
            return Err(DemuxError::InvalidEbmlHeader(format!(
                "unsupported DocType: {}",
                doc_type
            )));
        }

        if doc_type_read_version >= DEMUXER_DOC_TYPE_VERSION {
            return Err(DemuxError::InvalidEbmlHeader(format!(
                "unsupported DocTypeReadVersion: {}",
                doc_type_read_version
            )));
        }

        if max_id_length > 4 {
            return Err(DemuxError::InvalidEbmlHeader(format!(
                "unsupported MaxIdLength: {}",
                max_id_length
            )));
        }

        if max_size_length > 8 {
            return Err(DemuxError::InvalidEbmlHeader(format!(
                "unsupported MaxSizeLength: {}",
                max_size_length
            )));
        }

        Ok(Self {
            version,
            read_version,
            max_id_length,
            max_size_length,
            doc_type,
            doc_type_version,
            doc_type_read_version,
        })
    }
}

impl EbmlHeader {
    /// The EBML version used to create the file.
    pub fn version(&self) -> Option<u64> {
        self.version
    }

    /// The minimum EBML version a parser has to support to read this file.
    pub fn read_version(&self) -> Option<u64> {
        self.read_version
    }

    /// The maximum length of the IDs you'll find in this file (4 or less in Matroska).
    pub fn max_id_length(&self) -> u64 {
        self.max_id_length
    }

    /// The maximum length of the sizes you'll find in this file (8 or less in Matroska).
    pub fn max_size_length(&self) -> u64 {
        self.max_size_length
    }

    /// A string that describes the type of document that follows this EBML header ('matroska' / 'webm').
    pub fn doc_type(&self) -> &str {
        &self.doc_type
    }

    /// The version of DocType interpreter used to create the file.
    pub fn doc_type_version(&self) -> u64 {
        self.doc_type_version
    }

    /// The minimum DocType version an interpreter has to support to read this file.
    pub fn doc_type_read_version(&self) -> u64 {
        self.doc_type_read_version
    }
}

/// Contains general information about the segment.
#[derive(Clone, Debug)]
pub struct Info {
    timestamp_scale: NonZeroU64,
    duration: Option<f64>,
    date_utc: Option<i64>,
    title: Option<String>,
    muxing_app: String,
    writing_app: String,
}

impl<R: Read + Seek> ParsableElement<R> for Info {
    type Output = Self;

    fn new(_r: &mut R, fields: &[(ElementId, ElementData)]) -> Result<Self> {
        let timestamp_scale = find_nonzero_or(fields, ElementId::TimestampScale, 1000000)?;
        let duration = try_find_float(fields, ElementId::Duration)?;
        let date_utc = try_find_date(fields, ElementId::DateUtc)?;
        let title = try_find_string(fields, ElementId::Title)?;
        let muxing_app = find_string(fields, ElementId::MuxingApp)?;
        let writing_app = find_string(fields, ElementId::WritingApp)?;

        if let Some(duration) = duration {
            if duration < 0.0 {
                return Err(DemuxError::PositiveValueIsNotPositive);
            }
        }

        Ok(Self {
            timestamp_scale,
            duration,
            date_utc,
            title,
            muxing_app,
            writing_app,
        })
    }
}

impl Info {
    /// Timestamp scale in nanoseconds (1_000_000 means all timestamps in the Segment are expressed in milliseconds).
    pub fn timestamp_scale(&self) -> NonZeroU64 {
        self.timestamp_scale
    }

    /// Duration of the Segment in nanoseconds based on TimestampScale.
    pub fn duration(&self) -> Option<f64> {
        self.duration
    }

    /// The date and time that the Segment was created by the muxing application or library.
    pub fn date_utc(&self) -> Option<i64> {
        self.date_utc
    }

    /// General name of the Segment.
    pub fn title(&self) -> Option<&str> {
        self.title.as_deref()
    }

    /// Muxing application or library.
    pub fn muxing_app(&self) -> &str {
        &self.muxing_app
    }

    /// Writing  application.
    pub fn writing_app(&self) -> &str {
        &self.writing_app
    }
}

/// Describes a track.
#[derive(Clone, Debug)]
pub struct TrackEntry {
    track_number: NonZeroU64,
    track_uid: NonZeroU64,
    track_type: TrackType,
    flag_enabled: bool,
    flag_commentary: bool,
    flag_default: bool,
    flag_forced: bool,
    flag_hearing_impaired: bool,
    flag_lacing: bool,
    flag_original: bool,
    flag_visual_impaired: bool,
    default_duration: Option<NonZeroU64>,
    name: Option<String>,
    language: Option<String>,
    language_bcp47: Option<String>,
    codec_id: String,
    codec_private: Option<Vec<u8>>,
    codec_name: Option<String>,
    codec_delay: Option<u64>,
    seek_pre_roll: Option<u64>,
    audio: Option<Audio>,
    video: Option<Video>,
    content_encodings: Option<Vec<ContentEncoding>>,
}

impl<R: Read + Seek> ParsableElement<R> for TrackEntry {
    type Output = Self;

    fn new(r: &mut R, fields: &[(ElementId, ElementData)]) -> Result<Self> {
        let track_number = find_nonzero(fields, ElementId::TrackNumber)?;
        let track_uid = find_nonzero(fields, ElementId::TrackUid)?;
        let track_type = find_custom_type(fields, ElementId::TrackType)?;
        let flag_enabled = find_bool_or(fields, ElementId::FlagEnabled, true)?;
        let flag_commentary = find_bool_or(fields, ElementId::FlagCommentary, false)?;
        let flag_default = find_bool_or(fields, ElementId::FlagDefault, true)?;
        let flag_forced = find_bool_or(fields, ElementId::FlagForced, false)?;
        let flag_hearing_impaired = find_bool_or(fields, ElementId::FlagHearingImpaired, false)?;
        let flag_lacing = find_bool_or(fields, ElementId::FlagLacing, false)?;
        let flag_original = find_bool_or(fields, ElementId::FlagOriginal, false)?;
        let flag_visual_impaired = find_bool_or(fields, ElementId::FlagVisualImpaired, false)?;
        let default_duration = try_find_nonzero(fields, ElementId::DefaultDuration)?;
        let name = try_find_string(fields, ElementId::Name)?;
        let language = try_find_string(fields, ElementId::Language)?;
        let language_bcp47 = try_find_string(fields, ElementId::LanguageBcp47)?;
        let codec_id = find_string(fields, ElementId::CodecId)?;
        let codec_private = try_find_binary(r, fields, ElementId::CodecPrivate)?;
        let codec_name = try_find_string(fields, ElementId::CodecName)?;
        let codec_delay = try_find_unsigned(fields, ElementId::CodecDelay)?;
        let seek_pre_roll = try_find_unsigned(fields, ElementId::SeekPreRoll)?;

        let audio = try_parse_child::<_, Audio>(r, fields, ElementId::Audio)?;
        let video = try_parse_child::<_, Video>(r, fields, ElementId::Video)?;

        let content_encodings = try_parse_children::<_, ContentEncoding>(
            r,
            fields,
            ElementId::ContentEncodings,
            ElementId::ContentEncoding,
        )?;

        Ok(Self {
            track_number,
            track_uid,
            track_type,
            flag_enabled,
            flag_commentary,
            flag_default,
            flag_forced,
            flag_hearing_impaired,
            flag_lacing,
            flag_original,
            flag_visual_impaired,
            default_duration,
            name,
            language,
            language_bcp47,
            codec_id,
            codec_private,
            codec_name,
            codec_delay,
            seek_pre_roll,
            audio,
            video,
            content_encodings,
        })
    }
}

impl TrackEntry {
    /// The track number as used in the block header.
    pub fn track_number(&self) -> NonZeroU64 {
        self.track_number
    }

    /// A unique ID to identify the track.
    pub fn track_uid(&self) -> NonZeroU64 {
        self.track_uid
    }

    /// The type of the track.
    pub fn track_type(&self) -> TrackType {
        self.track_type
    }

    /// Indicates if a track is usable. It is possible to turn a not usable track
    /// into a usable track using chapter codecs or control tracks.
    pub fn flag_enabled(&self) -> bool {
        self.flag_enabled
    }

    /// Set if that track (audio, video or subs) should be eligible
    /// for automatic selection by the player.
    pub fn flag_default(&self) -> bool {
        self.flag_default
    }

    /// Set if and only if that track contains commentary.
    pub fn flag_commentary(&self) -> bool {
        self.flag_commentary
    }

    /// Applies only to subtitles. Set if that track should be eligible for automatic selection
    /// by the player if it matches the user's language preference, even if the user's preferences
    /// would normally not enable subtitles with the selected audio track.
    pub fn flag_forced(&self) -> bool {
        self.flag_forced
    }

    /// Set if and only if that track is suitable for users with hearing impairments.
    pub fn flag_hearing_impaired(&self) -> bool {
        self.flag_hearing_impaired
    }

    /// Indicates if the track may contain blocks using lacing.
    pub fn flag_lacing(&self) -> bool {
        self.flag_lacing
    }

    /// Set if and only if that track is in the content's original language.
    pub fn flag_original(&self) -> bool {
        self.flag_original
    }

    /// Set if and only if that track is suitable for users with visual impairments.
    pub fn flag_visual_impaired(&self) -> bool {
        self.flag_visual_impaired
    }

    /// Number of nanoseconds (not scaled via TimestampScale) per frame (one Element put into a (Simple)Block).
    pub fn default_duration(&self) -> Option<NonZeroU64> {
        self.default_duration
    }

    /// A human-readable track name.
    pub fn name(&self) -> Option<&str> {
        self.name.as_deref()
    }

    /// Specifies the language of the track.
    ///
    /// Can be either the 3 letters bibliographic ISO-639-2 form (like `fre` for french), or such a
    /// language code followed by a dash and a country code for specialities in languages
    /// (like `fre-ca` for Canadian French).
    pub fn language(&self) -> Option<&str> {
        self.language.as_deref()
    }

    /// Specifies the language of the track as IETF language tag.
    ///
    /// Starting with Matroska 4 the BCF47 language code is recommended instead of the regular
    /// language element used in Matroska 1-3. If a BCF47 language element is present the regular
    /// language element must be ignored.
    pub fn language_bcp47(&self) -> Option<&str> {
        self.language_bcp47.as_deref()
    }

    /// An ID corresponding to the codec.
    pub fn codec_id(&self) -> &str {
        &self.codec_id
    }

    /// Private data only known to the codec.
    pub fn codec_private(&self) -> Option<&[u8]> {
        self.codec_private.as_deref()
    }

    /// A human-readable string specifying the codec.
    pub fn codec_name(&self) -> Option<&str> {
        self.codec_name.as_deref()
    }

    /// CodecDelay is ehe codec-built-in delay in nanoseconds.
    /// This value must be subtracted from each block timestamp in order to get the actual timestamp.
    pub fn codec_delay(&self) -> Option<u64> {
        self.codec_delay
    }

    /// After a discontinuity, SeekPreRoll is the duration in nanoseconds of the data the decoder
    /// must decode before the decoded data is valid.
    pub fn seek_pre_roll(&self) -> Option<u64> {
        self.seek_pre_roll
    }

    /// Video settings.
    pub fn video(&self) -> Option<&Video> {
        self.video.as_ref()
    }

    /// Audio settings.
    pub fn audio(&self) -> Option<&Audio> {
        self.audio.as_ref()
    }

    /// Settings for several content encoding mechanisms like compression or encryption.
    pub fn content_encodings(&self) -> Option<&[ContentEncoding]> {
        match &self.content_encodings {
            None => None,
            Some(content_encodings) => Some(content_encodings),
        }
    }
}

/// Audio settings.
#[derive(Clone, Debug)]
pub struct Audio {
    sampling_frequency: f64,
    output_sampling_frequency: Option<f64>,
    channels: NonZeroU64,
    bit_depth: Option<NonZeroU64>,
}

impl<R: Read + Seek> ParsableElement<R> for Audio {
    type Output = Self;

    fn new(_r: &mut R, fields: &[(ElementId, ElementData)]) -> Result<Self> {
        let sampling_frequency = find_float_or(fields, ElementId::SamplingFrequency, 8000.0)?;
        let output_sampling_frequency = try_find_float(fields, ElementId::OutputSamplingFrequency)?;
        let channels = find_nonzero_or(fields, ElementId::Channels, 1)?;
        let bit_depth = try_find_nonzero(fields, ElementId::BitDepth)?;

        if sampling_frequency < 0.0 {
            return Err(DemuxError::PositiveValueIsNotPositive);
        }

        if let Some(output_sampling_frequency) = output_sampling_frequency {
            if output_sampling_frequency < 0.0 {
                return Err(DemuxError::PositiveValueIsNotPositive);
            }
        }

        Ok(Audio {
            sampling_frequency,
            output_sampling_frequency,
            channels,
            bit_depth,
        })
    }
}

impl Audio {
    /// Sampling frequency in Hz.
    pub fn sampling_frequency(&self) -> f64 {
        self.sampling_frequency
    }

    /// Real output sampling frequency in Hz.
    pub fn output_sampling_frequency(&self) -> Option<f64> {
        self.output_sampling_frequency
    }

    /// Numbers of channels in the track.
    pub fn channels(&self) -> NonZeroU64 {
        self.channels
    }

    /// Bits per sample.
    pub fn bit_depth(&self) -> Option<NonZeroU64> {
        self.bit_depth
    }
}

/// Video settings.
#[derive(Clone, Debug)]
pub struct Video {
    flag_interlaced: FlagInterlaced,
    stereo_mode: Option<StereoMode>,
    alpha_mode: Option<u64>,
    pixel_width: NonZeroU64,
    pixel_height: NonZeroU64,
    pixel_crop_bottom: Option<u64>,
    pixel_crop_top: Option<u64>,
    pixel_crop_left: Option<u64>,
    pixel_crop_right: Option<u64>,
    display_width: Option<NonZeroU64>,
    display_height: Option<NonZeroU64>,
    display_unit: Option<DisplayUnit>,
    aspect_ratio_type: Option<AspectRatioType>,
    colour: Option<Colour>,
}

impl<R: Read + Seek> ParsableElement<R> for Video {
    type Output = Self;

    fn new(r: &mut R, fields: &[(ElementId, ElementData)]) -> Result<Self> {
        let flag_interlaced =
            try_find_custom_type_or(fields, ElementId::FlagInterlaced, FlagInterlaced::Unknown)?;
        let stereo_mode = try_find_custom_type(fields, ElementId::StereoMode)?;
        let alpha_mode = try_find_unsigned(fields, ElementId::AlphaMode)?;
        let pixel_width = find_nonzero(fields, ElementId::PixelWidth)?;
        let pixel_height = find_nonzero(fields, ElementId::PixelHeight)?;
        let pixel_crop_bottom = try_find_unsigned(fields, ElementId::PixelCropBottom)?;
        let pixel_crop_top = try_find_unsigned(fields, ElementId::PixelCropTop)?;
        let pixel_crop_left = try_find_unsigned(fields, ElementId::PixelCropLeft)?;
        let pixel_crop_right = try_find_unsigned(fields, ElementId::PixelCropRight)?;
        let display_width = try_find_nonzero(fields, ElementId::DisplayWidth)?;
        let display_height = try_find_nonzero(fields, ElementId::DisplayHeight)?;
        let display_unit = try_find_custom_type(fields, ElementId::DisplayUnit)?;
        let aspect_ratio_type = try_find_custom_type(fields, ElementId::AspectRatioType)?;
        let colour = try_parse_child::<_, Colour>(r, fields, ElementId::Colour)?;

        Ok(Self {
            flag_interlaced,
            stereo_mode,
            alpha_mode,
            pixel_width,
            pixel_height,
            pixel_crop_bottom,
            pixel_crop_top,
            pixel_crop_left,
            pixel_crop_right,
            display_width,
            display_height,
            display_unit,
            aspect_ratio_type,
            colour,
        })
    }
}

impl Video {
    /// A flag to declare if the video is known to be progressive, or interlaced,
    /// and if applicable to declare details about the interlacement.
    pub fn flag_interlaced(&self) -> FlagInterlaced {
        self.flag_interlaced
    }

    /// Stereo-3D video mode.
    pub fn stereo_mode(&self) -> Option<StereoMode> {
        self.stereo_mode
    }

    /// Alpha Video Mode. Presence of this Element indicates that the
    /// BlockAdditional Element could contain Alpha data.
    pub fn alpha_mode(&self) -> Option<u64> {
        self.alpha_mode
    }

    /// Width of the encoded video frames in pixels.
    pub fn pixel_width(&self) -> NonZeroU64 {
        self.pixel_width
    }

    /// Height of the encoded video frames in pixels.
    pub fn pixel_height(&self) -> NonZeroU64 {
        self.pixel_height
    }

    /// The number of video pixels to remove at the bottom of the image.
    pub fn pixel_crop_bottom(&self) -> Option<u64> {
        self.pixel_crop_bottom
    }

    /// The number of video pixels to remove at the top of the image.
    pub fn pixel_crop_top(&self) -> Option<u64> {
        self.pixel_crop_top
    }

    /// The number of video pixels to remove on the left of the image.
    pub fn pixel_crop_left(&self) -> Option<u64> {
        self.pixel_crop_left
    }

    /// The number of video pixels to remove on the right of the image.
    pub fn pixel_crop_right(&self) -> Option<u64> {
        self.pixel_crop_right
    }

    /// Width of the video frames to display.
    /// Applies to the video frame after cropping (PixelCrop* Elements).
    pub fn display_width(&self) -> Option<NonZeroU64> {
        self.display_width
    }

    /// Height of the video frames to display.
    /// Applies to the video frame after cropping (PixelCrop* Elements).
    pub fn display_height(&self) -> Option<NonZeroU64> {
        self.display_height
    }

    /// How DisplayWidth & DisplayHeight are interpreted.
    pub fn display_unit(&self) -> Option<DisplayUnit> {
        self.display_unit
    }

    /// Specify the possible modifications to the aspect ratio.
    pub fn aspect_ratio_type(&self) -> Option<AspectRatioType> {
        self.aspect_ratio_type
    }

    /// Settings describing the colour format.
    pub fn colour(&self) -> Option<&Colour> {
        self.colour.as_ref()
    }
}

/// Settings describing the colour format.
#[derive(Clone, Debug)]
pub struct Colour {
    matrix_coefficients: Option<MatrixCoefficients>,
    bits_per_channel: Option<u64>,
    chroma_subsampling_horz: Option<u64>,
    chroma_subsampling_vert: Option<u64>,
    cb_subsampling_horz: Option<u64>,
    cb_subsampling_vert: Option<u64>,
    chroma_sitting_horz: Option<ChromaSitingHorz>,
    chroma_sitting_vert: Option<ChromaSitingVert>,
    range: Option<Range>,
    transfer_characteristics: Option<TransferCharacteristics>,
    primaries: Option<Primaries>,
    max_cll: Option<u64>,
    max_fall: Option<u64>,
    mastering_metadata: Option<MasteringMetadata>,
}

impl<R: Read + Seek> ParsableElement<R> for Colour {
    type Output = Self;

    fn new(r: &mut R, fields: &[(ElementId, ElementData)]) -> Result<Self> {
        let matrix_coefficients = try_find_custom_type(fields, ElementId::MatrixCoefficients)?;
        let bits_per_channel = try_find_unsigned(fields, ElementId::BitsPerChannel)?;
        let chroma_subsampling_horz = try_find_unsigned(fields, ElementId::ChromaSubsamplingHorz)?;
        let chroma_subsampling_vert = try_find_unsigned(fields, ElementId::ChromaSubsamplingVert)?;
        let cb_subsampling_horz = try_find_unsigned(fields, ElementId::CbSubsamplingHorz)?;
        let cb_subsampling_vert = try_find_unsigned(fields, ElementId::CbSubsamplingVert)?;
        let chroma_sitting_horz = try_find_custom_type(fields, ElementId::ChromaSitingHorz)?;
        let chroma_sitting_vert = try_find_custom_type(fields, ElementId::ChromaSitingVert)?;
        let range = try_find_custom_type(fields, ElementId::Range)?;
        let transfer_characteristics =
            try_find_custom_type(fields, ElementId::TransferCharacteristics)?;
        let primaries = try_find_custom_type(fields, ElementId::Primaries)?;
        let max_cll = try_find_unsigned(fields, ElementId::MatrixCoefficients)?;
        let max_fall = try_find_unsigned(fields, ElementId::MatrixCoefficients)?;
        let mastering_metadata =
            try_parse_child::<_, MasteringMetadata>(r, fields, ElementId::MasteringMetadata)?;

        Ok(Self {
            matrix_coefficients,
            bits_per_channel,
            chroma_subsampling_horz,
            chroma_subsampling_vert,
            cb_subsampling_horz,
            cb_subsampling_vert,
            chroma_sitting_horz,
            chroma_sitting_vert,
            range,
            transfer_characteristics,
            primaries,
            max_cll,
            max_fall,
            mastering_metadata,
        })
    }
}

impl Colour {
    /// The matrix coefficients of the video used to derive luma and chroma values
    /// from red, green, and blue color primaries.
    pub fn matrix_coefficients(&self) -> Option<MatrixCoefficients> {
        self.matrix_coefficients
    }
    /// Number of decoded bits per channel.
    pub fn bits_per_channel(&self) -> Option<u64> {
        self.bits_per_channel
    }

    /// The amount of pixels to remove in the Cr and Cb channels
    /// for every pixel not removed horizontally.
    pub fn chroma_subsampling_horz(&self) -> Option<u64> {
        self.chroma_subsampling_horz
    }

    /// The amount of pixels to remove in the Cr and Cb channels
    /// for every pixel not removed vertically.
    pub fn chroma_subsampling_vert(&self) -> Option<u64> {
        self.chroma_subsampling_vert
    }

    /// The amount of pixels to remove in the Cb channel for every pixel not removed horizontally.
    pub fn cb_subsampling_horz(&self) -> Option<u64> {
        self.cb_subsampling_horz
    }

    /// The amount of pixels to remove in the Cb channel for every pixel not removed vertically.
    pub fn cb_subsampling_vert(&self) -> Option<u64> {
        self.cb_subsampling_vert
    }

    /// How chroma is sub sampled horizontally.
    pub fn chroma_sitting_horz(&self) -> Option<ChromaSitingHorz> {
        self.chroma_sitting_horz
    }

    /// How chroma is sub sampled vertically.
    pub fn chroma_sitting_vert(&self) -> Option<ChromaSitingVert> {
        self.chroma_sitting_vert
    }

    /// Clipping of the color ranges.
    pub fn range(&self) -> Option<Range> {
        self.range
    }

    /// The transfer characteristics of the video.
    pub fn transfer_characteristics(&self) -> Option<TransferCharacteristics> {
        self.transfer_characteristics
    }

    /// The colour primaries of the video.
    pub fn primaries(&self) -> Option<Primaries> {
        self.primaries
    }

    /// Maximum brightness of a single pixel (cd/m^2^).
    pub fn max_cll(&self) -> Option<u64> {
        self.max_cll
    }

    /// Maximum brightness of a single full frame (cd/m^2^).
    pub fn max_fall(&self) -> Option<u64> {
        self.max_fall
    }

    /// SMPTE 2086 mastering data.
    pub fn mastering_metadata(&self) -> Option<&MasteringMetadata> {
        self.mastering_metadata.as_ref()
    }
}

/// SMPTE 2086 mastering data.
#[derive(Clone, Debug)]
pub struct MasteringMetadata {
    primary_r_chromaticity_x: Option<f64>,
    primary_r_chromaticity_y: Option<f64>,
    primary_g_chromaticity_x: Option<f64>,
    primary_g_chromaticity_y: Option<f64>,
    primary_b_chromaticity_x: Option<f64>,
    primary_b_chromaticity_y: Option<f64>,
    white_point_chromaticity_x: Option<f64>,
    white_point_chromaticity_y: Option<f64>,
    luminance_max: Option<f64>,
    luminance_min: Option<f64>,
}

impl<R: Read + Seek> ParsableElement<R> for MasteringMetadata {
    type Output = Self;

    fn new(_r: &mut R, fields: &[(ElementId, ElementData)]) -> Result<Self> {
        let primary_r_chromaticity_x = try_find_float(fields, ElementId::PrimaryRChromaticityX)?;
        let primary_r_chromaticity_y = try_find_float(fields, ElementId::PrimaryRChromaticityX)?;
        let primary_g_chromaticity_x = try_find_float(fields, ElementId::PrimaryGChromaticityX)?;
        let primary_g_chromaticity_y = try_find_float(fields, ElementId::PrimaryGChromaticityX)?;
        let primary_b_chromaticity_x = try_find_float(fields, ElementId::PrimaryBChromaticityX)?;
        let primary_b_chromaticity_y = try_find_float(fields, ElementId::PrimaryBChromaticityX)?;
        let white_point_chromaticity_x =
            try_find_float(fields, ElementId::WhitePointChromaticityX)?;
        let white_point_chromaticity_y =
            try_find_float(fields, ElementId::WhitePointChromaticityY)?;
        let luminance_max = try_find_float(fields, ElementId::LuminanceMax)?;
        let luminance_min = try_find_float(fields, ElementId::LuminanceMin)?;

        Ok(Self {
            primary_r_chromaticity_x,
            primary_r_chromaticity_y,
            primary_g_chromaticity_x,
            primary_g_chromaticity_y,
            primary_b_chromaticity_x,
            primary_b_chromaticity_y,
            white_point_chromaticity_x,
            white_point_chromaticity_y,
            luminance_max,
            luminance_min,
        })
    }
}

impl MasteringMetadata {
    /// Red X chromaticity coordinate, as defined by CIE 1931.
    pub fn primary_r_chromaticity_x(&self) -> Option<f64> {
        self.primary_r_chromaticity_x
    }

    /// Red Y chromaticity coordinate, as defined by CIE 1931.
    pub fn primary_r_chromaticity_y(&self) -> Option<f64> {
        self.primary_r_chromaticity_y
    }

    /// Green X chromaticity coordinate, as defined by CIE 1931.
    pub fn primary_g_chromaticity_x(&self) -> Option<f64> {
        self.primary_g_chromaticity_x
    }

    /// Green Y chromaticity coordinate, as defined by CIE 1931
    pub fn primary_g_chromaticity_y(&self) -> Option<f64> {
        self.primary_g_chromaticity_y
    }

    /// Blue X chromaticity coordinate, as defined by CIE 1931.
    pub fn primary_b_chromaticity_x(&self) -> Option<f64> {
        self.primary_b_chromaticity_x
    }

    /// Blue Y chromaticity coordinate, as defined by CIE 1931.
    pub fn primary_b_chromaticity_y(&self) -> Option<f64> {
        self.primary_b_chromaticity_y
    }

    /// White X chromaticity coordinate, as defined by CIE 1931.
    pub fn white_point_chromaticity_x(&self) -> Option<f64> {
        self.white_point_chromaticity_x
    }

    /// White Y chromaticity coordinate, as defined by CIE 1931.
    pub fn white_point_chromaticity_y(&self) -> Option<f64> {
        self.white_point_chromaticity_y
    }

    /// Maximum luminance. Represented in candelas per square meter (cd/m^2^).
    pub fn luminance_max(&self) -> Option<f64> {
        self.luminance_max
    }

    /// Minimum luminance. Represented in candelas per square meter (cd/m^2^).
    pub fn luminance_min(&self) -> Option<f64> {
        self.luminance_min
    }
}

/// Store content encoding information
#[derive(Clone, Debug)]
pub enum ContentEncodingValue {
    /// Other encoding not handled.
    Unknown,
    /// Indicate than content is compressed.
    Compression(ContentCompression),
    /// Indicate than content is encrypted.
    Encryption(ContentEncryption),
}

/// Settings for one content encoding like compression or encryption.
#[derive(Clone, Debug)]
pub struct ContentEncoding {
    order: u64,
    scope: u64,
    encoding: ContentEncodingValue,
}

impl<R: Read + Seek> ParsableElement<R> for ContentEncoding {
    type Output = Self;

    fn new(r: &mut R, fields: &[(ElementId, ElementData)]) -> Result<Self> {
        let order = find_unsigned_or(fields, ElementId::ContentEncodingOrder, 0)?;
        let scope = find_unsigned_or(fields, ElementId::ContentEncodingScope, 1)?;

        let encoding_type = try_find_custom_type_or(
            fields,
            ElementId::ContentEncodingType,
            ContentEncodingType::Compression,
        )?;

        let encoding = match encoding_type {
            ContentEncodingType::Unknown => ContentEncodingValue::Unknown,
            ContentEncodingType::Compression => {
                let compression =
                    parse_child::<_, ContentCompression>(r, fields, ElementId::ContentCompression)?;
                ContentEncodingValue::Compression(compression)
            }
            ContentEncodingType::Encryption => {
                let encryption =
                    parse_child::<_, ContentEncryption>(r, fields, ElementId::ContentEncryption)?;
                ContentEncodingValue::Encryption(encryption)
            }
        };

        Ok(Self {
            order,
            scope,
            encoding,
        })
    }
}

impl ContentEncoding {
    /// Tells when this modification was used during encoding / muxing starting
    /// with 0 and counting upwards.
    pub fn order(&self) -> u64 {
        self.order
    }

    /// A bit field that describes which Elements have been modified in this way.
    ///
    /// Values (big-endian) can be OR'ed:
    ///
    /// 1 - All frame contents, excluding lacing data.
    /// 2 - The track's private data.
    /// 4 - The next ContentEncoding.
    pub fn scope(&self) -> u64 {
        self.scope
    }

    /// Return what kind of transformation is applied.
    pub fn encoding(&self) -> &ContentEncodingValue {
        &self.encoding
    }
}

/// Settings describing the compression used.
#[derive(Clone, Debug)]
pub struct ContentCompression {
    algo: ContentCompAlgo,
    comp_settings: Option<Vec<u8>>,
}

impl<R: Read + Seek> ParsableElement<R> for ContentCompression {
    type Output = Self;

    fn new(r: &mut R, fields: &[(ElementId, ElementData)]) -> Result<Self> {
        let algo =
            try_find_custom_type_or(fields, ElementId::ContentCompAlgo, ContentCompAlgo::Zlib)?;
        let comp_settings = try_find_binary(r, fields, ElementId::ContentCompSettings)?;
        Ok(Self {
            algo,
            comp_settings,
        })
    }
}

impl ContentCompression {
    /// The compression algorithm used.
    pub fn algo(&self) -> ContentCompAlgo {
        self.algo
    }
    /// The compression settings.
    pub fn settings(&self) -> Option<&[u8]> {
        self.comp_settings.as_deref()
    }
}

/// Settings describing the encryption used.
#[derive(Clone, Debug)]
pub struct ContentEncryption {
    algo: ContentEncAlgo,
    key_id: Option<Vec<u8>>,
    aes_settings: Option<ContentEncAesSettings>,
}

impl<R: Read + Seek> ParsableElement<R> for ContentEncryption {
    type Output = Self;

    fn new(r: &mut R, fields: &[(ElementId, ElementData)]) -> Result<Self> {
        let algo = try_find_custom_type_or(
            fields,
            ElementId::ContentEncAlgo,
            ContentEncAlgo::NotEncrypted,
        )?;
        let key_id = try_find_binary(r, fields, ElementId::ContentEncKeyId)?;
        let aes_settings = try_parse_child::<_, ContentEncAesSettings>(
            r,
            fields,
            ElementId::ContentEncAesSettings,
        )?;

        Ok(Self {
            algo,
            key_id,
            aes_settings,
        })
    }
}

impl ContentEncryption {
    /// The encryption algorithm used.
    pub fn algo(&self) -> ContentEncAlgo {
        self.algo
    }

    /// The encryption algorithm used.
    pub fn key_id(&self) -> Option<&[u8]> {
        self.key_id.as_deref()
    }

    /// The encryption algorithm used.
    pub fn aes_settings(&self) -> Option<&ContentEncAesSettings> {
        self.aes_settings.as_ref()
    }
}

/// Settings describing the encryption algorithm used.
#[derive(Clone, Debug)]
pub struct ContentEncAesSettings {
    aes_settings_cipher_mode: Option<AesSettingsCipherMode>,
}

impl<R: Read + Seek> ParsableElement<R> for ContentEncAesSettings {
    type Output = Self;

    fn new(_r: &mut R, fields: &[(ElementId, ElementData)]) -> Result<Self> {
        let aes_settings_cipher_mode =
            try_find_custom_type(fields, ElementId::AesSettingsCipherMode)?;

        Ok(Self {
            aes_settings_cipher_mode,
        })
    }
}

impl ContentEncAesSettings {
    /// The AES cipher mode used in the encryption.
    pub fn aes_settings_cipher_mode(&self) -> Option<AesSettingsCipherMode> {
        self.aes_settings_cipher_mode
    }
}

/// Contains all information about a segment edition.
#[derive(Clone, Debug)]
pub struct EditionEntry {
    chapter_atoms: Vec<ChapterAtom>,
}

impl<R: Read + Seek> ParsableElement<R> for EditionEntry {
    type Output = Self;

    fn new(r: &mut R, fields: &[(ElementId, ElementData)]) -> Result<Self> {
        let chapter_atoms =
            find_children_in_fields::<_, ChapterAtom>(r, fields, ElementId::ChapterAtom)?;

        Ok(Self { chapter_atoms })
    }
}

impl EditionEntry {
    /// Contains the atom information to use as the chapter atom (apply to all tracks).
    pub fn chapter_atoms(&self) -> &[ChapterAtom] {
        self.chapter_atoms.as_ref()
    }
}

/// Contains the atom information to use as the chapter atom.
#[derive(Clone, Debug)]
pub struct ChapterAtom {
    uid: NonZeroU64,
    string_uid: Option<String>,
    time_start: u64,
    time_end: Option<u64>,
    displays: Vec<ChapterDisplay>,
}

impl<R: Read + Seek> ParsableElement<R> for ChapterAtom {
    type Output = Self;

    fn new(r: &mut R, fields: &[(ElementId, ElementData)]) -> Result<Self> {
        let uid = find_nonzero(fields, ElementId::ChapterUid)?;
        let string_uid = try_find_string(fields, ElementId::ChapterStringUid)?;
        let time_start = find_unsigned(fields, ElementId::ChapterTimeStart)?;
        let time_end = try_find_unsigned(fields, ElementId::ChapterTimeEnd)?;

        let displays =
            find_children_in_fields::<_, ChapterDisplay>(r, fields, ElementId::ChapterDisplay)?;

        Ok(Self {
            uid,
            string_uid,
            time_start,
            time_end,
            displays,
        })
    }
}

impl ChapterAtom {
    /// A unique ID to identify the Chapter.
    pub fn uid(&self) -> NonZeroU64 {
        self.uid
    }

    /// A unique string ID to identify the Chapter.
    pub fn string_uid(&self) -> Option<&str> {
        self.string_uid.as_deref()
    }

    /// Timestamp of the start of Chapter.
    pub fn time_start(&self) -> u64 {
        self.time_start
    }

    /// Timestamp of the end of Chapter.
    pub fn time_end(&self) -> Option<u64> {
        self.time_end
    }

    /// Contains all possible strings to use for the chapter display.
    pub fn displays(&self) -> &[ChapterDisplay] {
        self.displays.as_ref()
    }
}

/// Contains all possible strings to use for the chapter display.
#[derive(Clone, Debug)]
pub struct ChapterDisplay {
    string: String,
    language: Option<String>,
    language_ietf: Option<String>,
    country: Option<String>,
}

impl<R: Read + Seek> ParsableElement<R> for ChapterDisplay {
    type Output = Self;

    fn new(_r: &mut R, fields: &[(ElementId, ElementData)]) -> Result<Self> {
        let string = find_string(fields, ElementId::ChapString)?;
        let language = try_find_string(fields, ElementId::ChapLanguage)?;
        let language_ietf = try_find_string(fields, ElementId::ChapLanguageIetf)?;
        let country = try_find_string(fields, ElementId::ChapCountry)?;

        Ok(Self {
            string,
            language,
            language_ietf,
            country,
        })
    }
}

impl ChapterDisplay {
    /// Contains the string to use as the chapter atom.
    pub fn string(&self) -> &str {
        self.string.as_ref()
    }

    /// The languages corresponding to the string, in the bibliographic ISO-639-2 form.
    pub fn language(&self) -> Option<&str> {
        self.language.as_deref()
    }

    /// Specifies the language according to BCP47 and using the IANA Language Subtag Registry.
    pub fn language_ietf(&self) -> Option<&str> {
        self.language_ietf.as_deref()
    }

    /// The countries corresponding to the string, same 2 octets country-codes as in
    /// Internet domains based on ISO3166-1 alpha-2 codes.
    pub fn country(&self) -> Option<&str> {
        self.country.as_deref()
    }
}

/// A single metadata descriptor.
#[derive(Clone, Debug)]
pub struct Tag {
    targets: Option<Targets>,
    simple_tags: Vec<SimpleTag>,
}

impl<R: Read + Seek> ParsableElement<R> for Tag {
    type Output = Self;

    fn new(r: &mut R, fields: &[(ElementId, ElementData)]) -> Result<Self> {
        let targets = try_parse_child::<_, Targets>(r, fields, ElementId::Targets)?;
        let simple_tags = find_children_in_fields::<_, SimpleTag>(r, fields, ElementId::SimpleTag)?;

        Ok(Self {
            targets,
            simple_tags,
        })
    }
}

impl Tag {
    /// Specifies which other elements the metadata represented by the tag applies to.
    /// If empty or not present, then the Tag describes everything in the Segment.
    pub fn targets(&self) -> Option<&Targets> {
        self.targets.as_ref()
    }

    /// Contains general information about the target.
    pub fn simple_tags(&self) -> &[SimpleTag] {
        self.simple_tags.as_slice()
    }
}

/// Specifies which other elements the metadata represented by the tag applies to.
#[derive(Clone, Debug)]
pub struct Targets {
    target_type_value: Option<u64>,
    _target_type: Option<String>,
    tag_track_uid: Option<u64>,
}

impl<R: Read + Seek> ParsableElement<R> for Targets {
    type Output = Self;

    fn new(_r: &mut R, fields: &[(ElementId, ElementData)]) -> Result<Self> {
        let target_type_value = try_find_unsigned(fields, ElementId::TargetTypeValue)?;
        let target_type = try_find_string(fields, ElementId::TargetType)?;
        let tag_track_uid = try_find_unsigned(fields, ElementId::TagTrackUid)?;

        Ok(Self {
            target_type_value,
            _target_type: target_type,
            tag_track_uid,
        })
    }
}

impl Targets {
    /// A number to indicate the logical level of the target.
    pub fn target_type_value(&self) -> Option<u64> {
        self.target_type_value
    }

    /// A unique ID to identify the track(s) the tags belong to.
    /// If the value is 0 at this level, the tags apply to all tracks in the Segment.
    pub fn tag_track_uid(&self) -> Option<u64> {
        self.tag_track_uid
    }
}

/// Contains general information about the target.
#[derive(Clone, Debug)]
pub struct SimpleTag {
    name: String,
    language: Option<String>,
    default: Option<bool>,
    string: Option<String>,
    binary: Option<Vec<u8>>,
}

impl<R: Read + Seek> ParsableElement<R> for SimpleTag {
    type Output = Self;

    fn new(r: &mut R, fields: &[(ElementId, ElementData)]) -> Result<Self> {
        let name = find_string(fields, ElementId::TagName)?;
        let language = try_find_string(fields, ElementId::TagLanguage)?;
        let default = try_find_bool(fields, ElementId::TagDefault)?;
        let string = try_find_string(fields, ElementId::TagString)?;
        let binary = try_find_binary(r, fields, ElementId::TagBinary)?;

        Ok(Self {
            name,
            language,
            default,
            string,
            binary,
        })
    }
}

impl SimpleTag {
    /// The value of the tag.
    pub fn name(&self) -> &str {
        &self.name
    }

    /// Specifies the language of the tag.
    pub fn language(&self) -> Option<&str> {
        self.language.as_deref()
    }

    /// Indicate if this is the default/original language to use for the given tag.
    pub fn default(&self) -> Option<bool> {
        self.default
    }

    /// The value of the tag, if it is a string.
    pub fn string(&self) -> Option<&str> {
        self.string.as_deref()
    }

    /// The value of the tag, if it is binary.
    pub fn binary(&self) -> Option<&[u8]> {
        self.binary.as_deref()
    }
}

/// An entry in the seek head.
#[derive(Clone, Copy, Debug)]
struct SeekEntry {
    id: ElementId,
    offset: u64,
}

impl<R: Read + Seek> ParsableElement<R> for SeekEntry {
    type Output = Self;

    fn new(_r: &mut R, fields: &[(ElementId, ElementData)]) -> Result<Self> {
        let id: u32 = find_unsigned(fields, ElementId::SeekId)?.try_into()?;
        let id = id_to_element_id(id);
        let offset = find_unsigned(fields, ElementId::SeekPosition)?;

        Ok(Self { id, offset })
    }
}

/// Contains all information relative to a seek point in the segment.
#[derive(Clone, Debug)]
struct CuePoint {
    time: u64,
    track_position: CueTrackPositions,
}

impl<R: Read + Seek> ParsableElement<R> for CuePoint {
    type Output = Self;

    fn new(r: &mut R, fields: &[(ElementId, ElementData)]) -> Result<Self> {
        let time = find_unsigned(fields, ElementId::CueTime)?;
        let track_position =
            parse_child::<_, CueTrackPositions>(r, fields, ElementId::CueTrackPositions)?;

        Ok(Self {
            time,
            track_position,
        })
    }
}

/// Contain positions for different tracks corresponding to the timestamp.
#[derive(Clone, Debug)]
struct CueTrackPositions {
    _track: u64,
    cluster_position: u64,
    relative_position: Option<u64>,
    _duration: Option<u64>,
    _block_number: Option<u64>,
}

impl<R: Read + Seek> ParsableElement<R> for CueTrackPositions {
    type Output = Self;

    fn new(_r: &mut R, fields: &[(ElementId, ElementData)]) -> Result<Self> {
        let track = find_unsigned(fields, ElementId::CueTrack)?;
        let cluster_position = find_unsigned(fields, ElementId::CueClusterPosition)?;
        let relative_position = try_find_unsigned(fields, ElementId::CueRelativePosition)?;
        let duration = try_find_unsigned(fields, ElementId::CueDuration)?;
        let block_number = try_find_unsigned(fields, ElementId::CueBlockNumber)?;

        Ok(Self {
            _track: track,
            cluster_position,
            relative_position,
            _duration: duration,
            _block_number: block_number,
        })
    }
}

/// Demuxer for Matroska files.
#[derive(Clone, Debug)]
pub struct MatroskaFile<R: Read + Seek> {
    file: R,
    ebml_header: EbmlHeader,
    seek_head: HashMap<ElementId, u64>,
    info: Info,
    tracks: Vec<TrackEntry>,
    cue_points: Option<Vec<CuePoint>>,
    chapters: Option<Vec<EditionEntry>>,
    tags: Option<Vec<Tag>>,

    /// The timestamp of the current cluster.
    cluster_timestamp: u64,
    /// Queued frames of a block we are currently reading.
    queued_frames: VecDeque<LacedFrame>,
    /// The largest frame `next_frame` will read.
    max_frame_size: u64,
}

impl<R: Read + Seek> MatroskaFile<R> {
    /// Opens a Matroska file.
    pub fn open(mut file: R) -> Result<Self> {
        let ebml_header = parse_ebml_header(&mut file)?;

        let (segment_data_offset, _) = expect_master(&mut file, ElementId::Segment, None)?;

        let optional_seek_head = search_seek_head(&mut file, segment_data_offset)?;
        let mut seek_head = parse_seek_head(&mut file, segment_data_offset, optional_seek_head)?;

        if seek_head.is_empty() {
            build_seek_head(&mut file, segment_data_offset, &mut seek_head)?;
        }

        if !seek_head.contains_key(&ElementId::Cluster) {
            find_first_cluster_offset(&mut file, segment_data_offset, &mut seek_head)?;
        }

        let info = parse_segment_info(&mut file, &seek_head)?;

        let tracks = try_parse_top_element_collection::<_, TrackEntry>(
            &mut file,
            &seek_head,
            ElementId::Tracks,
            ElementId::TrackEntry,
        )?
        .ok_or(DemuxError::ElementNotFound(ElementId::Tracks))?;

        let mut cue_points = try_parse_top_element_collection::<_, CuePoint>(
            &mut file,
            &seek_head,
            ElementId::Cues,
            ElementId::CuePoint,
        )?;

        if let Some(cue_points) = cue_points.as_mut() {
            cue_points
                .iter_mut()
                .for_each(|p| p.track_position.cluster_position += segment_data_offset);
        }

        let chapters = try_parse_top_element_collection::<_, EditionEntry>(
            &mut file,
            &seek_head,
            ElementId::Chapters,
            ElementId::EditionEntry,
        )?;

        let tags = try_parse_top_element_collection::<_, Tag>(
            &mut file,
            &seek_head,
            ElementId::Tags,
            ElementId::Tag,
        )?;

        seek_to_first_cluster(&mut file, &seek_head)?;

        Ok(Self {
            file,
            ebml_header,
            seek_head,
            info,
            tracks,
            cue_points,
            chapters,
            tags,
            cluster_timestamp: 0,
            queued_frames: VecDeque::with_capacity(8),
            max_frame_size: u64::MAX,
        })
    }

    /// Sets the largest frame `next_frame` will read, in bytes. A block
    /// stating a larger one is refused before anything is allocated for it;
    /// otherwise a frame is bounded only by the stream length the reader
    /// reports.
    pub fn set_max_frame_size(&mut self, bytes: u64) {
        self.max_frame_size = bytes;
    }

    /// Returns the EBML header.
    pub fn ebml_header(&self) -> &EbmlHeader {
        &self.ebml_header
    }

    /// Returns the segment info.
    pub fn info(&self) -> &Info {
        &self.info
    }

    /// Returns the tracks of the file.
    pub fn tracks(&self) -> &[TrackEntry] {
        self.tracks.as_ref()
    }

    /// Returns the chapters of the file.
    pub fn chapters(&self) -> Option<&[EditionEntry]> {
        self.chapters.as_deref()
    }

    /// Element containing metadata describing tracks, editions,
    /// chapters, attachments, or the segment as a whole.
    pub fn tags(&self) -> Option<&[Tag]> {
        self.tags.as_deref()
    }

    /// Reads the next frame data into the given `Frame`.
    ///
    /// Returns `false` if the end of the file is reached.
    pub fn next_frame(&mut self, frame: &mut Frame) -> Result<bool> {
        if self.try_pop_frame(frame)? {
            return Ok(true);
        };

        // Search for the next block and read it.
        let mut block_end = None;
        loop {
            match next_element(&mut self.file) {
                Ok((element_id, element_data)) => match element_id {
                    // We enter cluster and block groups.
                    ElementId::Cluster => {
                        self.enter_data_location(&element_data)?;
                    }
                    ElementId::BlockGroup => {
                        let block_size = self.enter_data_location(&element_data)?;
                        block_end = Some(self.file.stream_position()? + block_size);
                    }
                    // Update the current cluster timestamp.
                    ElementId::Timestamp => {
                        if let ElementData::Unsigned(timestamp) = element_data {
                            self.cluster_timestamp = timestamp;
                        } else {
                            return Err(DemuxError::UnexpectedDataType);
                        }
                    }
                    // Parse the block data.
                    ElementId::SimpleBlock | ElementId::Block => {
                        if let ElementData::Location {
                            offset: header_start,
                            size: block_size,
                        } = element_data
                        {
                            self.file.seek(SeekFrom::Start(header_start))?;

                            parse_laced_frames(
                                &mut self.file,
                                &mut self.queued_frames,
                                block_size,
                                self.cluster_timestamp,
                                header_start,
                                element_id == ElementId::SimpleBlock,
                            )?;
                            self.try_pop_frame(frame)?;

                            // If simple block, set end of block to current position, to go out of the loop
                            block_end.get_or_insert(self.file.stream_position()?);
                        } else {
                            return Err(DemuxError::UnexpectedDataType);
                        };
                    }
                    ElementId::BlockDuration => {
                        if let ElementData::Unsigned(duration) = element_data {
                            if let Some(previous) = frame.duration.replace(duration) {
                                return Err(DemuxError::FrameAlreadyHasDuration(previous));
                            }
                        } else {
                            return Err(DemuxError::UnexpectedDataType);
                        }
                    }
                    _ => { /* We ignore all other elements */ }
                },
                // If we encounter an IO error, we assume that there
                // are no more blocks to handle (EOF).
                Err(err) => {
                    if let Some(err) = err.source() {
                        if err.downcast_ref::<std::io::Error>().is_some() {
                            return Ok(false);
                        }
                    }
                    return Err(err);
                }
            }

            // Manage the end of the loop. Return true if :
            // - a SimpleBlock found and read
            // - a BlockGroup found and read to the end. This happens when it contains one Block (frame) + BlockDuration.
            // - a BlockGroup found with laced frames decoded and pushed in queued_frames.
            //   The first frame should have been popped in `frame` and other frames waiting in `queued_frames`
            let block_ended = if let Some(val) = block_end {
                val == self.file.stream_position()?
            } else {
                false
            };
            if block_ended || !self.queued_frames.is_empty() {
                return Ok(true);
            }
        }
    }

    /// Read a frame that is left inside the block.
    fn try_pop_frame(&mut self, frame: &mut Frame) -> Result<bool> {
        if let Some(queued_frame) = self.queued_frames.pop_front() {
            frame.timestamp = queued_frame.timestamp;
            frame.track = queued_frame.track;
            frame.is_discardable = queued_frame.is_discardable;
            frame.is_invisible = queued_frame.is_invisible;
            frame.is_keyframe = queued_frame.is_keyframe;
            frame.duration = None; // Clean duration, as it's reported in different block than other frame content

            check_size_limit(queued_frame.size, self.max_frame_size)?;
            let available = remaining_len(&mut self.file)?;
            if queued_frame.size > available {
                return Err(DemuxError::ElementSizeExceedsStream {
                    declared: queued_frame.size,
                    available,
                });
            }
            let size: usize = queued_frame.size.try_into()?;
            frame.data.resize(size, 0_u8);
            self.file.read_exact(frame.data.as_mut_slice())?;

            Ok(true)
        } else {
            Ok(false)
        }
    }

    /// Seeks to the given timestamp. The next `next_frame()` will write the first frame that comes
    /// directly AFTER the given timestamp. If the timestamp is outside of the duration of the video,
    /// the next `next_frame()` will return `None`.
    ///
    /// Seek operations will use `Cues` inside the file for faster seek operation. If no `Cues` are
    /// present, this function will do a linear search through all clusters / blocks until the first
    /// frame after the given timestamp is found.
    pub fn seek(&mut self, seek_timestamp: u64) -> Result<()> {
        self.cluster_timestamp = 0;
        self.queued_frames.clear();

        let cluster_start = *self
            .seek_head
            .get(&ElementId::Cluster)
            .ok_or(DemuxError::CantFindCluster)?;

        let (target_offset, _cue_time) = self.seek_broad_phase(seek_timestamp, cluster_start)?;

        self.file.seek(SeekFrom::Start(target_offset))?;

        self.seek_narrow_phase(seek_timestamp)
    }

    /// Like [`MatroskaFile::seek`], but when a cue point was used the
    /// reader lands on the cue point itself rather than the first block at
    /// or after `seek_timestamp`. Cue points reference keyframes, so the
    /// landing block is decodable — the at-or-before contract a video
    /// player wants; `seek` instead lands mid-GOP for targets between
    /// keyframes. A timestamp past the last cue point lands on the last
    /// cue point (the tail plays out) instead of running to the end.
    pub fn seek_to_cue_point(&mut self, seek_timestamp: u64) -> Result<()> {
        self.cluster_timestamp = 0;
        self.queued_frames.clear();

        let cluster_start = *self
            .seek_head
            .get(&ElementId::Cluster)
            .ok_or(DemuxError::CantFindCluster)?;

        let (target_offset, cue_time) = self.seek_broad_phase(seek_timestamp, cluster_start)?;

        self.file.seek(SeekFrom::Start(target_offset))?;

        self.seek_narrow_phase(cue_time.unwrap_or(seek_timestamp))
    }

    // Return the data size for use by the caller
    fn enter_data_location(&mut self, element_data: &ElementData) -> Result<u64> {
        if let ElementData::Location { offset, size } = element_data {
            self.file.seek(SeekFrom::Start(*offset))?;
            Ok(*size)
        } else {
            Err(DemuxError::UnexpectedDataType)
        }
    }

    fn seek_broad_phase(
        &mut self,
        seek_timestamp: u64,
        cluster_start: u64,
    ) -> Result<(u64, Option<u64>)> {
        if let Some(cue_points) = self.cue_points.as_ref() {
            // Fast path if we have cue points.
            let seek_pos = cue_points
                .binary_search_by(|p| p.time.cmp(&seek_timestamp))
                .unwrap_or_else(|seek_pos| seek_pos.saturating_sub(1));

            if let Some(point) = cue_points.get(seek_pos) {
                if point.time <= seek_timestamp {
                    let cue_time = point.time;
                    let mut target_offset = point.track_position.cluster_position;

                    if let Some(relative_position) = point.track_position.relative_position {
                        // CueRelativePosition is relative to the data of
                        // the cluster the cue points at — not the first
                        // cluster of the segment.
                        let (cluster_data_offset, cluster_timestamp) =
                            self.get_cluster_offset_and_timestamp(target_offset)?;
                        self.cluster_timestamp = cluster_timestamp;
                        target_offset = cluster_data_offset + relative_position;
                    }

                    return Ok((target_offset, Some(cue_time)));
                }
            }
        };

        // Linear search the clusters. Offsets handed back point at the
        // cluster *element* (as the cue path does), so the narrow phase
        // enters the cluster with proper state; before any cluster has
        // been seen the first cluster is the landing point.
        let mut last_cluster_offset = cluster_start;
        let mut current_cluster_offset = cluster_start;
        let mut next_cluster_offset = 0;

        self.file.seek(SeekFrom::Start(cluster_start))?;

        loop {
            let element_start = self.file.stream_position()?;
            match next_element(&mut self.file) {
                Ok((element_id, element_data)) => match element_id {
                    // We enter clusters.
                    ElementId::Cluster => {
                        if let ElementData::Location { offset, size } = element_data {
                            // We can't do a broad phase search when having a live streaming file.
                            if size == u64::MAX {
                                return Ok((cluster_start, None));
                            }
                            self.file.seek(SeekFrom::Start(offset))?;
                            last_cluster_offset = current_cluster_offset;
                            current_cluster_offset = element_start;
                            next_cluster_offset = offset + size;
                        } else {
                            return Err(DemuxError::UnexpectedDataType);
                        }
                    }
                    // Check the timestamp and seek to the next cluster if we haven't overshoot yet.
                    ElementId::Timestamp => {
                        if let ElementData::Unsigned(timestamp) = element_data {
                            match timestamp {
                                t if t < seek_timestamp => {
                                    self.file.seek(SeekFrom::Start(next_cluster_offset))?;
                                }
                                t if t > seek_timestamp => {
                                    return Ok((last_cluster_offset, None));
                                }
                                _ => {
                                    return Ok((current_cluster_offset, None));
                                }
                            }
                        } else {
                            return Err(DemuxError::UnexpectedDataType);
                        }
                    }
                    _ => { /* We ignore all other elements */ }
                },
                // If we encounter an IO error, we assume that there
                // are no more blocks to handle (EOF).
                Err(err) => {
                    if let Some(err) = err.source() {
                        if err.downcast_ref::<std::io::Error>().is_some() {
                            return Ok((next_cluster_offset, None));
                        }
                    }
                    return Err(err);
                }
            }
        }
    }

    fn seek_narrow_phase(&mut self, seek_timestamp: u64) -> Result<()> {
        loop {
            let position = self.file.stream_position()?;
            match next_element(&mut self.file) {
                Ok((element_id, element_data)) => match element_id {
                    // We enter cluster and block groups.
                    ElementId::Cluster | ElementId::BlockGroup => {
                        self.enter_data_location(&element_data)?;
                    }
                    // Update the current cluster timestamp.
                    ElementId::Timestamp => {
                        if let ElementData::Unsigned(timestamp) = element_data {
                            self.cluster_timestamp = timestamp;
                        } else {
                            return Err(DemuxError::UnexpectedDataType);
                        }
                    }
                    // Parse the block data.
                    ElementId::SimpleBlock | ElementId::Block => {
                        if let ElementData::Location { offset, size } = element_data {
                            self.file.seek(SeekFrom::Start(offset))?;
                            let timestamp =
                                probe_block_timestamp(&mut self.file, self.cluster_timestamp)?;

                            match timestamp {
                                t if t < seek_timestamp => {
                                    // Jump to the next element.
                                    self.file.seek(SeekFrom::Start(offset + size))?;
                                }
                                _ => {
                                    // We found the first element after the seeked timestamp.
                                    self.file.seek(SeekFrom::Start(position))?;
                                    return Ok(());
                                }
                            }
                        } else {
                            return Err(DemuxError::UnexpectedDataType);
                        }
                    }
                    _ => { /* We ignore all other elements */ }
                },
                // If we encounter an IO error, we assume that there
                // are no more blocks to handle (EOF).
                Err(err) => {
                    if let Some(err) = err.source() {
                        if err.downcast_ref::<std::io::Error>().is_some() {
                            return Ok(());
                        }
                    }
                    return Err(err);
                }
            }
        }
    }

    fn get_cluster_offset_and_timestamp(&mut self, cluster_start: u64) -> Result<(u64, u64)> {
        let (offset, _) = expect_master(&mut self.file, ElementId::Cluster, Some(cluster_start))?;
        loop {
            match next_element(&mut self.file) {
                Ok((element_id, element_data)) => match element_id {
                    // Check the timestamp and seek to the next cluster if we haven't overshoot yet.
                    ElementId::Timestamp => {
                        return if let ElementData::Unsigned(timestamp) = element_data {
                            Ok((offset, timestamp))
                        } else {
                            Err(DemuxError::UnexpectedDataType)
                        }
                    }
                    ElementId::Cluster | ElementId::SimpleBlock | ElementId::BlockGroup => {
                        return Err(DemuxError::UnexpectedElement((
                            ElementId::Timestamp,
                            element_id,
                        )));
                    }
                    _ => { /* We ignore all other elements */ }
                },
                Err(_) => {
                    return Err(DemuxError::ElementNotFound(ElementId::Timestamp));
                }
            }
        }
    }
}

/// Parses and verifies the EBML header.
fn parse_ebml_header<R: Read + Seek>(r: &mut R) -> Result<EbmlHeader> {
    let (master_offset, master_size) = expect_master(r, ElementId::Ebml, None)?;
    let master_children = collect_children(r, master_offset, master_size)?;
    let header = EbmlHeader::new(r, &master_children)?;
    Ok(header)
}

/// Parses the seek head if present.
fn parse_seek_head<R: Read + Seek>(
    mut file: &mut R,
    segment_data_offset: u64,
    mut optional_seek_head: Option<(u64, u64)>,
) -> Result<HashMap<ElementId, u64>> {
    let mut seek_head = HashMap::new();

    while let Some((seek_head_data_offset, seek_head_data_size)) = optional_seek_head.take() {
        let seek_head_entries =
            collect_children(&mut file, seek_head_data_offset, seek_head_data_size)?;

        for (entry_id, entry_data) in &seek_head_entries {
            if let ElementId::Seek = entry_id {
                if let ElementData::Location { offset, size } = entry_data {
                    let seek_fields = collect_children(&mut file, *offset, *size)?;
                    if let Ok(seek_entry) = SeekEntry::new(&mut file, &seek_fields) {
                        if seek_entry.id == ElementId::SeekHead {
                            file.seek(SeekFrom::Start(segment_data_offset + seek_entry.offset))?;
                            let (element_id, element_data) = next_element(file)?;

                            // Check that a SeekHead element has been found as desired
                            if element_id != ElementId::SeekHead {
                                return Err(DemuxError::UnexpectedElement((
                                    ElementId::SeekHead,
                                    element_id,
                                )));
                            }

                            if let ElementData::Location { offset, size } = element_data {
                                // Push next SeekHead Element for use in next loop.
                                optional_seek_head.replace((offset, size)).ok_or(()).expect_err("It is expected to have only one reference to a SeekHead in a SeekHead element");
                            } else {
                                return Err(DemuxError::CantFindCluster);
                            }
                        } else {
                            seek_head
                                .insert(seek_entry.id, segment_data_offset + seek_entry.offset);
                        }
                    }
                }
            }
        }
    }

    Ok(seek_head)
}

/// Seeks the SeekHead element and returns the offset into it when present.
///
/// The specification states that the first non CRC-32 element should be a SeekHead if present.
fn search_seek_head<R: Read + Seek>(
    r: &mut R,
    segment_data_offset: u64,
) -> Result<Option<(u64, u64)>> {
    loop {
        let (element_id, size) = parse_element_header(r, Some(segment_data_offset))?;
        match element_id {
            ElementId::SeekHead => {
                let current_pos = r.stream_position()?;
                return Ok(Some((current_pos, size)));
            }
            ElementId::Crc32 => continue,
            _ => return Ok(None),
        }
    }
}

/// Build a SeekHead by parsing the top level entries.
fn build_seek_head<R: Read + Seek>(
    r: &mut R,
    segment_data_offset: u64,
    seek_head: &mut HashMap<ElementId, u64>,
) -> Result<()> {
    r.seek(SeekFrom::Start(segment_data_offset))?;
    loop {
        let position = r.stream_position()?;
        match next_element(r) {
            Ok((element_id, _)) => {
                if element_id == ElementId::Info
                    || element_id == ElementId::Tracks
                    || element_id == ElementId::Chapters
                    || element_id == ElementId::Cues
                    || element_id == ElementId::Tags
                    || element_id == ElementId::Cluster
                {
                    // We only need the first cluster entry.
                    if element_id != ElementId::Cluster
                        || !seek_head.contains_key(&ElementId::Cluster)
                    {
                        seek_head.insert(element_id, position);
                    }
                }
            }
            Err(_) => {
                // EOF or damaged file. We will stop looking for top level entries.
                break;
            }
        }
    }

    Ok(())
}

/// Tries to find the offset of the first cluster and save it in the SeekHead.
fn find_first_cluster_offset<R: Read + Seek>(
    r: &mut R,
    segment_data_offset: u64,
    seek_head: &mut HashMap<ElementId, u64>,
) -> Result<()> {
    // There is no guarantee about the order of the `Top-Level Elements`,
    // so search from the segment data offset.
    r.seek(SeekFrom::Start(segment_data_offset))?;
    loop {
        let position = r.stream_position()?;

        match next_element(r) {
            Ok((element_id, element_data)) => {
                if let ElementId::Cluster = element_id {
                    if let ElementData::Location { .. } = element_data {
                        seek_head.insert(ElementId::Cluster, position);
                        break;
                    } else {
                        return Err(DemuxError::UnexpectedDataType);
                    }
                }

                if let ElementData::Location { size, .. } = element_data {
                    if size == u64::MAX {
                        // No path left to walk on this level.
                        return Err(DemuxError::CantFindCluster);
                    }
                }
            }
            Err(_) => {
                // EOF or damaged file. We will stop looking for top level entries.
                return Err(DemuxError::CantFindCluster);
            }
        }
    }

    Ok(())
}

fn parse_segment_info<R: Read + Seek>(
    r: &mut R,
    seek_head: &HashMap<ElementId, u64>,
) -> Result<Info> {
    if let Some(offset) = seek_head.get(&ElementId::Info) {
        let (info_data_offset, info_data_size) = expect_master(r, ElementId::Info, Some(*offset))?;
        let child_fields = collect_children(r, info_data_offset, info_data_size)?;
        let info = Info::new(r, &child_fields)?;
        Ok(info)
    } else {
        Err(DemuxError::ElementNotFound(ElementId::Info))
    }
}

fn try_parse_top_element_collection<R, T>(
    r: &mut R,
    seek_head: &HashMap<ElementId, u64>,
    master_id: ElementId,
    child_id: ElementId,
) -> Result<Option<Vec<T::Output>>>
where
    R: Read + Seek,
    T: ParsableElement<R>,
{
    let cue_points = if let Some(offset) = seek_head.get(&master_id) {
        let cue_points = parse_children_at_offset::<_, T>(r, *offset, master_id, child_id)?;
        Some(cue_points)
    } else {
        None
    };
    Ok(cue_points)
}

fn find_children_in_fields<R, T>(
    r: &mut R,
    fields: &[(ElementId, ElementData)],
    child_id: ElementId,
) -> Result<Vec<T::Output>>
where
    R: Read + Seek,
    T: ParsableElement<R>,
{
    let mut children = vec![];
    for (_, data) in fields.iter().filter(|(id, _)| *id == child_id) {
        if let ElementData::Location { offset, size } = data {
            let child_fields = collect_children(r, *offset, *size)?;
            let child = T::new(r, &child_fields)?;
            children.push(child);
        } else {
            return Err(DemuxError::UnexpectedDataType);
        }
    }
    Ok(children)
}

fn seek_to_first_cluster<R: Read + Seek>(
    r: &mut R,
    seek_head: &HashMap<ElementId, u64>,
) -> Result<()> {
    if let Some(offset) = seek_head.get(&ElementId::Cluster) {
        r.seek(SeekFrom::Start(*offset))?;
        Ok(())
    } else {
        Err(DemuxError::ElementNotFound(ElementId::Cluster))
    }
}

#[cfg(test)]
mod tests {
    #![allow(clippy::panic)]

    use std::io::Cursor;

    use super::*;

    #[test]
    fn test_parse_ebml_header() -> Result<()> {
        let data: Vec<u8> = vec![
            0x1A, 0x45, 0xDF, 0xA3, 0xA2, 0x42, 0x86, 0x81, 0x01, 0x42, 0xF7, 0x81, 0x01, 0x42,
            0xF2, 0x81, 0x04, 0x42, 0xF3, 0x81, 0x08, 0x42, 0x82, 0x88, 0x6D, 0x61, 0x74, 0x72,
            0x6F, 0x73, 0x6B, 0x61, 0x42, 0x87, 0x81, 0x04, 0x42, 0x85, 0x81, 0x02,
        ];
        let mut cursor = Cursor::new(data);
        let ebml_header = parse_ebml_header(&mut cursor)?;
        assert_eq!(ebml_header.version, Some(1));
        assert_eq!(ebml_header.read_version, Some(1));
        assert_eq!(ebml_header.max_id_length, 4);
        assert_eq!(ebml_header.max_size_length, 8);
        assert_eq!(&ebml_header.doc_type, "matroska");
        assert_eq!(ebml_header.doc_type_version, 4);
        assert_eq!(ebml_header.doc_type_read_version, 2);

        Ok(())
    }
}
