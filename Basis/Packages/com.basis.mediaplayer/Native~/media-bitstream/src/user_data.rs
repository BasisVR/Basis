//! SEI `user_data_unregistered` (payload type 5) extraction: opaque bytes
//! an encoder or a relay stamped into the video, surfaced with the AU's
//! PTS and left unparsed. The 16-byte UUID that opens every such message
//! is split off so a consumer can pick its own out. x264 stamps its build
//! string through the same payload type on every keyframe, so a reader
//! filters on the UUID rather than on the type.

use crate::sei::scan_au_sei;

/// PTS gap (µs) beyond which a backwards jump is treated as a new timeline
/// rather than B-frame decode-order reordering (which is sub-second).
pub const EPOCH_SLACK_US: i64 = 1_000_000;

/// One `user_data_unregistered` message: the UUID it opened with and the
/// bytes that followed it, as of `pts_us`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SeiUserData {
    pub pts_us: i64,
    pub uuid: [u8; 16],
    pub payload: Vec<u8>,
}

/// Scans Annex-B video access units (in decode order) for type-5 SEI.
/// Stateless bar the PTS it last saw, which it keeps to spot a backwards
/// jump. One scanner per video stream.
#[derive(Default)]
pub struct UserDataScanner {
    last_pts: Option<i64>,
}

impl UserDataScanner {
    pub fn new() -> Self {
        Self::default()
    }

    /// Forget the timeline, on a seek, discontinuity or reconnect.
    pub fn reset(&mut self) {
        self.last_pts = None;
    }

