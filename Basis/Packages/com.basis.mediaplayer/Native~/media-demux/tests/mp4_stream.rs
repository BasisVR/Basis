//! Streaming MP4 demuxer tests over the committed fixtures: the three
//! layouts (faststart, trailing moov, fragmented) must demux identically,
//! events must interleave in decode order, and hostile input must produce
//! typed errors.

mod common;

use media_clock::{Generation, MediaTime};
use media_demux::{
    AudioCodec, DemuxLimits, Demuxer, EosReason, Format, MemSource, Mp4Demuxer, StreamEvent,
    VideoCodec,
};

fn fixture(name: &str) -> Vec<u8> {
    let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../fixtures")
        .join(name);
    std::fs::read(path).expect("fixture readable")
}

fn open(name: &str) -> Mp4Demuxer {
    Mp4Demuxer::open(
        Box::new(MemSource(fixture(name))),
        DemuxLimits::default(),
        Generation(1),
    )
    .expect("fixture opens")
}

struct Summary {
    video_formats: u32,
    audio_formats: u32,
    video_aus: u32,
    audio_aus: u32,
    video_keys: u32,
    first_audio_pts: Option<MediaTime>,
    first_video_au: Option<Vec<u8>>,
}

fn drain(demux: &mut Mp4Demuxer) -> Summary {
    let mut s = Summary {
        video_formats: 0,
        audio_formats: 0,
        video_aus: 0,
        audio_aus: 0,
        video_keys: 0,
        first_audio_pts: None,
        first_video_au: None,
    };
    let mut last_dts = MediaTime::from_micros(i64::MIN);
    loop {
        match demux.next_event().expect("no demux error") {
            StreamEvent::Format(_, Format::Video { codec, .. }) => {
                assert_eq!(codec, VideoCodec::H264);
                s.video_formats += 1;
            }
            StreamEvent::Format(_, Format::Audio { codec, .. }) => {
                assert_eq!(codec, AudioCodec::Aac);
                s.audio_formats += 1;
            }
            StreamEvent::Au(au) => {
                assert!(au.dts >= last_dts, "AUs interleaved in decode order");
                last_dts = au.dts;
                assert_eq!(au.generation, Generation(1));
                if au.data.starts_with(&[0, 0, 0, 1]) {
                    // Annex-B start code marks the video track's AUs.
                    s.video_aus += 1;
                    if au.key {
                        s.video_keys += 1;
                    }
                    if s.first_video_au.is_none() {
                        s.first_video_au = Some(au.data);
                    }
                } else {
                    s.audio_aus += 1;
                    if s.first_audio_pts.is_none() {
                        s.first_audio_pts = Some(au.pts);
                    }
                }
            }
            StreamEvent::Eos(reason) => {
                assert_eq!(reason, EosReason::Natural);
                return s;
            }
            other => panic!("unexpected event {other:?}"),
        }
    }
}

#[test]
fn faststart_demuxes_the_full_fixture() {
    let mut demux = open("h264-aac-640x360-30fps.mp4");
    assert_eq!(demux.take_notes(), Vec::<String>::new());
    let duration = demux.duration().expect("duration known");
    assert!((duration.as_millis() - 6000).abs() < 100, "{duration}");

    let s = drain(&mut demux);
    assert_eq!((s.video_formats, s.audio_formats), (1, 1));
    // The ffprobe-verified packet counts for this fixture.
    assert_eq!(s.video_aus, 180);
    assert_eq!(s.audio_aus, 283);
    assert_eq!(s.video_keys, 3, "6 s at GOP 60 / 30 fps");
    // The edit list shifts the priming AU ahead of the origin.
    assert_eq!(s.first_audio_pts, Some(MediaTime::from_micros(-21333)));
}

/// A `covr` item whose `data` box has type 14 (PNG), as ffmpeg writes an
/// attached picture, neither stops the file opening nor costs its tracks,
/// and the picture comes through as the art.
#[test]
fn a_png_cover_opens_and_is_the_art() {
    let mut demux = open("h264-aac-png-cover.mp4");
    let art = demux.artwork().expect("the cover is read");
    assert_eq!(art.mime, "image/png");
    assert!(art.data.starts_with(b"\x89PNG\r\n\x1a\n"), "the stored PNG");

    let s = drain(&mut demux);
    assert_eq!((s.video_formats, s.audio_formats), (1, 1));
    // The ffprobe-verified packet counts for this fixture.
    assert_eq!(s.video_aus, 60);
    assert_eq!(s.audio_aus, 95);
    assert_eq!(s.video_keys, 2, "2 s at GOP 30 / 30 fps");
}

/// A B-frame track's `mdhd` runs a frame past the 12 s its edit list,
/// `tkhd` and `mvhd` state; the duration is the edit list's.
#[test]
fn a_tracks_duration_is_its_edit_lists() {
    let demux = open("h264-640x360-30fps.mp4");
    assert_eq!(demux.duration(), Some(MediaTime::from_micros(12_000_000)));
}

/// Edit lists that stop both tracks at 3.9 s, partway through a group of
/// pictures and partway through an AAC frame. The table stops at the last
/// sample shown before then: for video the B-frame at 3.867 s, decoded after
/// the P-frame at 3.9 s it depends on, which is sent but not shown.
#[test]
fn an_edit_list_that_ends_early_stops_the_tracks_there() {
    let mut demux = open("h264-aac-edit-trimmed.mp4");
    let end = Some(MediaTime::from_micros(3_900_000));
    assert_eq!(demux.duration(), end);
    assert_eq!(demux.video_end(), end);
    assert_eq!(demux.audio_end(), end);
    let video = demux.video_track().expect("a video track").0;
    let audio = demux.audio_track().expect("an audio track").0;
    let aus = access_units(&mut demux);
    let video_pts: Vec<i64> = aus
        .iter()
        .filter(|(track, ..)| *track == video)
        .map(|(_, pts, ..)| pts.as_micros())
        .collect();
    // FFmpeg shows 117 of the 180 pictures, the last at 3.867 s.
    assert_eq!(video_pts.len(), 118);
    assert_eq!(video_pts[116], 3_900_000, "the reference past the end");
    assert_eq!(video_pts[117], 3_866_666);
    assert_eq!(
        video_pts.iter().filter(|&&pts| pts < 3_900_000).count(),
        117
    );
    // The priming frame, then every frame that starts before 3.9 s.
    let audio_pts: Vec<i64> = aus
        .iter()
        .filter(|(track, ..)| *track == audio)
        .map(|(_, pts, ..)| pts.as_micros())
        .collect();
    assert_eq!(audio_pts.len(), 184);
    assert!(audio_pts[183] < 3_900_000, "{}", audio_pts[183]);
}

