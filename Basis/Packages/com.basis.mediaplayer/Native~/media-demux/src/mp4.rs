//! Streaming progressive-MP4 demuxer: re_mp4 parses the box structure and
//! sample tables from positioned reads (mdat is skipped, so a trailing moov
//! costs a couple of range requests, never a full download); sample payloads
//! are then range-read on demand as the engine pulls.
//!
//! A fragmented file with an index (a `sidx`, or the `mfra` at its end) is
//! opened from the index alone: `ftyp`, `moov` and the index are all that
//! is read before the first picture, and a movie fragment is parsed when
//! playback or a seek reaches it. What the demuxer holds is then a
//! fragment's worth of sample references whatever the file's length, and a
//! twelve-hour video costs the same to open as a three-minute one.
//!
//! A source with no length is a live stream, read as it arrives (see
//! [`live`]).
//!
//! One video track and one audio track (the one asked for, else the first)
//! are interleaved in decode order. Remaining tracks are reported via
//! [`Mp4Demuxer::take_notes`], and tracks nothing here can play via
//! [`Mp4Demuxer::take_refusals`].

use std::collections::{BTreeMap, HashMap, VecDeque};
use std::io::{Read, Seek, SeekFrom};
use std::panic::{AssertUnwindSafe, catch_unwind};

use media_bitstream::{AudioSpecificConfig, parse_asc};
use media_clock::{Generation, MediaTime};
use re_mp4::{BoxHeader, BoxType, MoofBox, ReadBox as _};

use crate::demuxer::{AudioTrackInfo, DemuxLimits, DemuxOptions, Demuxer, push_note};
use crate::mp4_fragment::{Cursors, FragmentSample, TrackDefaults};
use crate::mp4_index::{IndexEntry, SegmentIndex};
use crate::source::{ByteSource, CachedSource, SourceReader};
use crate::{Au, AudioCodec, DemuxError, EosReason, Format, StreamEvent, TrackId, VideoCodec};

mod live;

#[derive(Debug, Clone, Copy)]
struct SampleRef {
    offset: u64,
    size: u32,
    pts: MediaTime,
    dts: MediaTime,
    sync: bool,
}

/// Where a track's sample references come from.
enum Samples {
    /// The whole table, built at open from `moov` or from a walk of every
    /// movie fragment.
    Whole { all: Vec<SampleRef>, next: usize },
    /// One fragment's worth at a time, read as playback reaches it.
    Held(VecDeque<SampleRef>),
}

impl Samples {
    fn peek(&self) -> Option<SampleRef> {
        match self {
            Self::Whole { all, next } => all.get(*next).copied(),
            Self::Held(held) => held.front().copied(),
        }
    }

    fn take(&mut self) {
        match self {
            Self::Whole { next, .. } => *next += 1,
            Self::Held(held) => {
                held.pop_front();
            }
        }
    }

    fn held(&self) -> usize {
        match self {
            Self::Whole { .. } => 0,
            Self::Held(held) => held.len(),
        }
    }

    /// How far apart in decode time the held samples lie.
    fn held_span(&self) -> MediaTime {
        match self {
            Self::Held(held) => match (held.front(), held.back()) {
                (Some(first), Some(last)) => last.dts.saturating_sub(first.dts),
                _ => MediaTime::ZERO,
            },
            Self::Whole { .. } => MediaTime::ZERO,
        }
    }
}

struct VideoTrack {
    id: TrackId,
    samples: Samples,
    /// H.264/HEVC conversion parameters (HEVC packs VPS/SPS/PPS into
    /// `sps`); `None` for codecs whose samples pass through as stored
    /// (VP9 raw frames, AV1 temporal units).
    avc: Option<AvcParams>,
    /// The edit list's shift, in the track's timescale: where
    /// presentation starts in the media, less any gap before the picture
    /// starts.
    shift: i64,
    /// Where the edit list stops presenting the track.
    end: Option<MediaTime>,
}

struct AvcParams {
    sps: Vec<Vec<u8>>,
    pps: Vec<Vec<u8>>,
    nal_length_size: usize,
}

struct AudioTrack {
    id: TrackId,
    samples: Samples,
    /// The edit list's shift, in the track's timescale: the encoder
    /// priming, which puts the samples before the origin at negative
    /// times, less any gap before the track starts.
    shift: i64,
    /// Where the track starts: the gap, so the priming it shifts past zero
    /// is still dropped.
    start: MediaTime,
    /// Where the edit list stops presenting the track.
    end: Option<MediaTime>,
}

/// The state a file read a fragment at a time carries between fragments.
struct Fragments {
    index: SegmentIndex,
    defaults: BTreeMap<u32, TrackDefaults>,
    cursors: Cursors,
    /// The subsegment to read when the held samples run out.
    next: usize,
}

/// A subsegment's samples, split by the tracks the demuxer has bound.
#[derive(Default)]
struct Loaded {
    video: Vec<SampleRef>,
    audio: Vec<SampleRef>,
}

pub struct Mp4Demuxer {
    src: CachedSource,
    limits: DemuxLimits,
    generation: Generation,
    duration: Option<MediaTime>,
    video: Option<VideoTrack>,
    audio: Option<AudioTrack>,
    pending: VecDeque<StreamEvent>,
    notes: Vec<String>,
    refusals: Vec<String>,
    emit_raw_video: bool,
    audio_tracks: Vec<AudioTrackInfo>,
    /// Cover art from `moov/udta/meta/ilst/covr`.
    artwork: Option<crate::Artwork>,
    /// Present when the file's fragments are read as they are reached.
    fragments: Option<Fragments>,
    /// The data of the fragments whose samples are queued.
    runs: crate::mp4_runs::HeldRuns,
    /// Present when the source has no length and is read as it arrives.
    live: Option<live::Live>,
}

impl Mp4Demuxer {
    pub fn open(
        src: Box<dyn ByteSource>,
        limits: DemuxLimits,
        generation: Generation,
    ) -> Result<Self, DemuxError> {
        Self::open_with(src, limits, generation, &DemuxOptions::default())
    }

    pub fn open_with(
        src: Box<dyn ByteSource>,
        limits: DemuxLimits,
        generation: Generation,
        options: &DemuxOptions,
    ) -> Result<Self, DemuxError> {
        // Cached reads: the box walk revisits headers and fragmented
        // files interleave per-track sample runs, both of which thrash a
        // ranged HTTP source without a cache.
        let mut src = CachedSource::new(src);
        let Some(len) = src.size().map_err(DemuxError::Source)? else {
            return Self::open_live(src, limits, generation, options);
        };

        // `moov` first, and nothing past it (see `scan_prefix`).
        let mut notes = Vec::new();
        let mut budget = limits.max_metadata_bytes;
        let head = {
            let mut reader = SourceReader::new(&mut src, len, budget);
            let head = scan_prefix(&mut reader, 0, len, &mut notes, true);
            budget = reader.remaining_budget();
            head
        };
        // A fragment takes its tracks, defaults and sample descriptions from
        // `moov`, so one met first cannot be read. The delivery formats
        // carrying fragments put `moov` first (RFC 8216 3.3; MSE's
        // initialization segment).
        if head.first_fragment.is_some() && head.moov_end.is_none() {
            return Err(DemuxError::Unsupported(
                "a movie fragment comes before the moov that describes it",
            ));
        }

        // With a trusted index the whole file need not be walked: `moov`
        // and the index say where every fragment is. Without one the
        // sample tables come from a walk of every `moof`, whose fetches
        // are sized to the headers it parses rather than to a cache block
        // (a fragmented file's fragments are far apart, so a block per
        // fragment would exhaust the budget part-way down a long file).
        let mut index = None;
        let mut mp4 = None;
        let mut walked = None;
        let mut configs = None;
        let mut fragmented = false;
        if let Some(moov_end) = head.moov_end {
            let header = read_metadata(&mut src, moov_end, &mut budget, false)?;
            // Read while `moov` is still in the cache, ahead of any index
            // at the far end of the file.
            configs = Some(audio_configs(&header, &mut src, moov_end, &mut budget));
            // `mvex` says fragments may follow `moov`, whether or not
            // `moov` keeps samples of its own. A file without it has
            // described itself entirely by here, and this parse is the
            // whole of it.
            if header.moov.mvex.is_some() {
                fragmented = true;
                let described = header.tracks().values().any(|t| !t.samples.is_empty());
                let video = header
                    .tracks()
                    .iter()
                    .find(|(_, track)| track.kind == Some(re_mp4::TrackKind::Video))
                    .map(|(id, _)| *id);
                let tail = {
                    let mut reader = SourceReader::new(&mut src, len, budget);
                    let tail = scan_prefix(&mut reader, moov_end, len, &mut notes, false);
                    budget = reader.remaining_budget();
                    tail
                };
                let media_end = media_end(&tail.indexes, len, &mut src);
                // An index describes fragments alone: a `moov` holding
                // samples of its own is walked.
                if !described && let Some(first) = tail.first_fragment {
                    let trusted: Vec<SegmentIndex> = tail
                        .indexes
                        .into_iter()
                        .filter(|candidate| {
                            candidate.starts_at(first) && candidate.tiles(media_end)
                        })
                        .collect();
                    index = choose_index(trusted, video);
                    if index.is_none() && media_end < len {
                        let found = MfraSearch {
                            header: &header,
                            video,
                            first,
                            media_end,
                            len,
                        };
                        index = found.index(&mut src, &mut budget, &limits, &mut notes);
                    }
                }
                if index.is_none() {
                    walked = Some(walk_fragments(
                        &mut src,
                        &header,
                        moov_end..media_end,
                        &limits,
                        &mut budget,
                        &mut notes,
                    )?);
                }
                mp4 = Some(header);
            } else {
                mp4 = Some(header);
            }
        }
        let mp4 = match mp4 {
            Some(mp4) => mp4,
            None => read_metadata(&mut src, len, &mut budget, fragmented)?,
        };

        let configs = configs.unwrap_or_else(|| audio_configs(&mp4, &mut src, len, &mut budget));

        let fragments = index.map(|index| Fragments {
            defaults: crate::mp4_fragment::track_defaults(&mp4.moov),
            cursors: Cursors::new(),
            index,
            next: 0,
        });
        let mut this = Self::new(src, limits, generation, notes, &mp4);
        this.fragments = fragments;
        this.bind(&mp4, options, &configs, walked.as_ref())?;
        if this.duration.is_none() {
            this.duration = stated_duration(&mp4, this.fragments.as_ref());
        }
        Ok(this)
    }

    fn new(
        src: CachedSource,
        limits: DemuxLimits,
        generation: Generation,
        notes: Vec<String>,
        mp4: &re_mp4::Mp4,
    ) -> Self {
        Self {
            src,
            limits,
            generation,
            duration: None,
            video: None,
            audio: None,
            pending: VecDeque::new(),
            notes,
            refusals: Vec::new(),
            emit_raw_video: false,
            audio_tracks: Vec::new(),
            artwork: artwork_from_moov(mp4),
            fragments: None,
            runs: crate::mp4_runs::HeldRuns::default(),
            live: None,
        }
    }

    /// Bind the tracks, failing when none of them can be played.
    fn bind(
        &mut self,
        mp4: &re_mp4::Mp4,
        options: &DemuxOptions,
        configs: &HashMap<u32, Vec<u8>>,
        walked: Option<&Walked>,
    ) -> Result<(), DemuxError> {
        self.extract_tracks(mp4, options, configs, walked)?;
        if self.video.is_none() && self.audio.is_none() {
            if !self.refusals.is_empty() {
                return Err(DemuxError::Refused(self.refusals.join("; ")));
            }
            return Err(DemuxError::Unsupported(
                "no decodable track (need H.264, H.265, VP9 or AV1 video, or AAC or MP3 audio)",
            ));
        }
        Ok(())
    }

    /// Per-track findings the engine should surface as diagnostics
    /// (skipped tracks, refused layouts). Drained once after open.
    pub fn take_notes(&mut self) -> Vec<String> {
        std::mem::take(&mut self.notes)
    }

    /// Tracks left out because nothing here can play them.
    pub fn take_refusals(&mut self) -> Vec<String> {
        std::mem::take(&mut self.refusals)
    }

    pub fn video_track(&self) -> Option<TrackId> {
        self.video.as_ref().map(|v| v.id)
    }

