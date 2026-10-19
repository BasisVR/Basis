//! MPEG-TS demuxer (PAT/PMT/PES): H.264/H.265 video plus AAC (ADTS) or
//! HDMV LPCM audio, with fuzz-hardened section clamps, a PES accumulation
//! cap and a mid-GOP join guard. The `ts_stream` fuzz target replays the
//! known crash inputs.
//!
//! Live TS over HTTP is Annex-B video + ADTS audio in 188-byte packets;
//! m2ts streams (192-byte packets, a 4-byte TP_extra_header before each
//! sync byte) carry the same tables plus Blu-ray HDMV LPCM audio
//! (stream_type 0x80). Sync is recovered on 0x47 with the packet stride
//! detected from two packets of lookahead; PAT→PMT locate the elementary
//! PIDs; PES packets reassemble per PID (delimited by payload-unit-start,
//! not the untrusted PES_packet_length) and flush as access units. PCR/PTS
//! are 90 kHz; the 33-bit wrap is unwrapped here so nothing downstream
//! sees a wrapped value.

use std::collections::VecDeque;

use media_clock::{Generation, MediaTime};

use crate::demuxer::{DemuxLimits, Demuxer, push_note};
use crate::source::ByteSource;
use crate::{Au, AudioCodec, DemuxError, EosReason, Format, StreamEvent, TrackId, VideoCodec};

const TS_PKT: usize = 188;
/// A PES buffer only flushes on the next payload-unit-start for its PID, so
/// a stream that sets PUSI once and never again would grow it without
/// bound. A single video PES (unbounded PES_packet_length, delimited by the
/// next PUSI) can legitimately reach a few MiB at high bitrate/4K, well
/// under this.
const MAX_PES: usize = 8 * 1024 * 1024;
/// Chunk size for source reads; a handful of packets beyond the stride
/// detector's two-packet lookahead.
const READ_CHUNK: usize = 64 * TS_PKT;

/// Blu-ray channel_assignment -> channel count (0 = reserved/unsupported).
const LPCM_CHANNELS: [u8; 16] = [0, 1, 0, 2, 3, 3, 4, 4, 5, 6, 7, 8, 0, 0, 0, 0];

/// Which of a table's 256 possible `section_number`s have been walked.
/// Fixed size, so a table claiming every section costs 32 bytes and no
/// allocation.
#[derive(Default)]
struct SectionSet([u64; 4]);

impl SectionSet {
    /// Mark `section` walked, reporting whether it already was.
    fn mark(&mut self, section: u8) -> bool {
        let mask = 1u64 << (section & 0x3F);
        let word = &mut self.0[usize::from(section >> 6)];
        let seen = *word & mask != 0;
        *word |= mask;
        seen
    }
}

