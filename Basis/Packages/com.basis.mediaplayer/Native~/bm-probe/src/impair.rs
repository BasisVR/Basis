//! `impair`: run a live source through a deterministic impairment schedule
//! and grade the Bank against the analytic stall model. A profile replays
//! the delivery gaps recorded from a VRCDN stream (the `media-testkit`
//! fixtures) on top of any lane, either a local TS file paced to 1x or a
//! real live URL. The run passes when the session survives and the measured
//! stall stays within the analytic model's residual for the configured
//! depth.

use std::process::ExitCode;
use std::sync::Arc;
use std::sync::atomic::Ordering;
use std::time::{Duration, Instant};

use media_clock::{Generation, MediaTime};
use media_demux::{ByteSource, ContainerKind, DemuxLimits};
use media_engine::{OpenRequest, Session, SourceLiveness, State};
use media_testkit::{GapCapture, ImpairProfile, ImpairedSource, PacedSource, RealClock, WallClock};

pub struct Options {
    pub url: String,
    pub profile: String,
    pub duration: Option<u64>,
    pub depth_ms: Option<u32>,
    pub allow_local: bool,
    pub csv: Option<std::path::PathBuf>,
}

pub fn run(options: &Options) -> ExitCode {
    let capture = match find_capture(&options.profile) {
        Some(capture) => capture,
        None => {
            eprintln!(
                "impair: unknown profile {:?}; available: {}",
                options.profile,
                GapCapture::all()
                    .iter()
                    .map(|c| c.name)
                    .collect::<Vec<_>>()
                    .join(", ")
            );
            return ExitCode::FAILURE;
        }
    };
    // One frame interval of separation at the captures' 30 fps.
    let profile = ImpairProfile::from_capture(&capture, MediaTime::from_micros(33_366));
    let grading = profile.clone();
    let clock: Arc<dyn WallClock> = Arc::new(RealClock::new());

    // The underlying lane: a live URL is already paced at 1x by reality; a
    // local TS file is paced here so the schedule means the same thing.
    let inner: Box<dyn ByteSource> =
        if options.url.starts_with("http://") || options.url.starts_with("https://") {
            match crate::open_live_source(&options.url, options.allow_local) {
                Ok(source) => source,
                Err(e) => {
                    eprintln!("impair: {e}");
                    return ExitCode::FAILURE;
                }
            }
        } else {
            let bytes = match std::fs::read(&options.url) {
                Ok(bytes) => bytes,
                Err(e) => {
                    eprintln!("impair: {}: {e}", options.url);
                    return ExitCode::FAILURE;
                }
            };
            if media_demux::sniff_container(&bytes[..bytes.len().min(1024)])
                != Some(ContainerKind::MpegTs)
            {
                eprintln!(
                    "impair: {} is not a TS file (the live carrier)",
                    options.url
                );
                return ExitCode::FAILURE;
            }
            let Some(rate) = ts_byte_rate(&bytes) else {
                eprintln!("impair: could not derive a byte rate from {}", options.url);
                return ExitCode::FAILURE;
            };
            println!("pacing {} at {rate} B/s (1x)", options.url);
            Box::new(PacedSource::new(
                Box::new(media_demux::MemSource(bytes)),
                rate,
                Arc::clone(&clock),
            ))
        };

    let impaired = ImpairedSource::new(inner, profile, Arc::clone(&clock));

    let mut request = OpenRequest::new(options.url.clone());
    request.liveness = SourceLiveness::Live;
    request.allow_local_addresses = options.allow_local;
    request.buffer_depth_ms = options.depth_ms;
    let mut session = Session::open_with_source(request, Box::new(impaired));
    let shared = session.shared().clone();
    let px = session.pipeline().clone();

    let window = Duration::from_secs(
        options
            .duration
            .unwrap_or((capture.duration.as_micros() / 1_000_000) as u64),
    );
    println!(
        "profile {} ({}): {} gaps over {}s analysed; running {}s at depth {}",
        capture.name,
        capture.impairment,
        capture.gaps.len(),
        capture.duration.as_micros() / 1_000_000,
        window.as_secs(),
        options
            .depth_ms
            .map(|ms| format!("{ms} ms"))
            .unwrap_or_else(|| "Auto".into()),
    );

    // Synthetic audio pull at the hardware cadence, as in `play`: a frame
    // budget anchored at the Playing transition and sized by the session's
    // real rate and channel count. Pulling too much or too little races the
    // audio-master clock and manufactures pipeline backpressure.
    let start = Instant::now();
    let mut audio_buf = vec![0f32; 2048];
    let mut audio_frames = 0u64;
    let mut audio_epoch: Option<Instant> = None;
    let mut recorder = media_diag::CaptureRecorder::default();
    let mut last_sample = Instant::now();
    let mut errored = false;
    while start.elapsed() < window {
        let state = shared.state.load(Ordering::Relaxed);
        if state == State::Error as u32 {
            errored = true;
            break;
        }
        if state == State::Ended as u32 {
            break;
        }
        let rate = shared.audio_rate.load(Ordering::Relaxed);
        let channels = shared.audio_channels.load(Ordering::Relaxed).max(1);
        if rate > 0 && state == State::Playing as u32 {
            let epoch = *audio_epoch.get_or_insert_with(Instant::now);
            let budget =
                epoch.elapsed().as_micros() as u64 * u64::from(rate) / 1_000_000 - audio_frames;
            if budget as usize >= audio_buf.len() / channels as usize {
                audio_frames += Session::read_audio(&px, &mut audio_buf) as u64;
            }
        }
        if last_sample.elapsed() >= Duration::from_millis(100) {
            recorder.sample(px.wall.now(), session.diag().as_ref());
            last_sample = Instant::now();
        }
        std::thread::sleep(Duration::from_millis(2));
    }
    // A capture that did not land fails the run rather than logging it:
    // the exit status is what a harness reads to decide whether there is
    // an artefact to collect.
    let captured = match &options.csv {
        None => true,
        Some(path) => match std::fs::File::create(path)
            .and_then(|file| recorder.write_csv(std::io::BufWriter::new(file)))
        {
            Ok(()) => true,
            Err(e) => {
                eprintln!("impair: csv: {e}");
                false
            }
        },
    };

    // Grade against the analytic model at the *achieved* total depth
    // (target lag + decoder cushion, the analytic table's depth axis),
    // over the gaps that fell inside the run window.
    let (metrics, cushion) = {
        let bank = px.bank.bank.lock().expect("bank lock");
        (bank.metrics(), bank.config().decoder_cushion)
    };
    let elapsed = start.elapsed();
    let target = metrics.target_lag;
    let depth = target + cushion;
    let window = MediaTime::from_micros(elapsed.as_micros() as i64).min(capture.duration);
    let analytic = grading.analytic_stall_fraction(depth, window);
    let measured = metrics.stall_total.as_micros() as f64 / elapsed.as_micros() as f64;
    let decoded = shared.frames_decoded.load(Ordering::Relaxed);
    let snapshot = session.diag().snapshot();
    let presented = snapshot[media_diag::Stage::Present as usize].out_count;

    println!(
        "video:     {decoded} decoded, {presented} presented\n\
         bank:      lag {} / target {}, {} reanchors, stall total {}\n\
         stall:     measured {:.3}% vs analytic {:.3}% at target depth",
        metrics.lag,
        target,
        metrics.reanchors,
        metrics.stall_total,
        measured * 100.0,
        analytic * 100.0,
    );

    // Pass: session alive and presentation genuinely flowed (at least
    // ~10 fps averaged over the non-stalled, post-startup window, so a total
    // starve cannot pass vacuously). On the deterministic file lane the
    // measured stall must also stay within the model's residual plus
    // decode/present noise. Over a real network the model's instant-recovery
    // assumption does not hold (TCP slow-start after each idle window,
    // bounded socket buffers), so there the comparison is only reported and
    // survival is the gate.
    let flowing_secs =
        (elapsed.as_secs_f64() * (1.0 - analytic.min(1.0)) - depth.as_micros() as f64 / 1e6 - 3.0)
            .max(0.0);
    let min_presented = (flowing_secs * 10.0) as u64;
    let margin = 0.005 + analytic * 0.5;
    let deterministic_lane =
        !options.url.starts_with("http://") && !options.url.starts_with("https://");
    let model_ok = !deterministic_lane || measured <= analytic + margin;
    let ok = !errored && decoded > 0 && presented >= min_presented && model_ok && captured;
    println!("{}", if ok { "IMPAIR PASS" } else { "IMPAIR FAIL" });
    session.close();
    if ok {
        ExitCode::SUCCESS
    } else {
        ExitCode::FAILURE
    }
}

fn find_capture(name: &str) -> Option<GapCapture> {
    GapCapture::all().into_iter().find(|c| c.name == name)
}

/// Total bytes over the pts span of a TS byte blob -> bytes per second.
fn ts_byte_rate(bytes: &[u8]) -> Option<u64> {
    let mut demux = media_demux::TsDemuxer::open(
        Box::new(media_demux::MemSource(bytes.to_vec())),
        DemuxLimits::default(),
        Generation(0),
    )
    .ok()?;
    let mut first: Option<MediaTime> = None;
    let mut last: Option<MediaTime> = None;
    loop {
        use media_demux::{Demuxer as _, StreamEvent};
        match demux.next_event() {
            Ok(StreamEvent::Au(au)) => {
                first.get_or_insert(au.pts);
                last = Some(au.pts);
            }
            Ok(StreamEvent::Eos(_)) | Err(_) => break,
            Ok(_) => {}
        }
    }
    let span = (last? - first?).as_micros();
    if span <= 0 {
        return None;
    }
    Some((bytes.len() as u128 * 1_000_000 / span as u128) as u64)
}
