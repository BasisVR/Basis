//! In-band CEA-608 closed-caption extraction.
//!
//! Captions ride inside the coded video as SEI user-data (ATSC A/53
//! `user_data_registered_itu_t_t35`, "GA94"), so one scan of each Annex-B
//! access unit covers every transport that carries the video transparently
//! (MPEG-TS, RTSP/RTP, RIST, HLS-with-TS). The scanner pulls the cc_data
//! triples, runs the CEA-608 field-1 caption decoder, and reports a cue
//! (the full displayed text) whenever displayed memory changed.
//!
//! Only CEA-608 field 1 (CC1) is decoded, to plain text. CEA-708 DTVCC
//! (cc_type 2/3) is parsed out but not decoded.

use crate::sei::scan_au_sei;
use crate::user_data::EPOCH_SLACK_US;

const ROWS: usize = 15;
const COLS: usize = 32;

/// Basic North American set: 0x20..=0x7F is ASCII bar these substitutions.
fn basic_cp(c: u8) -> u16 {
    match c {
        0x2A => 0x00E1, // á
        0x5C => 0x00E9, // é
        0x5E => 0x00ED, // í
        0x5F => 0x00F3, // ó
        0x60 => 0x00FA, // ú
        0x7B => 0x00E7, // ç
        0x7C => 0x00F7, // ÷
        0x7D => 0x00D1, // Ñ
        0x7E => 0x00F1, // ñ
        0x7F => 0x2588, // █
        _ => u16::from(c),
    }
}

/// Special characters: control 0x11, second byte 0x30-0x3F.
const SPECIAL: [u16; 16] = [
    0x00AE, 0x00B0, 0x00BD, 0x00BF, 0x2122, 0x00A2, 0x00A3, 0x266A, 0x00E0, 0x0020, 0x00E8, 0x00E2,
    0x00EA, 0x00EE, 0x00F4, 0x00FB,
];

/// Extended Spanish/Miscellaneous/French: control 0x12, second byte 0x20-0x3F.
const EXT_12: [u16; 32] = [
    0x00C1, 0x00C9, 0x00D3, 0x00DA, 0x00DC, 0x00FC, 0x2018, 0x00A1, 0x002A, 0x2019, 0x2014, 0x00A9,
    0x2120, 0x2022, 0x201C, 0x201D, 0x00C0, 0x00C2, 0x00C7, 0x00C8, 0x00CA, 0x00CB, 0x00EB, 0x00CE,
    0x00CF, 0x00EF, 0x00D4, 0x00D9, 0x00F9, 0x00DB, 0x00AB, 0x00BB,
];

/// Extended Portuguese/German/Danish: control 0x13, second byte 0x20-0x3F.
const EXT_13: [u16; 32] = [
    0x00C3, 0x00E3, 0x00CD, 0x00CC, 0x00EC, 0x00D2, 0x00F2, 0x00D5, 0x00F5, 0x007B, 0x007D, 0x005C,
    0x005E, 0x005F, 0x007C, 0x007E, 0x00C4, 0x00E4, 0x00D6, 0x00F6, 0x00DF, 0x00A5, 0x00A4, 0x2502,
    0x00C5, 0x00E5, 0x00D8, 0x00F8, 0x250C, 0x2510, 0x2514, 0x2518,
];

#[derive(Clone, Copy, PartialEq, Eq)]
enum Mode {
    PopOn,
    RollUp,
    PaintOn,
}

/// The CEA-608 field-1 (CC1) decoder state machine.
struct Cea608 {
    /// Displayed memory.
    disp: [[u16; COLS]; ROWS],
    /// Non-displayed memory (pop-on load buffer).
    nond: [[u16; COLS]; ROWS],
    mode: Mode,
    /// 2, 3 or 4 rows.
    rollup: usize,
    /// Cursor, 0-based. `col` may sit at COLS (past the last cell) after a
    /// full row or a tab clamp; writes there are dropped.
    row: usize,
    col: usize,
    /// Previous control pair, for transmission-doubling dedup.
    last: Option<(u8, u8)>,
}

impl Cea608 {
    fn new() -> Self {
        Self {
            disp: [[0; COLS]; ROWS],
            nond: [[0; COLS]; ROWS],
            mode: Mode::PopOn,
            rollup: 2,
            row: ROWS - 1,
            col: 0,
            last: None,
        }
    }