    /// Conformance/oracle mode: emit video payloads exactly as stored
    /// (length-prefixed), skipping Annex-B conversion, so payload hashes
    /// compare directly with ffprobe's per-packet data hashes, keyframes
    /// included. The conversion itself is covered by the decode tests.
    pub fn set_emit_raw_video(&mut self, raw: bool) {
        self.emit_raw_video = raw;
    }

    pub fn audio_track(&self) -> Option<TrackId> {
        self.audio.as_ref().map(|a| a.id)
    }

    fn extract_tracks(
        &mut self,
        mp4: &re_mp4::Mp4,
        options: &DemuxOptions,
        configs: &HashMap<u32, Vec<u8>>,
        walked: Option<&Walked>,
    ) -> Result<(), DemuxError> {
        let mut duration = MediaTime::ZERO;

        // The audio tracks a caller could pick between, in container
        // order, before any of them is bound. Only offered when there is
        // more than one: a picker with a single entry is not a choice.
        let audio_ids: Vec<u32> = mp4
            .tracks()
            .iter()
            .filter(|(_, track)| is_audio(mp4, track))
            .map(|(id, _)| *id)
            .collect();
        // Bind the requested track, or the first when the index is out of
        // range; an undecodable choice falls through to the next below.
        if audio_ids.len() > 1 {
            self.audio_tracks = audio_ids
                .iter()
                .filter_map(|id| {
                    let track = mp4.tracks().get(id)?;
                    let config = configs.get(id).and_then(|raw| parse_asc(raw));
                    Some(describe_audio(mp4, track, TrackId(*id), config))
                })
                .collect();
        }
        let wanted = audio_ids.get(options.audio_track).copied();
        if wanted.is_none() && options.audio_track != 0 {
            push_note(&mut self.notes, || {
                format!(
                    "audio track {} requested, container has {}; using the first",
                    options.audio_track,
                    audio_ids.len()
                )
            });
        }

        for (id, track) in mp4.tracks() {
            let track_id = TrackId(*id);
            let walked = walked.map(|w| w.get(id).map_or(&[][..], Vec::as_slice));
            let audio = is_audio(mp4, track);
            match track.kind {
                Some(re_mp4::TrackKind::Video) if self.video.is_none() => {
                    match self.extract_video(mp4, track, track_id, walked)? {
                        Some(()) => {}
                        None => continue,
                    }
                }
                // Skip past the unwanted tracks until the chosen one is
                // reached; if that one turns out undecodable the next
                // decodable track takes over, which is why this is not an
                // equality test.
                _ if audio && self.audio.is_none() && wanted.is_none_or(|w| *id >= w) => {
                    let config = configs.get(id).map(Vec::as_slice);
                    if self
                        .extract_audio(mp4, track, track_id, config, walked)
                        .is_none()
                    {
                        continue;
                    }
                }
                // An entry the box parser does not know has no kind of its
                // own, but the handler still says what the track was for.
                _ => {
                    let trak = track.trak(mp4);
                    let entry = sample_entry(&trak.mdia.minf.stbl.stsd.contents);
                    let refusal = match (track.kind, &trak.mdia.hdlr.handler_type.value) {
                        (None, b"vide") if self.video.is_none() => Some(unsupported_video(&entry)),
                        (None, b"soun") if self.audio.is_none() && !audio => Some(format!(
                            "audio codec '{entry}' is not supported (supported: AAC, MP3)"
                        )),
                        _ => None,
                    };
                    match refusal {
                        Some(refusal) => push_note(&mut self.refusals, || refusal),
                        None => push_note(&mut self.notes, || {
                            format!("track {id}: skipped ({:?})", track.kind)
                        }),
                    }
                    continue;
                }
            }
            let shift = [
                self.video.as_ref().map(|v| (v.id, v.shift)),
                self.audio.as_ref().map(|a| (a.id, a.shift)),
            ]
            .into_iter()
            .flatten()
            .find(|(id, _)| *id == track_id)
            .map_or(0, |(_, shift)| shift);
            let stated = presented_span(mp4, track)
                .max(walked.and_then(|samples| walked_end(samples, track.timescale, shift)));
            if let Some(track_duration) = stated {
                duration = duration.max(track_duration);
            }
        }

        if duration > MediaTime::ZERO {
            self.duration = Some(duration);
        }
        Ok(())
    }

    fn extract_video(
        &mut self,
        mp4: &re_mp4::Mp4,
        track: &re_mp4::Track,
        track_id: TrackId,
        walked: Option<&[FragmentSample]>,
    ) -> Result<Option<()>, DemuxError> {
        let stsd = &track.trak(mp4).mdia.minf.stbl.stsd;
        let mut codec_private = Vec::new();
        let (codec, box_width, box_height, avc) = match &stsd.contents {
            re_mp4::StsdBoxContent::Avc1(avc1) => {
                let avcc = &avc1.avcc.contents;
                (
                    VideoCodec::H264,
                    u32::from(avc1.width),
                    u32::from(avc1.height),
                    Some(AvcParams {
                        sps: avcc
                            .sequence_parameter_sets
                            .iter()
                            .map(|n| n.bytes.clone())
                            .collect(),
                        pps: avcc
                            .picture_parameter_sets
                            .iter()
                            .map(|n| n.bytes.clone())
                            .collect(),
                        nal_length_size: (avcc.length_size_minus_one & 0x3) as usize + 1,
                    }),
                )
            }
            re_mp4::StsdBoxContent::Hev1(hev) | re_mp4::StsdBoxContent::Hvc1(hev) => {
                let config = crate::hevc::parse_hvcc(&hev.hvcc.raw)?;
                (
                    VideoCodec::H265,
                    u32::from(hev.width),
                    u32::from(hev.height),
                    Some(AvcParams {
                        sps: config.sps,
                        pps: config.pps,
                        nal_length_size: config.nal_length_size,
                    }),
                )
            }
            // VP9/AV1 samples are stored as the decoders take them (raw
            // frames / temporal units): announce and pass through.
            re_mp4::StsdBoxContent::Vp09(vp09) => (
                VideoCodec::Vp9,
                u32::from(vp09.width),
                u32::from(vp09.height),
                None,
            ),
            re_mp4::StsdBoxContent::Av01(av01) => {
                // The av1C payload is a 4-byte header then the config OBUs
                // (the sequence header); hardware decoders want the OBUs
                // prepended to the first AU.
                if av01.av1c.raw.len() > 4 {
                    codec_private = av01.av1c.raw[4..].to_vec();
                }
                (
                    VideoCodec::Av1,
                    u32::from(av01.width),
                    u32::from(av01.height),
                    None,
                )
            }
            _ => {
                push_note(&mut self.refusals, || {
                    unsupported_video(&sample_entry(&stsd.contents))
                });
                return Ok(None);
            }
        };

        // The first real edit names where presentation starts in the
        // media, usually the reorder delay; with no edit list the times
        // are presented as the file states them.
        let trak = track.trak(mp4);
        let edits = edit_start(trak, u64::from(mp4.moov.mvhd.timescale), track.timescale);
        let shift = edits.media_time.saturating_sub(edits.empty);
        let end = edit_end(trak, u64::from(mp4.moov.mvhd.timescale));
        let samples = self.new_samples(self.collect_samples(
            track,
            moov_reorder(track, trak),
            walked,
            shift,
            end,
        )?);
        let width = if box_width != 0 {
            box_width
        } else {
            track.width as u32
        };
        let height = if box_height != 0 {
            box_height
        } else {
            track.height as u32
        };

        self.pending.push_back(StreamEvent::Format(
            track_id,
            Format::Video {
                codec,
                coded_width: width,
                coded_height: height,
                display_width: width,
                display_height: height,
                codec_private,
            },
        ));
        self.video = Some(VideoTrack {
            id: track_id,
            samples,
            avc,
            shift,
            end,
        });
        Ok(Some(()))
    }

    fn extract_audio(
        &mut self,
        mp4: &re_mp4::Mp4,
        track: &re_mp4::Track,
        track_id: TrackId,
        config: Option<&[u8]>,
        walked: Option<&[FragmentSample]>,
    ) -> Option<()> {
        let trak = track.trak(mp4);
        let mp3 = mp3_entry(&trak.mdia.minf.stbl.stsd.contents);
        let aac = match mp3 {
            Some(_) => None,
            None => Some(self.aac_config(trak, track_id, config)?),
        };

        // re_mp4 parses the edit list but does not apply it. For audio the
        // first real edit's media time is the encoder priming: shift the
        // track so priming samples carry negative timestamps and the PCM
        // stage can drop everything before the origin.
        let edits = edit_start(trak, u64::from(mp4.moov.mvhd.timescale), track.timescale);
        let shift = edits.media_time.saturating_sub(edits.empty);
        let start = MediaTime::from_micros(scale_to_us(edits.empty, track.timescale.max(1)));

        let end = edit_end(trak, u64::from(mp4.moov.mvhd.timescale));
        let samples =
            match self.collect_samples(track, moov_reorder(track, trak), walked, shift, end) {
                Ok(samples) => samples,
                Err(e) => {
                    push_note(&mut self.refusals, || {
                        format!("track {}: audio refused: {e}", track_id.0)
                    });
                    return None;
                }
            };

        let (codec, sample_rate, channels, codec_private) = match aac {
            Some((sample_rate, channels, asc)) => (AudioCodec::Aac, sample_rate, channels, asc),
            None => {
                let stated = mp3.unwrap_or_default();
                let first = match samples.first() {
                    Some(sample) => Some((sample.offset, sample.size)),
                    None => self.first_indexed_sample(track_id),
                };
                let (sample_rate, channels) = self.mp3_format(track_id, first, stated)?;
                (AudioCodec::Mp3, sample_rate, channels, Vec::new())
            }
        };
        self.pending.push_back(StreamEvent::Format(
            track_id,
            Format::Audio {
                codec,
                sample_rate,
                channels,
                codec_private,
            },
        ));
        self.audio = Some(AudioTrack {
            id: track_id,
            samples: self.new_samples(samples),
            shift,
            start,
            end,
        });
        Some(())
    }

    /// An `mp4a` track's output rate, channel count and
    /// AudioSpecificConfig, or `None` once the refusal is noted.
    fn aac_config(
        &mut self,
        trak: &re_mp4::TrakBox,
        track_id: TrackId,
        config: Option<&[u8]>,
    ) -> Option<(u32, u32, Vec<u8>)> {
        let stsd = &trak.mdia.minf.stbl.stsd;
        let re_mp4::StsdBoxContent::Mp4a(mp4a) = &stsd.contents else {
            push_note(&mut self.refusals, || {
                format!("track {}: skipped audio (not AAC/mp4a)", track_id.0)
            });
            return None;
        };
        let Some(esds) = &mp4a.esds else {
            push_note(&mut self.refusals, || {
                format!("track {}: mp4a without esds", track_id.0)
            });
            return None;
        };
        let dec = &esds.es_desc.dec_config;
        // 0x40 = MPEG-4 Audio, 0x67 = MPEG-2 AAC-LC.
        if dec.object_type_indication != 0x40 && dec.object_type_indication != 0x67 {
            push_note(&mut self.refusals, || {
                format!(
                    "track {}: skipped audio (object type {:#x}, not AAC or MP3)",
                    track_id.0, dec.object_type_indication
                )
            });
            return None;
        }
        // Where the walk to the `esds` did not reach, the fields the box
        // parser kept still make a whole config for a plain AAC core,
        // though not for HE-AAC, which the parse then refuses.
        let spec = &dec.dec_specific;
        let raw = match config {
            Some(raw) => Some(raw.to_vec()),
            None if (1..=31).contains(&spec.profile) && spec.freq_index < 15 => {
                push_note(&mut self.notes, || {
                    format!(
                        "track {}: AudioSpecificConfig rebuilt from esds fields",
                        track_id.0
                    )
                });
                Some(vec![
                    (spec.profile << 3) | (spec.freq_index >> 1),
                    ((spec.freq_index & 1) << 7) | ((spec.chan_conf & 0xF) << 3),
                ])
            }
            None => None,
        };
        let Some((asc, parsed)) = raw.and_then(|raw| {
            let parsed = parse_asc(&raw)?;
            Some((raw, parsed))
        }) else {
            push_note(&mut self.refusals, || {
                format!("track {}: no readable AudioSpecificConfig", track_id.0)
            });
            return None;
        };
        // Main, LC, SSR and LTP: the cores an AAC decoder takes. HE-AAC
        // reports its LC core here.
        if !(1..=4).contains(&parsed.object_type) {
            push_note(&mut self.refusals, || {
                format!(
                    "track {}: skipped audio (object type {}, not AAC)",
                    track_id.0, parsed.object_type
                )
            });
            return None;
        }
        // The in-box platform decoders handle at most 6 explicitly
        // signalled channels; wider layouts fault with an access violation
        // inside the Media Foundation decoder rather than returning an
        // error. PCE-defined layouts (channel configuration 0) leave the
        // real width unknown and are refused too.
        if parsed.channel_config < 1 || parsed.channel_config > 6 {
            push_note(&mut self.refusals, || {
                format!(
                    "track {}: refused AAC channel configuration {}",
                    track_id.0, parsed.channel_config
                )
            });
            return None;
        }
        Some((parsed.output_rate, u32::from(parsed.channels()), asc))
    }

