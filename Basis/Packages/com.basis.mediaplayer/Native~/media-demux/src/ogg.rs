//! Ogg demuxer: the `ogg` crate walks pages; this wrapper follows the
//! first Opus, Vorbis or FLAC stream and gives every packet an exact pts
//! from its own duration (granule positions only re-state it).
//!
//! Opus durations come from the TOC byte, and the timeline shifts by
//! pre-skip so priming samples carry negative pts, which the engine's
//! origin drop consumes. Vorbis packets are timed from the setup header's
//! modes (see `vorbis_timing`), and the first packet after a start or seek
//! outputs nothing. Ogg FLAC packets are whole frames, each stating its
//! block size.

use std::collections::VecDeque;
use std::io::{Read, Seek, SeekFrom};

use media_clock::{Generation, MediaTime};
use ogg::PacketReader;

use crate::artwork;
use crate::vorbis_timing::VorbisTiming;
use crate::{
    Artwork, Au, AudioCodec, ByteSource, DemuxError, DemuxLimits, Demuxer, EosReason, Format,
    StreamEvent, TrackId, push_note,
};

const TRACK: TrackId = TrackId(1);
const OPUS_RATE: u32 = 48_000;
/// Packets read while looking for the chosen stream's first packet, or
/// for its remaining headers, before the stream is taken as malformed.
/// Other streams' headers and early pages can interleave with them.
const HEADER_PACKET_CAP: usize = 256;

/// `Read + Seek` over a [`ByteSource`] for the page walker, with the same
/// served-bytes budget the Matroska path carries: a hostile layout must
/// become a typed refusal, not a spin.
struct SourceIo {
    src: Box<dyn ByteSource>,
    pos: u64,
    len: Option<u64>,
    served: u64,
    served_budget: u64,
    eof_reads: u32,
}

const EOF_READ_CAP: u32 = 1024;

impl Read for SourceIo {
    fn read(&mut self, buf: &mut [u8]) -> std::io::Result<usize> {
        let n = self
            .src
            .read_at(self.pos, buf)
            .map_err(std::io::Error::other)?;
        if n == 0 {
            self.eof_reads += 1;
            if self.eof_reads > EOF_READ_CAP {
                return Err(std::io::Error::other("ogg read budget: end-of-source loop"));
            }
        } else {
            self.served += n as u64;
            if self.served > self.served_budget {
                return Err(std::io::Error::other(
                    "ogg read budget: served bytes exceeded",
                ));
            }
        }
        self.pos += n as u64;
        Ok(n)
    }
}

impl Seek for SourceIo {
    fn seek(&mut self, from: SeekFrom) -> std::io::Result<u64> {
        let new = match from {
            SeekFrom::Start(offset) => offset as i64,
            SeekFrom::Current(delta) => self.pos as i64 + delta,
            SeekFrom::End(delta) => {
                let len = self
                    .len
                    .ok_or_else(|| std::io::Error::other("seek from end on an unsized source"))?;
                len as i64 + delta
            }
        };
        if new < 0 {
            return Err(std::io::Error::other("seek before start"));
        }
        self.pos = new as u64;
        Ok(self.pos)
    }
}

/// Packet duration in 48 kHz samples from the TOC byte (RFC 6716 §3.1).
/// `None` = malformed (code-3 packet without a count byte, or over the
/// 120 ms cap).
fn opus_packet_samples(packet: &[u8]) -> Option<u32> {
    let toc = *packet.first()?;
    let config = toc >> 3;
    let frame_samples: u32 = match config {
        0..=11 => [480, 960, 1920, 2880][usize::from(config % 4)],
        12..=15 => [480, 960][usize::from(config % 2)],
        _ => [120, 240, 480, 960][usize::from(config % 4)],
    };
    let frames: u32 = match toc & 0x03 {
        0 => 1,
        1 | 2 => 2,
        _ => u32::from(*packet.get(1)? & 0x3F),
    };
    if frames == 0 {
        return None;
    }
    let samples = frame_samples.checked_mul(frames)?;
    // RFC 6716: a packet must not exceed 120 ms.
    (samples <= 5760).then_some(samples)
}