    /// Scan one access unit, handing every type-5 message to `f`. A
    /// message shorter than its UUID is skipped. Returns true when the
    /// AU's PTS jumped backwards by more than the reordering slack: a new
    /// timeline, so whatever the caller queued from the old one is stale.
    pub fn scan_au(
        &mut self,
        annexb: &[u8],
        hevc: bool,
        pts_us: i64,
        mut f: impl FnMut(SeiUserData),
    ) -> bool {
        let mut new_epoch = false;
        if pts_us >= 0 {
            if let Some(last) = self.last_pts
                && pts_us < last.saturating_sub(EPOCH_SLACK_US)
            {
                new_epoch = true;
            }
            self.last_pts = Some(pts_us);
        }
        scan_au_sei(annexb, hevc, |payload_type, payload| {
            if payload_type != 5 || payload.len() < 16 {
                return;
            }
            let mut uuid = [0u8; 16];
            uuid.copy_from_slice(&payload[..16]);
            f(SeiUserData {
                pts_us,
                uuid,
                payload: payload[16..].to_vec(),
            });
        });
        new_epoch
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const UUID_A: [u8; 16] = [0xA0; 16];
    const UUID_B: [u8; 16] = [0xB0; 16];

    /// An H.264 SEI NAL carrying one message of `payload_type`, with the
    /// length prefix spelt out in 0xFF runs the way the walker expects.
    fn sei_nal(payload_type: u8, body: &[u8]) -> Vec<u8> {
        let mut nal = vec![0, 0, 1, 0x06, payload_type];
        let mut size = body.len();
        while size >= 255 {
            nal.push(0xFF);
            size -= 255;
        }
        nal.push(size as u8);
        nal.extend_from_slice(body);
        nal.push(0x80);
        nal
    }

    fn au(nals: &[Vec<u8>]) -> Vec<u8> {
        let mut out = vec![0, 0, 0, 1, 0x09, 0x10];
        for nal in nals {
            out.extend_from_slice(nal);
        }
        out.extend_from_slice(&[0, 0, 1, 0x41, 0x9A]);
        out
    }

    fn collect(scanner: &mut UserDataScanner, au: &[u8], pts: i64) -> (Vec<SeiUserData>, bool) {
        let mut seen = Vec::new();
        let epoch = scanner.scan_au(au, false, pts, |m| seen.push(m));
        (seen, epoch)
    }

    #[test]
    fn splits_uuid_from_payload_and_passes_every_uuid() {
        let mut body_a = UUID_A.to_vec();
        body_a.extend_from_slice(b"hello");
        let mut body_b = UUID_B.to_vec();
        body_b.extend_from_slice(b"x264 - core 164");
        let au = au(&[sei_nal(5, &body_a), sei_nal(5, &body_b)]);
        let (seen, _) = collect(&mut UserDataScanner::new(), &au, 1_000);
        assert_eq!(
            seen,
            vec![
                SeiUserData {
                    pts_us: 1_000,
                    uuid: UUID_A,
                    payload: b"hello".to_vec()
                },
                SeiUserData {
                    pts_us: 1_000,
                    uuid: UUID_B,
                    payload: b"x264 - core 164".to_vec()
                },
            ]
        );
    }

    #[test]
    fn ignores_other_payload_types_and_short_messages() {
        let mut body = UUID_A.to_vec();
        body.extend_from_slice(b"captions-shaped");
        let au = au(&[
            sei_nal(4, &body),
            sei_nal(5, &UUID_A[..15]),
            sei_nal(5, &UUID_A),
        ]);
        let (seen, _) = collect(&mut UserDataScanner::new(), &au, 0);
        // Type 4 is skipped; 15 bytes is short of a UUID; exactly a UUID is
        // a message with an empty payload.
        assert_eq!(
            seen,
            vec![SeiUserData {
                pts_us: 0,
                uuid: UUID_A,
                payload: vec![]
            }]
        );
    }

    #[test]
    fn large_payloads_survive_the_long_length_prefix() {
        let mut body = UUID_A.to_vec();
        body.extend((0..10_448u32).map(|i| (i % 251) as u8));
        let au = au(&[sei_nal(5, &body)]);
        let (seen, _) = collect(&mut UserDataScanner::new(), &au, 0);
        assert_eq!(seen.len(), 1);
        assert_eq!(seen[0].payload.len(), 10_448);
        assert_eq!(seen[0].payload[..3], [0, 1, 2]);
    }

    #[test]
    fn backwards_jump_past_the_slack_is_a_new_epoch() {
        let au = au(&[]);
        let mut scanner = UserDataScanner::new();
        assert!(!collect(&mut scanner, &au, 5_000_000).1);
        // B-frame reordering: sub-second backwards, same epoch.
        assert!(!collect(&mut scanner, &au, 4_900_000).1);
        assert!(collect(&mut scanner, &au, 1_000_000).1);
        // A reset forgets the timeline, so the next AU is not a jump.
        scanner.reset();
        assert!(!collect(&mut scanner, &au, 0).1);
    }

    #[test]
    fn epoch_check_holds_at_the_pts_extremes() {
        let au = au(&[]);
        let mut scanner = UserDataScanner::new();
        assert!(!collect(&mut scanner, &au, i64::MAX).1);
        // Any real pts after i64::MAX is a jump; no arithmetic on the
        // way there may overflow.
        assert!(collect(&mut scanner, &au, i64::MAX - EPOCH_SLACK_US - 1).1);
        assert!(collect(&mut scanner, &au, 0).1);
        // From a small last pts nothing non-negative can be a jump.
        let mut scanner = UserDataScanner::new();
        assert!(!collect(&mut scanner, &au, EPOCH_SLACK_US - 1).1);
        assert!(!collect(&mut scanner, &au, 0).1);
    }

    #[test]
    fn hevc_layout_is_walked() {
        let mut body = UUID_A.to_vec();
        body.extend_from_slice(b"hevc");
        // Prefix SEI NAL (type 39): 2-byte header, then the message.
        let mut nal = vec![0, 0, 1, 39 << 1, 0x01, 5, body.len() as u8];
        nal.extend_from_slice(&body);
        nal.push(0x80);
        let mut seen = Vec::new();
        UserDataScanner::new().scan_au(&nal, true, 0, |m| seen.push(m));
        assert_eq!(seen.len(), 1);
        assert_eq!(seen[0].payload, b"hevc");
    }
}