    /// Where a track's first sample lies in a file read by its fragment
    /// index. The earliest fragments can hold none of the track, so up to
    /// `MAX_SEEK_STEPS` of them are read, on cursors of their own so that
    /// playback's stay where they were.
    fn first_indexed_sample(&mut self, track_id: TrackId) -> Option<(u64, u32)> {
        let fragments = self.fragments.as_ref()?;
        let mut cursors = Cursors::new();
        let mut budget = MAX_FRAGMENT_SAMPLES;
        for entry in fragments.index.entries.iter().take(MAX_SEEK_STEPS) {
            let moofs = read_subsegment(&mut self.src, *entry, &self.limits).ok()?;
            for moof in &moofs {
                let built = crate::mp4_fragment::build(
                    moof,
                    &fragments.defaults,
                    &mut cursors,
                    &mut budget,
                )
                .ok()?;
                let first = built
                    .into_iter()
                    .filter(|(id, _)| *id == track_id.0)
                    .find_map(|(_, samples)| samples.first().copied());
                if let Some(sample) = first {
                    return Some((sample.offset, sample.size));
                }
            }
        }
        None
    }

    /// An MP3 track's sample rate and channel count, from its first frame's
    /// header, as the decoder will find them. A live source has no sample
    /// at hand when it opens; there the sample entry's values stand.
    fn mp3_format(
        &mut self,
        track_id: TrackId,
        first: Option<(u64, u32)>,
        stated: (u32, u32),
    ) -> Option<(u32, u32)> {
        let refuse = |this: &mut Self, why: String| {
            push_note(&mut this.refusals, || {
                format!("track {}: skipped audio ({why})", track_id.0)
            });
            None
        };
        let Some((offset, size)) = first else {
            let (sample_rate, channels) = stated;
            if sample_rate == 0 || !(1..=2).contains(&channels) {
                return refuse(
                    self,
                    format!("MP3 stated as {sample_rate} Hz, {channels} ch"),
                );
            }
            return Some(stated);
        };
        let mut header = [0u8; 4];
        if size < header.len() as u32 {
            return refuse(
                self,
                format!("first MP3 sample too short for a frame header ({size} of 4 bytes)"),
            );
        }
        if let Err(e) = self.src.read_exact_at(offset, &mut header) {
            return refuse(self, format!("first MP3 frame unreadable: {e}"));
        }
        match crate::mp3::frame_format(&header) {
            Some(format) => Some(format),
            None => refuse(self, "first frame is not MPEG audio Layer III".into()),
        }
    }

    /// The whole table for a file that states one, an empty queue for a
    /// file whose fragments are read as they are reached.
    fn new_samples(&self, all: Vec<SampleRef>) -> Samples {
        if self.fragments.is_some() || self.live.is_some() {
            Samples::Held(VecDeque::new())
        } else {
            Samples::Whole { all, next: 0 }
        }
    }

    /// A track's whole table: the samples `moov` holds, whose times
    /// `reorder` returns to the ones the file states, then those its
    /// walked fragments add. Where the edit list ends the track early, the
    /// table stops at the last sample shown before `end`: a sample decoded
    /// after that one can be no picture's reference and no part of the
    /// sound. Those decoded before it but shown past `end` stay.
    fn collect_samples(
        &self,
        track: &re_mp4::Track,
        reorder: i64,
        walked: Option<&[FragmentSample]>,
        shift: i64,
        end: Option<MediaTime>,
    ) -> Result<Vec<SampleRef>, DemuxError> {
        let mut all =
            self.collect_shifted_samples(&track.samples, shift.saturating_sub(reorder))?;
        for sample in walked.unwrap_or_default() {
            all.push(to_ref(
                *sample,
                track.timescale.max(1),
                shift,
                &self.limits,
            )?);
        }
        if let Some(end) = end
            && let Some(last) = all.iter().rposition(|s| s.pts < end)
        {
            all.truncate(last + 1);
        }
        Ok(all)
    }

    fn collect_shifted_samples(
        &self,
        samples: &[re_mp4::Sample],
        shift: i64,
    ) -> Result<Vec<SampleRef>, DemuxError> {
        samples
            .iter()
            .map(|s| {
                if s.size > self.limits.max_au_bytes {
                    return Err(DemuxError::Cap("sample larger than the AU ceiling"));
                }
                let (pts, dts) = shifted(
                    s.composition_timestamp,
                    s.decode_timestamp,
                    shift,
                    s.timescale.max(1),
                );
                Ok(SampleRef {
                    offset: s.offset,
                    size: s.size as u32,
                    pts,
                    dts,
                    sync: s.is_sync,
                })
            })
            .collect()
    }

    fn read_sample(&mut self, sample: SampleRef) -> Result<Vec<u8>, DemuxError> {
        if let Some(held) = self
            .runs
            .serve(self.src.inner_mut(), sample.offset, sample.size)
        {
            return held.map_err(DemuxError::Source);
        }
        let mut data = vec![0u8; sample.size as usize];
        self.src
            .read_exact_at(sample.offset, &mut data)
            .map_err(DemuxError::Source)?;
        Ok(data)
    }

    /// Convert one length-prefixed H.264 sample to Annex B (SPS/PPS
    /// prepended on keyframes so the stream stays decodable from any sync
    /// point); other codecs' samples pass through as stored.
    fn convert_sample(
        video: &VideoTrack,
        src: Vec<u8>,
        keyframe: bool,
    ) -> Result<Vec<u8>, DemuxError> {
        match &video.avc {
            Some(avc) => {
                crate::avc::to_annex_b(&avc.sps, &avc.pps, avc.nal_length_size, &src, keyframe)
            }
            None => Ok(src),
        }
    }

    fn next_video(&self) -> Option<SampleRef> {
        self.video.as_ref()?.samples.peek()
    }

    fn next_audio(&self) -> Option<SampleRef> {
        self.audio.as_ref()?.samples.peek()
    }

    /// Sample references the demuxer is holding. A file read a fragment
    /// at a time holds a fragment's worth whatever its length, which is
    /// what makes a twelve-hour video cost what a three-minute one does.
    pub fn held_samples(&self) -> usize {
        self.video.as_ref().map_or(0, |v| v.samples.held())
            + self.audio.as_ref().map_or(0, |a| a.samples.held())
    }

    /// Read the subsegment at `at` and turn its runs into sample
    /// references for the bound tracks. `anchor` seeds the decode
    /// timelines from the index, for a fragment reached by a seek that
    /// states no base time of its own.
    fn load_fragment(&mut self, at: usize, anchor: bool) -> Result<Loaded, DemuxError> {
        let fragments = self
            .fragments
            .as_mut()
            .expect("only a fragmented file loads fragments");
        let entry = *fragments
            .index
            .entries
            .get(at)
            .ok_or(DemuxError::Parse("mp4: subsegment past the index".into()))?;
        if anchor {
            let index_scale = fragments.index.timescale;
            for (id, defaults) in &fragments.defaults {
                let at = rescale(entry.time, u64::from(index_scale), defaults.timescale);
                fragments.cursors.insert(*id, at.cast_signed());
            }
        }

        let moofs = read_subsegment(&mut self.src, entry, &self.limits)?;
        let video_id = self.video.as_ref().map(|v| v.id.0);
        let audio_id = self.audio.as_ref().map(|a| a.id.0);
        let video_shift = self.video.as_ref().map_or(0, |v| v.shift);
        let audio_shift = self.audio.as_ref().map_or(0, |a| a.shift);
        let mut loaded = Loaded::default();
        let mut budget = MAX_FRAGMENT_SAMPLES;
        for moof in &moofs {
            let built = crate::mp4_fragment::build(
                moof,
                &fragments.defaults,
                &mut fragments.cursors,
                &mut budget,
            )
            .map_err(|why| DemuxError::Parse(format!("mp4: {why}")))?;
            for (track_id, samples) in built {
                let (out, shift) = if Some(track_id) == video_id {
                    (&mut loaded.video, video_shift)
                } else if Some(track_id) == audio_id {
                    (&mut loaded.audio, audio_shift)
                } else {
                    continue;
                };
                let timescale = fragments
                    .defaults
                    .get(&track_id)
                    .map_or(1, |d| d.timescale.max(1));
                for sample in samples {
                    out.push(to_ref(sample, timescale, shift, &self.limits)?);
                }
            }
        }
        Ok(loaded)
    }

    /// Read ahead until both bound tracks have something to pick from, or
    /// the file runs out.
    fn fill(&mut self) -> Result<(), DemuxError> {
        if self.live.is_some() {
            return self.fill_live();
        }
        while self.fragments.is_some() {
            let starved = self
                .video
                .as_ref()
                .is_some_and(|v| v.samples.peek().is_none())
                || self
                    .audio
                    .as_ref()
                    .is_some_and(|a| a.samples.peek().is_none());
            let fragments = self.fragments.as_ref().expect("checked above");
            if !starved || fragments.next >= fragments.index.entries.len() {
                break;
            }
            if self.held_samples() >= MAX_HELD_SAMPLES {
                break;
            }
            let at = fragments.next;
            self.runs.finish(self.src.inner_mut());
            let loaded = self.load_fragment(at, false)?;
            self.queue(loaded);
            self.fragments.as_mut().expect("checked above").next = at + 1;
        }
        Ok(())
    }

    fn queue(&mut self, loaded: Loaded) {
        let offsets = |samples: &[SampleRef]| -> Vec<(u64, u32)> {
            samples.iter().map(|s| (s.offset, s.size)).collect()
        };
        let tracks = [offsets(&loaded.video), offsets(&loaded.audio)];
        self.runs.hold(self.src.inner_mut(), &tracks);
        if let Some(Samples::Held(held)) = self.video.as_mut().map(|v| &mut v.samples) {
            held.extend(loaded.video);
        }
        if let Some(Samples::Held(held)) = self.audio.as_mut().map(|a| &mut a.samples) {
            held.extend(loaded.audio);
        }
    }

    fn clear_queues(&mut self) {
        self.runs.clear();
        if let Some(Samples::Held(held)) = self.video.as_mut().map(|v| &mut v.samples) {
            held.clear();
        }
        if let Some(Samples::Held(held)) = self.audio.as_mut().map(|a| &mut a.samples) {
            held.clear();
        }
    }

    /// Reposition by the index: find the subsegment holding the target,
    /// then the last sync sample at or before it inside that subsegment.
    fn seek_fragmented(&mut self, target: MediaTime) -> Result<MediaTime, DemuxError> {
        let (index_scale, entries) = {
            let fragments = self.fragments.as_ref().expect("fragmented");
            (fragments.index.timescale, fragments.index.entries.len())
        };
        let wanted = target.as_micros().max(0).cast_unsigned();
        let mut at = {
            let fragments = self.fragments.as_ref().expect("fragmented");
            fragments
                .index
                .floor(rescale(wanted, 1_000_000, u64::from(index_scale)))
        };
        let mut loaded = self.load_fragment(at, true)?;

        // The index states one track's times, and the landing is chosen
        // on another's, so its subsegment boundaries can be a sample out.
        let mut steps = 0;
        while at + 1 < entries
            && steps < MAX_SEEK_STEPS
            && last_dts(&loaded).is_some_and(|d| d < target)
        {
            at += 1;
            loaded = self.load_fragment(at, true)?;
            steps += 1;
        }
        let mut steps = 0;
        let mut key = landing(&loaded.video, target);
        while key.is_none() && at > 0 && steps < MAX_SEEK_STEPS && self.video.is_some() {
            at -= 1;
            loaded = self.load_fragment(at, true)?;
            key = landing(&loaded.video, target);
            steps += 1;
        }

        let landed = match key.and_then(|i| loaded.video.get(i)) {
            Some(sample) => sample.pts,
            // No video track, or none of the file's sync samples reaches
            // back to the target: the audio partition decides, as it does
            // for a file that has no picture at all.
            None => target,
        };

        self.clear_queues();
        let video = loaded
            .video
            .split_off(key.unwrap_or(0).min(loaded.video.len()));
        self.queue(Loaded {
            video,
            audio: loaded
                .audio
                .into_iter()
                .filter(|s| s.pts >= landed)
                .collect(),
        });
        self.fragments.as_mut().expect("fragmented").next = at + 1;
        Ok(landed)
    }
}

