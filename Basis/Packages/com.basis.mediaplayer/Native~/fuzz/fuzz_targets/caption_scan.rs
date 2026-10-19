//! Fuzz the SEI walker and what sits on it (the CEA-608 decoder and the
//! user-data scanner). Arbitrary bytes treated as an Annex-B access unit
//! must never panic, overread or hang: the scanners run on the demux
//! thread against attacker-controlled video AUs on every transport.
//! Both NAL layouts are driven, plus a PTS sequence that exercises the
//! backwards-jump epoch reset.

#![no_main]

use libfuzzer_sys::fuzz_target;
use media_bitstream::{CaptionScanner, UserDataScanner};

fuzz_target!(|data: &[u8]| {
    let mut scanner = CaptionScanner::new();
    let _ = scanner.scan_au(data, false, 0);
    let _ = scanner.scan_au(data, false, 30_000_000);
    let _ = scanner.scan_au(data, false, 1_000); // backwards: epoch reset
    let mut scanner = CaptionScanner::new();
    let _ = scanner.scan_au(data, true, 0);

    let mut scanner = UserDataScanner::new();
    let _ = scanner.scan_au(data, false, 0, |m| assert!(m.payload.len() <= data.len()));
    let _ = scanner.scan_au(data, false, 30_000_000, |_| {});
    let _ = scanner.scan_au(data, false, 1_000, |_| {});
    let mut scanner = UserDataScanner::new();
    let _ = scanner.scan_au(data, true, 0, |_| {});
});