    /// The buffer characters land in: pop-on loads off-screen, the others
    /// draw live.
    fn cur_buf(&mut self) -> &mut [[u16; COLS]; ROWS] {
        if self.mode == Mode::PopOn {
            &mut self.nond
        } else {
            &mut self.disp
        }
    }

    /// Pop-on loads into off-screen memory (no visible change until EOC
    /// flips it); roll-up and paint-on write straight to displayed memory,
    /// so each write is live.
    fn live(&self) -> bool {
        self.mode != Mode::PopOn
    }

    fn put_cp(&mut self, cp: u16) {
        if self.row >= ROWS || self.col >= COLS {
            return;
        }
        let (row, col) = (self.row, self.col);
        self.cur_buf()[row][col] = if cp == 0 { 0x20 } else { cp };
        self.col += 1;
    }

    /// Extended chars are transmitted after a standard fallback char and
    /// overwrite it.
    fn put_ext(&mut self, cp: u16) {
        if self.col > 0 {
            self.col -= 1;
        }
        self.put_cp(cp);
    }

    /// PAC row from the control pair (1-based); see CEA-608 §8.4.
    fn apply_pac(&mut self, b0: u8, b1: u8) {
        let mut row = match b0 & 0x07 {
            0 => 11,
            1 => 1,
            2 => 3,
            3 => 12,
            4 => 14,
            5 => 5,
            6 => 7,
            _ => 9,
        };
        if b0 != 0x10 && b1 >= 0x60 {
            row += 1;
        }
        self.row = row - 1;
        // Indent PACs (bit 4 set) carry a column; colour/style PACs leave
        // column 0.
        self.col = if b1 & 0x10 != 0 {
            usize::from((b1 & 0x0E) >> 1) * 4
        } else {
            0
        };
        if self.col >= COLS {
            self.col = 0;
        }
    }

    fn rollup_scroll(&mut self) {
        let base = if self.row < ROWS { self.row } else { ROWS - 1 };
        let rows = self.rollup.max(2);
        let top = base.saturating_sub(rows - 1);
        for r in top..base {
            self.disp[r] = self.disp[r + 1];
        }
        self.disp[base] = [0; COLS];
        self.row = base;
        self.col = 0;
    }

    /// Misc control (control 0x14, second byte 0x20-0x2F). Returns true if
    /// displayed memory changed and a cue should be emitted.
    fn misc_control(&mut self, b1: u8) -> bool {
        match b1 {
            0x20 => {
                self.mode = Mode::PopOn; // RCL
                false
            }
            0x21 => {
                // BS
                if self.col > 0 {
                    self.col -= 1;
                    let (row, col) = (self.row, self.col);
                    if row < ROWS {
                        self.cur_buf()[row][col] = 0;
                    }
                }
                self.live()
            }
            0x24 => {
                // DER
                let (row, col) = (self.row, self.col);
                if row < ROWS {
                    self.cur_buf()[row][col.min(COLS)..].fill(0);
                }
                self.live()
            }
            0x25..=0x27 => {
                self.mode = Mode::RollUp; // RU2/RU3/RU4
                self.rollup = usize::from(b1 - 0x23);
                false
            }
            0x29 => {
                self.mode = Mode::PaintOn; // RDC
                false
            }
            0x2C => {
                self.disp = [[0; COLS]; ROWS]; // EDM
                true
            }
            0x2D => {
                // CR
                if self.mode == Mode::RollUp {
                    self.rollup_scroll();
                    true
                } else {
                    false
                }
            }
            0x2E => {
                self.nond = [[0; COLS]; ROWS]; // ENM
                false
            }
            0x2F => {
                std::mem::swap(&mut self.disp, &mut self.nond); // EOC
                true
            }
            // FON (0x28), TR (0x2A), RTD (0x2B) and the rest: no display
            // change.
            _ => false,
        }
    }

