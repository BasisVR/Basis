//! Matroska demuxer rows: pinned AU counts against the source fixture,
//! codec announces for tracks without decode adapters, keyframe-clean
//! cue seeks, Annex-B conversion of stored H.264.

use media_clock::{Generation, MediaTime};
use media_demux::{
    AudioCodec, ByteSource, DemuxError, DemuxLimits, Demuxer, Format, MAX_NOTES, MemSource,
    MkvDemuxer, SourceError, StreamEvent, VideoCodec,
};

/// Just enough EBML to state a track list. The committed fixtures carry
/// the tracks they carry, and what this row needs is a file that names
/// more of them than the note bound allows.
mod ebml {
    /// The eight-byte size form throughout: marker `0x01`, then a 56-bit
    /// length. Valid at any length, and it keeps the writer trivial.
    fn size(len: u64) -> Vec<u8> {
        let mut v = vec![0x01u8];
        v.extend_from_slice(&len.to_be_bytes()[1..]);
        v
    }

    pub fn elem(id: &[u8], body: &[u8]) -> Vec<u8> {
        claiming(id, body.len() as u64, body)
    }

    /// An element whose size field states `declared` bytes, whatever
    /// follows it.
    pub fn claiming(id: &[u8], declared: u64, body: &[u8]) -> Vec<u8> {
        let mut v = id.to_vec();
        v.extend_from_slice(&size(declared));
        v.extend_from_slice(body);
        v
    }

    pub fn uint(id: &[u8], value: u64) -> Vec<u8> {
        let bytes = value.to_be_bytes();
        let first = bytes.iter().position(|b| *b != 0).unwrap_or(7);
        elem(id, &bytes[first..])
    }

    pub fn utf8(id: &[u8], value: &str) -> Vec<u8> {
        elem(id, value.as_bytes())
    }
}

/// A Matroska file around the given DocType element, track list and
/// cluster body.
fn mkv_file(doc_type: &[u8], track_list: &[u8], cluster: &[u8]) -> Vec<u8> {
    use ebml::{elem, uint, utf8};

    let mut header = Vec::new();
    header.extend(uint(&[0x42, 0x86], 1)); // EBMLVersion
    header.extend(uint(&[0x42, 0xF7], 1)); // EBMLReadVersion
    header.extend(uint(&[0x42, 0xF2], 4)); // EBMLMaxIDLength
    header.extend(uint(&[0x42, 0xF3], 8)); // EBMLMaxSizeLength
    header.extend_from_slice(doc_type);
    header.extend(uint(&[0x42, 0x87], 4)); // DocTypeVersion
    header.extend(uint(&[0x42, 0x85], 2)); // DocTypeReadVersion

    let mut info = Vec::new();
    info.extend(uint(&[0x2A, 0xD7, 0xB1], 1_000_000)); // TimestampScale
    info.extend(utf8(&[0x4D, 0x80], "basis")); // MuxingApp
    info.extend(utf8(&[0x57, 0x41], "basis")); // WritingApp

    let mut segment = Vec::new();
    segment.extend(elem(&[0x15, 0x49, 0xA9, 0x66], &info)); // Info
    segment.extend(elem(&[0x16, 0x54, 0xAE, 0x6B], track_list)); // Tracks
    segment.extend(elem(&[0x1F, 0x43, 0xB6, 0x75], cluster)); // Cluster

    let mut out = elem(&[0x1A, 0x45, 0xDF, 0xA3], &header); // EBML
    out.extend(elem(&[0x18, 0x53, 0x80, 0x67], &segment)); // Segment
    out
}

fn doc_type() -> Vec<u8> {
    ebml::utf8(&[0x42, 0x82], "matroska")
}

/// A TrackEntry of `kind` (1 video, 2 audio), with `extra` children.
fn track_entry(number: u64, kind: u64, codec_id: &str, extra: &[u8]) -> Vec<u8> {
    use ebml::{elem, uint, utf8};

    let mut entry = Vec::new();
    entry.extend(uint(&[0xD7], number)); // TrackNumber
    entry.extend(uint(&[0x73, 0xC5], number)); // TrackUID
    entry.extend(uint(&[0x83], kind)); // TrackType
    entry.extend(utf8(&[0x86], codec_id)); // CodecID
    entry.extend_from_slice(extra);
    elem(&[0xAE], &entry) // TrackEntry
}

