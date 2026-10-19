//! Demux layer: the typed `StreamEvent` path, the pull-based `Demuxer`
//! trait, the byte-source seam, and one demuxer per supported container
//! with [`open_auto`] choosing between them by sniffing.

#![forbid(unsafe_code)]

mod adts;
mod artwork;
mod avc;
mod demuxer;
mod event;
mod flac;
mod hevc;
mod mkv;
mod mp3;
mod mp4;
mod mp4_esds;
mod mp4_fragment;
mod mp4_index;
mod mp4_runs;
mod ogg_opus;
mod source;
mod ts;
mod wav;

pub use adts::AdtsDemuxer;
pub use artwork::Artwork;
pub use demuxer::{AudioTrackInfo, DemuxLimits, DemuxOptions, Demuxer, MAX_NOTES, push_note};
pub use event::{
    Au, AudioCodec, CaptionEvent, DiscontinuityReason, EosReason, Format, MetadataEvent,
    StreamEvent, TrackId, VideoCodec,
};
pub use flac::FlacDemuxer;
pub use mkv::MkvDemuxer;
pub use mp3::Mp3Demuxer;
pub use mp4::Mp4Demuxer;
pub use ogg_opus::OggOpusDemuxer;
pub use source::{ByteSource, MemSource, SourceError};
pub use ts::TsDemuxer;
pub use wav::WavDemuxer;

use media_clock::Generation;

/// Containers the router recognises. Sniffing decides; extension and
/// resolver hints are hints only.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ContainerKind {
    Mp4,
    MpegTs,
    Mkv,
    Flac,
    Ogg,
    Mp3,
    Adts,
    Wav,
}

/// Sniff the container from the first bytes of the stream. MP4 announces
/// itself with a box header; TS is recognised by sync bytes at a 188- or
/// 192-byte stride.
pub fn sniff_container(head: &[u8]) -> Option<ContainerKind> {
    // EBML magic: Matroska/WebM.
    if head.starts_with(&[0x1A, 0x45, 0xDF, 0xA3]) {
        return Some(ContainerKind::Mkv);
    }
    // RIFF is a container family; only the WAVE form is ours.
    if head.len() >= 12 && &head[..4] == b"RIFF" && &head[8..12] == b"WAVE" {
        return Some(ContainerKind::Wav);
    }
    if head.len() >= 8
        && matches!(
            &head[4..8],
            b"ftyp" | b"styp" | b"moov" | b"moof" | b"mdat" | b"free" | b"skip" | b"wide"
        )
    {
        return Some(ContainerKind::Mp4);
    }
    // Sync may not sit at offset 0: m2ts puts a 4-byte TP_extra_header
    // before each sync byte, and a mid-stream join can carry partial-packet
    // garbage. Find a sync byte within one packet and confirm the stride.
    for i in 0..head.len().min(192) {
        if head[i] != 0x47 {
            continue;
        }
        for stride in [188usize, 192] {
            if head.len() > i + 2 * stride
                && head[i + stride] == 0x47
                && head[i + 2 * stride] == 0x47
            {
                return Some(ContainerKind::MpegTs);
            }
        }
    }
    // A head too short to confirm a stride but leading with a sync byte is
    // still most plausibly TS.
    if !head.is_empty() && head[0] == 0x47 && head.len() < 2 * 188 + 1 {
        return Some(ContainerKind::MpegTs);
    }
    if head.starts_with(b"fLaC") {
        return Some(ContainerKind::Flac);
    }
    if head.starts_with(b"OggS") {
        return Some(ContainerKind::Ogg);
    }
    // ID3v2 leads MP3 files in the wild, and occasionally ADTS. Tags
    // routinely exceed any sniffable head, so the sniff does not guess: the
    // MP3 demuxer refuses an ADTS body with a clear message.
    if head.starts_with(b"ID3") {
        return Some(ContainerKind::Mp3);
    }
    // Bare MPEG audio sync: layer bits split ADTS (00) from MP3 (01 = III).
    if head.len() >= 4 && head[0] == 0xFF && head[1] & 0xE0 == 0xE0 {
        if head[1] & 0x06 == 0 {
            return Some(ContainerKind::Adts);
        }
        if head[1] & 0x06 == 0x02 {
            return Some(ContainerKind::Mp3);
        }
    }
    None
}

/// Open the right demuxer for a source by sniffing its head.
pub fn open_auto(
    src: Box<dyn ByteSource>,
    limits: DemuxLimits,
    generation: Generation,
) -> Result<Box<dyn Demuxer>, DemuxError> {
    open_auto_with(src, limits, generation, &DemuxOptions::default())
}

/// As [`open_auto`], with the open-time choices the engine passes through
/// from the session descriptor.
pub fn open_auto_with(
    mut src: Box<dyn ByteSource>,
    limits: DemuxLimits,
    generation: Generation,
    options: &DemuxOptions,
) -> Result<Box<dyn Demuxer>, DemuxError> {
    let mut head = [0u8; 1024];
    let mut filled = 0usize;
    while filled < head.len() {
        let n = src
            .read_at(filled as u64, &mut head[filled..])
            .map_err(DemuxError::Source)?;
        if n == 0 {
            break;
        }
        filled += n;
    }
    match sniff_container(&head[..filled]) {
        Some(ContainerKind::Mp4) => Ok(Box::new(Mp4Demuxer::open_with(
            src, limits, generation, options,
        )?)),
        Some(ContainerKind::MpegTs) => Ok(Box::new(TsDemuxer::open(src, limits, generation)?)),
        Some(ContainerKind::Mkv) => Ok(Box::new(MkvDemuxer::open_with(
            src, limits, generation, options,
        )?)),
        Some(ContainerKind::Flac) => Ok(Box::new(FlacDemuxer::open(src, limits, generation)?)),
        Some(ContainerKind::Ogg) => Ok(Box::new(OggOpusDemuxer::open(src, limits, generation)?)),
        Some(ContainerKind::Mp3) => Ok(Box::new(Mp3Demuxer::open(src, limits, generation)?)),
        Some(ContainerKind::Adts) => Ok(Box::new(AdtsDemuxer::open(src, limits, generation)?)),
        Some(ContainerKind::Wav) => Ok(Box::new(WavDemuxer::open(src, limits, generation)?)),
        None => Err(DemuxError::Unsupported(
            "unrecognised container (expected MP4, MPEG-TS, Matroska, WAV, FLAC, Ogg, MP3 or ADTS)",
        )),
    }
}

use std::fmt;

#[derive(Debug)]
pub enum DemuxError {
    Io(std::io::Error),
    /// The byte source failed underneath the demuxer.
    Source(SourceError),
    Parse(String),
    Unsupported(&'static str),
    /// Every track was left out because nothing here can play it. The
    /// text is the reasons, worded for the viewer.
    Refused(String),
    /// A parse-time cap tripped: typed refusal, never exhaustion.
    Cap(&'static str),
}

impl fmt::Display for DemuxError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Io(e) => write!(f, "io: {e}"),
            Self::Source(e) => write!(f, "source: {e}"),
            Self::Parse(e) => write!(f, "parse: {e}"),
            Self::Unsupported(what) => write!(f, "unsupported: {what}"),
            Self::Refused(why) => f.write_str(why),
            Self::Cap(what) => write!(f, "cap exceeded: {what}"),
        }
    }
}

impl std::error::Error for DemuxError {}
