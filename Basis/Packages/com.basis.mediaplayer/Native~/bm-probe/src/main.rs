//! `bm-probe`: runs the media engine from the command line, without Unity
//! or the C ABI. Decoded frames are hashed rather than shown.

#![forbid(unsafe_code)]

mod bench;
mod conformance;
mod impair;
mod play;
mod probe;

use std::process::ExitCode;

use clap::{Parser, Subcommand, ValueEnum};

/// The session's decode preference, as the open request carries it.
#[derive(Clone, Copy, ValueEnum)]
enum Decode {
    Fallback,
    Hardware,
    Software,
}

impl From<Decode> for media_engine::DecodePreference {
    fn from(decode: Decode) -> Self {
        match decode {
            Decode::Fallback => Self::HardwareWithFallback,
            Decode::Hardware => Self::HardwareOnly,
            Decode::Software => Self::SoftwareOnly,
        }
    }
}

#[derive(Parser)]
#[command(
    name = "bm-probe",
    about = "Run the Basis media engine without Unity: probe, play, benchmark and test sources"
)]
struct Cli {
    #[command(subcommand)]
    command: Command,
}

#[derive(Subcommand)]
enum Command {
    /// Open a file or URL and report its container and codecs, and with
    /// `--decode` the time to the first frame.
    Probe {
        /// A local file or an http(s) URL, in any container the engine reads.
        url: String,
        /// Also decode up to the first frame and time it (Windows only).
        #[arg(long)]
        decode: bool,
        /// Allow sources on private or loopback addresses, such as a test
        /// server on this machine. They are refused by default.
        #[arg(long)]
        allow_local: bool,
        /// Write the embedded cover art, as the container stores it, to
        /// this path.
        #[arg(long)]
        artwork_out: Option<std::path::PathBuf>,
    },
    /// Play a source through the whole engine for a set time, then print a
    /// summary. Optionally write the diagnostics capture as CSV.
    Play {
        /// Anything the player opens: a local file, http(s) (including HLS),
        /// rtsp:// (rtspt:// for RTSP over TCP), rist://, or whep:// and
        /// wheps:// for WHEP.
        url: String,
        /// Seconds to run.
        #[arg(long, default_value_t = 10)]
        duration: u64,
        /// Write the diagnostics capture here, one row per sample interval
        /// (columns described in DIAGNOSTICS.md).
        #[arg(long)]
        csv: Option<std::path::PathBuf>,
        /// Capture sample interval, milliseconds.
        #[arg(long, default_value_t = 100)]
        interval_ms: u64,
        /// Write the decoded audio here as raw interleaved 32-bit float.
        #[arg(long)]
        audio_out: Option<std::path::PathBuf>,
        /// Permit sources that resolve to private/loopback addresses.
        #[arg(long)]
        allow_local: bool,
        /// Treat the source as live. By default the engine decides from the
        /// source itself.
        #[arg(long)]
        live: bool,
        /// Which of the container's audio tracks to bind, by index into
        /// the offered list. Out of range falls back to the first.
        #[arg(long, default_value_t = 0)]
        audio_track: usize,
        /// Seek here, in ms, once playback has run for two seconds. The
        /// capture and the summary then cover the landing as well.
        #[arg(long)]
        seek_to_ms: Option<u64>,
        /// A separate audio-only source to play alongside `url`, which is
        /// then treated as video-only. This is how YouTube and other
        /// adaptive sources serve their higher qualities. Files and
        /// on-demand http(s) only.
        #[arg(long)]
        audio_url: Option<String>,
        /// Which decode routes the session may take: hardware with a
        /// software fallback, hardware only, or software only.
        #[arg(long, value_enum, default_value_t = Decode::Fallback)]
        decode: Decode,
    },
    /// Time how long a source takes to show its first frame and to settle
    /// after a seek, averaged over several runs.
    Bench {
        /// Anything the player opens, as for `play`.
        url: String,
        /// Runs to aggregate over.
        #[arg(long, default_value_t = 3)]
        runs: u32,
        /// Seek target in ms (default: 3/4 of the reported duration; the
        /// seek phase is skipped when neither is available).
        #[arg(long)]
        seek_to_ms: Option<u64>,
        /// Permit sources that resolve to private/loopback addresses.
        #[arg(long)]
        allow_local: bool,
        /// Open as a live source (no seek phase).
        #[arg(long)]
        live: bool,
        /// Skip the seek phase, for sources that cannot seek (such as raw
        /// MPEG-TS).
        #[arg(long)]
        no_seek: bool,
        /// Per-phase timeout, seconds.
        #[arg(long, default_value_t = 30)]
        timeout: u64,
    },
    /// Print what this machine can decode, as the JSON the engine reports
    /// through `bm_capabilities`.
    Caps {
        /// Print it on one line, exactly as the ABI returns it.
        #[arg(long)]
        compact: bool,
    },
    /// Check that a fixture demuxes to exactly what ffprobe reads from it:
    /// codec, packet count, timestamps and payload MD5. Needs ffprobe on
    /// PATH; the gate runs it over `fixtures/`.
    Conformance {
        /// A fixture file, or a folder of them.
        fixture: std::path::PathBuf,
    },
    /// Play a source through a recorded bad-network profile and check that
    /// playback survives. For a local file it must also stall no more than
    /// the buffer depth predicts. Prints IMPAIR PASS or IMPAIR FAIL.
    Impair {
        /// A local .ts file (played back at 1x, like a live stream) or a
        /// live http(s) URL.
        url: String,
        /// One of the recorded profiles: ts-clean, ts-rtt600-loss0,
        /// ts-rtt300-loss005, rtspt-rtt300-loss005 or ts-rtt300-loss05
        /// (transport, added round trip in ms, packet loss: loss005 is
        /// 0.05%).
        #[arg(long)]
        profile: String,
        /// Seconds to run. Defaults to the length of the recorded profile.
        #[arg(long)]
        duration: Option<u64>,
        /// Buffer depth in ms. Defaults to Auto.
        #[arg(long)]
        depth_ms: Option<u32>,
        /// Permit sources that resolve to private/loopback addresses.
        #[arg(long)]
        allow_local: bool,
        /// Write the diagnostics capture here.
        #[arg(long)]
        csv: Option<std::path::PathBuf>,
    },
}