/// One empty cluster: the walker wants to find the first one before it
/// will call the file open.
fn empty_cluster() -> Vec<u8> {
    ebml::uint(&[0xE7], 0) // Timestamp
}

/// A Matroska file naming `tracks` audio tracks whose codec id maps to
/// nothing, so each one is a refusal, behind one playable video track.
/// The playable track is video because a file with nothing playable fails
/// to open, leaving no refusals to drain, and a bound audio track would
/// send every later audio track to the catch-all arm as a note.
fn mkv_with_unmapped_audio_tracks(tracks: u64) -> Vec<u8> {
    // VP9 needs no codec private data.
    let mut track_list = track_entry(1, 1, "V_VP9", &[]);
    for number in 2..=tracks + 1 {
        track_list.extend(track_entry(number, 2, "A_BASIS/UNMAPPED", &[]));
    }
    mkv_file(&doc_type(), &track_list, &empty_cluster())
}

/// How many refusals track selection records is the container's to
/// choose: a file states its own track count, and every skipped track is
/// one. They go through the same bound as every note the demuxers keep.
#[test]
fn matroska_track_notes_are_capped() {
    let offered = 4 * MAX_NOTES as u64;
    let mut demux = MkvDemuxer::open(
        Box::new(MemSource(mkv_with_unmapped_audio_tracks(offered))),
        DemuxLimits::default(),
        Generation(0),
    )
    .expect("open");
    let notes = demux.take_refusals();
    assert!(
        offered as usize > MAX_NOTES,
        "the row has to overrun the cap"
    );
    assert_eq!(notes.len(), MAX_NOTES, "filled and stopped");
}

/// A source that states its own size, as a server does in its
/// `Content-Length` or `Content-Range` total. Nothing makes the bytes
/// exist.
struct ClaimedSize {
    bytes: Vec<u8>,
    claimed: u64,
}

impl ByteSource for ClaimedSize {
    fn size(&mut self) -> Result<Option<u64>, SourceError> {
        Ok(Some(self.claimed))
    }

    fn read_at(&mut self, offset: u64, buf: &mut [u8]) -> Result<usize, SourceError> {
        let Some(rest) = usize::try_from(offset)
            .ok()
            .and_then(|offset| self.bytes.get(offset..))
        else {
            return Ok(0);
        };
        let n = buf.len().min(rest.len());
        buf[..n].copy_from_slice(&rest[..n]);
        Ok(n)
    }
}

/// What the hostile server claims for a file of a few hundred bytes.
const CLAIMED: u64 = 2 << 40;
/// What a hostile element states: past any allocation the host can make,
/// but inside the claimed file, so a check against the stream's end
/// passes it.
const DECLARED: u64 = 1 << 40;

fn open_claimed(bytes: Vec<u8>) -> Result<MkvDemuxer, DemuxError> {
    MkvDemuxer::open(
        Box::new(ClaimedSize {
            bytes,
            claimed: CLAIMED,
        }),
        DemuxLimits::default(),
        Generation(0),
    )
}

fn assert_capped<T>(result: Result<T, DemuxError>) {
    match result {
        Err(DemuxError::Cap(_)) => {}
        Err(other) => panic!("refused, but not as a cap: {other}"),
        Ok(_) => panic!("accepted"),
    }
}

/// Strings in the EBML header, segment info and track entries are read
/// whole during open. One stating a terabyte is refused before anything
/// is allocated for it.
#[test]
fn a_string_element_stating_a_terabyte_is_refused() {
    let doc_type = ebml::claiming(&[0x42, 0x82], DECLARED, b"matroska");
    let file = mkv_file(
        &doc_type,
        &track_entry(1, 1, "V_VP9", &[]),
        &empty_cluster(),
    );
    assert_capped(open_claimed(file));
}

