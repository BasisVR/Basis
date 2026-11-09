//! Fixture rows for the FFmpeg adapter, fed the AUs the real demuxer
//! produces. A host without a loadable FFmpeg skips every row (on Windows,
//! put a shared build's bin directory on PATH to run them).

use decode_ffmpeg::{FfAudioDecoder, FfCodec, FfVideoDecoder, has_decoder, library};
use media_clock::Generation;
use media_decode::{AudioDecoder, PcmChunk, SubmitOutcome, VideoDecoder, VideoFrame, YuvMatrix};
use media_demux::{
    AudioCodec, DemuxLimits, Format, MemSource, StreamEvent, TrackId, VideoCodec, open_auto,
};

type Track = (TrackId, AudioCodec, u32, u32, Vec<u8>);

#[derive(Default)]
struct Demuxed {
    video: Option<(TrackId, VideoCodec)>,
    audio: Option<Track>,
    video_aus: Vec<(Vec<u8>, i64)>,
    audio_aus: Vec<(Vec<u8>, i64)>,
}

fn demux(name: &str) -> Demuxed {
    let path = concat!(env!("CARGO_MANIFEST_DIR"), "/../../fixtures/");
    let bytes = std::fs::read(format!("{path}{name}")).expect("fixture readable");
    let mut demuxer = open_auto(
        Box::new(MemSource(bytes)),
        DemuxLimits::default(),
        Generation::default(),
    )
    .expect("open");
    let mut out = Demuxed::default();
    loop {
        match demuxer.next_event().expect("event") {
            StreamEvent::Format(track, Format::Video { codec, .. }) => {
                out.video = Some((track, codec));
            }
            StreamEvent::Format(
                track,
                Format::Audio {
                    codec,
                    sample_rate,
                    channels,
                    codec_private,
                },
            ) => out.audio = Some((track, codec, sample_rate, channels, codec_private)),
            StreamEvent::Au(au) => {
                let entry = (au.data, au.pts.as_micros());
                if out.video.as_ref().is_some_and(|(t, _)| *t == au.track) {
                    out.video_aus.push(entry);
                } else {
                    out.audio_aus.push(entry);
                }
            }
            StreamEvent::Eos(_) => break,
            _ => {}
        }
    }
    out
}

fn ready(codec: FfCodec) -> bool {
    match library() {
        Ok(label) => {
            eprintln!("using {label}");
            has_decoder(codec)
        }
        Err(e) => {
            eprintln!("skipped: {}", e.0);
            false
        }
    }
}

fn decode_video(decoder: &mut dyn VideoDecoder, aus: &[(Vec<u8>, i64)]) -> Vec<VideoFrame> {
    let mut frames = Vec::new();
    for (au, pts) in aus {
        loop {
            match decoder.submit(au, *pts).expect("submit") {
                SubmitOutcome::Accepted => break,
                SubmitOutcome::NotAccepting => {
                    while let Some(f) = decoder.try_output().expect("output") {
                        frames.push(f);
                    }
                }
            }
        }
        while let Some(f) = decoder.try_output().expect("output") {
            frames.push(f);
        }
    }
    decoder.begin_drain().expect("drain");
    while let Some(f) = decoder.try_output().expect("output") {
        frames.push(f);
    }
    frames
}

fn decode_audio(decoder: &mut dyn AudioDecoder, aus: &[(Vec<u8>, i64)]) -> Vec<PcmChunk> {
    let mut chunks = Vec::new();
    for (au, pts) in aus {
        loop {
            match decoder.submit(au, *pts).expect("submit") {
                SubmitOutcome::Accepted => break,
                SubmitOutcome::NotAccepting => {
                    while let Some(c) = decoder.try_output().expect("output") {
                        chunks.push(c);
                    }
                }
            }
        }
    }
    decoder.begin_drain().expect("drain");
    while let Some(c) = decoder.try_output().expect("output") {
        chunks.push(c);
    }
    chunks
}