enum Codec {
    Opus,
    Vorbis(VorbisTiming),
    Flac,
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum Kind {
    Opus,
    Vorbis,
    Flac,
}

/// Which codec a stream's identification packet announces.
fn identify(first: &[u8]) -> Option<Kind> {
    if first.len() >= 19 && first.starts_with(b"OpusHead") {
        Some(Kind::Opus)
    } else if first.len() >= 30 && first.starts_with(b"\x01vorbis") {
        Some(Kind::Vorbis)
    } else if first.len() >= 51 && first.starts_with(b"\x7fFLAC") && &first[9..13] == b"fLaC" {
        Some(Kind::Flac)
    } else {
        None
    }
}

fn ogg_err(e: impl std::fmt::Display) -> DemuxError {
    DemuxError::Parse(format!("ogg: {e}"))
}

/// Lace Vorbis header packets the way Matroska's `A_VORBIS` CodecPrivate
/// stores them, which is what the decoder takes from either container.
fn lace_headers(packets: [&[u8]; 3]) -> Vec<u8> {
    let mut out = vec![2u8];
    for packet in &packets[..2] {
        out.extend(std::iter::repeat_n(255u8, packet.len() / 255));
        out.push((packet.len() % 255) as u8);
    }
    for packet in packets {
        out.extend_from_slice(packet);
    }
    out
}

/// Whether a packet is one of a stream's header packets rather than audio.
/// A seek that bisects back to the start of the stream reads them again.
fn is_header(codec: &Codec, packet: &[u8]) -> bool {
    match codec {
        Codec::Opus => packet.starts_with(b"OpusHead") || packet.starts_with(b"OpusTags"),
        // Vorbis audio packets have the low bit clear; headers are 1, 3, 5.
        Codec::Vorbis(_) => packet.first().is_some_and(|b| b & 1 == 1),
        // Everything that is not a frame (sync 0xFFF8/0xFFF9) is the
        // mapping header or a metadata block.
        Codec::Flac => !(packet.len() >= 2 && packet[0] == 0xFF && packet[1] & 0xFE == 0xF8),
    }
}

/// The next packet of `serial`, passing over other streams' packets.
fn next_packet_of(reader: &mut PacketReader<SourceIo>, serial: u32) -> Result<Vec<u8>, DemuxError> {
    for _ in 0..HEADER_PACKET_CAP {
        match reader.read_packet().map_err(ogg_err)? {
            Some(packet) if packet.stream_serial() == serial => return Ok(packet.data),
            Some(_) => continue,
            None => break,
        }
    }
    Err(DemuxError::Parse(
        "Ogg stream ended inside its headers".into(),
    ))
}

pub struct OggDemuxer {
    reader: PacketReader<SourceIo>,
    generation: Generation,
    serial: u32,
    codec: Codec,
    sample_rate: u32,
    channels: u32,
    /// Codec private data, held until the format is announced.
    private: Option<Vec<u8>>,
    /// Sample position of the next packet at the stream's own rate,
    /// pre-skip applied (Opus starts negative).
    samples_out: i64,
    notes: Vec<String>,
    foreign_noted: bool,
    ended: bool,
    /// Priming samples the encoder asks be discarded (Opus only); also the
    /// offset between granule positions and playback time.
    pre_skip: i64,
    duration: Option<MediaTime>,
    /// Whether the source can serve the backwards reads a bisection needs.
    seekable: bool,
    /// Cover art from a picture comment, or an Ogg FLAC PICTURE block.
    artwork: Option<Artwork>,
    /// Packets read ahead by a seek to find where its landing page starts,
    /// with their timing, served before anything new is read.
    landed: VecDeque<(Vec<u8>, i64, i64)>,
}

impl OggDemuxer {
    pub fn open(
        mut src: Box<dyn ByteSource>,
        limits: DemuxLimits,
        generation: Generation,
    ) -> Result<Self, DemuxError> {
        let len = src.size().map_err(DemuxError::Source)?;
        // Ogg carries no index and no duration field: the length is the
        // last page's granule position. Read it from the tail before the
        // reader takes the source, scanning backwards for the final page
        // header rather than walking the whole stream.
        let last_granules = match len {
            Some(total) if total > 0 => last_page_granules(src.as_mut(), total)?,
            _ => Vec::new(),
        };
        let seekable = len.is_some();
        let served_budget = limits
            .max_metadata_bytes
            .saturating_add(len.unwrap_or(0).saturating_mul(8));
        let io = SourceIo {
            src,
            pos: 0,
            len,
            served: 0,
            served_budget,
            eof_reads: 0,
        };
        let mut reader = PacketReader::new(io);
        let mut notes = Vec::new();

        // Every stream opens with its identification packet on a page of
        // its own, ahead of any other stream's data (RFC 3533). Take the
        // first one we can play; a video stream ahead of it is noted and
        // passed over.
        let mut first = None;
        for _ in 0..HEADER_PACKET_CAP {
            let Some(packet) = reader.read_packet().map_err(ogg_err)? else {
                break;
            };
            if !packet.first_in_stream() {
                break;
            }
            if let Some(kind) = identify(&packet.data) {
                first = Some((kind, packet));
                break;
            }
            push_note(&mut notes, || {
                format!(
                    "ignoring Ogg stream serial {:#x} (not Opus, Vorbis or FLAC)",
                    packet.stream_serial()
                )
            });
        }
        let Some((kind, first)) = first else {
            return Err(DemuxError::Unsupported(
                "Ogg stream holds no Opus, Vorbis or FLAC audio",
            ));
        };
        let serial = first.stream_serial();
        let ident = first.data;

        let mut pre_skip = 0i64;
        let artwork;
        let (codec, sample_rate, channels, private) = match kind {
            Kind::Opus => {
                let channels = u32::from(ident[9]);
                pre_skip = i64::from(u16::from_le_bytes([ident[10], ident[11]]));
                // Second packet: OpusTags, read for the cover art it may
                // carry and then dropped.
                let tags = next_packet_of(&mut reader, serial)?;
                if !tags.starts_with(b"OpusTags") {
                    return Err(DemuxError::Parse("Ogg Opus stream without OpusTags".into()));
                }
                artwork = artwork_from_tags(&tags, 8);
                (Codec::Opus, OPUS_RATE, channels, ident)
            }
            Kind::Vorbis => {
                let comment = next_packet_of(&mut reader, serial)?;
                let setup = next_packet_of(&mut reader, serial)?;
                if !comment.starts_with(b"\x03vorbis") || !setup.starts_with(b"\x05vorbis") {
                    return Err(DemuxError::Parse(
                        "Ogg Vorbis stream without its comment and setup headers".into(),
                    ));
                }
                artwork = artwork_from_tags(&comment, 7);
                // The identification header: version, channel count,
                // rate, bitrates, then the block sizes timing needs.
                let channels = u32::from(ident[11]);
                let rate = u32::from_le_bytes(ident[12..16].try_into().expect("sliced 4"));
                if !(1..=8).contains(&channels) || rate == 0 {
                    return Err(DemuxError::Unsupported(
                        "Vorbis stream needs 1-8 channels and a sample rate",
                    ));
                }
                let timing = VorbisTiming::new(&ident, &setup).ok_or_else(|| {
                    DemuxError::Parse("Vorbis headers state no usable modes".into())
                })?;
                let private = lace_headers([&ident, &comment, &setup]);
                (Codec::Vorbis(timing), rate, channels, private)
            }
            Kind::Flac => {
                // Ogg FLAC mapping: 0x7F "FLAC", version, a count of the
                // header packets that follow (0 = unstated), then the
                // native "fLaC" signature and STREAMINFO block.
                let stated = u16::from_be_bytes([ident[7], ident[8]]);
                let streaminfo = &ident[13..51];
                if streaminfo[0] & 0x7F != 0 {
                    return Err(DemuxError::Parse("Ogg FLAC without STREAMINFO".into()));
                }
                let info = &streaminfo[4..];
                let rate = (u32::from(info[10]) << 12)
                    | (u32::from(info[11]) << 4)
                    | (u32::from(info[12]) >> 4);
                let channels = u32::from((info[12] >> 1) & 0x07) + 1;
                if rate == 0 {
                    return Err(DemuxError::Parse("Ogg FLAC sample rate 0".into()));
                }
                // The decoder takes a native stream header; STREAMINFO is
                // all it needs, marked as the last block.
                let mut private = b"fLaC".to_vec();
                private.extend_from_slice(streaminfo);
                private[4] |= 0x80;
                // The remaining header packets are metadata blocks; a
                // PICTURE among them is the cover art. An unstated count
                // leaves them to be skipped as headers in the packet loop.
                let mut held = None;
                for _ in 0..usize::from(stated).min(HEADER_PACKET_CAP) {
                    let block = next_packet_of(&mut reader, serial)?;
                    if block.first().is_some_and(|b| b & 0x7F == 6)
                        && let Some(found) = block.get(4..).and_then(artwork::parse_picture_block)
                    {
                        artwork::prefer(&mut held, found);
                    }
                }
                artwork = held.map(|(_, art)| art);
                (Codec::Flac, rate, channels, private)
            }
        };

        let last_granule = last_granules
            .iter()
            .find(|(s, _)| *s == serial)
            .map(|(_, granule)| *granule);
        Ok(Self {
            reader,
            generation,
            serial,
            codec,
            sample_rate,
            channels,
            private: Some(private),
            samples_out: -pre_skip,
            notes,
            foreign_noted: false,
            ended: false,
            pre_skip,
            // A duration implies a working seek bar, so it is gated on the
            // same answer the bisection is.
            duration: match last_granule {
                Some(granule) if seekable => Some(granule_to_time(granule, pre_skip, sample_rate)),
                _ => None,
            },
            seekable,
            artwork,
            landed: VecDeque::new(),
        })
    }