/// Codec private data is binary and read whole during open, the same way.
#[test]
fn codec_private_data_stating_a_terabyte_is_refused() {
    let private = ebml::claiming(&[0x63, 0xA2], DECLARED, &[0; 4]); // CodecPrivate
    let file = mkv_file(
        &doc_type(),
        &track_entry(1, 1, "V_VP9", &private),
        &empty_cluster(),
    );
    assert_capped(open_claimed(file));
}

/// A block's frame is read whole when it is pulled. One stating more than
/// an access unit may hold is refused before anything is allocated for
/// it.
#[test]
fn a_frame_stating_a_terabyte_is_refused() {
    let mut cluster = empty_cluster();
    // Track 1, timestamp 0, keyframe, no lacing, then four bytes of the
    // terabyte the size field states.
    cluster.extend(ebml::claiming(
        &[0xA3], // SimpleBlock
        DECLARED,
        &[0x81, 0x00, 0x00, 0x80, 0, 0, 0, 0],
    ));
    let file = mkv_file(&doc_type(), &track_entry(1, 1, "V_VP9", &[]), &cluster);
    let mut demux = open_claimed(file).expect("open");
    let pulled = loop {
        match demux.next_event() {
            Ok(StreamEvent::Format(..)) => continue,
            other => break other,
        }
    };
    assert_capped(pulled);
}

/// Matroska track numbers are 64-bit and the engine's track ids 32-bit.
/// A number that does not fit is passed over: narrowed, it lands on
/// another track's id, and that track's decoder is handed its frames.
#[test]
fn a_track_number_past_32_bits_does_not_alias_another_track() {
    let mut track_list = track_entry(1, 1, "V_VP9", &[]);
    track_list.extend(track_entry((1 << 32) + 1, 2, "A_OPUS", &[]));
    let demux = MkvDemuxer::open(
        Box::new(MemSource(mkv_file(
            &doc_type(),
            &track_list,
            &empty_cluster(),
        ))),
        DemuxLimits::default(),
        Generation(0),
    )
    .expect("open");
    assert!(demux.video_track().is_some(), "the video track binds");
    assert!(
        demux.audio_track().is_none(),
        "the audio track is passed over"
    );
}

fn open(name: &str) -> MkvDemuxer {
    let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../fixtures/mkv");
    let bytes = std::fs::read(path.join(name)).expect("fixture");
    MkvDemuxer::open(
        Box::new(MemSource(bytes)),
        DemuxLimits::default(),
        Generation(0),
    )
    .expect("open")
}

#[test]
fn mkv_h264_aac_matches_the_source_counts() {
    let mut demux = open("h264-aac.mkv");
    let mut video = None;
    let mut audio = None;
    let (mut video_aus, mut audio_aus, mut keys) = (0, 0, 0);
    loop {
        match demux.next_event().expect("event") {
            StreamEvent::Format(
                track,
                Format::Video {
                    codec, coded_width, ..
                },
            ) => {
                assert_eq!(codec, VideoCodec::H264);
                assert_eq!(coded_width, 640);
                video = Some(track);
            }
            StreamEvent::Format(
                track,
                Format::Audio {
                    codec, sample_rate, ..
                },
            ) => {
                assert_eq!(codec, AudioCodec::Aac);
                assert_eq!(sample_rate, 48_000);
                audio = Some(track);
            }
            StreamEvent::Au(au) if Some(au.track) == video => {
                // Stored length-prefixed; emitted Annex B.
                assert!(au.data.starts_with(&[0, 0, 0, 1]), "Annex-B start code");
                if au.key {
                    keys += 1;
                    // Keyframes lead with SPS (NAL type 7 after the code).
                    assert_eq!(au.data[4] & 0x1F, 7, "SPS prepended on keyframes");
                }
                video_aus += 1;
            }
            StreamEvent::Au(au) if Some(au.track) == audio => audio_aus += 1,
            StreamEvent::Au(_) => {}
            StreamEvent::Eos(_) => break,
            _ => {}
        }
    }
    // The remux carries the source fixture's AU counts (GOP 60 ⇒ 3 keys).
    assert_eq!(video_aus, 180);
    assert_eq!(audio_aus, 283);
    assert_eq!(keys, 3);
}

