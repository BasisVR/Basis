//! `bench`: startup-to-first-frame and seek-to-settled for one lane,
//! repeated and aggregated, so tuning happens against numbers.

use std::process::ExitCode;
use std::sync::atomic::Ordering;
use std::time::{Duration, Instant};

use media_clock::MediaTime;
use media_engine::{OpenRequest, Session, State};

pub struct Options {
    pub url: String,
    pub runs: u32,
    pub seek_to_ms: Option<u64>,
    pub allow_local: bool,
    pub live: bool,
    /// Skip the seek phase (lanes whose demuxer refuses seeks).
    pub no_seek: bool,
    pub timeout_s: u64,
}

struct RunResult {
    first_decode: Option<Duration>,
    ttff: Option<Duration>,
    seek_settle: Option<Duration>,
}

pub fn run(options: &Options) -> ExitCode {
    println!(
        "lane:      {} ({})",
        options.url,
        if options.live { "live" } else { "vod" }
    );
    println!("runs:      {}", options.runs.max(1));

    let mut results = Vec::new();
    for run_index in 0..options.runs.max(1) {
        match bench_run(options) {
            Ok(result) => {
                println!(
                    "run {}:     ttff {} (first decode {}){}",
                    run_index + 1,
                    fmt_opt(result.ttff),
                    fmt_opt(result.first_decode),
                    match result.seek_settle {
                        Some(d) => format!(", seek settled {}", fmt(d)),
                        None => String::new(),
                    }
                );
                results.push(result);
            }
            Err(e) => {
                eprintln!("bench: run {}: {e}", run_index + 1);
                return ExitCode::FAILURE;
            }
        }
    }

    report("ttff", results.iter().filter_map(|r| r.ttff).collect());
    report(
        "decode",
        results.iter().filter_map(|r| r.first_decode).collect(),
    );
    report(
        "seek",
        results.iter().filter_map(|r| r.seek_settle).collect(),
    );
    ExitCode::SUCCESS
}

fn fmt(d: Duration) -> String {
    format!("{} ms", d.as_millis())
}

fn fmt_opt(d: Option<Duration>) -> String {
    d.map(fmt).unwrap_or_else(|| "-".into())
}

fn report(label: &str, mut samples: Vec<Duration>) {
    if samples.is_empty() {
        return;
    }
    samples.sort();
    let median = samples[samples.len() / 2];
    println!(
        "{label}:{}median {} / min {} / max {} over {}",
        " ".repeat(10 - label.len().min(9)),
        fmt(median),
        fmt(samples[0]),
        fmt(*samples.last().expect("non-empty")),
        samples.len()
    );
}