    /// Decode one parity-stripped byte pair from field 1. Returns true when
    /// displayed memory changed (the caller should serialise + emit a cue).
    fn pair(&mut self, b0: u8, b1: u8) -> bool {
        let (b0, b1) = (b0 & 0x7F, b1 & 0x7F);
        if b0 == 0 && b1 == 0 {
            return false;
        }

        let is_ctrl = (0x10..=0x1F).contains(&b0);
        if is_ctrl {
            // Control pairs are transmitted twice; drop the doubling.
            if self.last == Some((b0, b1)) {
                self.last = None;
                return false;
            }
            self.last = Some((b0, b1));
        } else {
            self.last = None;
        }

        if (0x18..=0x1F).contains(&b0) {
            return false; // channel 2; only CC1 is decoded
        }

        if is_ctrl {
            if b1 >= 0x40 {
                self.apply_pac(b0, b1);
                return false;
            }
            return match (b0, b1) {
                (0x11, 0x20..=0x2F) => {
                    self.put_cp(0x20); // mid-row style
                    self.live()
                }
                (0x11, 0x30..=0x3F) => {
                    self.put_cp(SPECIAL[usize::from(b1 - 0x30)]);
                    self.live()
                }
                (0x12, 0x20..=0x3F) => {
                    self.put_ext(EXT_12[usize::from(b1 - 0x20)]);
                    self.live()
                }
                (0x13, 0x20..=0x3F) => {
                    self.put_ext(EXT_13[usize::from(b1 - 0x20)]);
                    self.live()
                }
                (0x17, 0x21..=0x23) => {
                    self.col = (self.col + usize::from(b1 - 0x20)).min(COLS); // tab offset
                    false
                }
                (0x14, 0x20..=0x2F) => self.misc_control(b1),
                _ => false,
            };
        }

        let mut wrote = false;
        if b0 >= 0x20 {
            self.put_cp(basic_cp(b0));
            wrote = true;
        }
        if b1 >= 0x20 {
            self.put_cp(basic_cp(b1));
            wrote = true;
        }
        wrote && self.live()
    }

    /// Flatten displayed memory: non-empty rows top-to-bottom, leading and
    /// trailing spaces trimmed, rows joined with '\n'. Empty = nothing
    /// displayed (a clear cue).
    fn serialize(&self) -> String {
        let mut out = String::new();
        for row in &self.disp {
            let mut l = 0usize;
            let mut r = COLS;
            while l < r && (row[l] == 0 || row[l] == 0x20) {
                l += 1;
            }
            while r > l && (row[r - 1] == 0 || row[r - 1] == 0x20) {
                r -= 1;
            }
            if l >= r {
                continue;
            }
            if !out.is_empty() {
                out.push('\n');
            }
            for &cell in &row[l..r] {
                let cp = if cell == 0 { 0x20 } else { cell };
                out.push(char::from_u32(u32::from(cp)).unwrap_or(' '));
            }
        }
        out
    }
}

/// Parse the ATSC A/53 cc_data() payload of a registered user-data SEI
/// message (payload type 4) and hand each valid `(cc_type, byte, byte)`
/// triple to `f`. Non-A/53 payloads are ignored.
pub fn a53_cc_triples(payload: &[u8], mut f: impl FnMut(u8, u8, u8)) {
    if payload.len() < 8 {
        return;
    }
    if payload[0] != 0xB5 {
        return; // itu_t_t35_country_code = USA
    }
    if u16::from_be_bytes([payload[1], payload[2]]) != 0x0031 {
        return; // provider_code = ATSC
    }
    if &payload[3..7] != b"GA94" {
        return; // user_identifier
    }
    if payload[7] != 0x03 {
        return; // user_data_type_code = cc_data
    }

    let cc = &payload[8..];
    if cc.len() < 2 {
        return;
    }
    if (cc[0] >> 6) & 1 == 0 {
        return; // process_cc_data_flag
    }
    let count = usize::from(cc[0] & 0x1F);
    let mut idx = 2; // skip flags byte + em_data byte
    for _ in 0..count {
        if idx + 3 > cc.len() {
            break;
        }
        let flags = cc[idx];
        let valid = (flags >> 2) & 1 == 1;
        let cc_type = flags & 0x3;
        if valid {
            f(cc_type, cc[idx + 1], cc[idx + 2]);
        }
        idx += 3;
    }
}

/// A caption cue: the full displayed text as of `pts_us` (empty = cleared).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CaptionCue {
    pub pts_us: i64,
    pub text: String,
}