/// A file whose edit lists cover all of its media states no end.
#[test]
fn an_edit_list_over_the_whole_media_states_no_end_short_of_it() {
    let demux = open("h264-aac-640x360-30fps.mp4");
    let duration = demux.duration();
    assert!(demux.video_end() <= duration && demux.audio_end() <= duration);
    assert_eq!(demux.audio_end(), Some(MediaTime::from_micros(6_000_000)));
}

#[test]
fn audio_format_carries_the_files_asc() {
    let mut demux = open("h264-aac-640x360-30fps.mp4");
    loop {
        if let StreamEvent::Format(
            _,
            Format::Audio {
                sample_rate,
                channels,
                codec_private,
                ..
            },
        ) = demux.next_event().expect("event")
        {
            assert_eq!(sample_rate, 48000);
            assert_eq!(channels, 2);
            // AOT 2 (LC), frequency index 3 (48 kHz), channel config 2,
            // and the SBR sync extension saying SBR is absent.
            assert_eq!(codec_private, vec![0x11, 0x90, 0x56, 0xE5, 0x00]);
            return;
        }
    }
}

fn first_audio_format(demux: &mut Mp4Demuxer) -> Option<(u32, u32, Vec<u8>)> {
    loop {
        match demux.next_event().expect("event") {
            StreamEvent::Format(
                _,
                Format::Audio {
                    sample_rate,
                    channels,
                    codec_private,
                    ..
                },
            ) => return Some((sample_rate, channels, codec_private)),
            StreamEvent::Eos(..) => return None,
            _ => {}
        }
    }
}

/// Pad the file's one `esds` with `extra` zero bytes, growing every box
/// that holds it. The fixture keeps `moov` after the media, so no sample
/// offset moves.
fn grow_esds(mut bytes: Vec<u8>, extra: u32) -> Vec<u8> {
    let at = bytes
        .windows(4)
        .position(|w| w == b"esds")
        .expect("an esds")
        - 4;
    let mut pos = 0;
    let mut end = bytes.len();
    loop {
        let size = u32::from_be_bytes(bytes[pos..pos + 4].try_into().unwrap());
        let box_end = pos + size as usize;
        if !(pos..box_end).contains(&at) {
            pos = box_end;
            assert!(pos < end, "the esds is inside a box");
            continue;
        }
        bytes[pos..pos + 4].copy_from_slice(&(size + extra).to_be_bytes());
        if pos == at {
            bytes.splice(box_end..box_end, vec![0u8; extra as usize]);
            return bytes;
        }
        let skip = match &bytes[pos + 4..pos + 8] {
            b"stsd" => 8,
            b"mp4a" => 28,
            _ => 0,
        };
        end = box_end;
        pos += 8 + skip;
    }
}

#[test]
fn an_unreachable_esds_falls_back_to_the_parsed_fields() {
    let bytes = grow_esds(fixture("h264-aac-moov-trailing.mp4"), 8192);
    let mut demux = Mp4Demuxer::open(
        Box::new(MemSource(bytes)),
        DemuxLimits::default(),
        Generation(1),
    )
    .expect("opens");
    let notes = demux.take_notes();
    assert!(
        notes.iter().any(|n| n.contains("rebuilt from esds fields")),
        "the fallback is noted: {notes:?}"
    );
    assert_eq!(
        first_audio_format(&mut demux),
        Some((48000, 2, vec![0x11, 0x90]))
    );
}

#[test]
fn an_audio_object_type_that_is_not_aac_is_skipped() {
    let mut bytes = fixture("h264-aac-640x360-30fps.mp4");
    let config = [0x05, 0x80, 0x80, 0x80, 0x05, 0x11, 0x90];
    let at = bytes
        .windows(config.len())
        .position(|w| w == config)
        .expect("the fixture's config");
    // Object type 23 (ER AAC-LD) in place of 2, the rest unchanged.
    bytes[at + 5] = 0xB9;
    let mut demux = Mp4Demuxer::open(
        Box::new(MemSource(bytes)),
        DemuxLimits::default(),
        Generation(1),
    )
    .expect("opens on its video");
    assert_eq!(demux.audio_track(), None);
    let notes = demux.take_refusals();
    assert!(
        notes.iter().any(|n| n.contains("object type 23, not AAC")),
        "the refusal is noted: {notes:?}"
    );
}

fn audio_announce(demux: &mut Mp4Demuxer) -> Option<(AudioCodec, u32, u32, Vec<u8>)> {
    loop {
        match demux.next_event().expect("event") {
            StreamEvent::Format(
                _,
                Format::Audio {
                    codec,
                    sample_rate,
                    channels,
                    codec_private,
                },
            ) => return Some((codec, sample_rate, channels, codec_private)),
            StreamEvent::Eos(..) => return None,
            _ => {}
        }
    }
}

fn audio_frames(demux: &mut Mp4Demuxer) -> Vec<Vec<u8>> {
    let audio = demux.audio_track().expect("an audio track").0;
    access_units(demux)
        .into_iter()
        .filter(|au| au.0 == audio)
        .map(|au| au.4)
        .collect()
}

#[test]
fn mp3_is_read_from_either_sample_entry() {
    for name in [
        "h264-mp3-320x180.mp4",
        "h264-mp3-320x180.mov",
        "h264-mp3-320x180-frag.mov",
    ] {
        let mut demux = open(name);
        assert_eq!(
            audio_announce(&mut demux),
            Some((AudioCodec::Mp3, 44100, 2, Vec::new())),
            "{name}"
        );
        let frames = audio_frames(&mut demux);
        assert_eq!(frames.len(), 155, "{name}");
        assert!(
            frames.iter().all(|f| f.starts_with(&[0xFF, 0xFB])),
            "{name}: every sample is one MPEG-1 Layer III frame as stored"
        );
    }
}

#[test]
fn mp3_is_read_under_each_quicktime_code() {
    for code in [b"ms\0U", b"mp3 "] {
        let mut bytes = fixture("h264-mp3-320x180.mov");
        let found: Vec<usize> = bytes
            .windows(4)
            .enumerate()
            .filter(|(_, w)| *w == b".mp3")
            .map(|(at, _)| at)
            .collect();
        assert_eq!(found.len(), 1, "one sample entry code");
        bytes[found[0]..found[0] + 4].copy_from_slice(code);
        let mut demux = Mp4Demuxer::open(
            Box::new(MemSource(bytes)),
            DemuxLimits::default(),
            Generation(1),
        )
        .expect("opens");
        assert_eq!(
            audio_announce(&mut demux),
            Some((AudioCodec::Mp3, 44100, 2, Vec::new())),
            "{code:?}"
        );
    }
}