/// The last sync sample presenting at or before `target`, by decode
/// order: what the whole-table seek picks across a file's every sample.
fn landing(samples: &[SampleRef], target: MediaTime) -> Option<usize> {
    let mut key = None;
    for (i, sample) in samples.iter().enumerate() {
        if sample.dts > target {
            break;
        }
        if sample.sync {
            key = Some(i);
        }
    }
    key
}

fn last_dts(loaded: &Loaded) -> Option<MediaTime> {
    loaded
        .video
        .last()
        .or_else(|| loaded.audio.last())
        .map(|sample| sample.dts)
}

/// One fragment sample as the demuxer's own reference, on the shared
/// microsecond timeline and with the track's edit-list shift applied.
fn to_ref(
    sample: FragmentSample,
    timescale: u64,
    shift: i64,
    limits: &DemuxLimits,
) -> Result<SampleRef, DemuxError> {
    if u64::from(sample.size) > limits.max_au_bytes {
        return Err(DemuxError::Cap("sample larger than the AU ceiling"));
    }
    let (pts, dts) = shifted(sample.pts, sample.dts, shift, timescale);
    Ok(SampleRef {
        offset: sample.offset,
        size: sample.size,
        pts,
        dts,
        sync: sample.sync,
    })
}

/// A sample's stored times on the shared microsecond timeline, with the
/// track's edit-list shift applied. The shift is whatever the file says,
/// so the subtraction saturates rather than wrapping, and the scaling
/// clamps: hostile numbers cost a wrong timestamp rather than a panic or
/// a time running backwards.
fn shifted(pts: i64, dts: i64, shift: i64, timescale: u64) -> (MediaTime, MediaTime) {
    (
        MediaTime::from_micros(scale_to_us(pts.saturating_sub(shift), timescale)),
        MediaTime::from_micros(scale_to_us(dts.saturating_sub(shift), timescale)),
    )
}

/// Top-level boxes the prefix scan looks at before giving up. A
/// fragmented file's first `moof` follows `moov` within a box or two;
/// everything else has a handful of top-level boxes in total.
const MAX_PREFIX_BOXES: usize = 64;
/// Ceiling on one segment index, above ISO's own limit of 65,535
/// references at twelve bytes each.
const MAX_INDEX_BYTES: u64 = 1024 * 1024;
/// Ceiling on one movie fragment's header, well above any real one.
const MAX_FRAGMENT_BYTES: u64 = 16 * 1024 * 1024;
/// Sample references a fragmented file holds before it stops reading
/// ahead. Only a file whose fragments carry one track each comes near it.
const MAX_HELD_SAMPLES: usize = 65536;
/// Samples one subsegment may state, across all its fragments and
/// tracks: hours of 30 fps video with AAC beside it.
const MAX_FRAGMENT_SAMPLES: usize = 1 << 20;
/// Samples a file read by walking its fragments may state in all: about
/// fifteen hours of 30 fps video with AAC beside it.
const MAX_WALKED_SAMPLES: usize = 1 << 22;
/// Fragments a seek steps over looking for the one holding its target,
/// in either direction.
const MAX_SEEK_STEPS: usize = 64;
/// The longest a stated duration is believed, matching what the
/// Matroska demuxer accepts. Past it the file is not stating a length.
const MAX_DURATION_US: i64 = 100 * 3600 * 1_000_000;

const STYP: u32 = u32::from_be_bytes(*b"styp");
const SIDX: u32 = u32::from_be_bytes(*b"sidx");
const PRFT: u32 = u32::from_be_bytes(*b"prft");

/// What the top-level box chain holds before the media starts.
#[derive(Default)]
struct Prefix {
    /// One past the end of `moov`, where it comes before the media.
    moov_end: Option<u64>,
    /// Where the first `moof` begins, when the file is fragmented.
    first_fragment: Option<u64>,
    indexes: Vec<SegmentIndex>,
}

/// Walk the top-level boxes from `from`, parsing any segment index on
/// the way, and stop at the media (or, with `stop_after_moov`, at the
/// end of `moov`).
///
/// Stopping there is what keeps a progressive file's open honest.
/// Whether a file has fragments at all is stated inside `moov`, so
/// reading the header of the box that follows it answers nothing, and on
/// a file whose `moov` is megabytes of sample table that header is
/// megabytes into the file: a whole cache block fetched to look at eight
/// bytes of media. Sizes come from the headers, so a malformed chain
/// simply ends the scan and the sample-table parse reports it.
fn scan_prefix(
    reader: &mut SourceReader<'_>,
    from: u64,
    len: u64,
    notes: &mut Vec<String>,
    stop_after_moov: bool,
) -> Prefix {
    let mut prefix = Prefix::default();
    let mut pos = from;
    for _ in 0..MAX_PREFIX_BOXES {
        if len - pos < 8 || reader.seek(SeekFrom::Start(pos)).is_err() {
            break;
        }
        let Ok(header) = BoxHeader::read(reader) else {
            break;
        };
        // The header is eight bytes, or sixteen where the size is stated
        // as a 64-bit one and the box's own size counts from there.
        let Ok(body) = reader.stream_position() else {
            break;
        };
        // A box's size counts from `body - 8`, which is where an
        // ordinary header began and eight bytes into a 64-bit one, so
        // the span is what bounds it rather than the size itself. A
        // 64-bit size reaches the top of the range, so where it sums
        // past it there is nothing to walk to.
        let size = header.size;
        let Some(next) = (body - 8).checked_add(size) else {
            break;
        };
        if size < 8 || next > len {
            break;
        }

        match u32::from(header.name) {
            kind if kind == u32::from(BoxType::MoofBox) || kind == STYP => {
                prefix.first_fragment = Some(pos);
                break;
            }
            // Media: a file that puts it before `moov` keeps its tables
            // at the end, where the whole-file parse finds them.
            kind if kind == u32::from(BoxType::MdatBox) => break,
            kind if kind == u32::from(BoxType::MoovBox) => {
                prefix.moov_end = Some(next);
                if stop_after_moov {
                    break;
                }
            }
            // The body is what lies between the header and the end of
            // the box, which the walk has already worked out; taking it
            // from the stated size instead would depend on knowing that
            // a 64-bit size is reported eight bytes short.
            SIDX if next - body <= MAX_INDEX_BYTES => {
                let mut index = vec![0u8; (next - body) as usize];
                if reader.read_exact(&mut index).is_err() {
                    break;
                }
                match crate::mp4_index::parse(&index, next) {
                    Ok(index) => prefix.indexes.push(index),
                    Err(why) => push_note(notes, || format!("segment index ignored: {why}")),
                }
            }
            _ => {}
        }
        pos = next;
    }
    prefix
}

/// The index to seek by. The video track's, so a seek lands where the
/// picture does; on a file whose video track has no index the first
/// serves, since one fragment carries every track.
fn choose_index(trusted: Vec<SegmentIndex>, video: Option<u32>) -> Option<SegmentIndex> {
    let at = trusted
        .iter()
        .position(|candidate| Some(candidate.reference_id) == video)
        .unwrap_or(0);
    trusted.into_iter().nth(at)
}

/// What an opener that found no usable `sidx` knows when it looks for an
/// index in the file's trailing `mfra` instead.
struct MfraSearch<'a> {
    header: &'a re_mp4::Mp4,
    video: Option<u32>,
    first: u64,
    media_end: u64,
    len: u64,
}

impl MfraSearch<'_> {
    /// An index built from the `mfra`'s video `tfra` (the first `tfra`
    /// when there is no video), believed only when it starts at the first
    /// fragment and its last entry lands on one. That last fragment is read
    /// here, for the end of its samples: the `tfra` says where each
    /// fragment starts but not where the media ends.
    fn index(
        &self,
        src: &mut CachedSource,
        budget: &mut u64,
        limits: &DemuxLimits,
        notes: &mut Vec<String>,
    ) -> Option<SegmentIndex> {
        match self.build(src, budget, limits) {
            Ok(index) => Some(index),
            Err(why) => {
                push_note(notes, || format!("mfra not used: {why}"));
                None
            }
        }
    }

    fn build(
        &self,
        src: &mut CachedSource,
        budget: &mut u64,
        limits: &DemuxLimits,
    ) -> Result<SegmentIndex, String> {
        let size = self.len - self.media_end;
        if !(8..=MAX_INDEX_BYTES.min(*budget)).contains(&size) {
            return Err(format!("{size} bytes is not an index to read"));
        }
        let mut boxed = vec![0u8; size as usize];
        src.read_exact_at(self.media_end, &mut boxed)
            .map_err(|e| e.to_string())?;
        *budget -= size;
        if &boxed[4..8] != b"mfra" {
            return Err("the tail box is not an mfra".into());
        }
        let tables = crate::mp4_index::parse_mfra(&boxed[8..])?;
        let table = tables
            .iter()
            .find(|table| Some(table.track_id) == self.video)
            .or_else(|| tables.first())
            .ok_or("no tfra")?;
        let timescale = self
            .header
            .tracks()
            .get(&table.track_id)
            .and_then(|track| u32::try_from(track.timescale).ok())
            .ok_or("the tfra names no track with a timescale")?;
        let mut index = crate::mp4_index::from_random_access(table, timescale, self.media_end)?;
        if !index.starts_at(self.first) {
            return Err("the first fragment is not where the tfra starts".into());
        }

        let last = *index.entries.last().expect("an index has entries");
        let defaults = crate::mp4_fragment::track_defaults(&self.header.moov);
        let mut cursors = Cursors::new();
        for (id, track) in &defaults {
            let at = rescale(last.time, u64::from(timescale), track.timescale);
            cursors.insert(*id, at.cast_signed());
        }
        // The media ends where the last of any track's samples does, in the
        // index's timescale: a file's last fragment can hold audio alone.
        let mut end = None::<u64>;
        let mut budget = MAX_FRAGMENT_SAMPLES;
        for moof in read_subsegment(src, last, limits).map_err(|e| format!("{e:?}"))? {
            let built = crate::mp4_fragment::build(&moof, &defaults, &mut cursors, &mut budget)?;
            for (track_id, samples) in built {
                let Some(track) = defaults.get(&track_id) else {
                    continue;
                };
                for sample in samples {
                    let after = sample
                        .dts
                        .saturating_add(i64::from(sample.duration))
                        .max(0)
                        .cast_unsigned();
                    let after = rescale(after, track.timescale, u64::from(timescale));
                    end = Some(end.map_or(after, |e| e.max(after)));
                }
            }
        }
        let end = end.ok_or("the last fragment holds no samples")?;
        let duration = end
            .checked_sub(last.time)
            .and_then(|d| u32::try_from(d).ok())
            .filter(|d| *d > 0)
            .ok_or("the last fragment ends before it starts")?;
        index
            .entries
            .last_mut()
            .expect("an index has entries")
            .duration = duration;
        Ok(index)
    }
}

/// Where the file's media ends: its length, or the start of a trailing
/// `mfra`, which is written after the last fragment and is not indexed.
fn media_end(indexes: &[SegmentIndex], len: u64, src: &mut CachedSource) -> u64 {
    if len < 16 || indexes.iter().any(|index| index.tiles(len)) {
        return len;
    }
    // `mfro` is the last box in the file and states the `mfra` size.
    let mut tail = [0u8; 16];
    if src.read_exact_at(len - 16, &mut tail).is_err() {
        return len;
    }
    if &tail[4..8] != b"mfro" || u32::from_be_bytes([tail[0], tail[1], tail[2], tail[3]]) != 16 {
        return len;
    }
    let mfra = u64::from(u32::from_be_bytes([tail[12], tail[13], tail[14], tail[15]]));
    len.checked_sub(mfra).unwrap_or(len)
}