/// Scans Annex-B video access units (in decode order) for caption SEI and
/// runs the CEA-608 field-1 decoder. One scanner per video stream; feed
/// every AU, in the order the demuxer delivers them.
pub struct CaptionScanner {
    dec: Cea608,
    last_pts: Option<i64>,
}

impl Default for CaptionScanner {
    fn default() -> Self {
        Self::new()
    }
}

impl CaptionScanner {
    pub fn new() -> Self {
        Self {
            dec: Cea608::new(),
            last_pts: None,
        }
    }

    /// Drop all decoder state and pending text. Used on seeks,
    /// discontinuities and reconnects, where captions from the old timeline
    /// must not survive.
    pub fn reset(&mut self) {
        self.dec = Cea608::new();
        self.last_pts = None;
    }

    /// Scan one access unit; returns the cue when displayed memory changed
    /// (empty text = display cleared). `hevc` selects the H.265 NAL/SEI
    /// layout.
    pub fn scan_au(&mut self, annexb: &[u8], hevc: bool, pts_us: i64) -> Option<CaptionCue> {
        // A large backwards PTS jump marks a new timeline (loop replay or a
        // mid-stream discontinuity the demuxer did not flag). The slack
        // absorbs B-frame decode-order reordering, which is sub-second. If
        // the wiped display held text, that surfaces as a clear cue so the
        // consumer doesn't keep showing the old epoch's caption.
        let mut force_clear = false;
        if pts_us >= 0 {
            if let Some(last) = self.last_pts
                && pts_us < last.saturating_sub(EPOCH_SLACK_US)
            {
                force_clear = !self.dec.serialize().is_empty();
                self.dec = Cea608::new();
            }
            self.last_pts = Some(pts_us);
        }

        let mut changed = false;
        scan_au_sei(annexb, hevc, |payload_type, payload| {
            if payload_type == 4 {
                a53_cc_triples(payload, |cc_type, b0, b1| {
                    // cc_type 0 = CEA-608 field 1; 1 = field 2, 2/3 =
                    // CEA-708 DTVCC. Only field 1 is decoded.
                    if cc_type == 0 {
                        changed |= self.dec.pair(b0, b1);
                    }
                });
            }
        });
        // One cue per AU: roll-up/paint-on mutate displayed memory on every
        // pair, so coalescing keeps the consumer from churning while still
        // tracking live updates.
        if changed || force_clear {
            Some(CaptionCue {
                pts_us,
                text: self.dec.serialize(),
            })
        } else {
            None
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Wrap 608 byte pairs as a full Annex-B AU carrying one A/53 caption
    /// SEI (odd parity bits left zero; the decoder masks to 7 bits).
    fn au_with_pairs(pairs: &[(u8, u8)]) -> Vec<u8> {
        let mut cc = vec![0x40 | pairs.len() as u8, 0x00];
        for &(b0, b1) in pairs {
            cc.extend_from_slice(&[0x04, b0, b1]); // valid, cc_type 0
        }
        let mut payload = vec![0xB5, 0x00, 0x31];
        payload.extend_from_slice(b"GA94");
        payload.push(0x03);
        payload.extend_from_slice(&cc);
        payload.push(0xFF); // marker_bits tail byte

        let mut sei = vec![0x06, 4, payload.len() as u8];
        sei.extend_from_slice(&payload);
        sei.push(0x80);

        let mut au = vec![0, 0, 0, 1, 0x09, 0x10]; // AUD
        au.extend_from_slice(&[0, 0, 1]);
        au.extend_from_slice(&sei);
        au.extend_from_slice(&[0, 0, 1, 0x41, 0x9A]); // slice
        au
    }

    /// Control pairs are doubled on the wire; the decoder dedups them.
    fn doubled(pairs: &[(u8, u8)]) -> Vec<(u8, u8)> {
        let mut out = Vec::new();
        for &p in pairs {
            let ctrl = (0x10..=0x1F).contains(&(p.0 & 0x7F));
            out.push(p);
            if ctrl {
                out.push(p);
            }
        }
        out
    }

    #[test]
    fn popon_cue_appears_only_at_eoc() {
        let mut s = CaptionScanner::new();
        // RCL, PAC row 15 col 0, "HI", then EOC in a later AU.
        let load = doubled(&[(0x14, 0x20), (0x14, 0x70), (b'H', b'I')]);
        assert_eq!(s.scan_au(&au_with_pairs(&load), false, 1000), None);
        let flip = doubled(&[(0x14, 0x2F)]);
        let cue = s.scan_au(&au_with_pairs(&flip), false, 2000).unwrap();
        assert_eq!(cue.text, "HI");
        assert_eq!(cue.pts_us, 2000);
    }

    #[test]
    fn rollup_is_live_and_scrolls() {
        let mut s = CaptionScanner::new();
        // RU2 + PAC row 15, then live text.
        let start = doubled(&[(0x14, 0x25), (0x14, 0x70), (b'A', b'B')]);
        let cue = s.scan_au(&au_with_pairs(&start), false, 1000).unwrap();
        assert_eq!(cue.text, "AB");
        // CR scrolls AB up; new text lands on the base row.
        let next = doubled(&[(0x14, 0x2D), (b'C', b'D')]);
        let cue = s.scan_au(&au_with_pairs(&next), false, 2000).unwrap();
        assert_eq!(cue.text, "AB\nCD");
    }

    #[test]
    fn accented_specials_and_extended_overwrite() {
        let mut s = CaptionScanner::new();
        // Paint-on so writes are live. 0x2A in the basic set is á; the
        // extended é (0x12,0x21 -> É) overwrites its fallback char.
        let pairs = doubled(&[
            (0x14, 0x29), // RDC (paint-on)
            (0x14, 0x70), // PAC row 15
            (0x2A, b'e'),
            (0x12, 0x21), // extended É overwrites the fallback 'e'
        ]);
        let cue = s.scan_au(&au_with_pairs(&pairs), false, 1000).unwrap();
        assert_eq!(cue.text, "áÉ");
    }

    #[test]
    fn edm_emits_a_clear_cue() {
        let mut s = CaptionScanner::new();
        let start = doubled(&[(0x14, 0x25), (0x14, 0x70), (b'H', b'I')]);
        assert!(s.scan_au(&au_with_pairs(&start), false, 1000).is_some());
        let clear = doubled(&[(0x14, 0x2C)]);
        let cue = s.scan_au(&au_with_pairs(&clear), false, 2000).unwrap();
        assert_eq!(cue.text, "");
    }

    #[test]
    fn channel_2_and_field_2_are_ignored() {
        let mut s = CaptionScanner::new();
        // Channel-2 preamble + text (0x18-0x1F control range).
        let pairs = doubled(&[(0x14 | 0x08, 0x25), (b'N', b'O')]);
        // The text bytes land via channel-agnostic basic writes in pop-on
        // (off-screen) memory, so nothing displays.
        assert_eq!(s.scan_au(&au_with_pairs(&pairs), false, 1000), None);
    }

    #[test]
    fn backwards_pts_resets_the_epoch() {
        let mut s = CaptionScanner::new();
        let start = doubled(&[(0x14, 0x25), (0x14, 0x70), (b'H', b'I')]);
        assert!(
            s.scan_au(&au_with_pairs(&start), false, 30_000_000)
                .is_some()
        );
        // Loop replay: PTS rebases far backwards; old text must not leak
        // into the new epoch's first cue.
        let next = doubled(&[(0x14, 0x25), (0x14, 0x70), (b'A', b'B')]);
        let cue = s.scan_au(&au_with_pairs(&next), false, 1000).unwrap();
        assert_eq!(cue.text, "AB");
    }

    #[test]
    fn non_a53_user_data_is_ignored() {
        let mut triples = 0;
        a53_cc_triples(
            &[0xB5, 0x00, 0x31, b'X', b'0', b'9', b'4', 0x03],
            |_, _, _| triples += 1,
        );
        a53_cc_triples(b"\xb5\x000GA94\x03\x44\x00\x04AB", |_, _, _| triples += 1);
        assert_eq!(triples, 0);
        // Truncated triple list stops cleanly at the boundary.
        a53_cc_triples(b"\xb5\x001GA94\x03\x42\x00\x04A", |_, _, _| triples += 1);
        assert_eq!(triples, 0);
    }
}