    /// After a bisection: read the audio packets of the page it landed on
    /// and place them from that page's granule position, which states
    /// where its last packet ends. `None` when the stream ends first.
    fn read_landing_page(&mut self) -> Result<Option<i64>, DemuxError> {
        let mut page = Vec::new();
        for _ in 0..HEADER_PACKET_CAP {
            let Some(packet) = self.reader.read_packet().map_err(ogg_err)? else {
                break;
            };
            if packet.stream_serial() != self.serial {
                continue;
            }
            let ends_page = packet.last_in_page();
            let granule = packet.absgp_page();
            if !packet.data.is_empty() && !is_header(&self.codec, &packet.data) {
                let (offset, advance) = self.packet_timing(&packet.data)?;
                page.push((packet.data, offset, advance));
            }
            // -1 marks a page that completes no packet; the packet runs on
            // into the next, whose granule then counts it.
            if ends_page && granule != u64::MAX {
                let counted: i64 = page.iter().map(|(_, _, advance)| *advance).sum();
                self.landed = page.into();
                let end = i64::try_from(granule).unwrap_or(i64::MAX);
                return Ok(Some(
                    end.saturating_sub(self.pre_skip).saturating_sub(counted),
                ));
            }
        }
        self.landed = page.into();
        Ok(None)
    }

