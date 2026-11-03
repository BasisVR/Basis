use std::sync::Mutex;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{Receiver, RecvTimeoutError, SyncSender, TrySendError, sync_channel};
use std::time::Duration;

use media_clock::{Generation, MediaTime};
use media_demux::{
    Au, AudioCodec, DemuxError, Demuxer, EosReason, Format, StreamEvent, TrackId, VideoCodec,
};

pub const PUSH_SCHEME: &str = "basis-push";
pub const PUSH_VERSION: u32 = 1;
const VIDEO_TRACK: TrackId = TrackId(0);
const AUDIO_TRACK: TrackId = TrackId(1);
const FEED_DEPTH: usize = 512;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(i32)]
pub enum PushOutcome {
    Accepted = 0,
    NeedKeyframe = 1,
    Full = 2,
    Closed = 3,
    Refused = 4,
}

#[derive(Default)]
struct PushState {
    origin_us: Option<i64>,
    video: Option<(u32, u32)>,
    audio: Option<(u32, u32)>,
    waiting_key: bool,
    last_video_us: i64,
    last_audio_us: i64,
}

pub struct PushFeed {
    tx: SyncSender<StreamEvent>,
    state: Mutex<PushState>,
    closed: AtomicBool,
}

pub struct PushDemuxer {
    rx: Receiver<StreamEvent>,
    cancelled: Option<Box<dyn Fn() -> bool + Send>>,
    video_track: Option<TrackId>,
    audio_track: Option<TrackId>,
    ended: bool,
}

pub fn is_push_url(url: &str) -> bool {
    url.len() > PUSH_SCHEME.len()
        && url[..PUSH_SCHEME.len()].eq_ignore_ascii_case(PUSH_SCHEME)
        && url[PUSH_SCHEME.len()..].starts_with(':')
}

pub fn push_pair() -> (std::sync::Arc<PushFeed>, PushDemuxer) {
    let (tx, rx) = sync_channel(FEED_DEPTH);
    let feed = std::sync::Arc::new(PushFeed {
        tx,
        state: Mutex::new(PushState {
            waiting_key: true,
            last_video_us: i64::MIN,
            last_audio_us: i64::MIN,
            ..PushState::default()
        }),
        closed: AtomicBool::new(false),
    });
    (
        feed,
        PushDemuxer {
            rx,
            cancelled: None,
            video_track: None,
            audio_track: None,
            ended: false,
        },
    )
}

pub fn opus_head(channels: u8, input_rate: u32) -> Vec<u8> {
    let mut head = Vec::with_capacity(19);
    head.extend_from_slice(b"OpusHead");
    head.push(1);
    head.push(channels);
    head.extend_from_slice(&0u16.to_le_bytes());
    head.extend_from_slice(&input_rate.to_le_bytes());
    head.extend_from_slice(&0i16.to_le_bytes());
    head.push(0);
    head
}

impl PushFeed {
    pub fn close(&self) {
        if !self.closed.swap(true, Ordering::AcqRel) {
            let _ = self.tx.try_send(StreamEvent::Eos(EosReason::Natural));
        }
    }

    pub fn is_closed(&self) -> bool {
        self.closed.load(Ordering::Acquire)
    }

    fn rebase(state: &mut PushState, pts_us: i64) -> Option<i64> {
        let origin = *state.origin_us.get_or_insert(pts_us);
        let rebased = pts_us.checked_sub(origin)?;
        (rebased >= 0).then_some(rebased)
    }

    fn send(&self, event: StreamEvent) -> Result<(), PushOutcome> {
        match self.tx.try_send(event) {
            Ok(()) => Ok(()),
            Err(TrySendError::Full(_)) => Err(PushOutcome::Full),
            Err(TrySendError::Disconnected(_)) => Err(PushOutcome::Closed),
        }
    }

