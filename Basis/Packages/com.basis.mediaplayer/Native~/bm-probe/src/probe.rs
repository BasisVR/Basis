//! `probe`: open a source, report container/codec/track facts and (with
//! `--decode`) first-frame timing through the platform decoder.

use std::process::ExitCode;
use std::time::Instant;

use media_clock::Generation;
#[cfg(windows)]
use media_demux::Demuxer;
use media_demux::{AudioCodec, ContainerKind, DemuxLimits, Format, StreamEvent, VideoCodec};

pub fn run(
    url: &str,
    decode: bool,
    allow_local: bool,
    artwork_out: Option<&std::path::Path>,
) -> ExitCode {
    let opened = Instant::now();
    let mut source = match crate::open_source(url, allow_local) {
        Ok(source) => source,
        Err(e) => {
            eprintln!("probe: {url}: {e}");
            return ExitCode::FAILURE;
        }
    };
    let kind = {
        let mut head = [0u8; 1024];
        let n = source.read_at(0, &mut head).unwrap_or(0);
        media_demux::sniff_container(&head[..n])
    };
    let mut demux = match media_demux::open_auto(source, DemuxLimits::default(), Generation(0)) {
        Ok(demux) => demux,
        Err(e) => {
            eprintln!("probe: {url}: {e}");
            return ExitCode::FAILURE;
        }
    };
    let parse_ms = opened.elapsed().as_secs_f64() * 1e3;

    println!("source:        {url}");
    println!(
        "container:     {}",
        match kind {
            Some(ContainerKind::Mp4) => "mp4",
            Some(ContainerKind::MpegTs) => "mpeg-ts",
            Some(ContainerKind::Mkv) => "matroska",
            Some(ContainerKind::Flac) => "flac",
            Some(ContainerKind::Ogg) => "ogg",
            Some(ContainerKind::Mp3) => "mp3",
            Some(ContainerKind::Adts) => "adts",
            Some(ContainerKind::Wav) => "wav",
            None => "unknown",
        }
    );
    for note in demux.take_notes() {
        println!("note:          {note}");
    }
    for refusal in demux.take_refusals() {
        println!("refused:       {refusal}");
    }
    if let Some(duration) = demux.duration() {
        println!("duration:      {:.3}s", duration.as_micros() as f64 / 1e6);
    }

    // Walk the format events (they front the stream), keeping the first AU
    // for the decode phase; dropping it would cost a whole GOP.
    let mut video: Option<(VideoCodec, u32, u32)> = None;
    let mut audio: Option<(AudioCodec, u32, u32)> = None;
    let mut first_au: Option<media_demux::Au> = None;
    loop {
        match demux.next_event() {
            Ok(StreamEvent::Format(
                _,
                Format::Video {
                    codec,
                    display_width,
                    display_height,
                    ..
                },
            )) => video = Some((codec, display_width, display_height)),
            Ok(StreamEvent::Format(
                _,
                Format::Audio {
                    codec,
                    sample_rate,
                    channels,
                    ..
                },
            )) => audio = Some((codec, sample_rate, channels)),
            Ok(StreamEvent::Au(au)) => {
                first_au = Some(au);
                break;
            }
            Ok(_) | Err(_) => break,
        }
    }
    if let Some((codec, width, height)) = video {
        println!("video:         {codec:?} {width}x{height}");
    }
    if let Some((codec, rate, channels)) = audio {
        println!("audio:         {codec:?} {rate} Hz, {channels} ch");
    }
    match demux.artwork() {
        Some(art) => {
            println!("artwork:       {}, {} bytes", art.mime, art.data.len());
            if let Some(path) = artwork_out
                && let Err(e) = std::fs::write(path, &art.data)
            {
                eprintln!("probe: {}: {e}", path.display());
                return ExitCode::FAILURE;
            }
        }
        None if artwork_out.is_some() => {
            eprintln!("probe: {url}: no cover art to write");
            return ExitCode::FAILURE;
        }
        None => {}
    }
    println!("parse time:    {parse_ms:.1}ms");

    if decode {
        #[cfg(windows)]
        return decode_first_frame(demux.as_mut(), first_au, opened);
        #[cfg(not(windows))]
        {
            let _ = first_au;
            eprintln!("probe: --decode needs a platform decode adapter (Windows only for now)");
            return ExitCode::FAILURE;
        }
    }
    ExitCode::SUCCESS
}

/// The null sink: decode until the first frame lands, report timing and a
/// content hash instead of presenting.
#[cfg(windows)]
fn decode_first_frame(
    demux: &mut dyn Demuxer,
    first_au: Option<media_demux::Au>,
    opened: Instant,
) -> ExitCode {
    use media_decode::{SubmitOutcome, VideoDecoder};

    let mut decoder = match decode_mf::H264Decoder::new(false) {
        Ok(d) => d,
        Err(e) => {
            eprintln!("probe: decoder init: {e}");
            return ExitCode::FAILURE;
        }
    };
    let mut pending: Option<media_demux::Au> =
        first_au.filter(|au| au.data.starts_with(&[0, 0, 0, 1]));
    loop {
        match decoder.try_output() {
            Ok(Some(frame)) => {
                let ttff_ms = opened.elapsed().as_secs_f64() * 1e3;
                println!("first frame:   {ttff_ms:.1}ms from open");
                println!("coded size:    {}x{}", frame.width(), frame.height());
                println!("frame pts:     {}us", frame.pts_us());
                let nv12 = frame.as_nv12().expect("MF frames are NV12");
                println!("nv12 fnv1a:    {:016x}", crate::fnv1a(&nv12.data));
                return ExitCode::SUCCESS;
            }
            Ok(None) => {}
            Err(e) => {
                eprintln!("probe: decode: {e}");
                return ExitCode::FAILURE;
            }
        }
        let au = match pending.take() {
            Some(au) => au,
            None => loop {
                match demux.next_event() {
                    Ok(StreamEvent::Au(au)) if au.data.starts_with(&[0, 0, 0, 1]) => break au,
                    Ok(StreamEvent::Eos(_)) => {
                        eprintln!("probe: stream ended before a frame decoded");
                        return ExitCode::FAILURE;
                    }
                    Ok(_) => {}
                    Err(e) => {
                        eprintln!("probe: demux: {e}");
                        return ExitCode::FAILURE;
                    }
                }
            },
        };
        match decoder.submit(&au.data, au.pts.as_micros()) {
            Ok(SubmitOutcome::Accepted) => {}
            Ok(SubmitOutcome::NotAccepting) => pending = Some(au),
            Err(e) => {
                eprintln!("probe: submit: {e}");
                return ExitCode::FAILURE;
            }
        }
    }
}