/// Parse the box structure up to `upto`, containing the parser's panic
/// paths on inconsistent sample tables as a typed error: hostile metadata
/// is a refusal, not a session abort.
///
/// `budget` is what the open has left and is spent, not merely read. One
/// file can be parsed twice here (a prefix that turns out to describe
/// only part of itself is followed by a walk of the whole), and the
/// second parse continues the bound rather than restarting it.
fn read_metadata(
    src: &mut CachedSource,
    upto: u64,
    budget: &mut u64,
    sparse: bool,
) -> Result<re_mp4::Mp4, DemuxError> {
    let mut reader = if sparse {
        SourceReader::new_sparse(src.inner_mut(), upto, *budget)
    } else {
        SourceReader::new(&mut *src, upto, *budget)
    };
    let parsed = catch_unwind(AssertUnwindSafe(|| re_mp4::Mp4::read(&mut reader, upto)));
    *budget = reader.remaining_budget();
    match parsed {
        Ok(Ok(mp4)) => Ok(mp4),
        Ok(Err(re_mp4::Error::Io(io))) => Err(DemuxError::Io(io)),
        Ok(Err(other)) => Err(DemuxError::Parse(format!("mp4: {other}"))),
        Err(_) => Err(DemuxError::Parse(
            "mp4 parser panicked on inconsistent metadata".into(),
        )),
    }
}

/// Each `mp4a` track's config as the file states it, from the boxes
/// before `upto`, charged to the open's budget. A file with no `mp4a`
/// track is not walked.
fn audio_configs(
    mp4: &re_mp4::Mp4,
    src: &mut CachedSource,
    upto: u64,
    budget: &mut u64,
) -> HashMap<u32, Vec<u8>> {
    let has_mp4a = mp4.tracks().values().any(|track| {
        matches!(
            track.trak(mp4).mdia.minf.stbl.stsd.contents,
            re_mp4::StsdBoxContent::Mp4a(_)
        )
    });
    if !has_mp4a {
        return HashMap::new();
    }
    let mut reader = SourceReader::new(src, upto, *budget);
    let configs = crate::mp4_esds::audio_configs(&mut reader, upto);
    *budget = reader.remaining_budget();
    configs
}

/// Duration for a file whose tracks state none, which is every file
/// written with an empty `moov`: the movie header, then the fragment
/// duration `mvex` declares, then what the index spans.
fn stated_duration(mp4: &re_mp4::Mp4, fragments: Option<&Fragments>) -> Option<MediaTime> {
    let mvhd = &mp4.moov.mvhd;
    let timescale = u64::from(mvhd.timescale);
    stated_span(mvhd.duration, timescale)
        .or_else(|| {
            let mehd = mp4.moov.mvex.as_ref()?.mehd.as_ref()?;
            stated_span(mehd.fragment_duration, timescale)
        })
        .or_else(|| {
            let fragments = fragments?;
            stated_span(fragments.index.span(), u64::from(fragments.index.timescale))
        })
}

/// A sample entry's four-character code, as a viewer would quote it.
fn sample_entry(contents: &re_mp4::StsdBoxContent) -> String {
    let code = match contents {
        re_mp4::StsdBoxContent::Av01(_) => *b"av01",
        re_mp4::StsdBoxContent::Avc1(_) => *b"avc1",
        re_mp4::StsdBoxContent::Hev1(_) => *b"hev1",
        re_mp4::StsdBoxContent::Hvc1(_) => *b"hvc1",
        re_mp4::StsdBoxContent::Vp08(_) => *b"vp08",
        re_mp4::StsdBoxContent::Vp09(_) => *b"vp09",
        re_mp4::StsdBoxContent::Mp4a(_) => *b"mp4a",
        re_mp4::StsdBoxContent::Tx3g(_) => *b"tx3g",
        re_mp4::StsdBoxContent::Unknown(code) => code.value,
    };
    code.iter()
        .map(|&b| {
            if b.is_ascii_graphic() {
                char::from(b)
            } else {
                '?'
            }
        })
        .collect()
}

/// Whether a track holds sound. The box parser gives QuickTime's MP3
/// entries no kind of their own.
fn is_audio(mp4: &re_mp4::Mp4, track: &re_mp4::Track) -> bool {
    track.kind == Some(re_mp4::TrackKind::Audio)
        || mp3_entry(&track.trak(mp4).mdia.minf.stbl.stsd.contents).is_some()
}

/// For an entry that carries MP3, the sample rate and channel count it
/// states, or zeros where it states none. MP3 comes as an `mp4a` whose
/// `esds` names MPEG-1 (0x6B) or MPEG-2 (0x69) audio, or under a
/// QuickTime code (`.mp3`, the older `ms\0U`, or VLC's `mp3 `) whose
/// fields re_mp4 does not keep.
fn mp3_entry(contents: &re_mp4::StsdBoxContent) -> Option<(u32, u32)> {
    match contents {
        re_mp4::StsdBoxContent::Mp4a(mp4a) => {
            let esds = mp4a.esds.as_ref()?;
            matches!(esds.es_desc.dec_config.object_type_indication, 0x69 | 0x6B).then(|| {
                (
                    u32::from(mp4a.samplerate.value()),
                    u32::from(mp4a.channelcount),
                )
            })
        }
        re_mp4::StsdBoxContent::Unknown(code)
            if matches!(&code.value, b".mp3" | b"ms\0U" | b"mp3 ") =>
        {
            Some((0, 0))
        }
        _ => None,
    }
}

fn unsupported_video(entry: &str) -> String {
    format!("video codec '{entry}' is not supported (supported: H.264, H.265, VP9, AV1)")
}

/// Where a track's edit list puts its media, in the track's timescale:
/// the leading empty edits, a track starting after the movie does, and
/// the media time the first real edit starts from. An empty edit is
/// stated in the movie's timescale.
/// How far the parse of `moov` moved a track's composition times back:
/// it takes the smallest out whether or not an edit list asks for it.
/// The file decodes its first sample at zero, which makes its first
/// `ctts` offset that sample's stated composition time.
fn moov_reorder(track: &re_mp4::Track, trak: &re_mp4::TrakBox) -> i64 {
    let Some(first) = track.samples.first() else {
        return 0;
    };
    let stated = trak
        .mdia
        .minf
        .stbl
        .ctts
        .as_ref()
        .and_then(|ctts| ctts.entries.first())
        .map_or(0, |entry| i64::from(entry.sample_offset));
    stated.saturating_sub(first.composition_timestamp)
}

/// How long a track presents for: the sum of its edits, as `tkhd` states
/// it (8.3.2.3), where its edit list gives them lengths, else its media's
/// own length. `mdhd` is in media time, which a B-frame track's edit list
/// shifts, and can run past the end the edits give.
fn presented_span(mp4: &re_mp4::Mp4, track: &re_mp4::Track) -> Option<MediaTime> {
    track
        .trak(mp4)
        .edts
        .as_ref()
        .and_then(|e| e.elst.as_ref())
        .and_then(|elst| {
            elst.entries.iter().try_fold(0u64, |sum, entry| {
                let known = entry.segment_duration != u64::from(u32::MAX)
                    && entry.segment_duration != u64::MAX;
                known.then(|| sum.saturating_add(entry.segment_duration))
            })
        })
        .filter(|edits| *edits > 0)
        .and_then(|edits| stated_span(edits, u64::from(mp4.moov.mvhd.timescale)))
        .or_else(|| stated_span(track.duration, track.timescale))
}

/// Where an edit list stops presenting its track, on the timeline the
/// samples are emitted on: the empty edits ahead of it plus the one edit
/// that presents media. Only that shape is read; an edit list with more
/// than one span of media, or one played at another rate, plays the media
/// whole, as does an edit that states no length.
fn edit_end(trak: &re_mp4::TrakBox, movie_timescale: u64) -> Option<MediaTime> {
    let elst = trak.edts.as_ref()?.elst.as_ref()?;
    let gaps = elst
        .entries
        .iter()
        .take_while(|entry| entry.media_time == u64::MAX || entry.media_time == u64::from(u32::MAX))
        .count();
    let [media] = &elst.entries[gaps..] else {
        return None;
    };
    if media.media_rate != 1 || media.media_rate_fraction != 0 || media.segment_duration == 0 {
        return None;
    }
    let end = elst.entries[..gaps]
        .iter()
        .try_fold(media.segment_duration, |end, entry| {
            end.checked_add(entry.segment_duration)
        })?;
    stated_span(end, movie_timescale)
}

struct EditStart {
    empty: i64,
    media_time: i64,
}

fn edit_start(trak: &re_mp4::TrakBox, movie_timescale: u64, timescale: u64) -> EditStart {
    let mut start = EditStart {
        empty: 0,
        media_time: 0,
    };
    let Some(elst) = trak.edts.as_ref().and_then(|e| e.elst.as_ref()) else {
        return start;
    };
    for entry in &elst.entries {
        if entry.media_time != u64::MAX && entry.media_time != u64::from(u32::MAX) {
            start.media_time = i64::try_from(entry.media_time).unwrap_or(i64::MAX);
            break;
        }
        if movie_timescale != 0 {
            let gap = u128::from(entry.segment_duration) * u128::from(timescale)
                / u128::from(movie_timescale);
            start.empty = start
                .empty
                .saturating_add(i64::try_from(gap).unwrap_or(i64::MAX));
        }
    }
    start
}

/// A duration the container states, in microseconds, or `None` where it
/// states that it does not know one. All ones is the stated marker for
/// an unknown duration (8.2.2.3) in either width, and a span no file has
/// is the same claim made carelessly. Reporting no duration is honest:
/// a fabricated one reaches the seek bar, and a saturated zero reads as
/// live to every `duration <= 0` test.
fn stated_span(value: u64, timescale: u64) -> Option<MediaTime> {
    if timescale == 0 || value == u64::from(u32::MAX) || value == u64::MAX {
        return None;
    }
    let us = i128::from(value) * 1_000_000 / i128::from(timescale);
    (0 < us && us <= i128::from(MAX_DURATION_US)).then(|| MediaTime::from_micros(us as i64))
}

/// The movie fragments inside one indexed subsegment. The index says
/// where it starts and how long it is; a jump that does not land on a
/// fragment is a refusal rather than a search for one, because the file
/// came from a URL and the index is as untrusted as the rest of it.
fn read_subsegment(
    src: &mut CachedSource,
    entry: IndexEntry,
    limits: &DemuxLimits,
) -> Result<Vec<MoofBox>, DemuxError> {
    let ceiling = MAX_FRAGMENT_BYTES.min(limits.max_metadata_bytes);
    let end = entry.offset.saturating_add(u64::from(entry.size));
    let mut reader = SourceReader::new(&mut *src, end, ceiling);
    let mut moofs = Vec::new();
    let mut pos = entry.offset;

    while end - pos >= 8 {
        reader.seek(SeekFrom::Start(pos)).map_err(DemuxError::Io)?;
        let header = BoxHeader::read(&mut reader)
            .map_err(|e| DemuxError::Parse(format!("mp4: subsegment box header: {e}")))?;
        let body = reader.stream_position().map_err(DemuxError::Io)?;
        // As in the prefix walk, the size counts from `body - 8`, so a
        // 64-bit header's box reaches eight bytes further than its size,
        // and a size near the top of the range sums past it. The walk
        // ends when `pos` reaches `end`, so a sum that wrapped would
        // step backwards and could cycle rather than finish.
        let size = header.size;
        let past_the_end =
            DemuxError::Parse("mp4: a box in the subsegment runs past its end".into());
        let Some(next) = (body - 8).checked_add(size) else {
            return Err(past_the_end);
        };
        if size < 8 || next > end {
            return Err(past_the_end);
        }
        if header.name == BoxType::MoofBox {
            if size > ceiling {
                return Err(DemuxError::Cap("movie fragment above the header ceiling"));
            }
            let moof = match catch_unwind(AssertUnwindSafe(|| MoofBox::read_box(&mut reader, size)))
            {
                Ok(Ok(moof)) => moof,
                Ok(Err(e)) => return Err(DemuxError::Parse(format!("mp4: movie fragment: {e}"))),
                Err(_) => {
                    return Err(DemuxError::Parse(
                        "mp4 parser panicked on a movie fragment".into(),
                    ));
                }
            };
            moofs.push(moof);
        } else if moofs.is_empty() {
            // A subsegment may be introduced by a segment type, an event
            // message, a producer reference time or free space; anything
            // else means the index does not describe this file.
            let kind = u32::from(header.name);
            let allowed = kind == STYP
                || kind == PRFT
                || kind == u32::from(BoxType::EmsgBox)
                || kind == u32::from(BoxType::FreeBox);
            if !allowed {
                return Err(DemuxError::Parse(
                    "mp4: the segment index does not land on a movie fragment".into(),
                ));
            }
        }
        pos = next;
    }

    if moofs.is_empty() {
        return Err(DemuxError::Parse(
            "mp4: the segment index does not land on a movie fragment".into(),
        ));
    }
    Ok(moofs)
}