fn bench_run(options: &Options) -> Result<RunResult, String> {
    let mut request = OpenRequest::new(options.url.clone());
    request.allow_local_addresses = options.allow_local;
    if options.live {
        request.liveness = media_engine::SourceLiveness::Live;
    }

    let open_at = Instant::now();
    let mut session = Session::open(request);
    let shared = session.shared().clone();
    let px = session.pipeline().clone();
    let diag = session.diag().clone();
    let timeout = Duration::from_secs(options.timeout_s.max(5));

    let mut result = RunResult {
        first_decode: None,
        ttff: None,
        seek_settle: None,
    };

    // Phase 1: startup. Pull audio at the hardware cadence throughout, as
    // the Unity audio thread does: an unpulled ring changes the clock
    // master and with it what "playing" means.
    let mut audio = AudioPull::default();
    while result.ttff.is_none() {
        if open_at.elapsed() > timeout {
            session.close();
            return Err("timed out waiting for the first present".into());
        }
        let state = shared.state.load(Ordering::Relaxed);
        if state == State::Error as u32 {
            let code = shared.last_error.load(Ordering::Relaxed);
            session.close();
            return Err(format!("session error code {code}"));
        }
        if result.first_decode.is_none() && shared.frames_decoded.load(Ordering::Relaxed) > 0 {
            result.first_decode = Some(open_at.elapsed());
        }
        if presented(&diag) > 0 {
            result.ttff = Some(open_at.elapsed());
            break;
        }
        audio.pull(&shared, &px);
        // Audio-only lanes never present: their "first frame" is the
        // first audible audio. Playing is only reachable without video
        // via the audio thread, so this cannot fire early on A/V lanes.
        // Live lanes are audio-leading, so their budget is time-to-sound
        // and it is measured the same way.
        if state == State::Playing as u32
            && audio.frames > 0
            && (options.live || !px.video_active.load(Ordering::Relaxed))
        {
            result.ttff = Some(open_at.elapsed());
            break;
        }
        std::thread::sleep(Duration::from_millis(1));
    }

    // Phase 2: seek-to-settled (VOD lanes). Let playback run a moment so
    // the seek starts from a settled pipeline, then measure to the first
    // post-seek presentation on the new timeline.
    if !options.live && !options.no_seek {
        let settle_until = Instant::now() + Duration::from_millis(500);
        while Instant::now() < settle_until {
            audio.pull(&shared, &px);
            std::thread::sleep(Duration::from_millis(1));
        }
        let duration_us = shared.duration_us.load(Ordering::Relaxed);
        let target_us = match options.seek_to_ms {
            Some(ms) => Some(ms as i64 * 1000),
            None if duration_us > 0 => Some(duration_us * 3 / 4),
            None => None,
        };
        if let Some(target_us) = target_us {
            let generation_before = {
                let bank = px.bank.bank.lock().expect("bank lock");
                bank.generation()
            };
            let seek_at = Instant::now();
            session.seek(MediaTime::from_micros(target_us));

            // The demux thread advances the generation when it executes the
            // seek; presents before that are stale pre-flush frames.
            let mut presented_at_flush = None;
            loop {
                if seek_at.elapsed() > timeout {
                    session.close();
                    return Err("timed out waiting for the seek to settle".into());
                }
                let state = shared.state.load(Ordering::Relaxed);
                if state == State::Error as u32 {
                    let code = shared.last_error.load(Ordering::Relaxed);
                    session.close();
                    return Err(format!("session error code {code} during seek"));
                }
                if presented_at_flush.is_none() {
                    let generation = {
                        let bank = px.bank.bank.lock().expect("bank lock");
                        bank.generation()
                    };
                    if generation != generation_before {
                        presented_at_flush = Some(presented(&diag));
                    }
                } else if let Some(baseline) = presented_at_flush {
                    // Settled: a fresh present from the new timeline. The
                    // landed keyframe sits at most a GOP before the target,
                    // so the position gate separates it from stale frames.
                    // An audio-only lane never presents (MP4 audio seeks
                    // like any other MP4), so there the position on the new
                    // timeline is the whole signal.
                    let position = shared.position_us.load(Ordering::Relaxed);
                    let progressed = if px.video_active.load(Ordering::Relaxed) {
                        presented(&diag) > baseline
                    } else {
                        true
                    };
                    if progressed && position >= target_us - 2_500_000 {
                        result.seek_settle = Some(seek_at.elapsed());
                        break;
                    }
                }
                audio.pull(&shared, &px);
                std::thread::sleep(Duration::from_millis(1));
            }
        }
    }

    session.close();
    Ok(result)
}

fn presented(diag: &media_diag::SessionDiag) -> u64 {
    diag.snapshot()[media_diag::Stage::Present as usize].out_count
}

/// Hardware-cadence audio pull (the play/impair budget discipline: sized
/// by the session's real channel count, anchored at first pull).
#[derive(Default)]
struct AudioPull {
    epoch: Option<Instant>,
    frames: u64,
    buf: Vec<f32>,
}

impl AudioPull {
    fn pull(&mut self, shared: &media_engine::SessionShared, px: &media_engine::PipelineShared) {
        let rate = shared.audio_rate.load(Ordering::Relaxed);
        let channels = shared.audio_channels.load(Ordering::Relaxed).max(1);
        let state = shared.state.load(Ordering::Relaxed);
        if rate == 0 || state != State::Playing as u32 {
            return;
        }
        self.buf.resize(2048, 0.0);
        let epoch = *self.epoch.get_or_insert_with(Instant::now);
        let budget = epoch.elapsed().as_micros() as u64 * u64::from(rate) / 1_000_000 - self.frames;
        if budget as usize >= self.buf.len() / channels as usize {
            self.frames += Session::read_audio(px, &mut self.buf) as u64;
        }
    }
}