    pub fn push_video(
        &self,
        data: &[u8],
        pts_us: i64,
        key: bool,
        width: u32,
        height: u32,
    ) -> PushOutcome {
        if self.is_closed() {
            return PushOutcome::Closed;
        }
        if data.is_empty() || width == 0 || height == 0 || width > 8192 || height > 8192 {
            return PushOutcome::Refused;
        }
        let mut state = self.state.lock().unwrap_or_else(|e| e.into_inner());
        if state.waiting_key && !key {
            return PushOutcome::NeedKeyframe;
        }
        if let Some(dims) = state.video
            && dims != (width, height)
        {
            return PushOutcome::Refused;
        }
        let Some(rebased) = Self::rebase(&mut state, pts_us) else {
            return PushOutcome::Refused;
        };
        if rebased <= state.last_video_us {
            return if key {
                PushOutcome::Refused
            } else {
                PushOutcome::NeedKeyframe
            };
        }
        if state.video.is_none() {
            let format = Format::Video {
                codec: VideoCodec::H264,
                coded_width: width,
                coded_height: height,
                display_width: width,
                display_height: height,
                codec_private: Vec::new(),
            };
            if let Err(outcome) = self.send(StreamEvent::Format(VIDEO_TRACK, format)) {
                return outcome;
            }
            state.video = Some((width, height));
        }
        let time = MediaTime::from_micros(rebased);
        let au = Au {
            track: VIDEO_TRACK,
            data: data.to_vec(),
            pts: time,
            dts: time,
            key,
            generation: Generation(0),
        };
        match self.send(StreamEvent::Au(au)) {
            Ok(()) => {
                state.waiting_key = false;
                state.last_video_us = rebased;
                PushOutcome::Accepted
            }
            Err(outcome) => {
                state.waiting_key = true;
                outcome
            }
        }
    }

    pub fn push_audio(
        &self,
        data: &[u8],
        pts_us: i64,
        sample_rate: u32,
        channels: u32,
    ) -> PushOutcome {
        if self.is_closed() {
            return PushOutcome::Closed;
        }
        if data.is_empty()
            || !(1..=2).contains(&channels)
            || !matches!(sample_rate, 8000 | 12000 | 16000 | 24000 | 48000)
        {
            return PushOutcome::Refused;
        }
        let mut state = self.state.lock().unwrap_or_else(|e| e.into_inner());
        if let Some(format) = state.audio
            && format != (sample_rate, channels)
        {
            return PushOutcome::Refused;
        }
        let Some(rebased) = Self::rebase(&mut state, pts_us) else {
            return PushOutcome::Refused;
        };
        if rebased <= state.last_audio_us {
            return PushOutcome::Refused;
        }
        if state.audio.is_none() {
            let format = Format::Audio {
                codec: AudioCodec::Opus,
                sample_rate,
                channels,
                codec_private: opus_head(channels as u8, sample_rate),
            };
            if let Err(outcome) = self.send(StreamEvent::Format(AUDIO_TRACK, format)) {
                return outcome;
            }
            state.audio = Some((sample_rate, channels));
        }
        let time = MediaTime::from_micros(rebased);
        let au = Au {
            track: AUDIO_TRACK,
            data: data.to_vec(),
            pts: time,
            dts: time,
            key: true,
            generation: Generation(0),
        };
        match self.send(StreamEvent::Au(au)) {
            Ok(()) => {
                state.last_audio_us = rebased;
                PushOutcome::Accepted
            }
            Err(outcome) => outcome,
        }
    }
}

impl Drop for PushFeed {
    fn drop(&mut self) {
        self.closed.store(true, Ordering::Release);
    }
}

impl PushDemuxer {
    pub fn set_cancel(&mut self, cancelled: Box<dyn Fn() -> bool + Send>) {
        self.cancelled = Some(cancelled);
    }
}

impl Demuxer for PushDemuxer {
    fn next_event(&mut self) -> Result<StreamEvent, DemuxError> {
        if self.ended {
            return Ok(StreamEvent::Eos(EosReason::Natural));
        }
        loop {
            if self.cancelled.as_ref().is_some_and(|c| c()) {
                self.ended = true;
                return Ok(StreamEvent::Eos(EosReason::SourceLost));
            }
            match self.rx.recv_timeout(Duration::from_millis(100)) {
                Ok(event) => {
                    match &event {
                        StreamEvent::Format(track, Format::Video { .. }) => {
                            self.video_track = Some(*track)
                        }
                        StreamEvent::Format(track, Format::Audio { .. }) => {
                            self.audio_track = Some(*track)
                        }
                        StreamEvent::Eos(_) => self.ended = true,
                        _ => {}
                    }
                    return Ok(event);
                }
                Err(RecvTimeoutError::Timeout) => {}
                Err(RecvTimeoutError::Disconnected) => {
                    self.ended = true;
                    return Ok(StreamEvent::Eos(EosReason::Natural));
                }
            }
        }
    }