/// The PMT table whose sections have been walked: the PID that carried it
/// and its `version_number`/`current_next_indicator` group. A table is a
/// different table when either changes, and its sections are tracked
/// individually because a PMT may legitimately span several of them and
/// arrive in any order.
struct PmtTable {
    pid: u16,
    group: u8,
    walked: SectionSet,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum TsVideo {
    H264,
    H265,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum TsAudio {
    Aac,
    Lpcm,
}

/// Unwraps the 33-bit 90 kHz timestamp domain into monotonic ticks. B-frame
/// reorder moves PTS backwards by a few frames at most; only a jump back
/// across more than half the range is a wrap.
#[derive(Default)]
struct PtsUnwrap {
    last: Option<i64>,
    epoch: i64,
}

const PTS_RANGE: i64 = 1 << 33;

impl PtsUnwrap {
    fn unwrap(&mut self, raw: i64) -> i64 {
        if let Some(last) = self.last {
            let last_wrapped = last & (PTS_RANGE - 1);
            if raw < last_wrapped && last_wrapped - raw > PTS_RANGE / 2 {
                self.epoch += PTS_RANGE;
            }
        }
        let value = self.epoch + raw;
        self.last = Some(value);
        value
    }
}

#[derive(Default)]
struct EsAccum {
    buf: Vec<u8>,
    started: bool,
    pts_unwrap: PtsUnwrap,
    dts_unwrap: PtsUnwrap,
}

struct PesHeader {
    pts90: Option<i64>,
    dts90: Option<i64>,
    payload_off: usize,
}

/// The CRC-32/MPEG-2 a PSI section carries in its last four bytes
/// (H.222.0 §2.4.4): polynomial 0x04C11DB7, register all-ones to start,
/// nothing reflected and nothing inverted at the end. Run over the
/// section *including* its own CRC field, a sound one leaves zero.
///
/// Only a section that arrived whole can be checked: a clamped one has
/// its CRC in the packets behind this one, which this parser does not
/// reassemble.
fn psi_crc_ok(section: &[u8]) -> bool {
    let mut crc: u32 = 0xFFFF_FFFF;
    for &byte in section {
        crc ^= u32::from(byte) << 24;
        for _ in 0..8 {
            crc = if crc & 0x8000_0000 != 0 {
                (crc << 1) ^ 0x04C1_1DB7
            } else {
                crc << 1
            };
        }
    }
    crc == 0
}

/// PES packet header at the start of `p` (00 00 01 stream_id …).
fn parse_pes_header(p: &[u8]) -> Option<PesHeader> {
    if p.len() < 9 || p[0] != 0 || p[1] != 0 || p[2] != 1 {
        return None;
    }
    let flags = (p[7] >> 6) & 0x3;
    let hdr_len = usize::from(p[8]);
    let ts_at = |off: usize| -> Option<i64> {
        let f = p.get(off..off + 5)?;
        Some(
            (i64::from(f[0] >> 1 & 0x7) << 30)
                | (i64::from(f[1]) << 22)
                | (i64::from(f[2] >> 1) << 15)
                | (i64::from(f[3]) << 7)
                | i64::from(f[4] >> 1),
        )
    };
    let pts90 = if flags & 0x2 != 0 { ts_at(9) } else { None };
    let dts90 = if flags == 0x3 { ts_at(14) } else { None };
    Some(PesHeader {
        pts90,
        dts90,
        payload_off: 9 + hdr_len,
    })
}

fn ticks_to_us(t90: i64) -> i64 {
    t90 * 1000 / 90
}

pub struct TsDemuxer {
    src: Box<dyn ByteSource>,
    limits: DemuxLimits,
    generation: Generation,
    offset: u64,
    carry: Vec<u8>,
    pkt_size: Option<usize>,

    pmt_pid: Option<u16>,
    /// The program the PAT selected. One PMT PID may legally carry the
    /// tables of more than one program, and the section header's
    /// `program_number` is what tells them apart. Without it the first
    /// section walked binds, whichever program it belongs to.
    pmt_program: Option<u16>,
    /// Which PMT sections have been walked, and which table they belong
    /// to. The PID carries that table for the life of the stream, and a
    /// table that has not changed says nothing the last one did not.
    pmt_table: Option<PmtTable>,
    video_pid: Option<u16>,
    audio_pid: Option<u16>,
    video_codec: Option<TsVideo>,
    audio_codec: Option<TsAudio>,
    video: EsAccum,
    audio: EsAccum,

    video_announced: bool,
    audio_announced: bool,
    audio_sample_rate: u32,

    pending: VecDeque<StreamEvent>,
    notes: Vec<String>,
    emit_raw_audio: bool,
    ended: bool,
}

impl TsDemuxer {
    pub fn open(
        src: Box<dyn ByteSource>,
        limits: DemuxLimits,
        generation: Generation,
    ) -> Result<Self, DemuxError> {
        Ok(Self {
            src,
            limits,
            generation,
            offset: 0,
            carry: Vec::new(),
            pkt_size: None,
            pmt_pid: None,
            pmt_program: None,
            pmt_table: None,
            video_pid: None,
            audio_pid: None,
            video_codec: None,
            audio_codec: None,
            video: EsAccum::default(),
            audio: EsAccum::default(),
            video_announced: false,
            audio_announced: false,
            audio_sample_rate: 0,
            pending: VecDeque::new(),
            notes: Vec::new(),
            emit_raw_audio: false,
            ended: false,
        })
    }

    /// Per-track findings the engine should surface as diagnostics.
    pub fn take_notes(&mut self) -> Vec<String> {
        std::mem::take(&mut self.notes)
    }

    pub fn video_track(&self) -> Option<TrackId> {
        self.video_pid.map(|pid| TrackId(u32::from(pid)))
    }

    pub fn audio_track(&self) -> Option<TrackId> {
        self.audio_pid.map(|pid| TrackId(u32::from(pid)))
    }

    /// Conformance/oracle mode: emit audio frames exactly as stored (ADTS
    /// headers kept), so payload hashes compare directly with ffprobe's
    /// per-packet data hashes. Video already leaves as stored (Annex B).
    pub fn set_emit_raw_audio(&mut self, raw: bool) {
        self.emit_raw_audio = raw;
    }

    fn pes_cap(&self) -> usize {
        usize::try_from(self.limits.max_au_bytes)
            .unwrap_or(MAX_PES)
            .min(MAX_PES)
    }

    fn handle_packet(&mut self, pkt: &[u8]) {
        debug_assert!(pkt.len() >= TS_PKT);
        if pkt[0] != 0x47 {
            return;
        }
        let pusi = (pkt[1] >> 6) & 0x1 != 0;
        let pid = (u16::from(pkt[1] & 0x1F) << 8) | u16::from(pkt[2]);
        let afc = (pkt[3] >> 4) & 0x3;
        if afc & 0x1 == 0 {
            return; // no payload
        }
        let mut off = 4usize;
        if afc & 0x2 != 0 {
            off = 5 + usize::from(pkt[4]);
        }
        if off >= TS_PKT {
            return;
        }
        let payload = &pkt[off..TS_PKT];

        // A section starts after the pointer field of a payload-unit-start
        // packet. A continuation payload carries no pointer field, so
        // parsing one reads whatever it holds as a table header.
        if pid == 0 {
            if pusi {
                self.parse_pat(payload);
            }
        } else if Some(pid) == self.pmt_pid {
            if pusi {
                self.parse_pmt(pid, payload);
            }
        } else if Some(pid) == self.video_pid {
            self.feed_es(true, pusi, payload);
        } else if Some(pid) == self.audio_pid {
            self.feed_es(false, pusi, payload);
        }
    }

    fn parse_pat(&mut self, p: &[u8]) {
        // Need the pointer field plus the 8-byte section header after it.
        if p.is_empty() || 1 + usize::from(p[0]) + 8 > p.len() {
            return;
        }
        let s = &p[1 + usize::from(p[0])..];
        // A PAT is a program_association_section in PSI long form, and
        // whatever else arrives on PID 0 is not this table. The guard
        // above admits eight bytes, so the header is here to read.
        if s[0] != 0x00 || s[1] & 0x80 == 0 {
            return;
        }
        // current_next_indicator clear means this copy becomes applicable
        // later rather than being the one in force (H.222.0 §2.4.4.9), so
        // it may not repoint the PMT PID: doing so stops the demuxer
        // reading the PID that is in force, and the new table identity
        // discards the sections already walked under the old one.
        if s[5] & 1 == 0 {
            return;
        }
        let section_len = (usize::from(s[1] & 0x0F) << 8) | usize::from(s[2]);
        // section_len is attacker-controlled (12 bits); this demuxer reads a
        // single packet, so clamp it to what is present rather than run off
        // the buffer.
        let stated = 3 + section_len;
        // Where the section arrived whole, its own check is here to run,
        // and a section that fails it is corrupt rather than merely
        // unexpected: the PID it names is the one the demuxer reads the
        // program map from for the rest of the session.
        if stated <= s.len() && !psi_crc_ok(&s[..stated]) {
            return;
        }
        let total = stated.min(s.len());
        let Some(prog_bytes) = total.checked_sub(8 + 4) else {
            return; // minus 8-byte header, 4-byte CRC
        };
        let mut i = 0;
        while i + 4 <= prog_bytes {
            let prog = &s[8 + i..];
            let program = (u16::from(prog[0]) << 8) | u16::from(prog[1]);
            let pid = (u16::from(prog[2] & 0x1F) << 8) | u16::from(prog[3]);
            if program != 0 {
                // First real program, and which one it is: the pid alone
                // does not identify the table, since another program's
                // may share it.
                self.pmt_pid = Some(pid);
                self.pmt_program = Some(program);
                break;
            }
            i += 4;
        }
    }

    fn parse_pmt(&mut self, table_pid: u16, p: &[u8]) {
        if p.is_empty() || 1 + usize::from(p[0]) + 12 > p.len() {
            return;
        }
        let s = &p[1 + usize::from(p[0])..];
        // A PMT is a program_map_section in PSI long form. Whatever else
        // arrives on this PID is not this table, and must not be able to
        // take the identity a real section needs.
        if s[0] != 0x02 || s[1] & 0x80 == 0 {
            return;
        }
        // And it must be *this* program's table. A PMT PID may legally
        // carry more than one program's sections, distinguished only by
        // the section header's program_number (the table_id_extension
        // of H.222.0 §2.4.4). Without this, two programs sharing a pid
        // both arrive as version 0 section 0, the first one walked binds
        // its elementary streams and latches the identity, and the
        // section belonging to the program the PAT actually selected is
        // then dropped as a repeat. Screening here rather than widening
        // the identity keeps one program's sections reaching the latch,
        // so a pair from two programs cannot thrash it between them.
        let program = (u16::from(s[3]) << 8) | u16::from(s[4]);
        if self.pmt_program.is_some_and(|selected| selected != program) {
            return;
        }
        // current_next_indicator clear means this copy is the table that
        // becomes applicable later rather than the one in force (H.222.0
        // §2.4.4.9), so nothing it lists may bind a PID. Every claiming
        // arm below is guarded on the pid being unbound, so a next table
        // walked first takes a binding the applicable one can no longer
        // replace.
        if s[5] & 1 == 0 {
            return;
        }
        let section_len = (usize::from(s[1] & 0x0F) << 8) | usize::from(s[2]);
        let stated = 3 + section_len;
        // A section longer than the packet carrying it is not malformed:
        // the rest of it is in the packets behind this one, which this
        // parser does not reassemble.
        let whole = stated <= s.len();
        // As on the PAT: a whole section carries its own check, and one
        // that fails it must not bind. Every claiming arm below is
        // guarded on the pid being unbound, so a corrupt copy binding
        // first stands for as long as its version does and the sound copy
        // repeating behind it cannot replace it.
        if whole && !psi_crc_ok(&s[..stated]) {
            return;
        }
        let total = stated.min(s.len()); // clamp: section_len is untrusted
        let Some(es_end) = total.checked_sub(4) else {
            return; // up to CRC
        };
        let prog_info_len = (usize::from(s[10] & 0x0F) << 8) | usize::from(s[11]);
        // Where the descriptors end is where the entries begin. A stated
        // length that runs past the section leaves nothing to walk.
        let first = 12 + prog_info_len;
        if first > es_end {
            return;
        }

        // Where the whole section is here, its entries must tile the space
        // between the descriptors and the CRC exactly, and that is checked
        // before any of it is acted on. A copy whose entries overrun the
        // CRC, or leave a remainder too short to be another entry, is
        // abandoned; consuming the identity on the way would discard the
        // sound copy repeating behind it, and one malformed PMT would strand
        // the program for as long as that version stood. A clamped section
        // is exempt: what lies past the clamp is missing rather than wrong,
        // and refusing it would bind none of the tracks its first packet
        // does name.
        if whole {
            let mut scan = first;
            while scan + 5 <= es_end {
                let es_len = (usize::from(s[scan + 3] & 0x0F) << 8) | usize::from(s[scan + 4]);
                let next = scan + 5 + es_len;
                if next > es_end {
                    return;
                }
                scan = next;
            }
            if scan != es_end {
                return;
            }
        }

        // Only now is this a section the walk will read, so only now does
        // it count as walked: a malformed copy that returned above must
        // not consume the identity a sound one arriving later needs.
        //
        // Repeating the table is how MPEG-TS keeps a late joiner supplied,
        // so a walk belongs to a section this table has not shown yet
        // rather than to a section arriving. The table's identity is the
        // PID plus version_number + current_next_indicator: a PAT that
        // selects a different PMT PID names a different table even where
        // both are at version 0 section 0, which is the usual case.
        let group = s[5] & 0x3F;
        match &mut self.pmt_table {
            Some(table) if table.pid == table_pid && table.group == group => {
                if table.walked.mark(s[6]) {
                    return;
                }
            }
            slot => {
                let mut walked = SectionSet::default();
                walked.mark(s[6]);
                *slot = Some(PmtTable {
                    pid: table_pid,
                    group,
                    walked,
                });
            }
        }
        let mut i = first;
        while i + 5 <= es_end {
            let es = &s[i..];
            let stype = es[0];
            let pid = (u16::from(es[1] & 0x1F) << 8) | u16::from(es[2]);
            let es_len = (usize::from(es[3] & 0x0F) << 8) | usize::from(es[4]);
            match stype {
                0x1B if self.video_pid.is_none() => {
                    self.video_pid = Some(pid);
                    self.video_codec = Some(TsVideo::H264);
                }
                0x24 if self.video_pid.is_none() => {
                    self.video_pid = Some(pid);
                    self.video_codec = Some(TsVideo::H265);
                }
                0x0F | 0x11 if self.audio_pid.is_none() => {
                    self.audio_pid = Some(pid);
                    self.audio_codec = Some(TsAudio::Aac);
                }
                0x80 if self.audio_pid.is_none() => {
                    self.audio_pid = Some(pid);
                    self.audio_codec = Some(TsAudio::Lpcm);
                }
                _ => {
                    // Every entry lands here once video and audio are
                    // bound, and a PMT holds ~36 of them per packet, so
                    // this is the note sink a stream can drive hardest.
                    push_note(&mut self.notes, || {
                        format!("pmt: unclaimed stream_type {stype:#04x} on pid {pid}")
                    });
                }
            }
            i += 5 + es_len;
        }
    }

    fn feed_es(&mut self, is_video: bool, pusi: bool, payload: &[u8]) {
        if pusi {
            if is_video {
                self.flush_video();
            } else {
                self.flush_audio();
            }
            let e = if is_video {
                &mut self.video
            } else {
                &mut self.audio
            };
            e.started = true;
        }
        let cap = self.pes_cap();
        let e = if is_video {
            &mut self.video
        } else {
            &mut self.audio
        };
        if !e.started {
            return;
        }
        if e.buf.len() + payload.len() > cap {
            // Over cap: drop, resync on the next payload-unit-start.
            e.buf.clear();
            e.started = false;
            return;
        }
        e.buf.extend_from_slice(payload);
    }

    /// PES header -> (pts, dts) in µs, unwrapped. DTS falls back to PTS.
    fn pes_times(e: &mut EsAccum, header: &PesHeader) -> (Option<MediaTime>, Option<MediaTime>) {
        let pts = header
            .pts90
            .map(|t| MediaTime::from_micros(ticks_to_us(e.pts_unwrap.unwrap(t))));
        let dts = header
            .dts90
            .map(|t| MediaTime::from_micros(ticks_to_us(e.dts_unwrap.unwrap(t))));
        (pts, dts.or(pts))
    }

    fn flush_video(&mut self) {
        let started = std::mem::take(&mut self.video.started);
        if !started || self.video.buf.is_empty() {
            self.video.buf.clear();
            return;
        }
        let buf = std::mem::take(&mut self.video.buf);
        let Some(codec) = self.video_codec else {
            return;
        };
        let Some(header) = parse_pes_header(&buf) else {
            return;
        };
        if header.payload_off >= buf.len() {
            return;
        }
        let (pts, dts) = Self::pes_times(&mut self.video, &header);
        let au = &buf[header.payload_off..];
        let track = TrackId(u32::from(self.video_pid.unwrap_or(0)));

        if !self.video_announced {
            let (mut width, mut height) = (0u32, 0u32);
            if codec == TsVideo::H264 {
                let dims = media_bitstream::nal_units(au)
                    .filter(|nal| !nal.is_empty() && media_bitstream::h264_nal_type(nal[0]) == 7)
                    .find_map(media_bitstream::sps_dimensions);
                match dims {
                    Some((w, h)) => (width, height) = (w, h),
                    // Mid-GOP join (or an SPS we couldn't read dimensions
                    // from): drop this AU, which can't decode without its IDR
                    // anyway, and wait for the next SPS-bearing keyframe
                    // instead of announcing 0x0 and latching.
                    None => return,
                }
            }
            self.pending.push_back(StreamEvent::Format(
                track,
                Format::Video {
                    codec: match codec {
                        TsVideo::H264 => VideoCodec::H264,
                        TsVideo::H265 => VideoCodec::H265,
                    },
                    coded_width: width,
                    coded_height: height,
                    display_width: width,
                    display_height: height,
                    codec_private: Vec::new(),
                },
            ));
            self.video_announced = true;
        }

        let key = match codec {
            TsVideo::H264 => media_bitstream::h264_is_keyframe(au),
            TsVideo::H265 => media_bitstream::h265_is_keyframe(au),
        };
        let pts = pts.unwrap_or(MediaTime::ZERO);
        self.pending.push_back(StreamEvent::Au(Au {
            track,
            data: au.to_vec(),
            pts,
            dts: dts.unwrap_or(pts),
            key,
            generation: self.generation,
        }));
    }

    fn flush_audio(&mut self) {
        let started = std::mem::take(&mut self.audio.started);
        if !started || self.audio.buf.is_empty() {
            self.audio.buf.clear();
            return;
        }
        let buf = std::mem::take(&mut self.audio.buf);
        let Some(codec) = self.audio_codec else {
            return;
        };
        let Some(header) = parse_pes_header(&buf) else {
            return;
        };
        if header.payload_off >= buf.len() {
            return;
        }
        let (pts, _) = Self::pes_times(&mut self.audio, &header);
        let base = pts.unwrap_or(MediaTime::ZERO);
        let payload = &buf[header.payload_off..];
        match codec {
            TsAudio::Aac => self.flush_audio_adts(payload, base),
            TsAudio::Lpcm => self.flush_audio_lpcm(payload, base),
        }
    }

    fn flush_audio_adts(&mut self, mut p: &[u8], base: MediaTime) {
        let track = TrackId(u32::from(self.audio_pid.unwrap_or(0)));
        let mut frame_idx: i64 = 0;
        while p.len() >= 7 {
            let Some(adts) = media_bitstream::parse_adts(p) else {
                break;
            };
            if adts.frame_len > p.len() {
                break;
            }
            if !self.audio_announced {
                let asc = media_bitstream::build_asc(
                    adts.profile + 1,
                    adts.sample_rate,
                    adts.channel_config,
                );
                let channels = media_bitstream::aac_channels_from_config(adts.channel_config);
                self.audio_sample_rate = adts.sample_rate;
                self.pending.push_back(StreamEvent::Format(
                    track,
                    Format::Audio {
                        codec: AudioCodec::Aac,
                        sample_rate: adts.sample_rate,
                        channels: u32::from(channels),
                        codec_private: asc.to_vec(),
                    },
                ));
                self.audio_announced = true;
            }

            let rate = if adts.sample_rate > 0 {
                i64::from(adts.sample_rate)
            } else {
                48000
            };
            let pts =
                MediaTime::from_micros(base.as_micros() + frame_idx * 1024 * 1_000_000 / rate);
            let data = if self.emit_raw_audio {
                p[..adts.frame_len].to_vec()
            } else {
                p[adts.header_len..adts.frame_len].to_vec()
            };
            self.pending.push_back(StreamEvent::Au(Au {
                track,
                data,
                pts,
                dts: pts,
                key: true,
                generation: self.generation,
            }));
            p = &p[adts.frame_len..];
            frame_idx += 1;
        }
    }

    /// HDMV LPCM PES payload: a 4-byte header (16-bit data length; channel
    /// assignment + sample-rate code; bits-per-sample code) followed by
    /// big-endian PCM in Blu-ray channel order. The decode layer converts
    /// and reorders; the raw assignment + bits codes travel in the format
    /// announce's codec_private.
    fn flush_audio_lpcm(&mut self, p: &[u8], base: MediaTime) {
        if p.len() <= 4 {
            return;
        }
        let track = TrackId(u32::from(self.audio_pid.unwrap_or(0)));
        if !self.audio_announced {
            let assign = (p[2] >> 4) & 0xF;
            let rate_code = p[2] & 0xF;
            let bits_code = (p[3] >> 6) & 0x3; // 1 = 16-bit, 3 = 24-bit
            let channels = LPCM_CHANNELS[usize::from(assign)];
            // Only announce formats the LPCM decode path actually plays:
            // 48 kHz, 16- or 24-bit. Announcing 96/192 kHz or 20-bit would
            // have the decoder refuse and leave the session half-configured;
            // leaving them unannounced is graceful silence (video keeps
            // playing), matching the AAC path's behaviour for unsupported
            // audio.
            if rate_code != 1 || (bits_code != 1 && bits_code != 3) || channels == 0 {
                return;
            }
            self.audio_sample_rate = 48000;
            self.pending.push_back(StreamEvent::Format(
                track,
                Format::Audio {
                    codec: AudioCodec::Pcm,
                    sample_rate: 48000,
                    channels: u32::from(channels),
                    // Assignment + bits code + flags; bit 0 clear marks the
                    // big-endian samples this lane carries.
                    codec_private: vec![assign, bits_code, 0],
                },
            ));
            self.audio_announced = true;
        }
        let dlen = (usize::from(p[0]) << 8) | usize::from(p[1]);
        let body = &p[4..];
        let len = if dlen > 0 && dlen < body.len() {
            dlen
        } else {
            body.len()
        };
        self.pending.push_back(StreamEvent::Au(Au {
            track,
            data: body[..len].to_vec(),
            pts: base,
            dts: base,
            key: true,
            generation: self.generation,
        }));
    }

    /// Scan the carry buffer, consuming whole packets. Returns when the
    /// remaining bytes cannot hold another packet (or stride probe).
    fn scan_carry(&mut self) {
        let mut i = 0usize;
        loop {
            // Stride detection needs two packets of lookahead beyond the
            // sync byte; once locked, one packet at a time.
            let need = self.pkt_size.unwrap_or(2 * 192 + 1);
            if self.carry.len() - i < need {
                break;
            }
            if self.carry[i] != 0x47 {
                i += 1; // resync
                continue;
            }
            let stride = match self.pkt_size {
                Some(s) => s,
                None => {
                    if self.carry.get(i + 188) == Some(&0x47)
                        && self.carry.get(i + 376) == Some(&0x47)
                    {
                        self.pkt_size = Some(188);
                        188
                    } else if self.carry.get(i + 192) == Some(&0x47)
                        && self.carry.get(i + 384) == Some(&0x47)
                    {
                        self.pkt_size = Some(192); // m2ts
                        192
                    } else {
                        i += 1;
                        continue;
                    }
                }
            };
            let pkt = self.carry[i..i + TS_PKT].to_vec();
            self.handle_packet(&pkt);
            i += stride;
        }
        self.carry.drain(..i);
    }
}

impl Demuxer for TsDemuxer {
    fn next_event(&mut self) -> Result<StreamEvent, DemuxError> {
        loop {
            if let Some(event) = self.pending.pop_front() {
                return Ok(event);
            }
            if self.ended {
                return Ok(StreamEvent::Eos(EosReason::Natural));
            }

            let mut chunk = vec![0u8; READ_CHUNK];
            let n = self
                .src
                .read_at(self.offset, &mut chunk)
                .map_err(DemuxError::Source)?;
            if n == 0 {
                self.ended = true;
                self.flush_video();
                self.flush_audio();
                continue;
            }
            self.offset += n as u64;
            chunk.truncate(n);
            self.carry.extend_from_slice(&chunk);
            self.scan_carry();
        }
    }

    fn seek(
        &mut self,
        _target: MediaTime,
        _generation: Generation,
    ) -> Result<MediaTime, DemuxError> {
        Err(DemuxError::Unsupported("MPEG-TS is a live carrier"))
    }

    fn duration(&self) -> Option<MediaTime> {
        None
    }

    fn video_track(&self) -> Option<TrackId> {
        TsDemuxer::video_track(self)
    }

    fn audio_track(&self) -> Option<TrackId> {
        TsDemuxer::audio_track(self)
    }

    fn take_notes(&mut self) -> Vec<String> {
        TsDemuxer::take_notes(self)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn pts_unwrap_spans_the_33_bit_boundary() {
        let mut u = PtsUnwrap::default();
        let near_top = PTS_RANGE - 90_000; // one second before wrap
        assert_eq!(u.unwrap(near_top), near_top);
        // Post-wrap values continue monotonically.
        assert_eq!(u.unwrap(90_000), PTS_RANGE + 90_000);
        // B-frame reorder (small backwards step) is not a wrap.
        assert_eq!(u.unwrap(87_000), PTS_RANGE + 87_000);
    }

    #[test]
    fn pes_header_times() {
        // PES with PTS+DTS: flags 0b11, header length 10.
        let mut pes = vec![0u8, 0, 1, 0xE0, 0, 0, 0x80, 0xC0, 10];
        // PTS 90000 ticks (1 s): 33-bit encoding.
        let enc = |t: i64| -> [u8; 5] {
            [
                (0x3 << 4) | (((t >> 30) as u8 & 0x7) << 1) | 1,
                (t >> 22) as u8,
                (((t >> 15) as u8) << 1) | 1,
                (t >> 7) as u8,
                ((t as u8) << 1) | 1,
            ]
        };
        pes.extend_from_slice(&enc(90_000));
        pes.extend_from_slice(&enc(86_400));
        pes.extend_from_slice(&[0xAA; 4]);
        let h = parse_pes_header(&pes).unwrap();
        assert_eq!(h.pts90, Some(90_000));
        assert_eq!(h.dts90, Some(86_400));
        assert_eq!(h.payload_off, 19);
    }
}
