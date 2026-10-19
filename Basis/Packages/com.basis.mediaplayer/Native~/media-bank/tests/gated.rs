//! The gated release (per-track routing): a blocked track's events are
//! skipped in place (order intact, Eos a barrier) while the other track
//! keeps releasing, and the release cursor stays with the laggard so
//! banked() reads exactly as an ungated release would.

use media_bank::{Bank, BankConfig, BufferDepth, Liveness, PushOutcome};
use media_clock::{Generation, MediaTime};
use media_demux::{Au, AudioCodec, EosReason, Format, StreamEvent, TrackId};

const INTERVAL_US: i64 = 33_000;
const VIDEO: TrackId = TrackId(0);
const AUDIO: TrackId = TrackId(1);

fn au(track: TrackId, dts_us: i64) -> StreamEvent {
    StreamEvent::Au(Au {
        track,
        data: vec![0u8; 100],
        pts: MediaTime::from_micros(dts_us),
        dts: MediaTime::from_micros(dts_us),
        key: false,
        generation: Generation(0),
    })
}

/// Live priming config: 1x arrivals are due on arrival (the burst covers
/// the 1x line), so pops exercise the walk without hold choreography.
fn bank() -> Bank {
    Bank::new(
        BankConfig {
            depth: BufferDepth::Millis(3000),
            liveness: Liveness::Live,
            startup_burst: MediaTime::from_millis(2000),
            ..BankConfig::default()
        },
        Generation(0),
    )
    .unwrap()
}

fn audio_blocked(event: &StreamEvent) -> bool {
    match event {
        StreamEvent::Au(au) => au.track == AUDIO,
        StreamEvent::Format(_, Format::Audio { .. }) => true,
        _ => false,
    }
}

/// A seek that lands before a track's Format has been released keeps the
/// Format and drops only the old timeline's media: the demuxer does not
/// announce the track again.
#[test]
fn a_seek_keeps_a_format_not_yet_released() {
    let mut bank = bank();
    let wall = MediaTime::ZERO;
    let format = Format::Audio {
        codec: AudioCodec::Aac,
        sample_rate: 48_000,
        channels: 2,
        codec_private: vec![0x11, 0x90],
    };
    let _ = bank.push(wall, StreamEvent::Format(AUDIO, format.clone()));
    for i in 0..10 {
        let _ = bank.push(wall, au(AUDIO, i * INTERVAL_US));
    }

    bank.advance_generation(Generation(1));

    match bank.pop_due(wall) {
        Some(StreamEvent::Format(AUDIO, kept)) => assert_eq!(kept, format),
        other => panic!("the Format did not survive the seek: {other:?}"),
    }
    assert!(
        bank.pop_due(wall).is_none(),
        "the old timeline's media survived"
    );
    assert_eq!(bank.metrics().banked_bytes, 0);
}

#[test]
fn gated_pop_releases_the_open_track_past_a_blocked_head() {
    let mut bank = bank();
    let mut wall = MediaTime::ZERO;
    for i in 0..10i64 {
        wall = MediaTime::from_micros(i * INTERVAL_US);
        assert!(matches!(
            bank.push(wall, au(AUDIO, i * INTERVAL_US)),
            PushOutcome::Accepted
        ));
        assert!(matches!(
            bank.push(wall, au(VIDEO, i * INTERVAL_US)),
            PushOutcome::Accepted
        ));
    }

    // Audio blocked: only the video AUs come out, in order, past the
    // audio AUs parked ahead of them.
    let mut video_dts = Vec::new();
    while let Some(ev) = bank.pop_due_gated(wall, &audio_blocked) {
        let StreamEvent::Au(au) = ev else { panic!() };
        assert_eq!(au.track, VIDEO);
        video_dts.push(au.dts.as_micros());
    }
    assert_eq!(
        video_dts,
        (0..10).map(|i| i * INTERVAL_US).collect::<Vec<_>>()
    );

    // The cursor stayed with the laggard: the whole audio span still
    // counts as banked, exactly as if nothing had been released past it.
    assert_eq!(
        bank.metrics().banked,
        MediaTime::from_micros(9 * INTERVAL_US)
    );

    // Unblocked, the audio AUs release in their own order.
    let mut audio_dts = Vec::new();
    while let Some(ev) = bank.pop_due(wall) {
        let StreamEvent::Au(au) = ev else { panic!() };
        assert_eq!(au.track, AUDIO);
        audio_dts.push(au.dts.as_micros());
    }
    assert_eq!(
        audio_dts,
        (0..10).map(|i| i * INTERVAL_US).collect::<Vec<_>>()
    );
    assert_eq!(bank.metrics().banked, MediaTime::ZERO);
}