#[test]
fn mpeg2_audio_in_an_esds_is_mp3() {
    let mut bytes = fixture("h264-mp3-320x180.mp4");
    let at = bytes
        .windows(6)
        .position(|w| w == [0x04, 0x80, 0x80, 0x80, 0x0D, 0x6B])
        .expect("the fixture's object type");
    bytes[at + 5] = 0x69;
    let mut demux = Mp4Demuxer::open(
        Box::new(MemSource(bytes)),
        DemuxLimits::default(),
        Generation(1),
    )
    .expect("opens");
    assert_eq!(
        audio_announce(&mut demux).map(|a| a.0),
        Some(AudioCodec::Mp3)
    );
}

#[test]
fn an_mp3_track_that_is_not_layer_iii_is_refused() {
    let mut bytes = fixture("h264-mp3-320x180.mov");
    let first = audio_frames(&mut open("h264-mp3-320x180.mov")).remove(0);
    let at = bytes
        .windows(first.len())
        .position(|w| w == first)
        .expect("the first frame");
    // Layer II in place of Layer III.
    bytes[at + 1] = 0xFD;
    let mut demux = Mp4Demuxer::open(
        Box::new(MemSource(bytes)),
        DemuxLimits::default(),
        Generation(1),
    )
    .expect("opens on its video");
    assert_eq!(demux.audio_track(), None);
    assert!(demux.video_track().is_some());
    let refusals = demux.take_refusals();
    assert!(
        refusals
            .iter()
            .any(|n| n.contains("first frame is not MPEG audio Layer III")),
        "the refusal is noted: {refusals:?}"
    );
}

#[test]
fn an_mp3_first_sample_shorter_than_a_frame_header_is_refused() {
    let mut bytes = fixture("h264-mp3-320x180.mov");
    let first = audio_frames(&mut open("h264-mp3-320x180.mov")).remove(0);
    // The audio `stsz`: version and flags, a zero default size, the count,
    // then the first sample's size. Cut to three bytes, the sample's first
    // four still read as a valid header.
    let size = u32::try_from(first.len())
        .expect("a small frame")
        .to_be_bytes();
    let at = bytes
        .windows(4)
        .enumerate()
        .filter(|(_, w)| *w == b"stsz")
        .map(|(at, _)| at + 16)
        .find(|&at| bytes[at..at + 4] == size)
        .expect("the audio stsz");
    bytes[at..at + 4].copy_from_slice(&3u32.to_be_bytes());
    let mut demux = Mp4Demuxer::open(
        Box::new(MemSource(bytes)),
        DemuxLimits::default(),
        Generation(1),
    )
    .expect("opens on its video");
    assert_eq!(demux.audio_track(), None);
    let refusals = demux.take_refusals();
    assert!(
        refusals
            .iter()
            .any(|n| n.contains("too short for a frame header (3 of 4 bytes)")),
        "the refusal is noted: {refusals:?}"
    );
}

#[test]
fn all_layouts_demux_identically() {
    let baseline = drain(&mut open("h264-aac-640x360-30fps.mp4"));
    for layout in ["h264-aac-moov-trailing.mp4", "h264-aac-frag.mp4"] {
        let s = drain(&mut open(layout));
        assert_eq!(s.video_aus, baseline.video_aus, "{layout}");
        assert_eq!(s.audio_aus, baseline.audio_aus, "{layout}");
        assert_eq!(s.first_video_au, baseline.first_video_au, "{layout}");
    }
}

#[test]
fn seek_lands_on_a_keyframe() {
    // Every moov layout seeks the same way (the seek matrix rows).
    for layout in [
        "h264-aac-640x360-30fps.mp4",
        "h264-aac-moov-trailing.mp4",
        "h264-aac-frag.mp4",
    ] {
        seek_lands_on_a_keyframe_in(layout);
    }
}

fn seek_lands_on_a_keyframe_in(layout: &str) {
    let mut demux = open(layout);
    let landed = demux
        .seek(MediaTime::from_secs(3), Generation(2))
        .expect("seek");
    assert!(landed <= MediaTime::from_secs(3));
    assert!(landed >= MediaTime::ZERO);

    // First video AU after the seek is a keyframe with the new generation.
    loop {
        match demux.next_event().expect("event") {
            StreamEvent::Au(au) if au.data.starts_with(&[0, 0, 0, 1]) => {
                assert!(au.key, "seek must land keyframe-clean");
                assert_eq!(au.generation, Generation(2));
                assert_eq!(au.pts, landed);
                return;
            }
            StreamEvent::Au(_) => {}
            StreamEvent::Eos(_) => panic!("hit EOS before a video AU"),
            _ => {}
        }
    }
}

/// The fixture's picture starts 521 ms after its sound, which the video
/// track's edit list states as an empty edit ahead of the ordinary one
/// that skips the reorder delay. The picture's times carry the gap, as
/// ffprobe's do, and a seek lands on them.
#[test]
fn an_empty_edit_starts_the_video_late() {
    let mut demux = open("h264-aac-late-video.mp4");
    let aus = access_units(&mut demux);
    let first = |video: bool| {
        aus.iter()
            .filter(|au| au.4.starts_with(&[0, 0, 0, 1]) == video)
            .map(|au| au.1.as_micros())
            .min()
            .expect("the track has access units")
    };
    assert_eq!(first(true), 521_000, "video starts after the gap");
    assert_eq!(first(false), 0, "audio starts at the origin");

    // Keyframes every half second from 521 ms; the one at 1.521 s is
    // decoded at 1.438 s, after the target.
    let mut demux = open("h264-aac-late-video.mp4");
    let landed = demux
        .seek(MediaTime::from_micros(1_400_000), Generation(2))
        .expect("seek");
    assert_eq!(landed.as_micros(), 1_021_000);
    assert_eq!(
        demux.audio_start(),
        MediaTime::ZERO,
        "the sound is not late"
    );
}

/// The fixture's sound starts 500 ms after its picture and is primed by
/// 1,024 samples: an empty edit of 500 ms, then an edit from media time
/// 1024. The priming lands at 478.7 ms, after zero, so the demuxer names
/// where the sound itself begins for the audio stage to drop it against.
/// With no edit list a video track presents at the times the file
/// states: two B-frames of reorder delay put the first picture 83 ms in.
#[test]
fn a_video_track_without_an_edit_list_keeps_its_reorder_delay() {
    let mut demux = open("h264-aac-no-edit-list.mp4");
    let video = demux.video_track().expect("a video track").0;
    let first = access_units(&mut demux)
        .into_iter()
        .filter(|(track, ..)| *track == video)
        .map(|(_, pts, ..)| pts)
        .min()
        .expect("video access units");
    assert_eq!(first, MediaTime::from_micros(83_333));
}

