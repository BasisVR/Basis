//! Whether anything in a session can still be played. A track drops out
//! when its decoder refuses it, or when the source turns out not to carry
//! one. With neither kind left the session fails, giving the reasons the
//! refusals gave.

use std::sync::Mutex;
use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};

use crate::pipeline::Leg;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TrackKind {
    Video,
    Audio,
}

const VIDEO_SEEN: u32 = 1 << 0;
const AUDIO_SEEN: u32 = 1 << 1;
const VIDEO_REFUSED: u32 = 1 << 2;
const AUDIO_REFUSED: u32 = 1 << 3;
/// Set by the demux thread that carries video (the only one, on a single
/// source) once it knows every track it will announce.
const VIDEO_LEG_SETTLED: u32 = 1 << 4;
const AUDIO_LEG_SETTLED: u32 = 1 << 5;

fn seen(kind: TrackKind) -> u32 {
    match kind {
        TrackKind::Video => VIDEO_SEEN,
        TrackKind::Audio => AUDIO_SEEN,
    }
}

fn refused(kind: TrackKind) -> u32 {
    match kind {
        TrackKind::Video => VIDEO_REFUSED,
        TrackKind::Audio => AUDIO_REFUSED,
    }
}

/// Nothing left to play: each kind was refused, or never announced by a
/// source that has said all it will, and at least one was refused (a
/// source with no tracks at all fails at open).
fn nothing_playable(bits: u32) -> bool {
    let settled =
        bits & (VIDEO_LEG_SETTLED | AUDIO_LEG_SETTLED) == VIDEO_LEG_SETTLED | AUDIO_LEG_SETTLED;
    let out = |kind| bits & refused(kind) != 0 || (settled && bits & seen(kind) == 0);
    out(TrackKind::Video) && out(TrackKind::Audio) && bits & (VIDEO_REFUSED | AUDIO_REFUSED) != 0
}

#[derive(Default)]
pub struct Playable {
    bits: AtomicU32,
    reasons: Mutex<Vec<String>>,
    failed: AtomicBool,
}

impl Playable {
    /// A demux thread passed on a track's format.
    pub fn announced(&self, kind: TrackKind) {
        self.bits.fetch_or(seen(kind), Ordering::SeqCst);
    }

    /// A decoder was built for the kind after all (a live source
    /// re-announcing), so an earlier refusal no longer stands.
    pub fn decoding(&self, kind: TrackKind) {
        self.bits.fetch_and(!refused(kind), Ordering::SeqCst);
    }

    /// A reason something was left out, kept for the failure should nothing
    /// be left to play.
    pub fn reason(&self, reason: &str) {
        let mut reasons = self.reasons.lock().unwrap_or_else(|e| e.into_inner());
        if !reasons.iter().any(|r| r == reason) {
            reasons.push(reason.to_string());
        }
    }

    /// Every reason kept so far with `reason` after them, for a refusal
    /// that ends the session outright.
    pub fn all_reasons_with(&self, reason: &str) -> String {
        self.reason(reason);
        let reasons = self.reasons.lock().unwrap_or_else(|e| e.into_inner());
        reasons.join("; ")
    }

    /// The kind's decoder refused it. Returns the failure's detail when
    /// that leaves nothing to play.
    pub fn refused(&self, kind: TrackKind, reason: &str) -> Option<String> {
        self.reason(reason);
        self.mark(refused(kind))
    }

    /// A demux thread has announced every track it will: what it has not
    /// announced by now, it does not carry. Returns the failure's detail
    /// when that leaves nothing to play.
    pub fn settled(&self, leg: Leg) -> Option<String> {
        self.mark(match leg {
            Leg::Single => VIDEO_LEG_SETTLED | AUDIO_LEG_SETTLED,
            Leg::Video => VIDEO_LEG_SETTLED,
            Leg::Audio => AUDIO_LEG_SETTLED,
        })
    }

    /// Whichever of two racing marks lands second sees both, so one of
    /// them always makes the call, and only one makes it.
    fn mark(&self, bit: u32) -> Option<String> {
        let bits = self.bits.fetch_or(bit, Ordering::SeqCst) | bit;
        if !nothing_playable(bits) || self.failed.swap(true, Ordering::SeqCst) {
            return None;
        }
        let reasons = self.reasons.lock().unwrap_or_else(|e| e.into_inner());
        Some(reasons.join("; "))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_refused_video_with_audio_announced_plays_on() {
        let p = Playable::default();
        p.announced(TrackKind::Video);
        p.announced(TrackKind::Audio);
        assert_eq!(p.settled(Leg::Single), None);
        assert_eq!(p.refused(TrackKind::Video, "no video"), None);
    }

    #[test]
    fn a_refused_video_alone_fails_once_the_source_is_settled() {
        let p = Playable::default();
        p.announced(TrackKind::Video);
        // A transport stream can still announce audio after the video
        // decoder refused.
        assert_eq!(p.refused(TrackKind::Video, "no video"), None);
        assert_eq!(p.settled(Leg::Single).as_deref(), Some("no video"));
        // Decided once.
        assert_eq!(p.settled(Leg::Single), None);
    }

    #[test]
    fn a_refusal_after_the_source_settled_fails_at_once() {
        let p = Playable::default();
        p.announced(TrackKind::Audio);
        assert_eq!(p.settled(Leg::Single), None);
        assert_eq!(
            p.refused(TrackKind::Audio, "no audio").as_deref(),
            Some("no audio")
        );
    }

    #[test]
    fn both_refused_fail_with_both_reasons() {
        let p = Playable::default();
        p.announced(TrackKind::Video);
        p.announced(TrackKind::Audio);
        assert_eq!(p.refused(TrackKind::Video, "no video"), None);
        assert_eq!(
            p.refused(TrackKind::Audio, "no audio").as_deref(),
            Some("no video; no audio")
        );
    }

    #[test]
    fn a_split_pair_waits_for_both_legs() {
        let p = Playable::default();
        p.announced(TrackKind::Video);
        assert_eq!(p.refused(TrackKind::Video, "no video"), None);
        // The audio leg has not settled, so its audio may yet come.
        assert_eq!(p.settled(Leg::Video), None);
        assert_eq!(p.settled(Leg::Audio).as_deref(), Some("no video"));
    }

    #[test]
    fn a_decoder_built_after_a_refusal_withdraws_it() {
        let p = Playable::default();
        p.announced(TrackKind::Video);
        assert_eq!(p.refused(TrackKind::Video, "no video"), None);
        p.decoding(TrackKind::Video);
        assert_eq!(p.settled(Leg::Single), None);
    }
}