    fn seek(
        &mut self,
        _target: MediaTime,
        _generation: Generation,
    ) -> Result<MediaTime, DemuxError> {
        Err(DemuxError::Unsupported("seek on a pushed stream"))
    }

    fn duration(&self) -> Option<MediaTime> {
        None
    }

    fn video_track(&self) -> Option<TrackId> {
        self.video_track
    }

    fn audio_track(&self) -> Option<TrackId> {
        self.audio_track
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn scheme_matches_case_insensitively() {
        assert!(is_push_url("basis-push://screen/7"));
        assert!(is_push_url("BASIS-PUSH:x"));
        assert!(!is_push_url("basis-push"));
        assert!(!is_push_url("https://basis-push:1"));
    }

    #[test]
    fn video_waits_for_keyframe_and_announces_once() {
        let (feed, mut demux) = push_pair();
        assert_eq!(
            feed.push_video(&[0, 0, 1, 0x41], 1_000, false, 64, 32),
            PushOutcome::NeedKeyframe
        );
        assert_eq!(
            feed.push_video(&[0, 0, 1, 0x65], 2_000, true, 64, 32),
            PushOutcome::Accepted
        );
        assert_eq!(
            feed.push_video(&[0, 0, 1, 0x41], 3_000, false, 64, 32),
            PushOutcome::Accepted
        );
        assert_eq!(
            feed.push_video(&[0, 0, 1, 0x41], 4_000, false, 128, 32),
            PushOutcome::Refused
        );
        assert!(matches!(
            demux.next_event().unwrap(),
            StreamEvent::Format(
                VIDEO_TRACK,
                Format::Video {
                    coded_width: 64,
                    ..
                }
            )
        ));
        let StreamEvent::Au(first) = demux.next_event().unwrap() else {
            panic!("au expected")
        };
        assert!(first.key);
        assert_eq!(first.pts, MediaTime::ZERO);
        let StreamEvent::Au(second) = demux.next_event().unwrap() else {
            panic!("au expected")
        };
        assert_eq!(second.pts, MediaTime::from_micros(1_000));
        assert_eq!(demux.video_track(), Some(VIDEO_TRACK));
    }

    #[test]
    fn audio_shares_the_origin_and_rejects_regressions() {
        let (feed, mut demux) = push_pair();
        assert_eq!(
            feed.push_audio(&[1, 2], 10_000, 48_000, 2),
            PushOutcome::Accepted
        );
        assert_eq!(
            feed.push_audio(&[1, 2], 10_000, 48_000, 2),
            PushOutcome::Refused
        );
        assert_eq!(
            feed.push_video(&[0, 0, 1, 0x65], 30_000, true, 16, 16),
            PushOutcome::Accepted
        );
        let StreamEvent::Format(_, Format::Audio { codec_private, .. }) =
            demux.next_event().unwrap()
        else {
            panic!("format expected")
        };
        assert_eq!(&codec_private[..8], b"OpusHead");
        assert!(matches!(
            demux.next_event().unwrap(),
            StreamEvent::Au(Au {
                pts: MediaTime::ZERO,
                ..
            })
        ));
        assert!(matches!(
            demux.next_event().unwrap(),
            StreamEvent::Format(VIDEO_TRACK, _)
        ));
        let StreamEvent::Au(video) = demux.next_event().unwrap() else {
            panic!("au expected")
        };
        assert_eq!(video.pts, MediaTime::from_micros(20_000));
    }

    #[test]
    fn close_ends_the_stream() {
        let (feed, mut demux) = push_pair();
        feed.close();
        assert_eq!(feed.push_audio(&[1], 0, 48_000, 1), PushOutcome::Closed);
        assert!(matches!(
            demux.next_event().unwrap(),
            StreamEvent::Eos(EosReason::Natural)
        ));
        assert!(matches!(demux.next_event().unwrap(), StreamEvent::Eos(_)));
    }

    #[test]
    fn a_full_feed_holds_video_until_the_next_keyframe() {
        let (feed, _demux) = push_pair();
        assert_eq!(
            feed.push_video(&[0, 0, 1, 0x65], 0, true, 16, 16),
            PushOutcome::Accepted
        );
        let mut pts = 1;
        while feed.push_video(&[0, 0, 1, 0x41], pts, false, 16, 16) == PushOutcome::Accepted {
            pts += 1;
        }
        assert_eq!(
            feed.push_video(&[0, 0, 1, 0x41], pts + 1, false, 16, 16),
            PushOutcome::NeedKeyframe
        );
    }
}
