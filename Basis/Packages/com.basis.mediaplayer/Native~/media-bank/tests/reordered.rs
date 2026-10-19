//! A source whose dts runs out of order (RTP carries presentation time
//! only, so an RTSP B-frame stream arrives with dts equal to pts). The
//! release cursor measures how much is banked, so it must not step back
//! when a B frame is released after the reference frame it depends on.

use media_bank::{Bank, BankConfig, BufferDepth, Liveness, PushOutcome};
use media_clock::{Generation, MediaTime};
use media_demux::{Au, StreamEvent, TrackId};

const FRAME_US: i64 = 33_333;

fn au(pts_us: i64) -> StreamEvent {
    StreamEvent::Au(Au {
        track: TrackId(0),
        data: vec![0u8; 100],
        pts: MediaTime::from_micros(pts_us),
        dts: MediaTime::from_micros(pts_us),
        key: false,
        generation: Generation(0),
    })
}

#[test]
fn a_drained_bank_reports_nothing_banked_when_dts_runs_out_of_order() {
    let cfg = BankConfig {
        depth: BufferDepth::Millis(1000),
        liveness: Liveness::Live,
        startup_burst: MediaTime::ZERO,
        ..BankConfig::default()
    };
    let mut bank = Bank::new(cfg, Generation(0)).unwrap();

    // Decode order I0 P3 B1 B2 P6 B4 B5 ..., each arriving at its decode
    // slot, ending on a B frame two frames behind the newest reference.
    let mut slot = 0i64;
    let mut push = |bank: &mut Bank, frame: i64| {
        let wall = MediaTime::from_micros(slot * FRAME_US);
        assert!(matches!(
            bank.push(wall, au(frame * FRAME_US)),
            PushOutcome::Accepted
        ));
        slot += 1;
    };
    push(&mut bank, 0);
    for group in 0..60 {
        let base = group * 3;
        push(&mut bank, base + 3);
        push(&mut bank, base + 1);
        push(&mut bank, base + 2);
    }

    let mut released = 0;
    for ms in 0..20_000 {
        while let Some(event) = bank.pop_due(MediaTime::from_millis(ms)) {
            if matches!(event, StreamEvent::Au(_)) {
                released += 1;
            }
        }
    }
    assert_eq!(released, 181);
    assert_eq!(bank.metrics().banked_bytes, 0);
    assert_eq!(bank.metrics().banked, MediaTime::ZERO);
}
