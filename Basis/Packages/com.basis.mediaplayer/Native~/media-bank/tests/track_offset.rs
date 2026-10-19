//! Live tracks whose stamps sit apart at arrival. A relay can open a join
//! with a cached keyframe stamped seconds before the live frames that
//! follow it, with the sound starting from that same earlier moment. Both
//! tracks then arrive together at 1x while one is stamped seconds ahead of
//! the other. Played by the stamps, the ahead track has to wait for the
//! other; the other must still release as it arrives, or the viewer sits
//! behind live by the gap twice over.

use std::collections::VecDeque;

use media_bank::{Bank, BankConfig, BankMetrics, Liveness, PushOutcome};
use media_clock::{Generation, MediaTime};
use media_demux::{Au, StreamEvent, TrackId};

const VIDEO: TrackId = TrackId(0);
const AUDIO: TrackId = TrackId(1);
const VIDEO_US: i64 = 33_333;
const AUDIO_US: i64 = 21_333;
const RUN_SECS: i64 = 60;
const GAP_US: i64 = 3_000_000;

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

struct Outcome {
    /// Median wall time from arrival to release over the last 10 s, ms.
    video_hold_ms: i64,
    audio_hold_ms: i64,
    metrics: BankMetrics,
}

/// Both tracks arrive together at 1x from wall zero, `ahead` stamped
/// `GAP_US` ahead of the other and pushed first at each instant. With
/// `stale_first`, one AU of `ahead` stamped at the other track's moment
/// opens the join (a relay's cached keyframe); without it the first AU the
/// Bank sees is already stamped ahead. The clock starts (the presentation
/// signal) 20 ms after the first release, as an audio-led live start does.
/// Release order within each track is asserted as it goes.
fn run(ahead: TrackId, stale_first: bool) -> Outcome {
    let mut bank = Bank::new(
        BankConfig {
            liveness: Liveness::Live,
            ..BankConfig::default()
        },
        Generation(0),
    )
    .unwrap();
    let base = 100_000_000i64;
    let stamp = |track: TrackId, at_us: i64| {
        if track == ahead {
            base + GAP_US + at_us
        } else {
            base + at_us
        }
    };
    let mut arrivals: VecDeque<(TrackId, i64)> = VecDeque::new();
    let mut last_released = [i64::MIN; 2];
    let mut holds = [Vec::new(), Vec::new()];
    let mut first_release: Option<i64> = None;
    let (mut next_video, mut next_audio) = (0i64, 0i64);

    let push = |bank: &mut Bank, arrivals: &mut VecDeque<_>, wall: MediaTime, track, dts| {
        assert!(matches!(
            bank.push(wall, au(track, dts)),
            PushOutcome::Accepted
        ));
        arrivals.push_back((track, wall.as_micros()));
    };
    if stale_first {
        push(&mut bank, &mut arrivals, MediaTime::ZERO, ahead, base);
    }
    let order = if ahead == VIDEO {
        [VIDEO, AUDIO]
    } else {
        [AUDIO, VIDEO]
    };

    for ms in 0..RUN_SECS * 1000 {
        let wall_us = ms * 1000;
        let wall = MediaTime::from_micros(wall_us);
        for track in order {
            let (next, step) = if track == VIDEO {
                (&mut next_video, VIDEO_US)
            } else {
                (&mut next_audio, AUDIO_US)
            };
            while *next <= wall_us {
                push(&mut bank, &mut arrivals, wall, track, stamp(track, *next));
                *next += step;
            }
        }
        while let Some(event) = bank.pop_due(wall) {
            let StreamEvent::Au(au) = event else { continue };
            let slot = au.track.0 as usize;
            let dts = au.dts.as_micros();
            assert!(
                dts > last_released[slot],
                "track {slot} released out of order: {dts} after {}",
                last_released[slot]
            );
            last_released[slot] = dts;
            let index = arrivals
                .iter()
                .position(|(track, _)| *track == au.track)
                .expect("released AU was pushed");
            let (_, arrived) = arrivals.remove(index).expect("index in range");
            first_release.get_or_insert(wall_us);
            if wall_us > (RUN_SECS - 10) * 1_000_000 {
                holds[slot].push(wall_us - arrived);
            }
        }
        if bank.awaiting_presentation()
            && first_release.is_some_and(|first| wall_us >= first + 20_000)
        {
            bank.presentation_started(wall);
        }
    }
    let median = |v: &mut Vec<i64>| {
        v.sort_unstable();
        v[v.len() / 2] / 1000
    };
    Outcome {
        video_hold_ms: median(&mut holds[VIDEO.0 as usize]),
        audio_hold_ms: median(&mut holds[AUDIO.0 as usize]),
        metrics: bank.metrics(),
    }
}

fn assert_only_the_ahead_track_waits(outcome: &Outcome, ahead_hold_ms: i64, behind_hold_ms: i64) {
    let m = &outcome.metrics;
    assert!(
        behind_hold_ms <= 100,
        "the track stamped behind waited {behind_hold_ms} ms for the one stamped ahead"
    );
    assert!(
        (2_500..=3_000).contains(&ahead_hold_ms),
        "the track stamped ahead waited {ahead_hold_ms} ms, wanted the 3 s gap less the pace lead"
    );
    assert_eq!(m.reanchors, 0, "the debt bound fought the schedule");
    assert_eq!(m.stall_total, MediaTime::ZERO);
    assert!(
        m.lag <= MediaTime::from_millis(100),
        "lag {} counts the gap as distance from the live edge",
        m.lag
    );
    assert!(
        m.target_lag <= MediaTime::from_millis(100),
        "Auto learned the gap as a target: {}",
        m.target_lag
    );
}