#[test]
fn an_empty_edit_keeps_the_audio_priming_ahead_of_the_start() {
    let mut demux = open("h264-aac-late-audio.mp4");
    assert_eq!(demux.audio_start().as_micros(), 500_000);
    let first_audio = access_units(&mut demux)
        .iter()
        .filter(|au| !au.4.starts_with(&[0, 0, 0, 1]))
        .map(|au| au.1.as_micros())
        .min()
        .expect("the track has access units");
    // 500 ms less 1,024 samples at 48 kHz.
    assert_eq!(first_audio, 478_666);
}

#[test]
fn truncated_metadata_is_a_typed_error() {
    let mut bytes = fixture("h264-aac-640x360-30fps.mp4");
    bytes.truncate(4000); // Mid-moov.
    let result = Mp4Demuxer::open(
        Box::new(MemSource(bytes)),
        DemuxLimits::default(),
        Generation(1),
    );
    assert!(result.is_err());
}

/// A `trun` of version 1 states signed composition offsets: a B-frame
/// presented before the frame decoded ahead of it carries a negative one.
/// With no index the file is read by walking its fragments, as a live
/// stream's segments are. Read unsigned, each B-frame would land 2^32
/// ticks later than it belongs and never come due.
#[test]
fn negative_composition_offsets_keep_b_frames_in_place() {
    let mut demux = open("h264-aac-negcts-frag.mp4");
    let duration = demux.duration().expect("the walk measures the file");
    assert!(
        duration <= MediaTime::from_micros(6_100_000),
        "duration {duration:?}"
    );

    let mut pts: Vec<i64> = access_units(&mut demux)
        .into_iter()
        .filter(|(track, ..)| Some(*track) == demux.video_track().map(|t| t.0))
        .map(|(_, pts, ..)| pts.as_micros())
        .collect();
    assert_eq!(pts.len(), 144, "every frame of 6 s at 24 fps");
    pts.sort_unstable();
    let first = pts[0];
    for (i, at) in pts.iter().enumerate() {
        let expected = first + (i as i64 * 1_000_000) / 24;
        assert!(
            (at - expected).abs() <= 1,
            "frame {i} presents at {at} us, expected {expected}"
        );
    }
    assert!(pts[143] < 6_100_000, "last frame at {} us", pts[143]);
}

/// A capture cut off inside the header of the box after its last whole
/// fragment, here a 64-bit one with only twelve of its sixteen bytes,
/// plays what it holds.
#[test]
fn a_walk_cut_inside_a_box_header_keeps_the_fragments_before_it() {
    let mut bytes = fixture("h264-aac-negcts-frag.mp4");
    bytes.extend_from_slice(&1u32.to_be_bytes());
    bytes.extend_from_slice(b"mdat");
    bytes.extend_from_slice(&[0; 4]);
    let mut demux = Mp4Demuxer::open(
        Box::new(MemSource(bytes)),
        DemuxLimits::default(),
        Generation(1),
    )
    .expect("the whole fragments still open");
    let notes = demux.take_notes();
    assert!(
        notes.iter().any(|n| n.contains(
            "fragment walk stopped at byte 145989 of 146001: a box header there is cut short"
        )),
        "the early end is noted: {notes:?}"
    );
    assert_eq!(drain(&mut demux).video_aus, 144);
}

/// A box stating a size shorter than its own header, part-way down the
/// file, ends the walk there; the fragments before it play and a note
/// says where the rest was lost.
#[test]
fn a_walk_stopped_by_a_corrupt_box_size_says_so() {
    let bytes = fixture("h264-aac-negcts-frag.mp4");
    let size_at = |at: usize| u32::from_be_bytes(bytes[at..at + 4].try_into().unwrap()) as usize;
    let moof = size_at(0) + size_at(size_at(0));
    let after = moof + size_at(moof);
    let after = after + size_at(after);
    let mut corrupt = bytes[..after].to_vec();
    corrupt.extend_from_slice(&4u32.to_be_bytes());
    corrupt.extend_from_slice(b"free");
    corrupt.extend_from_slice(&bytes[after..]);
    let mut demux = Mp4Demuxer::open(
        Box::new(MemSource(corrupt)),
        DemuxLimits::default(),
        Generation(1),
    )
    .expect("the first fragment still opens");
    let notes = demux.take_notes();
    assert!(
        notes.iter().any(|n| n.contains(&format!(
            "fragment walk stopped at byte {after} of {}: a box there states a size shorter than its header",
            bytes.len() + 8
        ))),
        "the early end is noted: {notes:?}"
    );
    assert_eq!(drain(&mut demux).video_aus, 24, "one fragment of 24 frames");
}

/// A `moov` that holds samples of its own and declares fragments is read
/// as both: its samples, then every fragment after it.
#[test]
fn samples_in_the_moov_are_followed_by_its_fragments() {
    let mut demux = open("h264-aac-moov-and-frag.mp4");
    let duration = demux.duration().expect("a duration");
    assert!(
        duration >= MediaTime::from_micros(6_000_000),
        "duration {duration:?}"
    );
    let video = demux.video_track().expect("a video track").0;
    let mut pts: Vec<i64> = access_units(&mut demux)
        .into_iter()
        .filter(|(track, ..)| *track == video)
        .map(|(_, pts, ..)| pts.as_micros())
        .collect();
    assert_eq!(pts.len(), 144, "every frame of 6 s at 24 fps");
    // One cadence across the join, where `moov`'s 24 samples end and the
    // fragments' begin.
    pts.sort_unstable();
    for (i, at) in pts.iter().enumerate() {
        let expected = pts[0] + (i as i64 * 1_000_000) / 24;
        assert!(
            (at - expected).abs() <= 1,
            "frame {i} presents at {at} us, expected {expected}"
        );
    }
}

/// A box between `moov` and the first fragment does not hide the
/// fragments behind it: they are walked from the end of `moov`.
#[test]
fn fragments_behind_a_box_after_the_moov_are_walked() {
    let bytes = fixture("h264-aac-negcts-frag.mp4");
    let size_at = |at: usize| u32::from_be_bytes(bytes[at..at + 4].try_into().unwrap()) as usize;
    let moov_end = size_at(0) + size_at(size_at(0));
    let mut spaced = bytes[..moov_end].to_vec();
    spaced.extend_from_slice(&8u32.to_be_bytes());
    spaced.extend_from_slice(b"mdat");
    spaced.extend_from_slice(&bytes[moov_end..]);
    let mut demux = Mp4Demuxer::open(
        Box::new(MemSource(spaced)),
        DemuxLimits::default(),
        Generation(1),
    )
    .expect("opens");
    let video = demux.video_track().expect("a video track").0;
    let pts: Vec<i64> = access_units(&mut demux)
        .into_iter()
        .filter(|(track, ..)| *track == video)
        .map(|(_, pts, ..)| pts.as_micros())
        .collect();
    assert_eq!(pts.len(), 144);
    assert!(
        pts.iter().all(|at| (0..6_100_000).contains(at)),
        "every frame within the file's six seconds"
    );
}