#[test]
fn eos_never_overtakes_a_blocked_event() {
    let mut bank = bank();
    let wall = MediaTime::ZERO;
    assert!(matches!(
        bank.push(wall, au(VIDEO, 0)),
        PushOutcome::Accepted
    ));
    assert!(matches!(
        bank.push(wall, au(AUDIO, 0)),
        PushOutcome::Accepted
    ));
    assert!(matches!(
        bank.push(wall, StreamEvent::Eos(EosReason::Natural)),
        PushOutcome::Accepted
    ));

    let Some(StreamEvent::Au(v)) = bank.pop_due_gated(wall, &audio_blocked) else {
        panic!("video AU expected");
    };
    assert_eq!(v.track, VIDEO);
    // The audio AU is blocked and Eos must not pass it: nothing pops,
    // and there is no wall deadline to wait on; only an unblock helps.
    assert!(bank.pop_due_gated(wall, &audio_blocked).is_none());
    assert!(bank.next_due_gated(wall, &audio_blocked).is_none());

    let Some(StreamEvent::Au(a)) = bank.pop_due(wall) else {
        panic!("audio AU expected");
    };
    assert_eq!(a.track, AUDIO);
    assert!(matches!(bank.pop_due(wall), Some(StreamEvent::Eos(_))));
}

#[test]
fn blocked_format_keeps_its_place_ahead_of_its_track() {
    let mut bank = bank();
    let wall = MediaTime::ZERO;
    let format = Format::Audio {
        codec: AudioCodec::Aac,
        sample_rate: 48_000,
        channels: 2,
        codec_private: vec![0x11, 0x90],
    };
    assert!(matches!(
        bank.push(wall, StreamEvent::Format(AUDIO, format)),
        PushOutcome::Accepted
    ));
    assert!(matches!(
        bank.push(wall, au(AUDIO, 0)),
        PushOutcome::Accepted
    ));
    assert!(matches!(
        bank.push(wall, au(VIDEO, 0)),
        PushOutcome::Accepted
    ));

    // The audio Format and AU are both skipped; video releases past them.
    let Some(StreamEvent::Au(v)) = bank.pop_due_gated(wall, &audio_blocked) else {
        panic!("video AU expected");
    };
    assert_eq!(v.track, VIDEO);

    // Unblocked, the Format still precedes its track's AU.
    assert!(matches!(
        bank.pop_due(wall),
        Some(StreamEvent::Format(AUDIO, _))
    ));
    let Some(StreamEvent::Au(a)) = bank.pop_due(wall) else {
        panic!("audio AU expected");
    };
    assert_eq!(a.track, AUDIO);
}

#[test]
fn next_due_reads_past_a_blocked_head() {
    let mut bank = bank();
    // Arrival at t=0 for both tracks; the video AU one interval later on
    // the timeline, so its priming-line due sits in the future.
    let wall = MediaTime::ZERO;
    assert!(matches!(
        bank.push(wall, au(AUDIO, 0)),
        PushOutcome::Accepted
    ));
    let far = 3_000_000i64;
    assert!(matches!(
        bank.push(wall, au(VIDEO, far)),
        PushOutcome::Accepted
    ));
    // Ungated, the head (audio, due now) sets the deadline.
    assert_eq!(bank.next_due(wall), Some(wall));
    // Gated, the deadline is the first admitted event's: the video AU's
    // priming line (rel 3 s against a 2 s burst = due at t+1 s).
    assert_eq!(
        bank.next_due_gated(wall, &audio_blocked),
        Some(MediaTime::from_micros(far - 2_000_000))
    );
}
