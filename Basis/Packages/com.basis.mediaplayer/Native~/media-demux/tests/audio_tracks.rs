//! Audio track enumeration and open-time selection: the container states
//! the languages, the demuxer offers them in container order, and the
//! session binds the one it was asked for.

use media_clock::Generation;
use media_demux::{
    AudioCodec, DemuxLimits, DemuxOptions, Format, MemSource, StreamEvent, open_auto,
    open_auto_with,
};

fn fixture(name: &str) -> Vec<u8> {
    let path = concat!(env!("CARGO_MANIFEST_DIR"), "/../fixtures/");
    std::fs::read(format!("{path}{name}")).expect("fixture readable")
}

/// The audio track the demuxer actually bound, as its announced track id
/// and channel count.
fn bound_track(name: &str, options: &DemuxOptions) -> (u32, u32) {
    let mut demuxer = open_auto_with(
        Box::new(MemSource(fixture(name))),
        DemuxLimits::default(),
        Generation::default(),
        options,
    )
    .expect("open");
    for _ in 0..4096 {
        if let StreamEvent::Format(id, Format::Audio { channels, .. }) =
            demuxer.next_event().expect("event")
        {
            return (id.0, channels);
        }
    }
    panic!("no audio format announced");
}

#[test]
fn multi_audio_containers_offer_their_languages() {
    for name in ["h264-multiaudio.mp4", "mkv/h264-multiaudio.mkv"] {
        let demuxer = open_auto(
            Box::new(MemSource(fixture(name))),
            DemuxLimits::default(),
            Generation::default(),
        )
        .expect("open");
        let tracks = demuxer.audio_tracks();
        assert_eq!(tracks.len(), 2, "{name} offers both audio tracks");
        assert_eq!(
            tracks
                .iter()
                .map(|t| t.language.clone())
                .collect::<Vec<_>>(),
            vec![Some("eng".to_string()), Some("jpn".to_string())],
            "{name} states language per track, in container order"
        );
        for track in &tracks {
            assert_eq!(track.codec, AudioCodec::Aac);
            assert_eq!(track.sample_rate, 48_000, "{name}");
            assert_eq!(track.channels, 2, "{name}");
        }
    }

    // Matroska carries a track name as well; MP4's mdhd does not.
    let mkv = open_auto(
        Box::new(MemSource(fixture("mkv/h264-multiaudio.mkv"))),
        DemuxLimits::default(),
        Generation::default(),
    )
    .expect("open");
    assert_eq!(
        mkv.audio_tracks()
            .iter()
            .map(|t| t.label.clone())
            .collect::<Vec<_>>(),
        vec![Some("English".to_string()), Some("Japanese".to_string())]
    );
}

#[test]
fn a_single_audio_track_is_not_offered_as_a_choice() {
    for name in [
        "h264-aac-640x360-30fps.mp4",
        "mkv/h264-aac.mkv",
        "sine-48k-stereo.wav",
    ] {
        let demuxer = open_auto(
            Box::new(MemSource(fixture(name))),
            DemuxLimits::default(),
            Generation::default(),
        )
        .expect("open");
        assert!(
            demuxer.audio_tracks().is_empty(),
            "{name} has nothing to pick between"
        );
    }
}

#[test]
fn the_requested_track_is_the_one_bound() {
    for name in ["h264-multiaudio.mp4", "mkv/h264-multiaudio.mkv"] {
        let first = bound_track(name, &DemuxOptions::default());
        let second = bound_track(name, &DemuxOptions { audio_track: 1 });
        assert_ne!(
            first.0, second.0,
            "{name}: track 1 must bind a different track id from track 0"
        );

        let listed = open_auto(
            Box::new(MemSource(fixture(name))),
            DemuxLimits::default(),
            Generation::default(),
        )
        .expect("open")
        .audio_tracks();
        assert_eq!(first.0, listed[0].id.0, "{name} index 0");
        assert_eq!(second.0, listed[1].id.0, "{name} index 1");
    }
}

#[test]
fn an_out_of_range_request_falls_back_to_the_first_track() {
    for name in ["h264-multiaudio.mp4", "mkv/h264-multiaudio.mkv"] {
        let first = bound_track(name, &DemuxOptions::default());
        let silly = bound_track(name, &DemuxOptions { audio_track: 9 });
        assert_eq!(
            first, silly,
            "{name}: a stale index must play, not fail the session"
        );
    }
}

/// The OBS shape: a recording with the mix, the microphone and the desktop
/// on separate tracks, none of them tagged with a language or a name. The
/// picker still has to offer all three and bind the one asked for, so the
/// enumeration cannot depend on metadata being present, and the labelling
/// above it cannot depend on language alone to tell rows apart.
#[test]
fn untagged_multi_track_recordings_are_still_selectable() {
    let name = "mkv/h264-obs-3track.mkv";
    let tracks = open_auto(
        Box::new(MemSource(fixture(name))),
        DemuxLimits::default(),
        Generation::default(),
    )
    .expect("open")
    .audio_tracks();
    assert_eq!(tracks.len(), 3);
    assert!(
        tracks
            .iter()
            .all(|t| t.language.is_none() && t.label.is_none()),
        "nothing to label them by, which is the point of this fixture"
    );
    // The track ids differ, so a picker always has something unambiguous
    // to key on even when every row would otherwise read the same.
    let ids: Vec<u32> = tracks.iter().map(|t| t.id.0).collect();
    assert_eq!(ids.len(), 3);
    assert!(ids.windows(2).all(|w| w[0] != w[1]));

    for (index, id) in ids.iter().enumerate() {
        let (bound, _) = bound_track(name, &DemuxOptions { audio_track: index });
        assert_eq!(bound, *id, "index {index} binds its own track");
    }
}
