//! Ogg Opus demuxer: the `ogg` crate walks pages; this wrapper
//! follows the OpusHead stream, derives per-packet durations from the TOC
//! byte (exact for valid streams; granule positions only re-state it) and
//! shifts the timeline by pre-skip so priming samples carry negative pts,
//! which the engine's origin drop consumes.

use std::io::{Read, Seek, SeekFrom};

use media_clock::{Generation, MediaTime};
use ogg::PacketReader;

use crate::artwork;
use crate::{
    Artwork, Au, AudioCodec, ByteSource, DemuxError, DemuxLimits, Demuxer, EosReason, Format,
    StreamEvent, TrackId, push_note,
};

const TRACK: TrackId = TrackId(1);
const OPUS_RATE: i64 = 48_000;

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
fn packet_samples(packet: &[u8]) -> Option<u32> {
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

pub struct OggOpusDemuxer {
    reader: PacketReader<SourceIo>,
    generation: Generation,
    serial: u32,
    channels: u32,
    /// OpusHead, announced as codec private data.
    head: Option<Vec<u8>>,
    /// 48 kHz sample position of the next packet, pre-skip applied (starts
    /// negative).
    samples_out: i64,
    notes: Vec<String>,
    foreign_noted: bool,
    ended: bool,
    /// 48 kHz priming samples the encoder asks be discarded; also the
    /// offset between granule positions and playback time.
    pre_skip: i64,
    duration: Option<MediaTime>,
    /// Whether the source can serve the backwards reads a bisection needs.
    seekable: bool,
    /// Cover art from a METADATA_BLOCK_PICTURE comment in OpusTags.
    artwork: Option<Artwork>,
}

impl OggOpusDemuxer {
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
        let last_granule = match len {
            Some(total) if total > 0 => last_page_granule(src.as_mut(), total)?,
            _ => None,
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

        // First packet of the first stream must be OpusHead (RFC 7845).
        let first = reader
            .read_packet()
            .map_err(|e| DemuxError::Parse(format!("ogg: {e}")))?
            .ok_or(DemuxError::Unsupported("empty Ogg stream"))?;
        if first.data.len() < 19 || !first.data.starts_with(b"OpusHead") {
            return Err(DemuxError::Unsupported(
                "Ogg stream is not Opus (no OpusHead)",
            ));
        }
        let serial = first.stream_serial();
        let channels = u32::from(first.data[9]);
        let pre_skip = i64::from(u16::from_le_bytes([first.data[10], first.data[11]]));

        // Second packet: OpusTags, read for the cover art it may carry
        // and then dropped.
        let tags = reader
            .read_packet()
            .map_err(|e| DemuxError::Parse(format!("ogg: {e}")))?;
        let Some(tags) = tags.filter(|t| t.data.starts_with(b"OpusTags")) else {
            return Err(DemuxError::Parse("Ogg Opus stream without OpusTags".into()));
        };
        let artwork = artwork_from_tags(&tags.data);

        Ok(Self {
            reader,
            generation,
            serial,
            channels,
            head: Some(first.data),
            samples_out: -pre_skip,
            notes: Vec::new(),
            foreign_noted: false,
            ended: false,
            pre_skip,
            // A duration implies a working seek bar, so it is gated on the
            // same answer the bisection is.
            duration: match last_granule {
                Some(granule) if seekable => Some(granule_to_time(granule, pre_skip)),
                _ => None,
            },
            seekable,
            artwork,
        })
    }
}

/// Pull the cover art out of an OpusTags packet. The comment list is
/// `vendor`, a count, then `NAME=value` entries; `METADATA_BLOCK_PICTURE`
/// holds a base64 FLAC PICTURE block, which is why one parser serves both
/// containers.
fn artwork_from_tags(packet: &[u8]) -> Option<Artwork> {
    const TAG: &[u8] = b"METADATA_BLOCK_PICTURE=";
    let le32 = |at: usize| -> Option<usize> {
        Some(u32::from_le_bytes(packet.get(at..at + 4)?.try_into().ok()?) as usize)
    };
    // "OpusTags" (8), vendor length + vendor, then the comment count.
    let vendor_len = le32(8)?;
    let mut at = 12usize.checked_add(vendor_len)?;
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

/// Playback time for an absolute granule position: granules count 48 kHz
/// samples from before the priming ones, which never reach the output.
fn granule_to_time(granule: u64, pre_skip: i64) -> MediaTime {
    let samples = (granule as i64).saturating_sub(pre_skip).max(0);
    MediaTime::from_micros(samples.saturating_mul(1_000_000) / 48_000)
}

/// The granule position of the last page in the stream, found by scanning
/// backwards through the tail for a capture pattern. Bounded: a stream
/// whose final page sits further back than this is treated as having no
/// stated duration rather than read in full.
fn last_page_granule(src: &mut dyn ByteSource, total: u64) -> Result<Option<u64>, DemuxError> {
    const TAIL: u64 = 64 * 1024;
    let start = total.saturating_sub(TAIL);
    let mut tail = vec![0u8; (total - start) as usize];
    src.read_exact_at(start, &mut tail)
        .map_err(DemuxError::Source)?;
    // A page header is 27 bytes; the granule sits at offset 6.
    for i in (0..tail.len().saturating_sub(27)).rev() {
        if &tail[i..i + 4] == b"OggS" && tail[i + 4] == 0 {
            let granule = u64::from_le_bytes(tail[i + 6..i + 14].try_into().expect("sliced 8"));
            // -1 marks a page that completes no packet; keep looking.
            if granule != u64::MAX {
                return Ok(Some(granule));
            }
        }
    }
    Ok(None)
}

impl Demuxer for OggOpusDemuxer {
    fn next_event(&mut self) -> Result<StreamEvent, DemuxError> {
        if let Some(head) = self.head.take() {
            return Ok(StreamEvent::Format(
                TRACK,
                Format::Audio {
                    codec: AudioCodec::Opus,
                    sample_rate: OPUS_RATE as u32,
                    channels: self.channels,
                    codec_private: head,
                },
            ));
        }
        if self.ended {
            return Ok(StreamEvent::Eos(EosReason::Natural));
        }
        loop {
            let packet = match self
                .reader
                .read_packet()
                .map_err(|e| DemuxError::Parse(format!("ogg: {e}")))?
            {
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
            if packet.data.is_empty() {
                continue;
            }
            // The two header packets carry no audio and no valid TOC. They
            // are consumed at open, but a seek that bisects back to the
            // start of the stream reads them again.
            if packet.data.starts_with(b"OpusHead") || packet.data.starts_with(b"OpusTags") {
                continue;
            }
            let Some(samples) = packet_samples(&packet.data) else {
                return Err(DemuxError::Parse("malformed Opus packet TOC".into()));
            };
            let pts = MediaTime::from_micros(self.samples_out * 1_000_000 / OPUS_RATE);
            self.samples_out += i64::from(samples);
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
        let goal = (target.as_micros().max(0).saturating_mul(48_000) / 1_000_000)
            .saturating_add(self.pre_skip)
            .max(0) as u64;
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
        // The bisection lands on a page boundary at or before the target,
        // and a page can hold many packets, so the timeline is re-anchored
        // on the request rather than on the page's granule, which states
        // the end of the page's last packet, not the start of its first.
        // Approximate, as seeking an indexless container is in every player.
        self.samples_out = target.as_micros().max(0).saturating_mul(48_000) / 1_000_000;
        self.ended = false;
        Ok(MediaTime::from_micros(
            self.samples_out.saturating_mul(1_000_000) / 48_000,
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
        let art = artwork_from_tags(&packet).expect("art found");
        assert_eq!(art.mime, "image/png");
        assert_eq!(art.data, b"pngbytes");
    }

    /// Vorbis comment names are case-insensitive, and writers disagree.
    #[test]
    fn the_comment_name_is_matched_case_insensitively() {
        let encoded = base64_encode(&picture(b"x"));
        let mut comment = b"metadata_block_picture=".to_vec();
        comment.extend_from_slice(&encoded);
        assert!(artwork_from_tags(&tags_packet(&[&comment])).is_some());
    }

    #[test]
    fn tags_without_a_picture_yield_none_and_rubbish_never_panics() {
        assert!(artwork_from_tags(&tags_packet(&[b"TITLE=A song"])).is_none());
        let packet = tags_packet(&[b"METADATA_BLOCK_PICTURE=not base64"]);
        assert!(artwork_from_tags(&packet).is_none());
        for len in 0..48usize {
            let bytes: Vec<u8> = (0..len).map(|i| (i * 5 + 3) as u8).collect();
            let _ = artwork_from_tags(&bytes);
        }
    }
}