    /// For one packet, in samples at the stream rate: where it starts past
    /// the clock, and how far it moves the clock. Only Vorbis ever starts
    /// past it.
    fn packet_timing(&mut self, packet: &[u8]) -> Result<(i64, i64), DemuxError> {
        match &mut self.codec {
            Codec::Opus => opus_packet_samples(packet)
                .map(|samples| (0, i64::from(samples)))
                .ok_or_else(|| DemuxError::Parse("malformed Opus packet TOC".into())),
            Codec::Vorbis(timing) => timing
                .packet(packet)
                .ok_or_else(|| DemuxError::Parse("malformed Vorbis packet".into())),
            Codec::Flac => crate::flac::frame_block_size(packet)
                .map(|samples| (0, i64::from(samples)))
                .ok_or_else(|| DemuxError::Parse("malformed Ogg FLAC frame header".into())),
        }
    }
}

/// Pull the cover art out of a comment header: Vorbis comments after a
/// `magic` of 8 bytes (`OpusTags`) or 7 (`\x03vorbis`). The list is
/// `vendor`, a count, then `NAME=value` entries; `METADATA_BLOCK_PICTURE`
/// holds a base64 FLAC PICTURE block, which is why one parser serves every
/// container that carries one.
fn artwork_from_tags(packet: &[u8], magic: usize) -> Option<Artwork> {
    const TAG: &[u8] = b"METADATA_BLOCK_PICTURE=";
    let le32 = |at: usize| -> Option<usize> {
        Some(u32::from_le_bytes(packet.get(at..at + 4)?.try_into().ok()?) as usize)
    };
    let vendor_len = le32(magic)?;
    let mut at = (magic + 4).checked_add(vendor_len)?;
    let count = le32(at)?;
    at += 4;
    let mut held: Option<(u32, Artwork)> = None;
    for _ in 0..count.min(1024) {
        let len = le32(at)?;
        at += 4;
        let comment = packet.get(at..at.checked_add(len)?)?;
        at += len;
        // The name is case-insensitive per the Vorbis comment spec.
        if comment.len() > TAG.len()
            && comment[..TAG.len()].eq_ignore_ascii_case(TAG)
            && let Some(raw) = artwork::base64(&comment[TAG.len()..])
            && let Some(found) = artwork::parse_picture_block(&raw)
        {
            artwork::prefer(&mut held, found);
        }
    }
    held.map(|(_, art)| art)
}

/// Playback time for an absolute granule position. Granules count samples
/// at the stream rate (48 kHz for Opus) from before the priming ones,
/// which never reach the output.
fn granule_to_time(granule: u64, pre_skip: i64, rate: u32) -> MediaTime {
    let samples = (granule as i64).saturating_sub(pre_skip).max(0);
    MediaTime::from_micros(samples.saturating_mul(1_000_000) / i64::from(rate.max(1)))
}

/// The granule position of the last page of each stream in the tail,
/// found by scanning backwards for capture patterns. Bounded: a stream
/// whose final page sits further back than this is treated as having no
/// stated duration rather than read in full.
fn last_page_granules(src: &mut dyn ByteSource, total: u64) -> Result<Vec<(u32, u64)>, DemuxError> {
    const TAIL: u64 = 64 * 1024;
    let start = total.saturating_sub(TAIL);
    let mut tail = vec![0u8; (total - start) as usize];
    src.read_exact_at(start, &mut tail)
        .map_err(DemuxError::Source)?;
    let mut found: Vec<(u32, u64)> = Vec::new();
    // A page header is 27 bytes; the granule sits at offset 6 and the
    // serial at 14.
    for i in (0..tail.len().saturating_sub(27)).rev() {
        if &tail[i..i + 4] == b"OggS" && tail[i + 4] == 0 {
            let granule = u64::from_le_bytes(tail[i + 6..i + 14].try_into().expect("sliced 8"));
            let serial = u32::from_le_bytes(tail[i + 14..i + 18].try_into().expect("sliced 4"));
            // -1 marks a page that completes no packet; keep looking.
            if granule != u64::MAX && !found.iter().any(|(s, _)| *s == serial) {
                found.push((serial, granule));
            }
        }
    }
    Ok(found)
}

impl Demuxer for OggDemuxer {
    fn next_event(&mut self) -> Result<StreamEvent, DemuxError> {
        if let Some(private) = self.private.take() {
            let codec = match self.codec {
                Codec::Opus => AudioCodec::Opus,
                Codec::Vorbis { .. } => AudioCodec::Vorbis,
                Codec::Flac => AudioCodec::Flac,
            };
            return Ok(StreamEvent::Format(
                TRACK,
                Format::Audio {
                    codec,
                    sample_rate: self.sample_rate,
                    channels: self.channels,
                    codec_private: private,
                },
            ));
        }
        if self.ended {
            return Ok(StreamEvent::Eos(EosReason::Natural));
        }
        if let Some((data, offset, advance)) = self.landed.pop_front() {
            let pts = MediaTime::from_micros(
                (self.samples_out + offset).saturating_mul(1_000_000) / i64::from(self.sample_rate),
            );
            self.samples_out += advance;
            return Ok(StreamEvent::Au(Au {
                track: TRACK,
                data,
                pts,
                dts: pts,
                key: true,
                generation: self.generation,
            }));
        }
        loop {
            let packet = match self.reader.read_packet().map_err(ogg_err)? {
                Some(packet) => packet,
                None => {
                    self.ended = true;
                    return Ok(StreamEvent::Eos(EosReason::Natural));
                }
            };
            if packet.stream_serial() != self.serial {
                // Multiplexed or chained secondary stream: one stream is
                // the contract here.
                if !self.foreign_noted {
                    self.foreign_noted = true;
                    push_note(&mut self.notes, || {
                        format!(
                            "ignoring packets from Ogg stream serial {:#x}",
                            packet.stream_serial()
                        )
                    });
                }
                continue;
            }
            if packet.data.is_empty() || is_header(&self.codec, &packet.data) {
                continue;
            }
            let (offset, advance) = self.packet_timing(&packet.data)?;
            let rate = i64::from(self.sample_rate);
            let pts = MediaTime::from_micros(
                (self.samples_out + offset).saturating_mul(1_000_000) / rate,
            );
            self.samples_out += advance;
            return Ok(StreamEvent::Au(Au {
                track: TRACK,
                data: packet.data,
                pts,
                dts: pts,
                key: true,
                generation: self.generation,
            }));
        }
    }