/// A fragment placed ahead of `moov` names tracks and defaults that
/// nothing has described yet, and is refused rather than read.
#[test]
fn a_fragment_before_the_moov_is_refused() {
    let bytes = fixture("h264-aac-negcts-frag.mp4");
    let size_at = |at: usize| u32::from_be_bytes(bytes[at..at + 4].try_into().unwrap()) as usize;
    let moov = size_at(0);
    let moof = moov + size_at(moov);
    let mdat = moof + size_at(moof);
    let after = mdat + size_at(mdat);
    assert_eq!(&bytes[moov + 4..moov + 8], b"moov");
    assert_eq!(&bytes[moof + 4..moof + 8], b"moof");
    assert_eq!(&bytes[mdat + 4..mdat + 8], b"mdat");
    // ftyp, the first fragment, then moov and the rest.
    let moved = [
        &bytes[..moov],
        &bytes[moof..after],
        &bytes[moov..moof],
        &bytes[after..],
    ]
    .concat();
    match Mp4Demuxer::open(
        Box::new(MemSource(moved)),
        DemuxLimits::default(),
        Generation(1),
    ) {
        Err(media_demux::DemuxError::Unsupported(why)) => {
            assert_eq!(
                why,
                "a movie fragment comes before the moov that describes it"
            );
        }
        Err(e) => panic!("refused for the wrong reason: {e}"),
        Ok(_) => panic!("a fragment ahead of moov must not be read"),
    }
}

/// A file whose fragments are as far apart as a real long video's: the
/// walk has to pay for the headers it parses, not for a cache block per
/// fragment, or the budget runs out part-way down the file.
#[test]
fn many_fragment_files_open_when_their_fragments_are_spread_out() {
    for name in ["h264-aac-manyfrag.mp4", "h264-aac-manyfrag-sidx.mp4"] {
        let baseline = drain(&mut open(name));
        let inflated = common::inflate(&fixture(name));
        let counters = inflated.counters();
        let len = inflated.len();
        assert!(
            len > DemuxLimits::default().max_metadata_bytes,
            "{name} must outgrow the budget a block per fragment would charge ({len} bytes)"
        );

        let mut demux = Mp4Demuxer::open(Box::new(inflated), DemuxLimits::default(), Generation(1))
            .unwrap_or_else(|e| panic!("{name} spread out must open: {e:?}"));
        let fetched = counters.bytes();
        assert!(
            fetched < 16 * 1024 * 1024,
            "{name} open fetched {fetched} bytes"
        );

        // The padding is past every sample, so the streams are the ones
        // the plain fixture holds.
        let s = drain(&mut demux);
        assert_eq!(s.video_aus, baseline.video_aus, "{name}");
        assert_eq!(s.audio_aus, baseline.audio_aus, "{name}");
        assert_eq!(s.video_keys, baseline.video_keys, "{name}");
        assert_eq!(s.first_video_au, baseline.first_video_au, "{name}");
        assert_eq!(s.first_audio_pts, baseline.first_audio_pts, "{name}");
    }
}

/// Every access unit a demuxer yields from here, in order.
fn access_units(demux: &mut Mp4Demuxer) -> Vec<(u32, MediaTime, MediaTime, bool, Vec<u8>)> {
    let mut out = Vec::new();
    loop {
        match demux.next_event().expect("no demux error") {
            StreamEvent::Au(au) => out.push((au.track.0, au.pts, au.dts, au.key, au.data)),
            StreamEvent::Eos(_) => return out,
            _ => {}
        }
    }
}

/// The next `n` access units, or as many as the file has left.
fn first_access_units(
    demux: &mut Mp4Demuxer,
    n: usize,
) -> Vec<(u32, MediaTime, MediaTime, bool, Vec<u8>)> {
    let mut out = Vec::new();
    while out.len() < n {
        match demux.next_event().expect("no demux error") {
            StreamEvent::Au(au) => out.push((au.track.0, au.pts, au.dts, au.key, au.data)),
            StreamEvent::Eos(_) => break,
            _ => {}
        }
    }
    out
}

/// The same file read the way one with no usable index is: its index is
/// left describing the layout before the fragments were spread out, so
/// it covers none of the file and the whole of it is walked instead.
fn open_walked(name: &str) -> Mp4Demuxer {
    Mp4Demuxer::open(
        Box::new(common::inflate_untiled(&fixture(name))),
        DemuxLimits::default(),
        Generation(1),
    )
    .unwrap_or_else(|e| panic!("{name} must open by the walk: {e:?}"))
}

fn open_spread(name: &str) -> (Mp4Demuxer, common::Counters) {
    let source = common::inflate(&fixture(name));
    let counters = source.counters();
    let demux = Mp4Demuxer::open(Box::new(source), DemuxLimits::default(), Generation(1))
        .unwrap_or_else(|e| panic!("{name} spread out must open: {e:?}"));
    (demux, counters)
}

/// A file with an index it can be believed on is opened from the index:
/// `ftyp`, `moov` and the index, then the first fragment. Nothing else is
/// read before playback, and what comes out is what a walk of every
/// fragment yields, access unit for access unit.
#[test]
fn an_indexed_file_opens_from_its_index() {
    let (mut indexed, counters) = open_spread("h264-aac-manyfrag-sidx.mp4");
    let fetched = counters.bytes();
    assert!(
        fetched < 1024 * 1024,
        "opening from the index fetched {fetched} bytes"
    );

    let (mut walked, walk_counters) = open_spread("h264-aac-manyfrag.mp4");
    assert!(
        walk_counters.bytes() > fetched * 2,
        "the fixture without an index must cost more to open, or this row \
         proves nothing: {} against {fetched}",
        walk_counters.bytes()
    );
    assert_eq!(
        indexed.duration().map(|d| d.as_millis() / 100),
        walked.duration().map(|d| d.as_millis() / 100),
    );
    assert_eq!(access_units(&mut indexed), access_units(&mut walked));
}