/// Every sample of a fragmented file read by walking its fragments, by
/// track.
type Walked = BTreeMap<u32, Vec<FragmentSample>>;

/// Walk the fragments that follow `moov`, building each as
/// the indexed path does. Only fragment headers are fetched, charged to
/// the open's budget, and the samples the whole file may state are
/// capped: a run with no per-sample fields states its count in four
/// bytes. A box running past the end, or a header cut short or
/// unreadable, ends the walk with a note saying where, as a capture cut
/// off mid-fragment does. A failing source or an exhausted budget fails
/// the open instead: a table silently short of later fragments would end
/// the file early.
fn walk_fragments(
    src: &mut CachedSource,
    header: &re_mp4::Mp4,
    media: std::ops::Range<u64>,
    limits: &DemuxLimits,
    budget: &mut u64,
    notes: &mut Vec<String>,
) -> Result<Walked, DemuxError> {
    let mut reader = SourceReader::new_sparse(src.inner_mut(), media.end, *budget);
    let walked = walk_moofs(&mut reader, header, media, limits, notes);
    *budget = reader.remaining_budget();
    walked
}

fn walk_moofs(
    reader: &mut SourceReader<'_>,
    header: &re_mp4::Mp4,
    media: std::ops::Range<u64>,
    limits: &DemuxLimits,
    notes: &mut Vec<String>,
) -> Result<Walked, DemuxError> {
    let defaults = crate::mp4_fragment::track_defaults(&header.moov);
    let ceiling = MAX_FRAGMENT_BYTES.min(limits.max_metadata_bytes);
    // A track fragment with no `tfdt` carries on from the samples before
    // it, which for the first are the ones `moov` holds.
    let mut cursors: Cursors = header
        .moov
        .traks
        .iter()
        .map(|trak| (trak.tkhd.track_id, moov_decode_end(trak)))
        .collect();
    let mut samples_left = MAX_WALKED_SAMPLES;
    let mut walked = Walked::new();
    let mut pos = media.start;

    let stopped = loop {
        if pos == media.end {
            break None;
        }
        if media.end - pos < 8 {
            break Some("fewer bytes are left than a box header takes");
        }
        if reader.seek(SeekFrom::Start(pos)).is_err() {
            break Some("the box there cannot be reached");
        }
        let header = match BoxHeader::read(&mut *reader) {
            Ok(header) => header,
            Err(re_mp4::Error::Io(e)) if e.kind() != std::io::ErrorKind::UnexpectedEof => {
                return Err(DemuxError::Io(e));
            }
            Err(_) => break Some("a box header there is cut short or unreadable"),
        };
        let body = reader.stream_position().map_err(DemuxError::Io)?;
        // Measured from `body - 8` as in the other walks, so a 64-bit
        // header's box reaches as far as its size says.
        let size = header.size;
        let Some(next) = (body - 8).checked_add(size) else {
            break Some("a box there states a size past any file's end");
        };
        if size < 8 {
            break Some("a box there states a size shorter than its header");
        }
        if next > media.end {
            break Some("a box there runs past the end of the file");
        }
        if header.name == BoxType::MoofBox {
            if size > ceiling {
                return Err(DemuxError::Cap("movie fragment above the header ceiling"));
            }
            let moof = match catch_unwind(AssertUnwindSafe(|| {
                MoofBox::read_box(&mut *reader, size)
            })) {
                Ok(Ok(moof)) => moof,
                Ok(Err(e)) => return Err(DemuxError::Parse(format!("mp4: movie fragment: {e}"))),
                Err(_) => {
                    return Err(DemuxError::Parse(
                        "mp4 parser panicked on a movie fragment".into(),
                    ));
                }
            };
            let built =
                crate::mp4_fragment::build(&moof, &defaults, &mut cursors, &mut samples_left)
                    .map_err(|why| DemuxError::Parse(format!("mp4: {why}")))?;
            for (track_id, samples) in built {
                walked.entry(track_id).or_default().extend(samples);
            }
        }
        pos = next;
    };
    if let Some(why) = stopped {
        push_note(notes, || {
            format!(
                "fragment walk stopped at byte {pos} of {}: {why}; the fragments before it play",
                media.end
            )
        });
    }
    Ok(walked)
}

/// Where the samples `moov` holds stop decoding, as the file states the
/// times: the sum of its decode deltas.
fn moov_decode_end(trak: &re_mp4::TrakBox) -> i64 {
    let end = trak
        .mdia
        .minf
        .stbl
        .stts
        .entries
        .iter()
        .fold(0u64, |end, entry| {
            end.saturating_add(u64::from(entry.sample_count) * u64::from(entry.sample_delta))
        });
    i64::try_from(end).unwrap_or(i64::MAX)
}

/// Where a walked track's media ends: the latest any sample finishes
/// presenting, with the track's edit-list shift applied as it is to the
/// samples themselves.
fn walked_end(samples: &[FragmentSample], timescale: u64, shift: i64) -> Option<MediaTime> {
    let end = samples
        .iter()
        .map(|s| {
            s.pts
                .saturating_sub(shift)
                .saturating_add(i64::from(s.duration))
        })
        .max()?;
    stated_span(u64::try_from(end).ok()?, timescale)
}

/// Move a time from one timescale to another.
fn rescale(value: u64, from: u64, to: u64) -> u64 {
    if from == 0 {
        return value;
    }
    (u128::from(value) * u128::from(to) / u128::from(from)) as u64
}

/// What a picker needs to show for one audio track, read straight from
/// the container rather than from a bound decoder: a track that is never
/// selected still has to be describable.
fn describe_audio(
    mp4: &re_mp4::Mp4,
    track: &re_mp4::Track,
    id: TrackId,
    config: Option<AudioSpecificConfig>,
) -> AudioTrackInfo {
    let trak = track.trak(mp4);
    // ISO 639-2/T, with the unset marker spelled out rather than shown.
    let language = match trak.mdia.mdhd.language.as_str() {
        "und" | "" => None,
        other => Some(other.to_string()),
    };
    let contents = &trak.mdia.minf.stbl.stsd.contents;
    if let Some((sample_rate, channels)) = mp3_entry(contents) {
        return AudioTrackInfo {
            id,
            language,
            label: None,
            codec: AudioCodec::Mp3,
            sample_rate,
            channels,
        };
    }
    let (sample_rate, channels) = match contents {
        re_mp4::StsdBoxContent::Mp4a(mp4a) => match config {
            Some(config) => (config.output_rate, u32::from(config.channels())),
            None => (
                u32::from(mp4a.samplerate.value()),
                u32::from(mp4a.channelcount),
            ),
        },
        _ => (0, 0),
    };
    AudioTrackInfo {
        id,
        language,
        label: None,
        codec: AudioCodec::Aac,
        sample_rate,
        channels,
    }
}

/// Cover art from the iTunes-style metadata atom. The box parser already
/// walks `udta`, so this reads what it found rather than opening a second
/// path through the same untrusted bytes.
fn artwork_from_moov(mp4: &re_mp4::Mp4) -> Option<crate::Artwork> {
    let udta = mp4.moov.udta.as_ref()?;
    let re_mp4::MetaBox::Mdir { ilst } = udta.meta.as_ref()? else {
        return None;
    };
    let item = ilst.as_ref()?.items.get(&re_mp4::MetadataKey::Poster)?;
    let data = &item.data.data;
    if data.is_empty() || data.len() > crate::artwork::MAX_ARTWORK_BYTES {
        return None;
    }
    // `covr` states only "image"; the format is in the bytes, so it is
    // sniffed rather than trusted.
    let mime = if data.starts_with(&[0x89, b'P', b'N', b'G']) {
        "image/png"
    } else if data.starts_with(&[0xFF, 0xD8]) {
        "image/jpeg"
    } else {
        return None;
    };
    Some(crate::Artwork {
        mime: mime.to_string(),
        data: data.clone(),
    })
}

impl Demuxer for Mp4Demuxer {
    fn next_event(&mut self) -> Result<StreamEvent, DemuxError> {
        if let Some(event) = self.pending.pop_front() {
            return Ok(event);
        }
        self.fill()?;

        // Interleave in decode order; audio wins ties so it never trails a
        // burst of larger video AUs.
        let take_video = match (self.next_video(), self.next_audio()) {
            (Some(v), Some(a)) => v.dts < a.dts,
            (Some(_), None) => true,
            (None, Some(_)) => false,
            (None, None) => return Ok(StreamEvent::Eos(EosReason::Natural)),
        };

        if take_video {
            let sample = self.next_video().expect("checked above");
            let raw = self.read_sample(sample)?;
            let video = self.video.as_mut().expect("checked above");
            let data = if self.emit_raw_video {
                raw
            } else {
                Self::convert_sample(video, raw, sample.sync)?
            };
            let track = video.id;
            video.samples.take();
            Ok(StreamEvent::Au(Au {
                track,
                data,
                pts: sample.pts,
                dts: sample.dts,
                key: sample.sync,
                generation: self.generation,
            }))
        } else {
            let sample = self.next_audio().expect("checked above");
            let data = self.read_sample(sample)?;
            let audio = self.audio.as_mut().expect("checked above");
            let track = audio.id;
            audio.samples.take();
            Ok(StreamEvent::Au(Au {
                track,
                data,
                pts: sample.pts,
                dts: sample.dts,
                key: true,
                generation: self.generation,
            }))
        }
    }

    fn seek(&mut self, target: MediaTime, generation: Generation) -> Result<MediaTime, DemuxError> {
        if self.live.is_some() {
            return Err(DemuxError::Unsupported("seek on a live MP4 stream"));
        }
        self.generation = generation;
        if self.fragments.is_some() {
            return self.seek_fragmented(target);
        }

        let landed = if let Some(VideoTrack {
            samples: Samples::Whole { all, next },
            ..
        }) = &mut self.video
        {
            // Last sync sample at or before the target, by decode order
            // (sync samples present at their decode time).
            let key = landing(all, target).unwrap_or(0);
            *next = key;
            all.get(key).map(|s| s.pts).unwrap_or(target)
        } else {
            target
        };

        if let Some(AudioTrack {
            samples: Samples::Whole { all, next },
            ..
        }) = &mut self.audio
        {
            *next = all.partition_point(|s| s.pts < landed);
        }
        Ok(landed)
    }

    fn duration(&self) -> Option<MediaTime> {
        self.duration
    }

    fn video_track(&self) -> Option<TrackId> {
        Mp4Demuxer::video_track(self)
    }

    fn audio_track(&self) -> Option<TrackId> {
        Mp4Demuxer::audio_track(self)
    }

    fn audio_tracks(&self) -> Vec<AudioTrackInfo> {
        self.audio_tracks.clone()
    }

    fn artwork(&self) -> Option<&crate::Artwork> {
        self.artwork.as_ref()
    }

    fn audio_start(&self) -> MediaTime {
        self.audio.as_ref().map_or(MediaTime::ZERO, |a| a.start)
    }

    fn video_end(&self) -> Option<MediaTime> {
        self.video.as_ref().and_then(|v| v.end)
    }

    fn audio_end(&self) -> Option<MediaTime> {
        self.audio.as_ref().and_then(|a| a.end)
    }

    fn take_notes(&mut self) -> Vec<String> {
        Mp4Demuxer::take_notes(self)
    }

    fn take_refusals(&mut self) -> Vec<String> {
        Mp4Demuxer::take_refusals(self)
    }
}