#[test]
fn webm_vp9_opus_announces_without_adapters() {
    let mut demux = open("vp9-opus.webm");
    let mut saw_vp9 = false;
    let mut saw_opus = false;
    let mut aus = 0;
    loop {
        match demux.next_event().expect("event") {
            StreamEvent::Format(_, Format::Video { codec, .. }) => {
                assert_eq!(codec, VideoCodec::Vp9);
                saw_vp9 = true;
            }
            StreamEvent::Format(
                _,
                Format::Audio {
                    codec,
                    codec_private,
                    ..
                },
            ) => {
                assert_eq!(codec, AudioCodec::Opus);
                assert!(codec_private.starts_with(b"OpusHead"));
                saw_opus = true;
            }
            StreamEvent::Au(_) => aus += 1,
            StreamEvent::Eos(_) => break,
            _ => {}
        }
    }
    assert!(saw_vp9 && saw_opus);
    assert!(aus > 100, "frames flow even without decode adapters: {aus}");
}

#[test]
fn seek_lands_keyframe_clean_at_or_before_target() {
    let mut demuxer = open("h264-aac.mkv");
    // Drain the format announces first.
    let video = loop {
        match demuxer.next_event().expect("event") {
            StreamEvent::Format(track, Format::Video { .. }) => break track,
            StreamEvent::Eos(_) => panic!("no video format"),
            _ => {}
        }
    };
    // The remux's first video pts is 21 ms: a target before any content
    // legitimately lands there, everything else at or before the target.
    let first_pts = MediaTime::from_micros(21_000);
    for target_ms in [0i64, 1000, 2000, 3000, 5000] {
        let target = MediaTime::from_millis(target_ms);
        let landed = demuxer.seek(target, Generation(2)).expect("seek");
        assert!(
            landed <= target.max(first_pts) + MediaTime::from_millis(1),
            "seek({target_ms}ms) landed late at {landed}"
        );
        // The first video AU from the landing point must be a keyframe
        // with pts at or before the target.
        loop {
            match demuxer.next_event().expect("event") {
                StreamEvent::Au(au) if au.track == video => {
                    assert!(au.key, "seek({target_ms}ms): first video AU not a keyframe");
                    assert!(
                        au.pts <= target.max(first_pts) + MediaTime::from_millis(1),
                        "seek({target_ms}ms): first video AU at {}",
                        au.pts
                    );
                    break;
                }
                StreamEvent::Eos(_) => panic!("seek({target_ms}ms): hit EOS before video"),
                _ => {}
            }
        }
    }
}

/// Overwrite the first 8-byte float element tagged `id`. Both floats the
/// demuxer reads are declared in the header, ahead of any media data, so
/// the first match is the declared one rather than a byte coincidence.
fn patch_f64(bytes: &mut [u8], id: &[u8], value: f64) {
    let at = bytes
        .windows(id.len() + 1)
        .position(|w| &w[..id.len()] == id && w[id.len()] == 0x88)
        .expect("element present");
    let off = at + id.len() + 1;
    bytes[off..off + 8].copy_from_slice(&value.to_be_bytes());
}

fn open_patched(name: &str, id: &[u8], value: f64) -> MkvDemuxer {
    let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../fixtures/mkv");
    let mut bytes = std::fs::read(path.join(name)).expect("fixture");
    patch_f64(&mut bytes, id, value);
    MkvDemuxer::open(
        Box::new(MemSource(bytes)),
        DemuxLimits::default(),
        Generation(0),
    )
    .expect("open")
}

/// The same, for a one-byte unsigned element (`Channels`).
fn patch_u8(bytes: &mut [u8], id: &[u8], value: u8) {
    let at = bytes
        .windows(id.len() + 1)
        .position(|w| &w[..id.len()] == id && w[id.len()] == 0x81)
        .expect("element present");
    bytes[at + id.len() + 1] = value;
}