fn check_audio(chunks: &[PcmChunk], rate: u32, channels: u32, min_frames: usize) {
    let mut frames = 0;
    let mut peak = 0f32;
    for c in chunks {
        assert_eq!((c.sample_rate, c.channels), (rate, channels));
        assert_eq!(c.data.len() % channels as usize, 0);
        frames += c.data.len() / channels as usize;
        for s in &c.data {
            assert!(s.is_finite());
            peak = peak.max(s.abs());
        }
    }
    assert!(
        frames >= min_frames,
        "decoded {frames} frames, wanted {min_frames}"
    );
    assert!(peak > 0.01 && peak <= 1.5, "peak {peak}");
}

#[test]
fn h264_fixture_decodes_every_frame_in_order() {
    if !ready(FfCodec::H264) {
        return;
    }
    let demuxed = demux("h264-aac-640x360-30fps.mp4");
    assert!(matches!(demuxed.video, Some((_, VideoCodec::H264))));
    let mut decoder = FfVideoDecoder::new(FfCodec::H264, false).expect("decoder");
    let frames = decode_video(&mut decoder, &demuxed.video_aus);
    assert_eq!(frames.len(), demuxed.video_aus.len());
    let mut last = i64::MIN;
    for f in &frames {
        let nv12 = f.as_nv12().expect("cpu frame");
        assert_eq!((nv12.width, nv12.height), (640, 360));
        assert_eq!(nv12.data.len(), 640 * 360 * 3 / 2);
        assert!(nv12.pts_us > last, "pts {} after {last}", nv12.pts_us);
        last = nv12.pts_us;
    }
    let mid = frames[frames.len() / 2].as_nv12().unwrap();
    let (min, max) = mid.data[..640 * 360]
        .iter()
        .fold((255u8, 0u8), |(a, b), &v| (a.min(v), b.max(v)));
    assert!(max - min > 32, "luma {min}..{max} looks blank");
    assert_ne!(mid.color.matrix, YuvMatrix::Bt2020);
}

#[test]
fn h264_live_decoder_survives_reset_and_redecodes() {
    if !ready(FfCodec::H264) {
        return;
    }
    let demuxed = demux("h264-640x360-30fps.mp4");
    let mut decoder = FfVideoDecoder::new(FfCodec::H264, true).expect("decoder");
    let first = decode_video(&mut decoder, &demuxed.video_aus[..30]);
    assert!(first.len() >= 28, "{}", first.len());
    decoder.reset().expect("reset");
    let again = decode_video(&mut decoder, &demuxed.video_aus[..30]);
    assert!(again.len() >= 28, "{}", again.len());
}

#[test]
fn garbage_input_is_an_error_or_nothing_never_a_crash() {
    if !ready(FfCodec::H264) {
        return;
    }
    let mut decoder = FfVideoDecoder::new(FfCodec::H264, true).expect("decoder");
    let mut junk = vec![0u8, 0, 0, 1, 0x65];
    junk.extend((0..4096u32).map(|i| (i.wrapping_mul(2_654_435_761) >> 13) as u8));
    for i in 0..20 {
        let _ = decoder.submit(&junk, i * 33_333);
        while let Ok(Some(_)) = decoder.try_output() {}
    }
}

#[test]
fn aac_fixture_decodes_stereo_pcm() {
    if !ready(FfCodec::Aac) {
        return;
    }
    let demuxed = demux("aac-48k-stereo.m4a");
    let (_, codec, rate, channels, asc) = demuxed.audio.clone().expect("audio track");
    assert_eq!(codec, AudioCodec::Aac);
    let mut decoder = FfAudioDecoder::new(FfCodec::Aac, rate, channels, &asc).expect("decoder");
    let chunks = decode_audio(&mut decoder, &demuxed.audio_aus);
    check_audio(&chunks, 48_000, 2, (demuxed.audio_aus.len() - 2) * 1024);
}

#[test]
fn mp3_fixture_decodes_stereo_pcm() {
    if !ready(FfCodec::Mp3) {
        return;
    }
    let demuxed = demux("sine-48k-stereo.mp3");
    let (_, codec, rate, channels, _) = demuxed.audio.clone().expect("audio track");
    assert_eq!(codec, AudioCodec::Mp3);
    let mut decoder = FfAudioDecoder::new(FfCodec::Mp3, rate, channels, &[]).expect("decoder");
    let chunks = decode_audio(&mut decoder, &demuxed.audio_aus);
    check_audio(&chunks, 48_000, 2, 48_000);
}