fn scale_to_us(value: i64, timescale: u64) -> i64 {
    let us = i128::from(value) * 1_000_000 / i128::from(timescale.max(1));
    us.clamp(i128::from(i64::MIN), i128::from(i64::MAX)) as i64
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::MemSource;

    fn subsegment(bytes: Vec<u8>, size: u32) -> Result<Vec<MoofBox>, DemuxError> {
        let mut src = CachedSource::new(Box::new(MemSource(bytes)));
        let entry = IndexEntry {
            offset: 0,
            size,
            time: 0,
            duration: 0,
        };
        read_subsegment(&mut src, entry, &DemuxLimits::default())
    }

    fn header(size: u32, kind: &[u8; 4]) -> Vec<u8> {
        let mut out = size.to_be_bytes().to_vec();
        out.extend_from_slice(kind);
        out
    }

    /// A box stating its size as a 64-bit one, whose sixteen-byte header
    /// is the case both box walks have to measure from the right place.
    fn header64(size: u64, kind: &[u8; 4]) -> Vec<u8> {
        let mut out = 1u32.to_be_bytes().to_vec();
        out.extend_from_slice(kind);
        out.extend_from_slice(&size.to_be_bytes());
        out
    }

    fn prefix_of(bytes: Vec<u8>) -> Prefix {
        let len = bytes.len() as u64;
        let mut src = CachedSource::new(Box::new(MemSource(bytes)));
        let mut notes = Vec::new();
        let mut reader = SourceReader::new(&mut src, len, 64 * 1024);
        scan_prefix(&mut reader, 0, len, &mut notes, false)
    }

    /// A 64-bit size is stated from the start of the box and reported
    /// eight bytes short of it, so a box overrunning the file by exactly
    /// those eight bytes is the one a length check placed on the
    /// reported size lets through. The walk then steps past the end and
    /// measures what is left of the file as a negative number.
    #[test]
    fn a_sixty_four_bit_box_is_bounded_by_where_it_ends() {
        let mut bytes = header(16, b"ftyp");
        bytes.extend_from_slice(&[0u8; 8]);
        let at = bytes.len() as u64;
        // Sixteen bytes of header, stating a box that ends eight bytes
        // past the file.
        bytes.extend_from_slice(&header64(
            (bytes.len() + 16 - at as usize + 8) as u64,
            b"moov",
        ));
        assert_eq!(bytes.len(), 32);
        let prefix = prefix_of(bytes);
        assert!(prefix.moov_end.is_none(), "the box does not fit the file");
        assert_eq!(prefix.first_fragment, None);

        // The same overrun inside a subsegment, where the walk runs on
        // the playback path with no fence around it. Free space rather
        // than a fragment, so nothing inside the box is read and the
        // walk reaches its next step with the overrun in hand.
        let over = header64(24, b"free");
        let err = subsegment(over, 16).expect_err("refused");
        assert!(matches!(err, DemuxError::Parse(_)), "{err}");

        // A 64-bit size reaches the top of the range, so the sum that
        // finds the box's end runs past it: a walk that steps backwards
        // from a wrapped one could cycle rather than finish, and the
        // subsegment walk finishes only by reaching the end.
        let mut wraps = header(16, b"ftyp");
        wraps.extend_from_slice(&[0u8; 8]);
        wraps.extend_from_slice(&header64(u64::MAX, b"free"));
        let prefix = prefix_of(wraps);
        assert!(prefix.moov_end.is_none());
        assert_eq!(prefix.first_fragment, None);

        let mut wraps = header(8, b"free");
        wraps.extend_from_slice(&header64(u64::MAX, b"free"));
        let err = subsegment(wraps, 24).expect_err("refused");
        assert!(matches!(err, DemuxError::Parse(_)), "{err}");

        // A 64-bit box that does fit is walked, so the bound is not
        // simply refusing the form.
        let mut fits = header(16, b"ftyp");
        fits.extend_from_slice(&[0u8; 8]);
        fits.extend_from_slice(&header64(24, b"moov"));
        fits.extend_from_slice(&[0u8; 8]);
        fits.extend_from_slice(&header(8, b"moof"));
        let prefix = prefix_of(fits);
        assert!(prefix.moov_end.is_some());
        assert_eq!(prefix.first_fragment, Some(40));
    }

    /// A fragment header whose declared size is beyond anything real is
    /// refused on the header alone, before a byte of it is fetched.
    #[test]
    fn an_absurd_fragment_size_is_refused_before_it_is_read() {
        let bytes = header(32 * 1024 * 1024, b"moof");
        let err = subsegment(bytes, u32::MAX).expect_err("refused");
        assert!(
            matches!(err, DemuxError::Cap(_)),
            "{err} should be a cap refusal"
        );
    }

    /// The index says a subsegment begins here; if a movie fragment does
    /// not, the index describes some other file.
    #[test]
    fn an_offset_that_is_not_a_fragment_is_a_parse_error() {
        let mut bytes = header(16, b"mdat");
        bytes.extend_from_slice(&[0u8; 8]);
        let err = subsegment(bytes, 16).expect_err("refused");
        assert!(matches!(err, DemuxError::Parse(_)), "{err}");

        // Nothing at all in the range is the same refusal.
        assert!(matches!(
            subsegment(Vec::new(), 0).expect_err("refused"),
            DemuxError::Parse(_)
        ));
    }

    #[test]
    fn a_box_running_past_the_subsegment_is_a_parse_error() {
        let bytes = header(64, b"moof");
        let err = subsegment(bytes, 16).expect_err("refused");
        assert!(matches!(err, DemuxError::Parse(_)), "{err}");
    }

    /// A muxed file written with an index per track has to be seeked by
    /// the video track's, since that is the one whose times a landing is
    /// chosen against.
    #[test]
    fn the_video_tracks_index_is_the_one_seeked_by() {
        let index = |reference_id| SegmentIndex {
            reference_id,
            timescale: 1000,
            entries: Vec::new(),
            end: 0,
        };
        let two = || vec![index(1), index(2)];
        assert_eq!(
            choose_index(two(), Some(2)).expect("chosen").reference_id,
            2
        );
        assert_eq!(
            choose_index(two(), Some(1)).expect("chosen").reference_id,
            1
        );
        // A track the indexes say nothing about, and a file with no
        // picture at all, both fall back to the first.
        assert_eq!(
            choose_index(two(), Some(9)).expect("chosen").reference_id,
            1
        );
        assert_eq!(choose_index(two(), None).expect("chosen").reference_id, 1);
        assert!(choose_index(Vec::new(), Some(1)).is_none());
    }

    /// An edit list's start is whatever the file says. An empty edit
    /// converts from the movie's timescale to the track's, and a media
    /// time past `i64::MAX` clamps there rather than wrapping to a
    /// negative shift that would move every sample later.
    #[test]
    fn an_edit_lists_start_is_read_without_wrapping() {
        let mut elst = re_mp4::ElstBox {
            version: 1,
            ..Default::default()
        };
        for (segment_duration, media_time) in [(500, u64::MAX), (1000, 1 << 63)] {
            elst.entries.push(Default::default());
            let entry = elst.entries.last_mut().expect("just pushed");
            entry.segment_duration = segment_duration;
            entry.media_time = media_time;
        }
        let trak = re_mp4::TrakBox {
            edts: Some(re_mp4::EdtsBox { elst: Some(elst) }),
            ..Default::default()
        };
        let start = edit_start(&trak, 1000, 48_000);
        assert_eq!(start.empty, 24_000);
        assert_eq!(start.media_time, i64::MAX);
    }

    /// Encoder priming is stated once in the edit list and applied to
    /// every fragment's samples, so the samples ahead of the origin carry
    /// negative times and the PCM stage drops them.
    #[test]
    fn the_priming_shift_reaches_a_fragments_samples() {
        let sample = FragmentSample {
            offset: 0,
            size: 4,
            dts: 1024,
            pts: 2048,
            duration: 1024,
            sync: true,
        };
        let plain = to_ref(sample, 48000, 0, &DemuxLimits::default()).expect("within the caps");
        assert_eq!(plain.dts.as_micros(), 21333);
        assert_eq!(plain.pts.as_micros(), 42666);

        let primed = to_ref(sample, 48000, 2048, &DemuxLimits::default()).expect("within the caps");
        assert_eq!(primed.dts.as_micros(), -21333);
        assert_eq!(primed.pts.as_micros(), 0);

        let huge = FragmentSample {
            size: u32::MAX,
            ..sample
        };
        assert!(matches!(
            to_ref(huge, 48000, 0, &DemuxLimits::default()).expect_err("above the AU ceiling"),
            DemuxError::Cap(_)
        ));

        // The shift is whatever the edit list states, so it reaches the
        // subtraction as any `i64` and the scaling as any product of
        // one: both saturate, so a hostile edit costs a wrong timestamp
        // rather than a panic or a time that has wrapped round to the
        // other end of the range.
        let ends = |(pts, dts): (MediaTime, MediaTime)| (pts.as_micros(), dts.as_micros());
        assert_eq!(
            ends(shifted(0, 0, i64::MIN, 1)),
            (i64::MAX, i64::MAX),
            "a shift that would carry the times past the top of the range"
        );
        assert_eq!(
            ends(shifted(0, 0, i64::MAX, 1)),
            (i64::MIN, i64::MIN),
            "and past the bottom of it"
        );
        assert_eq!(
            ends(shifted(i64::MIN, i64::MAX, i64::MIN, 1)),
            (0, i64::MAX),
            "the times themselves at the ends of the range"
        );
    }

    /// A `sidx` body stated behind a 64-bit size begins eight bytes
    /// further in than one behind an ordinary size, and both are read
    /// from the span the walk computed rather than from the size the
    /// header reports. Read a byte short or a byte long and the index
    /// parses as rubbish, or the read runs past the end of the file and
    /// the walk stops, taking every box behind it.
    #[test]
    fn a_segment_index_behind_a_sixty_four_bit_size_is_read_whole() {
        // version 0, one reference to media, no first offset.
        let mut index = vec![0u8, 0, 0, 0];
        index.extend_from_slice(&1u32.to_be_bytes()); // reference_ID
        index.extend_from_slice(&1000u32.to_be_bytes()); // timescale
        index.extend_from_slice(&0u32.to_be_bytes()); // earliest pts
        index.extend_from_slice(&0u32.to_be_bytes()); // first offset
        index.extend_from_slice(&0u16.to_be_bytes()); // reserved
        index.extend_from_slice(&1u16.to_be_bytes()); // reference count
        index.extend_from_slice(&8u32.to_be_bytes()); // to media, 8 bytes
        index.extend_from_slice(&1000u32.to_be_bytes()); // duration
        index.extend_from_slice(&0x9000_0000u32.to_be_bytes()); // SAP type 1

        for wide in [false, true] {
            let mut bytes = header(16, b"ftyp");
            bytes.extend_from_slice(&[0u8; 8]);
            let head = if wide {
                header64(16 + index.len() as u64, b"sidx")
            } else {
                header(8 + index.len() as u32, b"sidx")
            };
            bytes.extend_from_slice(&head);
            bytes.extend_from_slice(&index);
            let at = bytes.len() as u64;
            bytes.extend_from_slice(&header(8, b"moof"));

            let prefix = prefix_of(bytes);
            assert_eq!(prefix.first_fragment, Some(at), "wide: {wide}");
            let found = prefix.indexes.first().expect("the index parses");
            assert_eq!(found.timescale, 1000, "wide: {wide}");
            assert_eq!(found.entries.len(), 1, "wide: {wide}");
            // The first reference is measured from the end of the box,
            // so getting the body's extent wrong moves it off the
            // fragment as well as misreading the table.
            assert!(found.starts_at(at), "wide: {wide}");

            // With nothing behind the box, reading past its end reads
            // past the file's: the read fails, the walk stops, and the
            // index goes with it. An over-read is invisible anywhere
            // else, since the table is as long as its count says and
            // whatever follows it is never looked at.
            let mut last = header(16, b"ftyp");
            last.extend_from_slice(&[0u8; 8]);
            last.extend_from_slice(&head);
            last.extend_from_slice(&index);
            assert_eq!(
                prefix_of(last).indexes.len(),
                1,
                "the index is the last box in the file, wide: {wide}"
            );
        }
    }

    /// One file can be parsed twice at open (a prefix that turns out to
    /// describe only part of itself is followed by a walk of the whole).
    /// Both must come out of the one budget, or the cap on what an open
    /// may fetch is worth double what it says.
    #[test]
    fn a_second_parse_continues_the_budget_rather_than_restarting_it() {
        let bytes = std::fs::read(
            std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
                .join("../fixtures/h264-aac-640x360-30fps.mp4"),
        )
        .expect("fixture readable");
        let len = bytes.len() as u64;
        let mut src = CachedSource::new(Box::new(MemSource(bytes)));

        let whole = DemuxLimits::default().max_metadata_bytes;
        let mut budget = whole;
        read_metadata(&mut src, len, &mut budget, false).expect("the fixture parses");
        let after_one = budget;
        assert!(after_one < whole, "the parse reported no spend at all");

        read_metadata(&mut src, len, &mut budget, false).expect("the fixture parses again");
        assert!(
            budget < after_one,
            "the second parse started the bound again: {budget} left after {after_one}"
        );
        assert_eq!(
            whole - budget,
            2 * (whole - after_one),
            "two parses of one file cost twice one"
        );
    }

    /// The walk down to each `esds` reads metadata like any parse at
    /// open, so it spends from the same budget.
    #[test]
    fn the_audio_config_walk_spends_the_open_budget() {
        let bytes = std::fs::read(
            std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
                .join("../fixtures/h264-aac-640x360-30fps.mp4"),
        )
        .expect("fixture readable");
        let len = bytes.len() as u64;
        let mut src = CachedSource::new(Box::new(MemSource(bytes)));

        let mut budget = DemuxLimits::default().max_metadata_bytes;
        let mp4 = read_metadata(&mut src, len, &mut budget, false).expect("the fixture parses");
        let after_parse = budget;
        let configs = audio_configs(&mp4, &mut src, len, &mut budget);
        assert_eq!(configs.len(), 1, "the fixture's one AAC track");
        assert!(
            budget < after_parse,
            "the walk spent nothing: {budget} left after {after_parse}"
        );
    }

    /// All ones is how a container states that it does not know its own
    /// length, and a length no file has is the same claim made
    /// carelessly. Either reaches the seek bar as fact if believed.
    #[test]
    fn a_duration_the_container_does_not_know_is_reported_as_none() {
        assert_eq!(
            stated_span(90_000, 1000),
            Some(MediaTime::from_secs(90)),
            "an ordinary duration"
        );
        assert_eq!(stated_span(0, 1000), None, "zero is unknown, not live");
        assert_eq!(stated_span(1000, 0), None, "no timescale to scale by");
        assert_eq!(
            stated_span(u64::from(u32::MAX), 1000),
            None,
            "the version 0 unknown marker, which is 49 days if believed"
        );
        assert_eq!(
            stated_span(u64::MAX, 1000),
            None,
            "the version 1 unknown marker"
        );
        // Against a large timescale the markers scale to a span that
        // looks perfectly ordinary, so recognising them is what refuses
        // these two rather than the plausibility bound.
        assert_eq!(
            stated_span(u64::from(u32::MAX), u64::from(u32::MAX)),
            None,
            "the version 0 marker, which is one second if believed"
        );
        assert_eq!(
            stated_span(u64::MAX, u64::MAX),
            None,
            "and the version 1 marker, likewise"
        );
        assert_eq!(
            stated_span(101 * 3600 * 1000, 1000),
            None,
            "longer than any file"
        );
        assert_eq!(
            stated_span(99 * 3600 * 1000, 1000),
            Some(MediaTime::from_secs(99 * 3600)),
            "and the bound is not refusing long files as such"
        );
    }

    /// A fragment whose run states only a count: sixteen bytes that name
    /// any number of samples, all taking the track defaults.
    fn counted_fragment(count: u32) -> Vec<u8> {
        let mut mfhd = header(16, b"mfhd");
        mfhd.extend_from_slice(&[0; 4]);
        mfhd.extend_from_slice(&1u32.to_be_bytes());
        let mut tfhd = header(16, b"tfhd");
        tfhd.extend_from_slice(&0x0002_0000u32.to_be_bytes()); // default base is moof
        tfhd.extend_from_slice(&1u32.to_be_bytes());
        let mut trun = header(16, b"trun");
        trun.extend_from_slice(&[0; 4]);
        trun.extend_from_slice(&count.to_be_bytes());
        let traf = [header(8 + 32, b"traf"), tfhd, trun].concat();
        [header(8 + 16 + 40, b"moof"), mfhd, traf].concat()
    }

    /// A fragment stating no base decode time carries on from the samples
    /// before it, which for the first fragment are the ones `moov` holds:
    /// hiding every `tfdt` leaves the walked times as they were.
    #[test]
    fn a_fragment_without_a_base_time_follows_the_moovs_samples() {
        let bytes = std::fs::read(
            std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
                .join("../fixtures/h264-aac-moov-and-frag.mp4"),
        )
        .expect("fixture readable");
        let size_at = |at: usize| u32::from_be_bytes(bytes[at..at + 4].try_into().unwrap());
        let moov_end = u64::from(size_at(0) + size_at(size_at(0) as usize));
        let walk = |bytes: Vec<u8>| {
            let len = bytes.len() as u64;
            let mut src = CachedSource::new(Box::new(MemSource(bytes)));
            let mut budget = DemuxLimits::default().max_metadata_bytes;
            let header =
                read_metadata(&mut src, moov_end, &mut budget, false).expect("the moov parses");
            walk_fragments(
                &mut src,
                &header,
                moov_end..len,
                &DemuxLimits::default(),
                &mut budget,
                &mut Vec::new(),
            )
            .expect("the fragments walk")
        };

        // `tfdt` becomes `free`, a box the fragment parser steps over.
        let mut hidden = bytes.clone();
        let mut at = moov_end as usize;
        while let Some(found) = hidden[at..].windows(4).position(|w| w == b"tfdt") {
            hidden[at + found..at + found + 4].copy_from_slice(b"free");
            at += found + 4;
        }
        assert_ne!(hidden, bytes, "the fixture's fragments state base times");

        let stated = walk(bytes);
        let followed = walk(hidden);
        for (track, samples) in &stated {
            let dts = |walked: &Walked| walked[track].iter().map(|s| s.dts).collect::<Vec<_>>();
            assert_eq!(dts(&followed), dts(&stated), "track {track}");
            assert!(
                samples[0].dts > 0,
                "track {track} starts after the moov's samples"
            );
        }
    }

    /// Running out of budget part-way down the fragments fails the open
    /// rather than ending the file where the walk stopped.
    #[test]
    fn a_walk_that_runs_out_of_budget_fails_the_open() {
        let bytes = std::fs::read(
            std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
                .join("../fixtures/h264-aac-negcts-frag.mp4"),
        )
        .expect("fixture readable");
        let size_at = |at: usize| u32::from_be_bytes(bytes[at..at + 4].try_into().unwrap());
        let moov_end = (size_at(0) + size_at(size_at(0) as usize)) as u64;
        let len = bytes.len() as u64;
        let mut src = CachedSource::new(Box::new(MemSource(bytes)));
        let mut budget = DemuxLimits::default().max_metadata_bytes;
        let header =
            read_metadata(&mut src, moov_end, &mut budget, false).expect("the moov parses");
        let mut budget = 1024;
        assert!(matches!(
            walk_fragments(
                &mut src,
                &header,
                moov_end..len,
                &DemuxLimits::default(),
                &mut budget,
                &mut Vec::new(),
            ),
            Err(DemuxError::Io(_))
        ));
    }

    /// A track with these edits, as (segment duration, media time, rate),
    /// the media time `u64::MAX` for an empty edit.
    fn trak_with_edits(edits: &[(u64, u64, u16)]) -> re_mp4::TrakBox {
        let mut elst = re_mp4::ElstBox::default();
        for &(segment_duration, media_time, media_rate) in edits {
            elst.entries.push(Default::default());
            let entry = elst.entries.last_mut().expect("just pushed");
            entry.segment_duration = segment_duration;
            entry.media_time = media_time;
            entry.media_rate = media_rate;
        }
        re_mp4::TrakBox {
            edts: Some(re_mp4::EdtsBox { elst: Some(elst) }),
            ..Default::default()
        }
    }

    /// Only empty edits followed by one edit of media at the normal rate
    /// give an end; any other shape plays the media whole.
    #[test]
    fn an_edit_list_ends_its_track_only_in_the_shape_it_can() {
        let end = |edits: &[(u64, u64, u16)]| edit_end(&trak_with_edits(edits), 1000);
        let at = |us| Some(MediaTime::from_micros(us));
        assert_eq!(end(&[(3900, 1024, 1)]), at(3_900_000));
        assert_eq!(end(&[(500, u64::MAX, 1), (3000, 1024, 1)]), at(3_500_000));
        assert_eq!(
            end(&[(500, u64::from(u32::MAX), 1), (3000, 1024, 1)]),
            at(3_500_000),
            "a version 0 empty edit"
        );
        assert_eq!(end(&[]), None);
        assert_eq!(end(&[(500, u64::MAX, 1)]), None, "no media at all");
        assert_eq!(end(&[(3000, 1024, 1), (3000, 1024, 1)]), None);
        assert_eq!(end(&[(3000, 1024, 1), (500, u64::MAX, 1)]), None);
        assert_eq!(end(&[(3000, 1024, 2)]), None, "another rate");
        assert_eq!(end(&[(0, 1024, 1)]), None, "no length stated");
        assert_eq!(edit_end(&re_mp4::TrakBox::default(), 1000), None);
    }

    /// A walked track ends where its last sample does on the timeline the
    /// samples are emitted on: an audio track's priming is not counted.
    #[test]
    fn a_walked_end_takes_the_edit_list_shift() {
        let sample = |pts| FragmentSample {
            offset: 0,
            size: 4,
            dts: pts,
            pts,
            duration: 1024,
            sync: true,
        };
        let samples = [sample(0), sample(48000)];
        assert_eq!(
            walked_end(&samples, 48000, 1024),
            Some(MediaTime::from_micros(1_000_000))
        );
        assert_eq!(
            walked_end(&samples, 48000, 0),
            Some(MediaTime::from_micros(1_021_333))
        );
    }

    /// The walk caps the samples a file states in all, not per fragment:
    /// each run here is within a fragment's allowance, and only the file
    /// as a whole goes past. Pinned on both sides of the limit.
    #[test]
    fn a_walked_file_may_state_so_many_samples_and_no_more() {
        let fixture = std::fs::read(
            std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
                .join("../fixtures/h264-aac-negcts-frag.mp4"),
        )
        .expect("fixture readable");
        // `ftyp` then `moov`, each stated in an ordinary size.
        let size_at = |at: usize| u32::from_be_bytes(fixture[at..at + 4].try_into().unwrap());
        let ftyp = size_at(0) as usize;
        assert_eq!(&fixture[ftyp + 4..ftyp + 8], b"moov");
        let moov_end = (ftyp + size_at(ftyp) as usize) as u64;
        let init = fixture[..moov_end as usize].to_vec();

        let walk = |counts: &[u32]| {
            let mut bytes = init.clone();
            for count in counts {
                bytes.extend(counted_fragment(*count));
            }
            let len = bytes.len() as u64;
            let mut src = CachedSource::new(Box::new(MemSource(bytes)));
            let mut budget = DemuxLimits::default().max_metadata_bytes;
            let header =
                read_metadata(&mut src, moov_end, &mut budget, false).expect("the moov parses");
            walk_fragments(
                &mut src,
                &header,
                moov_end..len,
                &DemuxLimits::default(),
                &mut budget,
                &mut Vec::new(),
            )
        };

        let half = 1 << 21;
        let walked = walk(&[half, half]).expect("four million samples in all");
        assert_eq!(walked.values().map(Vec::len).sum::<usize>(), 1 << 22);
        assert!(
            matches!(walk(&[half, half + 1]), Err(DemuxError::Parse(_))),
            "one sample past the file's allowance"
        );
    }

    /// A subsegment may be introduced by a segment type box, which is
    /// what a file cut for delivery in pieces carries.
    #[test]
    fn a_segment_type_may_precede_the_fragment() {
        let mut bytes = header(16, b"styp");
        bytes.extend_from_slice(b"msdh\0\0\0\0");
        bytes.extend_from_slice(&header(24, b"moof"));
        bytes.extend_from_slice(&header(16, b"mfhd"));
        bytes.extend_from_slice(&[0u8; 8]);
        let moofs = subsegment(bytes, 40).expect("one fragment");
        assert_eq!(moofs.len(), 1);
        assert_eq!(moofs[0].start, 16);
    }
}