fn open_channels(name: &str, channels: u8) -> MkvDemuxer {
    let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../fixtures/mkv");
    let mut bytes = std::fs::read(path.join(name)).expect("fixture");
    patch_u8(&mut bytes, CHANNELS, channels);
    MkvDemuxer::open(
        Box::new(MemSource(bytes)),
        DemuxLimits::default(),
        Generation(0),
    )
    .expect("open")
}

const CHANNELS: &[u8] = &[0x9F];
const SAMPLING_FREQUENCY: &[u8] = &[0xB5];
const DURATION: &[u8] = &[0x44, 0x89];

/// `f64 as u32` saturates, so an unfiltered NaN would announce 0 Hz and
/// +Inf would announce `u32::MAX`, the value that sizes the playback
/// ring. Neither may reach the announce; the video track is unaffected.
#[test]
fn an_implausible_sampling_frequency_skips_the_audio_track() {
    for hostile in [f64::NAN, f64::INFINITY, 5.0e8] {
        let mut demuxer = open_patched("h264-aac.mkv", SAMPLING_FREQUENCY, hostile);
        let mut saw_video = false;
        for _ in 0..64 {
            match demuxer.next_event().expect("event") {
                StreamEvent::Format(_, Format::Audio { sample_rate, .. }) => {
                    panic!("announced {sample_rate} Hz from a declared {hostile}");
                }
                StreamEvent::Format(_, Format::Video { .. }) => saw_video = true,
                StreamEvent::Eos(_) => break,
                _ => {}
            }
        }
        assert!(saw_video, "video still announces past a hostile {hostile}");
        assert!(
            demuxer
                .take_refusals()
                .iter()
                .any(|n| n.contains("skipped audio")),
            "the skip is reported for {hostile}"
        );
    }
}

/// A track whose geometry is refused must not be offered to the picker
/// either, or the offered list and the bound track disagree.
#[test]
fn an_implausible_sampling_frequency_leaves_the_picker_empty() {
    let demuxer = open_patched("h264-multiaudio.mkv", SAMPLING_FREQUENCY, f64::NAN);
    // Assert emptiness rather than `all` over the list, which holds
    // vacuously on an empty list. Unpatched the fixture offers a picker;
    // patched, the implausible entry is filtered and the one usable track
    // left is not a choice.
    assert!(
        !open("h264-multiaudio.mkv").audio_tracks().is_empty(),
        "the fixture offers a picker before the patch"
    );
    assert!(
        demuxer.audio_tracks().is_empty(),
        "the 0 Hz entry reached the picker: {:?}",
        demuxer.audio_tracks()
    );
}

/// `f64 as i64` saturates NaN to 0, which every "duration <= 0" liveness
/// test reads as live, and +Inf to `i64::MAX`, a 292 000-year VOD.
/// Reporting no duration keeps "unknown" honest.
#[test]
fn an_implausible_duration_is_reported_as_none() {
    // 1.0e300 stays finite through the scale (the fixture's
    // TimestampScale is 1 ms, so a tick is 1000 us and it lands at
    // 1e303) and so tests the ceiling rather than the finite check. By
    // the same scale the 100 h ceiling is 3.6e8 ticks, so 3.7e8 sits an
    // hour or so past it and nothing else about it is implausible.
    for hostile in [f64::NAN, f64::INFINITY, 1.0e300, 3.7e8] {
        let demuxer = open_patched("h264-aac.mkv", DURATION, hostile);
        assert_eq!(
            demuxer.duration(),
            None,
            "a declared {hostile} must not become a duration"
        );
    }
    // The unpatched fixture still reports its real duration.
    assert!(open("h264-aac.mkv").duration().is_some());
}