/// The demuxer holds a fragment's worth of sample references, not the
/// file's worth, however far into the file playback has reached.
/// The committed fixture is covered as it stands as well as spread out:
/// its index stops at the `mfra` it ends with, so believing the index at
/// all means reading that box's length off the end of the file.
#[test]
fn reading_a_fragment_at_a_time_does_not_accumulate() {
    for mut demux in [
        open_spread("h264-aac-manyfrag-sidx.mp4").0,
        open("h264-aac-manyfrag-sidx.mp4"),
    ] {
        // To the end of the file, or the claim is only about the part
        // of it that was read: what accumulates does so as the file goes
        // on. The count bounds a hung test, nothing more.
        let mut most = 0usize;
        let mut ended = false;
        for _ in 0..100_000 {
            if matches!(demux.next_event().expect("event"), StreamEvent::Eos(_)) {
                ended = true;
                break;
            }
            most = most.max(demux.held_samples());
        }
        assert!(ended, "the file did not reach its end");
        assert!(most > 0, "the file is read a fragment at a time");
        assert!(most < 200, "held {most} sample references");
    }
}

/// Seeking by the index has to land where a seek over the whole table
/// lands: the same sync sample, and the same stream after it. Swept
/// across the file rather than sampled at a few points, because what a
/// seek lands on turns on where the target falls inside its subsegment.
#[test]
fn an_index_seek_lands_where_the_whole_table_does() {
    // Fragments opening on a keyframe, and fragments whose keyframes are
    // inside them, where the landing is part-way through a fragment and
    // the samples ahead of it belong before it.
    for (name, until) in [
        ("h264-aac-manyfrag-sidx.mp4", 40_500i64),
        ("h264-aac-longfrag-sidx.mp4", 20_500),
    ] {
        let mut targets = 0;
        for ms in (0..until).step_by(137) {
            let target = MediaTime::from_millis(ms);
            let mut indexed = open_spread(name).0;
            let mut walked = open_walked(name);

            let by_index = indexed.seek(target, Generation(2)).expect("index seek");
            let by_table = walked.seek(target, Generation(2)).expect("table seek");
            assert_eq!(by_index, by_table, "{name} landing for {target}");
            assert_eq!(
                first_access_units(&mut indexed, 24),
                first_access_units(&mut walked, 24),
                "{name} stream after {target}"
            );
            targets += 1;
        }
        assert!(targets > 100, "{name}: only {targets} targets swept");

        // And to the end from the start, the middle, and past the last
        // picture, where the audio track runs on alone.
        for ms in [0, until / 2, until * 2] {
            let target = MediaTime::from_millis(ms);
            let mut indexed = open_spread(name).0;
            let mut walked = open_walked(name);
            assert_eq!(
                indexed.seek(target, Generation(3)).expect("index seek"),
                walked.seek(target, Generation(3)).expect("table seek"),
                "{name} landing for {target}"
            );
            assert_eq!(
                access_units(&mut indexed),
                access_units(&mut walked),
                "{name} to the end from {ms} ms"
            );
        }
    }
}

/// An index that does not account for the whole file says nothing
/// trustworthy about where its fragments are, so the file is read the
/// way one with no index at all is.
#[test]
fn an_index_that_covers_nothing_is_not_used() {
    let source = common::inflate_untiled(&fixture("h264-aac-manyfrag-sidx.mp4"));
    let counters = source.counters();
    let mut demux = Mp4Demuxer::open(Box::new(source), DemuxLimits::default(), Generation(1))
        .expect("the file still opens");
    let (mut walked, _) = open_spread("h264-aac-manyfrag.mp4");
    assert!(
        counters.bytes() > 4 * 1024 * 1024,
        "the whole file was walked, not opened from the index"
    );
    assert_eq!(access_units(&mut demux), access_units(&mut walked));
}

/// Opening a file fetches the metadata it parses and nothing past it.
/// A progressive file's `moov` holds every sample table, so on a long
/// one it is megabytes and the box behind it sits that far into the
/// file: reading its header to find out whether this file has fragments
/// costs a whole cache block of media, and answers nothing that `moov`
/// has not already said.
#[test]
fn opening_a_progressive_file_reads_no_further_than_its_moov() {
    let fixture = fixture("h264-aac-640x360-30fps.mp4");
    let baseline = drain(&mut open("h264-aac-640x360-30fps.mp4"));

    // A `moov` four cache blocks long, so anything read past it shows.
    let source = common::pad_moov(&fixture, 1024 * 1024);
    let counters = source.counters();
    let mut demux = Mp4Demuxer::open(Box::new(source), DemuxLimits::default(), Generation(1))
        .expect("a padded moov still opens");
    let fetched = counters.bytes();

    assert_eq!(
        demux.duration().map(|d| d.as_millis() / 100),
        Some(60),
        "the padded file is still the fixture"
    );
    assert_eq!(demux.take_notes(), Vec::<String>::new());
    assert!(
        fetched <= 512 * 1024,
        "open fetched {fetched} bytes; the moov and what the parse needs of it is one block,          so this is a block of media read to look at a box header"
    );
    // The tracks are the fixture's: padding `moov` does not disturb what
    // it states, only where the media behind it sits.
    assert_eq!(baseline.video_aus, 180);
}

#[test]
fn metadata_budget_trips_as_an_error() {
    let result = Mp4Demuxer::open(
        Box::new(MemSource(fixture("h264-aac-640x360-30fps.mp4"))),
        DemuxLimits {
            max_metadata_bytes: 1024,
            ..DemuxLimits::default()
        },
        Generation(1),
    );
    assert!(result.is_err());
}

#[test]
fn video_only_fixture_still_demuxes() {
    let mut demux = open("h264-640x360-30fps.mp4");
    let s = drain(&mut demux);
    assert_eq!((s.video_formats, s.audio_formats), (1, 0));
    assert!(s.video_aus > 0);
    assert_eq!(s.audio_aus, 0);
}

/// Open a fixture spread out with its `mfra` as the only index, and hold
/// the open to that index. The walk it falls back to yields the same
/// stream, and a row comparing streams alone would pass on the walk too.
/// The open must note no refusal of the `mfra` and fetch less than a walk.
fn open_by_mfra(name: &str) -> (Mp4Demuxer, common::Counters) {
    let source = common::inflate_mfra(&fixture(name));
    let counters = source.counters();
    let mut demux = Mp4Demuxer::open(Box::new(source), DemuxLimits::default(), Generation(1))
        .unwrap_or_else(|e| panic!("{name} with only an mfra must open: {e:?}"));
    let notes = demux.take_notes();
    assert!(
        !notes.iter().any(|n| n.starts_with("mfra not used")),
        "{name} must open from its mfra: {notes:?}"
    );
    let fetched = counters.bytes();
    assert!(
        fetched < 1536 * 1024,
        "{name} opened from its mfra fetched {fetched} bytes"
    );
    (demux, counters)
}