fn main() -> ExitCode {
    let cli = Cli::parse();
    match cli.command {
        Command::Probe {
            url,
            decode,
            allow_local,
            artwork_out,
        } => probe::run(&url, decode, allow_local, artwork_out.as_deref()),
        Command::Play {
            url,
            duration,
            csv,
            interval_ms,
            audio_out,
            allow_local,
            live,
            audio_track,
            seek_to_ms,
            audio_url,
            decode,
        } => play::run(&play::Options {
            url,
            duration,
            csv,
            interval_ms,
            audio_out,
            allow_local,
            live,
            audio_track,
            seek_to_ms,
            audio_url,
            decode: decode.into(),
        }),
        Command::Bench {
            url,
            runs,
            seek_to_ms,
            allow_local,
            live,
            no_seek,
            timeout,
        } => bench::run(&bench::Options {
            url,
            runs,
            seek_to_ms,
            allow_local,
            live,
            no_seek,
            timeout_s: timeout,
        }),
        Command::Caps { compact } => {
            let caps = media_engine::capabilities();
            if compact {
                println!("{}", caps.to_json());
            } else {
                println!(
                    "{}",
                    serde_json::to_string_pretty(&caps).expect("capability set serialises")
                );
            }
            ExitCode::SUCCESS
        }
        Command::Conformance { fixture } => conformance::run(&fixture),
        Command::Impair {
            url,
            profile,
            duration,
            depth_ms,
            allow_local,
            csv,
        } => impair::run(&impair::Options {
            url,
            profile,
            duration,
            depth_ms,
            allow_local,
            csv,
        }),
    }
}

/// Open a sequential live byte source for a URL (the impair harness wraps
/// it; the engine builds its own for play).
pub(crate) fn open_live_source(
    url: &str,
    allow_local: bool,
) -> Result<Box<dyn media_demux::ByteSource>, String> {
    let gate: std::sync::Arc<dyn media_io::AddressGate> = if allow_local {
        std::sync::Arc::new(media_io::AllowAllGate)
    } else {
        std::sync::Arc::new(media_io::PublicAddressGate)
    };
    media_io::HttpLiveSource::open(
        url,
        media_io::IoLimits::default(),
        gate,
        media_io::CancelToken::new(),
    )
    .map(|s| Box::new(s) as Box<dyn media_demux::ByteSource>)
    .map_err(|e| e.to_string())
}

/// Open a byte source for a path-or-URL argument.
pub(crate) fn open_source(
    url: &str,
    allow_local: bool,
) -> Result<Box<dyn media_demux::ByteSource>, String> {
    if url.starts_with("http://") || url.starts_with("https://") {
        let gate: std::sync::Arc<dyn media_io::AddressGate> = if allow_local {
            std::sync::Arc::new(media_io::AllowAllGate)
        } else {
            std::sync::Arc::new(media_io::PublicAddressGate)
        };
        media_io::HttpSource::open(
            url,
            media_io::IoLimits::default(),
            gate,
            media_io::CancelToken::new(),
        )
        .map(|s| Box::new(s) as Box<dyn media_demux::ByteSource>)
        .map_err(|e| e.to_string())
    } else {
        media_io::FileSource::open(std::path::Path::new(url))
            .map(|s| Box::new(s) as Box<dyn media_demux::ByteSource>)
            .map_err(|e| e.to_string())
    }
}

#[cfg(windows)]
pub(crate) fn fnv1a(data: &[u8]) -> u64 {
    let mut hash = 0xcbf2_9ce4_8422_2325u64;
    for &b in data {
        hash ^= b as u64;
        hash = hash.wrapping_mul(0x0000_0100_0000_01b3);
    }
    hash
}