/// Stated channels is a u64 upstream, so narrowing it to the engine's u32
/// is lossy in its own right (2^32 + 8 arrives as 8), and it is the ring's
/// other sizing factor. Out of range refuses the track, as an out-of-range
/// rate does.
#[test]
fn an_implausible_channel_count_skips_the_audio_track() {
    let mut demuxer = open_channels("h264-aac.mkv", 65);
    let mut saw_video = false;
    for _ in 0..64 {
        match demuxer.next_event().expect("event") {
            StreamEvent::Format(_, Format::Audio { channels, .. }) => {
                panic!("announced {channels} channels from a declared 65");
            }
            StreamEvent::Format(_, Format::Video { .. }) => saw_video = true,
            StreamEvent::Eos(_) => break,
            _ => {}
        }
    }
    assert!(
        saw_video,
        "video still announces past a hostile channel count"
    );
    assert!(
        demuxer
            .take_refusals()
            .iter()
            .any(|n| n.contains("skipped audio")),
        "the skip is reported"
    );
    // 64 is the last accepted value, so the bound is a ceiling and not an
    // accident of the fixture.
    let mut ok = open_channels("h264-aac.mkv", 64);
    let announced = (0..64).find_map(|_| match ok.next_event().expect("event") {
        StreamEvent::Format(_, Format::Audio { channels, .. }) => Some(channels),
        _ => None,
    });
    assert_eq!(announced, Some(64));
}

/// As with the rate: a track the binding path refuses must not be offered
/// to the picker either.
#[test]
fn an_implausible_channel_count_leaves_the_picker_empty() {
    let demuxer = open_channels("h264-multiaudio.mkv", 65);
    assert!(
        !open("h264-multiaudio.mkv").audio_tracks().is_empty(),
        "the fixture offers a picker before the patch"
    );
    assert!(
        demuxer.audio_tracks().is_empty(),
        "the out-of-range entry reached the picker: {:?}",
        demuxer.audio_tracks()
    );
}

/// The field is a float because some rates are not integers, so a
/// near-miss must land on the rate the encoder meant rather than one
/// below it. Truncating 47 999.999… to 47 999 drifts all session.
/// In range and finite stays accepted either way.
#[test]
fn a_near_integer_sampling_frequency_rounds_to_the_intended_rate() {
    for (declared, expect) in [
        (47_999.999_999_999_99_f64, 48_000_u32),
        (48_000.4, 48_000),
        (44_055.944_055_944_055, 44_056),
    ] {
        let mut demuxer = open_patched("h264-aac.mkv", SAMPLING_FREQUENCY, declared);
        let announced = (0..64).find_map(|_| match demuxer.next_event().expect("event") {
            StreamEvent::Format(_, Format::Audio { sample_rate, .. }) => Some(sample_rate),
            _ => None,
        });
        assert_eq!(announced, Some(expect), "declared {declared}");
    }
}

#[test]
fn an_unknown_video_codec_is_refused_and_the_audio_plays() {
    let mut track_list = track_entry(1, 1, "V_BASIS/UNMAPPED", &[]);
    track_list.extend(track_entry(2, 2, "A_OPUS", &[]));
    let mut demux = MkvDemuxer::open(
        Box::new(MemSource(mkv_file(
            &doc_type(),
            &track_list,
            &empty_cluster(),
        ))),
        DemuxLimits::default(),
        Generation(0),
    )
    .expect("opens on its audio");
    assert!(demux.video_track().is_none());
    assert_eq!(
        demux.take_refusals(),
        [
            "video codec 'V_BASIS/UNMAPPED' is not supported (supported: \
          V_MPEG4/ISO/AVC, V_MPEGH/ISO/HEVC, V_VP8, V_VP9, V_AV1)"
        ]
    );
}

#[test]
fn a_file_with_only_refused_tracks_fails_with_the_reason() {
    let refused = MkvDemuxer::open(
        Box::new(MemSource(mkv_file(
            &doc_type(),
            &track_entry(1, 1, "V_BASIS/UNMAPPED", &[]),
            &empty_cluster(),
        ))),
        DemuxLimits::default(),
        Generation(0),
    );
    match refused {
        Err(DemuxError::Refused(why)) => assert!(
            why.starts_with("video codec 'V_BASIS/UNMAPPED' is not supported"),
            "{why}"
        ),
        Err(e) => panic!("refused for the wrong reason: {e}"),
        Ok(_) => panic!("a file with nothing playable must not open"),
    }
}