/// A fragmented file with no segment index usually still ends in an
/// `mfra`, which lists where the fragments are, so the file opens from it:
/// `ftyp` and `moov`, the tail of the file that holds the `mfra`, the last
/// fragment, which says where the media ends, and the first. A handful of
/// cache blocks, where the walk reads one per fragment. What comes out is
/// what the walk yields.
#[test]
fn a_file_with_only_an_mfra_opens_from_it() {
    let (mut indexed, counters) = open_by_mfra("h264-aac-manyfrag.mp4");
    let fetched = counters.bytes();

    let (mut walked, walk_counters) = open_spread("h264-aac-manyfrag.mp4");
    assert!(
        walk_counters.bytes() > fetched * 4,
        "the walk must cost more, or this row proves nothing: {} against {fetched}",
        walk_counters.bytes()
    );
    assert_eq!(
        indexed.duration().map(|d| d.as_millis() / 100),
        walked.duration().map(|d| d.as_millis() / 100),
    );
    assert_eq!(access_units(&mut indexed), access_units(&mut walked));
}

/// A seek on a file opened from its `mfra` lands where a seek over the
/// whole table does, swept across the file, on fragments that open on a
/// keyframe and on fragments with keyframes inside them.
#[test]
fn an_mfra_seek_lands_where_the_whole_table_does() {
    for (name, until) in [
        ("h264-aac-manyfrag-sidx.mp4", 40_500i64),
        ("h264-aac-longfrag-sidx.mp4", 20_500),
    ] {
        for ms in (0..until).step_by(311) {
            let target = MediaTime::from_millis(ms);
            let mut indexed = open_by_mfra(name).0;
            let mut walked = open_walked(name);
            assert_eq!(
                indexed.seek(target, Generation(2)).expect("mfra seek"),
                walked.seek(target, Generation(2)).expect("table seek"),
                "{name} landing for {target}"
            );
            assert_eq!(
                first_access_units(&mut indexed, 24),
                first_access_units(&mut walked, 24),
                "{name} stream after {target}"
            );
        }
        for ms in [0, until / 2, until * 2] {
            let target = MediaTime::from_millis(ms);
            let mut indexed = open_by_mfra(name).0;
            let mut walked = open_walked(name);
            assert_eq!(
                indexed.seek(target, Generation(3)).expect("mfra seek"),
                walked.seek(target, Generation(3)).expect("table seek"),
                "{name} landing for {target}"
            );
            assert_eq!(
                access_units(&mut indexed),
                access_units(&mut walked),
                "{name} to the end from {ms} ms"
            );
        }
    }
}

/// An `mfra` whose offsets do not land on this file's fragments (written
/// for another layout, or hostile) is not believed, and the file is walked.
#[test]
fn an_mfra_that_misses_the_fragments_is_not_used() {
    let source = common::inflate_stale_mfra(&fixture("h264-aac-manyfrag.mp4"));
    let mut demux = Mp4Demuxer::open(Box::new(source), DemuxLimits::default(), Generation(1))
        .expect("the file still opens");
    let notes = demux.take_notes();
    assert!(
        notes.iter().any(|n| n.starts_with("mfra not used")),
        "the stale mfra must be read and refused: {notes:?}"
    );
    let (mut walked, _) = open_spread("h264-aac-manyfrag.mp4");
    assert_eq!(access_units(&mut demux), access_units(&mut walked));
}

/// A fragment larger than a cache block (here a megabyte of video, then
/// its audio) is served in decode order, which alternates between its two
/// runs; fetched a block at a time, that is a request and a round trip per
/// block over HTTP. A fragment this size is read whole instead: its
/// header, then its data in one read. The first fragment's header came in
/// with the open's read past `moov`; draining then costs its data, and the
/// second fragment's header and data. Fragments too large to read whole
/// are covered by the rows in `mp4_runs`.
#[test]
fn a_fragment_is_read_in_one_pass() {
    const READS: u64 = 1 + (1 + 1);
    let (mut demux, counters) = open_spread("h264-aac-bigfrag-sidx.mp4");
    let before = counters.jumps();
    let streamed = access_units(&mut demux);
    let jumps = counters.jumps() - before;
    assert_eq!(
        streamed,
        access_units(&mut open_walked("h264-aac-bigfrag-sidx.mp4"))
    );
    assert_eq!(jumps, READS, "draining the file cost {jumps} jumps");
}

/// The fixture with its video sample entry renamed to one no decoder here
/// takes. The brand list in `ftyp` carries the same four bytes first.
fn with_unknown_video_entry(name: &str) -> Vec<u8> {
    let mut bytes = fixture(name);
    let at = bytes
        .windows(4)
        .rposition(|w| w == b"avc1")
        .expect("the fixture's sample entry");
    bytes[at..at + 4].copy_from_slice(b"xvid");
    bytes
}

#[test]
fn an_unknown_video_sample_entry_is_refused_and_the_audio_plays() {
    let mut demux = Mp4Demuxer::open(
        Box::new(MemSource(with_unknown_video_entry(
            "h264-aac-640x360-30fps.mp4",
        ))),
        DemuxLimits::default(),
        Generation(1),
    )
    .expect("opens on its audio");
    assert_eq!(demux.video_track(), None);
    assert!(demux.audio_track().is_some());
    assert_eq!(
        demux.take_refusals(),
        ["video codec 'xvid' is not supported (supported: H.264, H.265, VP9, AV1)"]
    );
}

#[test]
fn a_file_with_only_refused_tracks_fails_with_the_reason() {
    let refused = Mp4Demuxer::open(
        Box::new(MemSource(with_unknown_video_entry(
            "h264-640x360-30fps.mp4",
        ))),
        DemuxLimits::default(),
        Generation(1),
    );
    match refused {
        Err(media_demux::DemuxError::Refused(why)) => assert_eq!(
            why,
            "video codec 'xvid' is not supported (supported: H.264, H.265, VP9, AV1)"
        ),
        Err(e) => panic!("refused for the wrong reason: {e}"),
        Ok(_) => panic!("a file with nothing playable must not open"),
    }
}

/// Every access unit, by track, as `(pts, dts, key, data)`.
type ByTrack = std::collections::BTreeMap<u32, Vec<(MediaTime, MediaTime, bool, Vec<u8>)>>;

fn by_track(demux: &mut dyn Demuxer) -> ByTrack {
    let mut out = ByTrack::new();
    loop {
        match demux.next_event().expect("no demux error") {
            StreamEvent::Au(au) => {
                out.entry(au.track.0)
                    .or_default()
                    .push((au.pts, au.dts, au.key, au.data));
            }
            StreamEvent::Eos(reason) => {
                assert_eq!(reason, EosReason::Natural);
                return out;
            }
            _ => {}
        }
    }
}

