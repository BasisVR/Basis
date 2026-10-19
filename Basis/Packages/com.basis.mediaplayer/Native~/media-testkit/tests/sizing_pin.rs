//! Pin the committed network-delay fixtures to their analytic sizing table:
//! if a fixture is regenerated and its gap reconstruction drifts, this
//! fails before any Bank behaviour is measured against it.

use media_clock::MediaTime;
use media_testkit::GapCapture;

const DEPTHS_MS: [i64; 8] = [460, 1000, 1500, 2000, 2500, 3000, 4000, 5000];

fn assert_lane(capture: &GapCapture, published_pct: [f64; 8]) {
    for (depth_ms, expected) in DEPTHS_MS.iter().zip(published_pct) {
        let got = capture.analytic_stall_fraction(MediaTime::from_millis(*depth_ms)) * 100.0;
        assert!(
            (got - expected).abs() < 0.005,
            "{} at {depth_ms}ms: analytic {got:.3}% vs published {expected:.2}%",
            capture.name,
        );
    }
}

#[test]
fn fixtures_reproduce_the_published_sizing_table() {
    assert_lane(
        &GapCapture::ts_rtt600_loss0(),
        [2.90, 0.28, 0.08, 0.00, 0.00, 0.00, 0.00, 0.00],
    );
    assert_lane(
        &GapCapture::ts_rtt300_loss005(),
        [22.64, 7.96, 3.56, 1.24, 0.06, 0.00, 0.00, 0.00],
    );
    assert_lane(
        &GapCapture::rtspt_rtt300_loss005(),
        [7.26, 0.47, 0.09, 0.00, 0.00, 0.00, 0.00, 0.00],
    );
    assert_lane(
        &GapCapture::ts_rtt300_loss05(),
        [90.88, 70.57, 57.85, 49.00, 41.16, 33.78, 21.50, 12.51],
    );
}

#[test]
fn clean_baseline_has_no_gaps() {
    let capture = GapCapture::ts_clean();
    assert!(capture.gaps.is_empty());
    assert!(capture.duration > MediaTime::from_secs(170));
}
