//! The typed event path every demuxer emits, consumed by the Bank, the
//! decoders and the metadata surface. Access units carry a generation so
//! stale data cannot cross a seek boundary.

use media_clock::{Generation, MediaTime};

#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct TrackId(pub u32);

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum VideoCodec {
    H264,
    H265,
    Vp8,
    Vp9,
    Av1,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AudioCodec {
    Aac,
    Opus,
    Flac,
    Mp3,
    Pcm,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Format {
    Video {
        codec: VideoCodec,
        coded_width: u32,
        coded_height: u32,
        display_width: u32,
        display_height: u32,
        /// Codec initialisation data the decoder needs alongside the AU
        /// stream (AV1: the config OBUs from `av1C`, which hardware
        /// decoders want prepended to the first AU). Empty when the codec
        /// carries its configuration in-band. H.264/HEVC parameter sets
        /// are written into the Annex-B stream itself and never ride here.
        codec_private: Vec<u8>,
    },
    Audio {
        codec: AudioCodec,
        sample_rate: u32,
        channels: u32,
        /// Codec initialisation data the platform decoder needs before the
        /// first AU (AAC: the AudioSpecificConfig). Empty when the codec
        /// carries its configuration in-band.
        codec_private: Vec<u8>,
    },
}

/// One compressed access unit: bytes, timestamps, keyframe flag, generation.
#[derive(Debug, Clone)]
pub struct Au {
    pub track: TrackId,
    pub data: Vec<u8>,
    pub pts: MediaTime,
    pub dts: MediaTime,
    pub key: bool,
    pub generation: Generation,
}

#[derive(Debug, Clone)]
pub enum MetadataEvent {
    Scte35 {
        pts: Option<MediaTime>,
        payload: Vec<u8>,
    },
    SeiUserData {
        pts: MediaTime,
        payload: Vec<u8>,
    },
    Klv {
        pts: Option<MediaTime>,
        payload: Vec<u8>,
    },
}

#[derive(Debug, Clone)]
pub struct CaptionEvent {
    pub track: TrackId,
    pub pts: MediaTime,
    pub payload: Vec<u8>,
}

/// Why the timeline broke. Downstream reacts by snapping, never slewing.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DiscontinuityReason {
    PcrWrap,
    AdSplice,
    DecoderReset,
    Reconnect,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EosReason {
    /// The source ended normally.
    Natural,
    /// The source went away underneath us (socket death, file truncation).
    SourceLost,
}

#[derive(Debug, Clone)]
pub enum StreamEvent {
    Format(TrackId, Format),
    Au(Au),
    Metadata(MetadataEvent),
    Caption(CaptionEvent),
    Discontinuity(TrackId, DiscontinuityReason),
    Eos(EosReason),
}

impl StreamEvent {
    /// Payload bytes this event holds, for the Bank's byte accounting.
    pub fn payload_bytes(&self) -> usize {
        match self {
            Self::Au(au) => au.data.len(),
            Self::Metadata(MetadataEvent::Scte35 { payload, .. })
            | Self::Metadata(MetadataEvent::SeiUserData { payload, .. })
            | Self::Metadata(MetadataEvent::Klv { payload, .. }) => payload.len(),
            Self::Caption(c) => c.payload.len(),
            Self::Format(..) | Self::Discontinuity(..) | Self::Eos(..) => 0,
        }
    }

    pub fn generation(&self) -> Option<Generation> {
        match self {
            Self::Au(au) => Some(au.generation),
            _ => None,
        }
    }
}