    fn seek(&mut self, target: MediaTime, generation: Generation) -> Result<MediaTime, DemuxError> {
        if !self.seekable {
            return Err(DemuxError::Unsupported("seek on a streaming Ogg source"));
        }
        let rate = i64::from(self.sample_rate);
        let at = target.as_micros().max(0).saturating_mul(rate) / 1_000_000;
        let goal = at.saturating_add(self.pre_skip).max(0) as u64;
        let landed = self
            .reader
            .seek_absgp(Some(self.serial), goal)
            .map_err(|e| DemuxError::Parse(format!("ogg seek: {e}")))?;
        if !landed {
            return Err(DemuxError::Parse(
                "ogg seek found no page at or after the target".into(),
            ));
        }
        // The bisection can land mid-packet, and a half-packet carried
        // over from before the seek parses as a malformed TOC. Drop what
        // the reader is still holding so the next read starts clean.
        self.reader.delete_unread_packets();
        self.generation = generation;
        // The decoder restarts its overlap from the next packet.
        if let Codec::Vorbis(timing) = &mut self.codec {
            timing.restart();
        }
        // The bisection lands on a page boundary at or before the target,
        // and a page can hold a second or more of audio, so the request is
        // no anchor. The landing page's granule states where its last
        // packet ends; its packets' own durations, counted back from there,
        // say where its first one starts.
        self.ended = false;
        self.samples_out = self.read_landing_page()?.unwrap_or(at);
        Ok(MediaTime::from_micros(
            self.samples_out.saturating_mul(1_000_000) / rate,
        ))
    }