/// The shape seen on a relay: a stale keyframe first, the sound from its
/// moment on, the live picture seconds later. The picture waits; the sound
/// plays as it arrives.
#[test]
fn a_stale_first_frame_holds_the_picture_not_the_sound() {
    let outcome = run(VIDEO, true);
    assert_only_the_ahead_track_waits(&outcome, outcome.video_hold_ms, outcome.audio_hold_ms);
}

/// The same the other way round: whichever track is stamped ahead waits,
/// and the other is never held behind it.
#[test]
fn a_stale_first_sound_holds_the_sound_not_the_picture() {
    let outcome = run(AUDIO, true);
    assert_only_the_ahead_track_waits(&outcome, outcome.audio_hold_ms, outcome.video_hold_ms);
}

/// No stale AU: the first AU the Bank sees is the one stamped ahead, so the
/// other track's stamps start before the Bank's origin. It still releases
/// as it arrives.
#[test]
fn a_track_stamped_ahead_from_the_first_au_waits_alone() {
    let outcome = run(VIDEO, false);
    assert_only_the_ahead_track_waits(&outcome, outcome.video_hold_ms, outcome.audio_hold_ms);
    let outcome = run(AUDIO, false);
    assert_only_the_ahead_track_waits(&outcome, outcome.audio_hold_ms, outcome.video_hold_ms);
}

/// Aligned tracks at 1x until `audio_stops_us`, then video alone, the
/// clock started 20 ms after the first release. Returns the bank, the
/// wall reached and the next video stamp; every push must be accepted.
fn run_until_audio_stops(audio_stops_us: i64, secs: i64) -> (Bank, MediaTime, i64) {
    let mut bank = Bank::new(
        BankConfig {
            liveness: Liveness::Live,
            ..BankConfig::default()
        },
        Generation(0),
    )
    .unwrap();
    let (mut next_video, mut next_audio) = (0i64, 0i64);
    let mut first_release: Option<i64> = None;
    let mut wall = MediaTime::ZERO;
    for ms in 0..secs * 1000 {
        let wall_us = ms * 1000;
        wall = MediaTime::from_micros(wall_us);
        while next_video <= wall_us {
            let outcome = bank.push(wall, au(VIDEO, next_video));
            assert!(
                matches!(outcome, PushOutcome::Accepted),
                "video refused at {wall} with audio stopped at {audio_stops_us} us"
            );
            next_video += VIDEO_US;
        }
        while next_audio <= wall_us.min(audio_stops_us) {
            assert!(matches!(
                bank.push(wall, au(AUDIO, next_audio)),
                PushOutcome::Accepted
            ));
            next_audio += AUDIO_US;
        }
        while bank.pop_due(wall).is_some() {
            first_release.get_or_insert(wall_us);
        }
        if bank.awaiting_presentation()
            && first_release.is_some_and(|first| wall_us >= first + 20_000)
        {
            bank.presentation_started(wall);
        }
    }
    (bank, wall, next_video)
}

/// A track that stops arriving has nothing queued, and must not hold the
/// queue's cursor at its last release: the other track would fill the
/// time cap against it and be refused.
#[test]
fn a_track_that_stops_does_not_hold_the_other_at_the_time_cap() {
    let (bank, _, _) = run_until_audio_stops(5_000_000, 45);
    let banked = bank.metrics().banked;
    assert!(
        banked <= MediaTime::from_millis(600),
        "banked {banked} measured from the stopped track"
    );
}

/// Once a track has stopped, the one still arriving decides lag and
/// decay: a surplus landing on it is returned as it would be alone.
#[test]
fn a_track_that_stops_does_not_decide_lag_and_decay() {
    let (mut bank, mut wall, mut next_video) = run_until_audio_stops(5_000_000, 10);
    // 1.5 s of video lands at once, ahead of the schedule.
    for _ in 0..45 {
        assert!(matches!(
            bank.push(wall, au(VIDEO, next_video)),
            PushOutcome::Accepted
        ));
        next_video += VIDEO_US;
    }
    while bank.pop_due(wall).is_some() {}
    assert!(
        bank.metrics().lag >= MediaTime::from_millis(800),
        "lag {} did not follow the surplus on the track still arriving",
        bank.metrics().lag
    );
    // 20,000 ppm returns a second in 50 s; give it 100.
    for _ in 0..100_000 {
        wall += MediaTime::from_millis(1);
        while next_video <= wall.as_micros() + 1_500_000 {
            assert!(matches!(
                bank.push(wall, au(VIDEO, next_video)),
                PushOutcome::Accepted
            ));
            next_video += VIDEO_US;
        }
        while bank.pop_due(wall).is_some() {}
    }
    let m = bank.metrics();
    assert!(
        m.lag <= MediaTime::from_millis(100),
        "lag {} after decay",
        m.lag
    );
    assert!(
        m.banked <= MediaTime::from_millis(600),
        "banked {} after decay",
        m.banked
    );
}
