//! SEI message walking, shared by the caption and type-5 user-data scanners.
//!
//! An SEI arrives inside a video access unit as a NAL (H.264 type 6,
//! H.265 types 39/40) whose RBSP holds a sequence of `(payload_type,
//! payload_size, payload)` messages, each length prefix coded as a run of
//! 0xFF bytes plus a terminator byte. Both accumulators are u64: a run is
//! bounded only by the NAL length, and a narrower accumulator overflows on
//! a hostile run of 0xFF.

use crate::annexb::{h264_nal_type, h265_nal_type, nal_units};

/// Strip emulation-prevention bytes (00 00 03 -> 00 00) from a NAL payload
/// (header already removed).
pub fn unescape_rbsp(payload: &[u8]) -> Vec<u8> {
    let mut rbsp = Vec::with_capacity(payload.len());
    let mut zeros = 0usize;
    for &b in payload {
        if zeros >= 2 && b == 0x03 {
            zeros = 0;
            continue;
        }
        rbsp.push(b);
        zeros = if b == 0 { zeros + 1 } else { 0 };
    }
    rbsp
}

/// Walk the SEI messages in an unescaped RBSP, calling `f(payload_type,
/// payload)` for each complete message. Malformed length prefixes end the
/// walk; they never read past the buffer.
pub fn sei_messages(rbsp: &[u8], mut f: impl FnMut(u64, &[u8])) {
    let mut p = 0usize;
    while p + 2 <= rbsp.len() {
        let mut payload_type = 0u64;
        while p < rbsp.len() && rbsp[p] == 0xFF {
            payload_type += 255;
            p += 1;
        }
        if p >= rbsp.len() {
            break;
        }
        payload_type += u64::from(rbsp[p]);
        p += 1;

        let mut size = 0u64;
        while p < rbsp.len() && rbsp[p] == 0xFF {
            size += 255;
            p += 1;
        }
        if p >= rbsp.len() {
            break;
        }
        size += u64::from(rbsp[p]);
        p += 1;

        if size > (rbsp.len() - p) as u64 {
            break;
        }
        let size = size as usize;
        f(payload_type, &rbsp[p..p + size]);
        p += size;
        if p < rbsp.len() && rbsp[p] == 0x80 {
            break; // rbsp_trailing_bits
        }
    }
}

/// Walk one Annex-B access unit and deliver every SEI message in it.
/// `hevc` selects the H.265 NAL layout (2-byte header, SEI types 39/40).
pub fn scan_au_sei(annexb: &[u8], hevc: bool, mut f: impl FnMut(u64, &[u8])) {
    let header_len = if hevc { 2 } else { 1 };
    for nal in nal_units(annexb) {
        if nal.len() <= header_len {
            continue;
        }
        let is_sei = if hevc {
            matches!(h265_nal_type(nal[0]), 39 | 40)
        } else {
            h264_nal_type(nal[0]) == 6
        };
        if !is_sei {
            continue;
        }
        let rbsp = unescape_rbsp(&nal[header_len..]);
        sei_messages(&rbsp, &mut f);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn unescape_strips_emulation_prevention() {
        assert_eq!(unescape_rbsp(&[0, 0, 3, 1]), vec![0, 0, 1]);
        assert_eq!(unescape_rbsp(&[0, 0, 3, 3]), vec![0, 0, 3]);
        // 03 not preceded by two zeros passes through.
        assert_eq!(unescape_rbsp(&[0, 3, 0, 3]), vec![0, 3, 0, 3]);
    }

    #[test]
    fn walks_messages_and_long_prefixes() {
        // type 4, size 2, payload [0xAA, 0xBB]; then type 0xFF+1=256, size 1.
        let rbsp = [4, 2, 0xAA, 0xBB, 0xFF, 1, 1, 0xCC];
        let mut seen = Vec::new();
        sei_messages(&rbsp, |t, p| seen.push((t, p.to_vec())));
        assert_eq!(seen, vec![(4, vec![0xAA, 0xBB]), (256, vec![0xCC])]);
    }

    #[test]
    fn hostile_ff_runs_do_not_overflow_or_overread() {
        let rbsp = vec![0xFF; 4096];
        sei_messages(&rbsp, |_, _| panic!("no complete message exists"));
        // Truncated size prefix: type 4 then only 0xFFs.
        let mut rbsp = vec![4u8];
        rbsp.extend_from_slice(&[0xFF; 64]);
        sei_messages(&rbsp, |_, _| panic!("no complete message exists"));
        // Size larger than remaining bytes stops the walk.
        sei_messages(&[4, 200, 0, 0], |_, _| panic!("payload is truncated"));
    }

    #[test]
    fn au_scan_filters_sei_nals() {
        // AUD, then an H.264 SEI NAL (type 6) carrying one message.
        let au = [
            0, 0, 0, 1, 0x09, 0x10, // AUD
            0, 0, 1, 0x06, 4, 1, 0xAB, 0x80, // SEI: type 4 size 1
            0, 0, 1, 0x41, 0x9A, // slice
        ];
        let mut seen = Vec::new();
        scan_au_sei(&au, false, |t, p| seen.push((t, p.to_vec())));
        assert_eq!(seen, vec![(4, vec![0xAB])]);
    }
}