    fn duration(&self) -> Option<MediaTime> {
        self.duration
    }

    fn audio_track(&self) -> Option<TrackId> {
        Some(TRACK)
    }

    fn artwork(&self) -> Option<&Artwork> {
        self.artwork.as_ref()
    }

    fn take_notes(&mut self) -> Vec<String> {
        std::mem::take(&mut self.notes)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn tags_packet(comments: &[&[u8]]) -> Vec<u8> {
        let mut out = b"OpusTags".to_vec();
        let vendor = b"test";
        out.extend_from_slice(&(vendor.len() as u32).to_le_bytes());
        out.extend_from_slice(vendor);
        out.extend_from_slice(&(comments.len() as u32).to_le_bytes());
        for c in comments {
            out.extend_from_slice(&(c.len() as u32).to_le_bytes());
            out.extend_from_slice(c);
        }
        out
    }

    fn base64_encode(data: &[u8]) -> Vec<u8> {
        const A: &[u8] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        let mut out = Vec::new();
        for chunk in data.chunks(3) {
            let b = [
                chunk[0],
                *chunk.get(1).unwrap_or(&0),
                *chunk.get(2).unwrap_or(&0),
            ];
            let n = (u32::from(b[0]) << 16) | (u32::from(b[1]) << 8) | u32::from(b[2]);
            out.push(A[(n >> 18) as usize & 63]);
            out.push(A[(n >> 12) as usize & 63]);
            out.push(if chunk.len() > 1 {
                A[(n >> 6) as usize & 63]
            } else {
                b'='
            });
            out.push(if chunk.len() > 2 {
                A[n as usize & 63]
            } else {
                b'='
            });
        }
        out
    }

    fn picture(data: &[u8]) -> Vec<u8> {
        let mut out = 3u32.to_be_bytes().to_vec();
        out.extend_from_slice(&(9u32).to_be_bytes());
        out.extend_from_slice(b"image/png");
        out.extend_from_slice(&0u32.to_be_bytes());
        out.extend_from_slice(&[0u8; 16]);
        out.extend_from_slice(&(data.len() as u32).to_be_bytes());
        out.extend_from_slice(data);
        out
    }

    /// The picture comment sits behind a vendor string and any number of
    /// ordinary comments, each length-prefixed, and the walk has to step
    /// over all of them to reach it.
    #[test]
    fn art_is_found_behind_the_other_comments() {
        let encoded = base64_encode(&picture(b"pngbytes"));
        let mut comment = b"METADATA_BLOCK_PICTURE=".to_vec();
        comment.extend_from_slice(&encoded);
        let packet = tags_packet(&[b"TITLE=A song", b"ARTIST=Someone", &comment, b"DATE=2026"]);
        let art = artwork_from_tags(&packet, 8).expect("art found");
        assert_eq!(art.mime, "image/png");
        assert_eq!(art.data, b"pngbytes");
    }

    /// A Vorbis comment header carries the same list behind a 7-byte magic.
    #[test]
    fn art_is_found_in_a_vorbis_comment_header() {
        let encoded = base64_encode(&picture(b"jpg"));
        let mut comment = b"METADATA_BLOCK_PICTURE=".to_vec();
        comment.extend_from_slice(&encoded);
        let mut packet = tags_packet(&[b"TITLE=A song", &comment]);
        packet.splice(..8, b"\x03vorbis".iter().copied());
        assert_eq!(
            artwork_from_tags(&packet, 7).expect("art found").data,
            b"jpg"
        );
    }

    /// Vorbis comment names are case-insensitive, and writers disagree.
    #[test]
    fn the_comment_name_is_matched_case_insensitively() {
        let encoded = base64_encode(&picture(b"x"));
        let mut comment = b"metadata_block_picture=".to_vec();
        comment.extend_from_slice(&encoded);
        assert!(artwork_from_tags(&tags_packet(&[&comment]), 8).is_some());
    }

    #[test]
    fn tags_without_a_picture_yield_none_and_rubbish_never_panics() {
        assert!(artwork_from_tags(&tags_packet(&[b"TITLE=A song"]), 8).is_none());
        let packet = tags_packet(&[b"METADATA_BLOCK_PICTURE=not base64"]);
        assert!(artwork_from_tags(&packet, 8).is_none());
        for len in 0..48usize {
            let bytes: Vec<u8> = (0..len).map(|i| (i * 5 + 3) as u8).collect();
            let _ = artwork_from_tags(&bytes, 8);
            let _ = artwork_from_tags(&bytes, 7);
        }
    }

    #[test]
    fn header_packets_are_told_from_audio() {
        assert!(is_header(&Codec::Flac, b"\x7fFLAC\x01\x00"));
        assert!(is_header(&Codec::Flac, &[0x04, 0, 0, 8]));
        assert!(!is_header(&Codec::Flac, &[0xFF, 0xF8, 0x69, 0x08]));
        assert!(is_header(&Codec::Opus, b"OpusTags...."));
        assert!(!is_header(&Codec::Opus, &[0xFC, 0xFF]));
    }

    #[test]
    fn identification_packets_are_recognised_and_short_ones_are_not() {
        let mut flac = b"\x7fFLAC\x01\x00\x00\x01fLaC".to_vec();
        flac.resize(51, 0);
        assert!(identify(&flac) == Some(Kind::Flac));
        assert!(identify(&flac[..50]).is_none());
        let mut vorbis = b"\x01vorbis".to_vec();
        vorbis.resize(30, 0);
        assert!(identify(&vorbis) == Some(Kind::Vorbis));
        assert!(identify(b"\x80theora").is_none());
    }
}