fn open_live(bytes: Vec<u8>) -> Result<Box<dyn Demuxer>, media_demux::DemuxError> {
    media_demux::open_auto(
        Box::new(common::LiveSource::new(bytes)),
        DemuxLimits::default(),
        Generation(1),
    )
}

/// A fragmented file served as a live stream, with no length and every
/// byte read once, plays what the file does, sample for sample. The
/// fixtures between them hold fragments carrying both tracks, one track
/// each, a segment index ahead of the first or of every fragment, signed
/// composition offsets, and keyframes inside fragments.
#[test]
fn a_live_stream_plays_every_sample_the_file_does() {
    for name in [
        "h264-aac-frag.mp4",
        "h264-aac-negcts-frag.mp4",
        "h264-aac-manyfrag.mp4",
        "h264-aac-manyfrag-sidx.mp4",
        "h264-aac-longfrag-sidx.mp4",
        "h264-aac-bigfrag-sidx.mp4",
        "h264-aac-livefrag.mp4",
    ] {
        let file = by_track(&mut open(name));
        let mut live = open_live(fixture(name)).unwrap_or_else(|e| panic!("{name}: {e}"));
        let streamed = by_track(live.as_mut());
        assert_eq!(streamed.len(), 2, "{name}: both tracks");
        assert_eq!(streamed, file, "{name}");
    }
}

#[test]
fn a_live_stream_has_no_length_and_does_not_seek() {
    let mut live = open_live(fixture("h264-aac-livefrag.mp4")).expect("opens");
    assert_eq!(live.duration(), None);
    match live.seek(MediaTime::from_secs(2), Generation(2)) {
        Err(media_demux::DemuxError::Unsupported(why)) => {
            assert_eq!(why, "seek on a live MP4 stream");
        }
        other => panic!("a live stream must refuse a seek: {other:?}"),
    }
}

/// Only a fragmented file says where its samples are as they arrive.
#[test]
fn a_live_stream_that_is_not_fragmented_is_refused() {
    for name in ["h264-aac-640x360-30fps.mp4", "h264-aac-moov-trailing.mp4"] {
        match open_live(fixture(name)) {
            Err(media_demux::DemuxError::Unsupported(why)) => assert_eq!(
                why, "progressive MP4 needs a source with a known length",
                "{name}"
            ),
            Err(e) => panic!("{name}: refused for the wrong reason: {e}"),
            Ok(_) => panic!("{name}: a progressive file must not open as a live stream"),
        }
    }
}

/// A stream cut part-way through a fragment's media data ends after the
/// fragments before it.
#[test]
fn a_live_stream_cut_inside_a_fragment_ends_after_the_ones_before_it() {
    let bytes = fixture("h264-aac-livefrag.mp4");
    let whole = by_track(open_live(bytes.clone()).expect("opens").as_mut());
    let mdats = top_level(&bytes)
        .into_iter()
        .filter(|(kind, _, _)| kind == b"mdat")
        .collect::<Vec<_>>();
    let (_, at, size) = mdats[mdats.len() / 2];
    let cut = by_track(
        open_live(bytes[..at + size / 2].to_vec())
            .expect("opens")
            .as_mut(),
    );
    let played: usize = cut.values().map(Vec::len).sum();
    assert!(played > 0, "the fragments before the cut play");
    for (track, units) in &cut {
        assert!(whole[track].starts_with(units), "track {track}");
    }
    assert!(
        played < whole.values().map(Vec::len).sum(),
        "the cut costs samples"
    );
}

/// A stream whose `moov` declares a track it never sends plays the other
/// no more than half a second behind what has arrived.
#[test]
fn a_track_that_never_arrives_holds_the_other_back_half_a_second() {
    let bytes = fixture("h264-aac-livefrag.mp4");
    let boxes = top_level(&bytes);
    // The fixture's audio is track 2, in fragments of its own.
    let mut video_only = Vec::new();
    let mut skip_next = false;
    for &(kind, at, size) in &boxes {
        let boxed = &bytes[at..at + size];
        if std::mem::take(&mut skip_next) {
            assert_eq!(&kind, b"mdat");
            continue;
        }
        if &kind == b"moof" && fragment_track(boxed) == 2 {
            skip_next = true;
            continue;
        }
        video_only.extend_from_slice(boxed);
    }
    let video_ends: Vec<usize> = top_level(&video_only)
        .into_iter()
        .filter(|(kind, _, _)| kind == b"mdat")
        .map(|(_, at, size)| at + size)
        .collect();

    let source = common::LiveSource::new(video_only.clone());
    let served = source.served();
    let mut live = media_demux::open_auto(Box::new(source), DemuxLimits::default(), Generation(1))
        .expect("opens");
    let mut n = 0usize;
    loop {
        match live.next_event().expect("no demux error") {
            StreamEvent::Au(au) => {
                // One picture per fragment at 24 fps: half a second is
                // twelve of them past the one going out. Near the end,
                // finding the end reads the rest of the stream.
                let bound = video_ends.get(n + 13).copied().unwrap_or(video_only.len());
                let read = served.load(std::sync::atomic::Ordering::Relaxed) as usize;
                assert!(
                    read <= bound,
                    "picture {n} (dts {:?}) went out with {read} bytes read, past {bound}",
                    au.dts
                );
                n += 1;
            }
            StreamEvent::Eos(_) => break,
            _ => {}
        }
    }
    assert_eq!(n, video_ends.len(), "every picture plays");
}

/// Top-level boxes as `(kind, offset, size)`.
fn top_level(bytes: &[u8]) -> Vec<([u8; 4], usize, usize)> {
    let mut out = Vec::new();
    let mut at = 0usize;
    while at + 8 <= bytes.len() {
        let size = u32::from_be_bytes(bytes[at..at + 4].try_into().unwrap()) as usize;
        assert!(size >= 8 && at + size <= bytes.len(), "box at {at}");
        out.push((bytes[at + 4..at + 8].try_into().unwrap(), at, size));
        at += size;
    }
    out
}

/// The track a single-track `moof` carries, from its `tfhd`.
fn fragment_track(moof: &[u8]) -> u32 {
    let traf = top_level(&moof[8..])
        .into_iter()
        .find(|(kind, _, _)| kind == b"traf")
        .map(|(_, at, _)| 8 + at)
        .expect("a traf");
    let tfhd = top_level(&moof[traf + 8..])
        .into_iter()
        .find(|(kind, _, _)| kind == b"tfhd")
        .map(|(_, at, _)| traf + 8 + at)
        .expect("a tfhd");
    u32::from_be_bytes(moof[tfhd + 12..tfhd + 16].try_into().unwrap())
}
