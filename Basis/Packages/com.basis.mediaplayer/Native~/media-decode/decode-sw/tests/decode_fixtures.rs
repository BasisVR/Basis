//! Fixture rows for the software decoders. Each decoder is driven with the
//! AUs its real demuxer produces from a committed sine fixture, and the PCM
//! out is checked for count and content.

use decode_sw::{AlacDecoder, FlacDecoder, OpusDecoder, PcmDecoder, VorbisDecoder};
use media_clock::Generation;
use media_decode::{AudioDecoder, PcmChunk, SubmitOutcome};
use media_demux::{AudioCodec, DemuxLimits, Format, MemSource, StreamEvent, open_auto};

struct Demuxed {
    codec: AudioCodec,
    sample_rate: u32,
    channels: u32,
    codec_private: Vec<u8>,
    aus: Vec<(Vec<u8>, i64)>,
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
    let mut codec_private = Vec::new();
    let mut aus = Vec::new();
    let mut format = (AudioCodec::Pcm, 0, 0);
    loop {
        match demuxer.next_event().expect("event") {
            StreamEvent::Format(
                _,
                Format::Audio {
                    codec,
                    sample_rate,
                    channels,
                    codec_private: p,
                },
            ) => {
                format = (codec, sample_rate, channels);
                codec_private = p;
            }
            StreamEvent::Au(au) => aus.push((au.data, au.pts.as_micros())),
            StreamEvent::Eos(_) => break,
            _ => {}
        }
    }
    Demuxed {
        codec: format.0,
        sample_rate: format.1,
        channels: format.2,
        codec_private,
        aus,
    }
}

fn decode_all(decoder: &mut dyn AudioDecoder, aus: &[(Vec<u8>, i64)]) -> Vec<PcmChunk> {
    let mut chunks = Vec::new();
    for (au, pts) in aus {
        loop {
            match decoder.submit(au, *pts).expect("submit") {
                SubmitOutcome::Accepted => break,
                SubmitOutcome::NotAccepting => {
                    if let Some(chunk) = decoder.try_output().expect("output") {
                        chunks.push(chunk);
                    }
                }
            }
        }
    }
    decoder.begin_drain().expect("drain");
    while let Some(chunk) = decoder.try_output().expect("output") {
        chunks.push(chunk);
    }
    chunks
}

fn sanity(chunks: &[PcmChunk], min_frames: u64, max_frames: u64) {
    let mut frames = 0u64;
    let mut peak = 0.0f32;
    for chunk in chunks {
        assert_eq!(chunk.sample_rate, 48_000);
        assert_eq!(chunk.channels, 2);
        frames += chunk.data.len() as u64 / 2;
        for &s in &chunk.data {
            assert!(s.is_finite());
            peak = peak.max(s.abs());
        }
    }
    assert!(
        (min_frames..=max_frames).contains(&frames),
        "decoded {frames} frames, expected {min_frames}..={max_frames}"
    );
    // A sine fixture has real signal without clipping.
    assert!(peak > 0.05 && peak <= 1.0, "peak {peak} out of range");
}

#[test]
fn flac_fixture_decodes_bit_for_bit_shaped_pcm() {
    let demuxed = demux("sine-48k-stereo.flac");
    let mut decoder = FlacDecoder::new(&demuxed.codec_private).expect("decoder");
    assert_eq!(decoder.output_format(), (48_000, 2));
    let chunks = decode_all(&mut decoder, &demuxed.aus);
    // FLAC is lossless: exactly 6 s at 48 kHz.
    sanity(&chunks, 288_000, 288_000);
    // Chunk pts mirror the demuxer's frame pts.
    assert_eq!(chunks[0].pts_us, 0);
    assert_eq!(chunks[1].pts_us, 96_000);
}

#[test]
fn opus_fixture_decodes_with_pre_skip_before_the_origin() {
    let demuxed = demux("sine-48k-stereo.opus");
    let mut decoder = OpusDecoder::new(&demuxed.codec_private).expect("decoder");
    assert_eq!(decoder.output_format(), (48_000, 2));
    let chunks = decode_all(&mut decoder, &demuxed.aus);
    // 301 packets × 960 samples; the leading pre-skip's worth carries
    // negative pts and is the engine's to drop.
    sanity(&chunks, 301 * 960, 301 * 960);
    assert!(chunks[0].pts_us < 0);
    // Priming is consumed within the first packets: pts crosses zero.
    assert!(chunks.iter().any(|c| c.pts_us >= 0));
}

/// Claxon's channel cap is 8, so the 7.1 fixture decodes to eight-channel
/// PCM end to end. Splitting the channels out is the managed side's job.
#[test]
fn flac_71_fixture_decodes_eight_channels() {
    let demuxed = demux("sine-48k-71.flac");
    let mut decoder = FlacDecoder::new(&demuxed.codec_private).expect("decoder");
    assert_eq!(decoder.output_format(), (48_000, 8));
    let chunks = decode_all(&mut decoder, &demuxed.aus);
    let mut frames = 0u64;
    let mut peak = 0.0f32;
    for chunk in &chunks {
        assert_eq!(chunk.sample_rate, 48_000);
        assert_eq!(chunk.channels, 8);
        frames += chunk.data.len() as u64 / 8;
        for &s in &chunk.data {
            assert!(s.is_finite());
            peak = peak.max(s.abs());
        }
    }
    // Lossless: exactly 6 s at 48 kHz.
    assert_eq!(frames, 288_000);
    assert!(peak > 0.05 && peak <= 1.0, "peak {peak} out of range");
}

/// An AU decoding to more sample frames than any real one is refused: its
/// size says nothing about what it decodes to. The whole 6 s fixture in one
/// AU is past the ceiling; its first frame alone is accepted.
#[test]
fn flac_refuses_an_au_decoding_past_the_ceiling() {
    let demuxed = demux("sine-48k-stereo.flac");
    let mut decoder = FlacDecoder::new(&demuxed.codec_private).expect("decoder");
    let whole: Vec<u8> = demuxed.aus.iter().flat_map(|(au, _)| au.clone()).collect();
    let refused = decoder.submit(&whole, 0).expect_err("refused");
    assert!(refused.0.contains("sample frames"), "{}", refused.0);
    assert!(decoder.try_output().expect("output").is_none());

    let mut decoder = FlacDecoder::new(&demuxed.codec_private).expect("decoder");
    assert!(matches!(
        decoder.submit(&demuxed.aus[0].0, 0),
        Ok(SubmitOutcome::Accepted)
    ));
}

/// A FLAC frame of two constant 16-bit subframes, 16 samples long: the
/// smallest block FLAC allows, stated in 14 bytes.
fn flac_constant_frame() -> Vec<u8> {
    fn crc(bytes: &[u8], poly: u16, width: u32) -> u16 {
        let top = 1u16 << (width - 1);
        let mask = if width == 16 {
            u16::MAX
        } else {
            (1 << width) - 1
        };
        let mut crc = 0u16;
        for &byte in bytes {
            crc ^= u16::from(byte) << (width - 8);
            for _ in 0..8 {
                crc = if crc & top != 0 {
                    (crc << 1) ^ poly
                } else {
                    crc << 1
                } & mask;
            }
        }
        crc
    }
    // Fixed blocking; block size from an 8-bit field, 48 kHz; stereo,
    // 16-bit; frame number 0; block size 16.
    let mut frame = vec![0xFF, 0xF8, 0x6A, 0x18, 0x00, 15];
    frame.push(crc(&frame, 0x07, 8) as u8);
    for _ in 0..2 {
        frame.extend_from_slice(&[0x00, 0x10, 0x00]);
    }
    frame.extend_from_slice(&crc(&frame, 0x8005, 16).to_be_bytes());
    frame
}

/// An AU of small blocks stays under the frame ceiling but would fill
/// the output queue many times over; it is refused and leaves nothing
/// queued. One such frame decodes, so the refusal is the queue's.
/// An AU that fits an empty queue but not the room left is pushed back
/// instead, and accepted once the queue drains.
#[test]
fn flac_refuses_an_au_overfilling_the_queue() {
    let demuxed = demux("sine-48k-stereo.flac");
    let mut decoder = FlacDecoder::new(&demuxed.codec_private).expect("decoder");
    assert!(matches!(
        decoder.submit(&flac_constant_frame(), 0),
        Ok(SubmitOutcome::Accepted)
    ));
    let chunk = decoder.try_output().expect("output").expect("a chunk");
    assert_eq!(chunk.data.len(), 32);

    let au = flac_constant_frame().repeat(100);
    let refused = decoder.submit(&au, 0).expect_err("refused");
    assert!(refused.0.contains("queue"), "{}", refused.0);
    assert!(decoder.try_output().expect("output").is_none());

    for _ in 0..63 {
        assert!(matches!(
            decoder.submit(&flac_constant_frame(), 0),
            Ok(SubmitOutcome::Accepted)
        ));
    }
    let pair = flac_constant_frame().repeat(2);
    assert!(matches!(
        decoder.submit(&pair, 0),
        Ok(SubmitOutcome::NotAccepting)
    ));
    decoder.try_output().expect("output").expect("a chunk");
    assert!(matches!(
        decoder.submit(&pair, 0),
        Ok(SubmitOutcome::Accepted)
    ));
    let mut queued = 0;
    while decoder.try_output().expect("output").is_some() {
        queued += 1;
    }
    assert_eq!(queued, 64);
}

#[test]
fn flac_refuses_a_broken_header() {
    assert!(FlacDecoder::new(b"not flac").is_err());
    assert!(OpusDecoder::new(b"not opus").is_err());
}

/// Mapping family 1 stores 5.1 in Vorbis order (FL C FR RL RR LFE); the
/// decoder hands it out in WAV order. The fixture sounds one speaker at a
/// time, a second each in WAV order, three times over, so second `k` must
/// be loud in slot `k % 6` alone.
#[test]
fn opus_51_fixture_decodes_in_wav_order() {
    let demuxed = demux("sine-48k-51.opus");
    assert_eq!(
        demuxed.codec_private[18], 1,
        "fixture must be mapping family 1"
    );
    let mut decoder = OpusDecoder::new(&demuxed.codec_private).expect("decoder");
    assert_eq!(decoder.output_format(), (48_000, 6));
    let pcm: Vec<f32> = decode_all(&mut decoder, &demuxed.aus)
        .into_iter()
        .flat_map(|chunk| chunk.data)
        .collect();
    assert!(
        pcm.len() / 6 >= 18 * 48_000,
        "decoded {} frames",
        pcm.len() / 6
    );

    for second in 0..18 {
        let window = &pcm[6 * (second * 48_000 + 12_000)..6 * (second * 48_000 + 36_000)];
        let mut power = [0.0f64; 6];
        for frame in window.as_chunks::<6>().0 {
            for (slot, &s) in frame.iter().enumerate() {
                power[slot] += f64::from(s) * f64::from(s);
            }
        }
        let speaker = second % 6;
        for (slot, &p) in power.iter().enumerate() {
            if slot != speaker {
                // At least 40 dB below the speaker sounding.
                assert!(
                    p * 10_000.0 < power[speaker],
                    "second {second}: slot {slot} is loud, expected only slot {speaker}"
                );
            }
        }
    }
}

fn opus_head(channels: u8, family: u8, table: &[u8]) -> Vec<u8> {
    let mut head = b"OpusHead".to_vec();
    head.extend_from_slice(&[1, channels, 0, 0, 128, 187, 0, 0, 0, 0, family]);
    head.extend_from_slice(table);
    head
}

#[test]
fn opus_refuses_unsupported_layouts() {
    // Family 0 is mono/stereo only.
    assert!(OpusDecoder::new(&opus_head(6, 0, &[])).is_err());
    // Ambisonics (family 2) is not decoded.
    assert!(OpusDecoder::new(&opus_head(4, 2, &[4, 0, 0, 1, 2, 3])).is_err());
    // Past 7.1.
    assert!(OpusDecoder::new(&opus_head(9, 255, &[9, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8])).is_err());
    // Mapping table shorter than the channel count.
    assert!(OpusDecoder::new(&opus_head(6, 1, &[4, 2, 0, 4, 1])).is_err());
    // A mapping entry naming a stream channel that does not exist.
    assert!(OpusDecoder::new(&opus_head(6, 1, &[4, 2, 0, 4, 1, 2, 3, 6])).is_err());
    // The same layout, well formed, is accepted.
    assert!(OpusDecoder::new(&opus_head(6, 1, &[4, 2, 0, 4, 1, 2, 3, 5])).is_ok());
}

/// The AV1 fixture's video AUs and the size its container states.
fn av1_fixture() -> (Vec<(Vec<u8>, i64)>, u32, u32) {
    let path = concat!(
        env!("CARGO_MANIFEST_DIR"),
        "/../../fixtures/mkv/av1-opus.webm"
    );
    let bytes = std::fs::read(path).expect("fixture readable");
    let mut demuxer = open_auto(
        Box::new(MemSource(bytes)),
        DemuxLimits::default(),
        Generation::default(),
    )
    .expect("open");
    let mut video = None;
    let mut size = (0, 0);
    let mut aus = Vec::new();
    loop {
        match demuxer.next_event().expect("event") {
            StreamEvent::Format(
                track,
                Format::Video {
                    coded_width,
                    coded_height,
                    ..
                },
            ) => {
                video = Some(track);
                size = (coded_width, coded_height);
            }
            StreamEvent::Au(au) if Some(au.track) == video => {
                aus.push((au.data, au.pts.as_micros()))
            }
            StreamEvent::Eos(_) => break,
            _ => {}
        }
    }
    (aus, size.0, size.1)
}

#[test]
fn av1_fixture_decodes_through_rav1d() {
    use media_decode::VideoDecoder;
    let (aus, width, height) = av1_fixture();
    let mut decoder = decode_sw::SwAv1Decoder::new(width * height).expect("decoder");
    let mut frames = 0usize;
    let mut last_pts = i64::MIN;
    for (au, pts) in &aus {
        loop {
            match decoder.submit(au, *pts).expect("submit") {
                SubmitOutcome::Accepted => break,
                SubmitOutcome::NotAccepting => {
                    if let Some(frame) = decoder.try_output().expect("output") {
                        assert!(frame.pts_us() >= last_pts, "pts must not regress");
                        last_pts = frame.pts_us();
                        frames += 1;
                    }
                }
            }
        }
        while let Some(frame) = decoder.try_output().expect("output") {
            let nv12 = frame.as_nv12().expect("nv12");
            assert!(nv12.width > 0 && !nv12.data.is_empty());
            assert!(frame.pts_us() >= last_pts, "pts must not regress");
            last_pts = frame.pts_us();
            frames += 1;
        }
    }
    decoder.begin_drain().expect("drain");
    while let Some(_frame) = decoder.try_output().expect("output") {
        frames += 1;
    }
    assert!(
        frames >= aus.len() - 2,
        "decoded {frames} of {} frames",
        aus.len()
    );
}

/// A frame the bitstream states past the decoder's limit fails the decode,
/// whatever the container says: one pixel under the fixture's own size is
/// enough to refuse it. A limit of zero, which rav1d reads as none, is
/// refused outright.
#[test]
fn av1_refuses_a_frame_past_the_size_limit() {
    use media_decode::VideoDecoder;
    assert!(decode_sw::SwAv1Decoder::new(0).is_err());
    let (aus, width, height) = av1_fixture();
    let mut decoder = decode_sw::SwAv1Decoder::new(width * height - 1).expect("decoder");
    let refused = aus.iter().any(|(au, pts)| {
        decoder.submit(au, *pts).is_err()
            || std::iter::from_fn(|| decoder.try_output().transpose()).any(|r| r.is_err())
    });
    assert!(refused, "a frame past the limit decoded");
}

/// The sine WAV a fixture was encoded from, as the PCM adapter reads it:
/// the reference every other lane's output is held to.
fn reference(name: &str) -> Vec<f32> {
    let demuxed = demux(name);
    let mut decoder = PcmDecoder::new(
        demuxed.sample_rate,
        demuxed.channels,
        &demuxed.codec_private,
    )
    .expect("reference decoder");
    decode_all(&mut decoder, &demuxed.aus)
        .into_iter()
        .flat_map(|chunk| chunk.data)
        .collect()
}

/// Decode a fixture through the decoder its codec routes to, checking the
/// chunk timeline is continuous: each chunk starts where the last ended.
/// Returns the output as interleaved samples, and the sample frame the
/// first chunk starts on. That is zero wherever the container counts in
/// samples; Matroska counts in milliseconds and states the encoder delay
/// in nanoseconds, so its first block can land a fraction of a
/// millisecond in.
fn decode_fixture(name: &str) -> (Demuxed, Vec<f32>, usize) {
    let demuxed = demux(name);
    let mut decoder: Box<dyn AudioDecoder> = match demuxed.codec {
        AudioCodec::Alac => Box::new(AlacDecoder::new(&demuxed.codec_private).expect("alac")),
        AudioCodec::Vorbis => Box::new(VorbisDecoder::new(&demuxed.codec_private).expect("vorbis")),
        AudioCodec::Flac => Box::new(FlacDecoder::new(&demuxed.codec_private).expect("flac")),
        AudioCodec::Pcm => Box::new(
            PcmDecoder::new(
                demuxed.sample_rate,
                demuxed.channels,
                &demuxed.codec_private,
            )
            .expect("pcm"),
        ),
        other => panic!("{name}: unexpected codec {other:?}"),
    };
    assert_eq!(
        decoder.output_format(),
        (demuxed.sample_rate, demuxed.channels),
        "{name}: decoder format matches the announce"
    );
    let chunks = decode_all(decoder.as_mut(), &demuxed.aus);
    let channels = demuxed.channels as usize;
    let rate = i64::from(demuxed.sample_rate);
    let mut out = Vec::new();
    let start = chunks.first().map_or(0, |c| c.pts_us * rate / 1_000_000);
    assert!(
        (0..=48).contains(&start),
        "{name}: output starts at sample {start}"
    );
    let mut next = start;
    for chunk in &chunks {
        let at = chunk.pts_us * rate / 1_000_000;
        assert!(
            (at - next).abs() <= 1,
            "{name}: chunk at sample {at}, expected {next}"
        );
        next += (chunk.data.len() / channels) as i64;
        out.extend_from_slice(&chunk.data);
    }
    (demuxed, out, start as usize)
}

/// Signal-to-noise of `got` against `want`, per channel, in dB.
fn snr_per_channel(got: &[f32], want: &[f32], channels: usize) -> Vec<f64> {
    let frames = got.len().min(want.len()) / channels;
    (0..channels)
        .map(|c| {
            let (mut signal, mut noise) = (0.0f64, 0.0f64);
            for f in 0..frames {
                let w = f64::from(want[f * channels + c]);
                let g = f64::from(got[f * channels + c]);
                signal += w * w;
                noise += (w - g) * (w - g);
            }
            10.0 * (signal / noise.max(1e-20)).log10()
        })
        .collect()
}

/// Lossless lanes reproduce the source WAV sample for sample, in WAVE
/// channel order, on a continuous timeline from zero.
#[test]
fn lossless_lanes_match_their_source_exactly() {
    let stereo = reference("sine-48k-stereo.wav");
    let surround = reference("sine-48k-51-distinct.wav");
    let surround_frames = surround.len() / 6;
    for (name, want, frames) in [
        ("sine-48k-stereo-alac.m4a", &stereo, 96_000usize),
        ("sine-48k-51-alac.m4a", &surround, surround_frames),
        ("sine-48k-stereo.oga", &stereo, 96_000),
        ("sine-48k-stereo.aiff", &stereo, 24_000),
        ("sine-48k-stereo-sowt.aifc", &stereo, 24_000),
        ("sine-48k-stereo-f32.aifc", &stereo, 12_000),
        ("sine-48k-stereo-f32.wav", &stereo, 12_000),
    ] {
        let (demuxed, got, start) = decode_fixture(name);
        let channels = demuxed.channels as usize;
        assert_eq!(start, 0, "{name}: output starts at the origin");
        assert_eq!(demuxed.sample_rate, 48_000, "{name}");
        assert_eq!(got.len(), frames * channels, "{name}: frame count");
        assert!(
            got == want[..frames * channels],
            "{name}: output differs from the source"
        );
    }
}

/// Vorbis is lossy, so it is held to the source by SNR on every channel,
/// which also pins the channel order and the timeline: the 5.1 source has
/// a different tone on every channel, so a channel in the wrong slot or a
/// timeline off by a block lands below 0 dB. The bar is 10 dB because
/// libvorbis codes the LFE coarsely (ffmpeg's own decode of that fixture
/// reaches 14 dB there; every other channel clears 30).
#[test]
fn vorbis_lanes_track_their_source() {
    let stereo = reference("sine-48k-stereo.wav");
    let surround = reference("sine-48k-51-distinct.wav");
    let surround_frames = surround.len() / 6;
    for (name, want, frames) in [
        ("sine-48k-stereo.ogg", &stereo, 96_000usize),
        ("sine-48k-51-vorbis.ogg", &surround, surround_frames),
        ("mkv/vorbis-stereo.webm", &stereo, 96_000),
    ] {
        let (demuxed, got, start) = decode_fixture(name);
        let channels = demuxed.channels as usize;
        assert_eq!(demuxed.codec, AudioCodec::Vorbis, "{name}");
        if name.ends_with(".ogg") {
            assert_eq!(start, 0, "{name}: Ogg counts in samples, so starts exactly");
        }
        let decoded = got.len() / channels;
        // Without end trimming the last block's padding plays too.
        assert!(
            (frames..=frames + 2048).contains(&decoded),
            "{name}: decoded {decoded} frames, expected about {frames}"
        );
        for (c, snr) in snr_per_channel(&got, want, channels).iter().enumerate() {
            assert!(*snr > 10.0, "{name}: channel {c} SNR {snr:.1} dB");
        }
    }
}

/// Clips arrive from other players, so every added lane meets damaged
/// input: each fixture is cut short and has bytes overwritten across its
/// length, then demuxed and decoded to the end. Errors are expected; a
/// panic is the failure.
#[test]
fn added_lanes_survive_damaged_files() {
    use std::panic::{AssertUnwindSafe, catch_unwind};

    fn run(bytes: Vec<u8>) {
        let Ok(mut demuxer) = open_auto(
            Box::new(MemSource(bytes)),
            DemuxLimits::default(),
            Generation::default(),
        ) else {
            return;
        };
        let mut decoder: Option<Box<dyn AudioDecoder>> = None;
        for _ in 0..20_000 {
            match demuxer.next_event() {
                Ok(StreamEvent::Format(
                    _,
                    Format::Audio {
                        codec,
                        sample_rate,
                        channels,
                        codec_private,
                    },
                )) => {
                    decoder = match codec {
                        AudioCodec::Alac => AlacDecoder::new(&codec_private)
                            .ok()
                            .map(|d| Box::new(d) as Box<dyn AudioDecoder>),
                        AudioCodec::Vorbis => VorbisDecoder::new(&codec_private)
                            .ok()
                            .map(|d| Box::new(d) as Box<dyn AudioDecoder>),
                        AudioCodec::Flac => FlacDecoder::new(&codec_private)
                            .ok()
                            .map(|d| Box::new(d) as Box<dyn AudioDecoder>),
                        AudioCodec::Pcm => PcmDecoder::new(sample_rate, channels, &codec_private)
                            .ok()
                            .map(|d| Box::new(d) as Box<dyn AudioDecoder>),
                        _ => None,
                    };
                }
                Ok(StreamEvent::Au(au)) => {
                    if let Some(decoder) = decoder.as_mut() {
                        let _ = decoder.submit(&au.data, au.pts.as_micros());
                        while let Ok(Some(_)) = decoder.try_output() {}
                    }
                }
                Ok(StreamEvent::Eos(_)) | Err(_) => return,
                Ok(_) => {}
            }
        }
    }

    let path = concat!(env!("CARGO_MANIFEST_DIR"), "/../../fixtures/");
    for name in [
        "sine-48k-stereo.ogg",
        "sine-48k-51-vorbis.ogg",
        "sine-48k-stereo.oga",
        "sine-48k-stereo.aiff",
        "sine-48k-stereo-sowt.aifc",
        "sine-48k-stereo-f32.aifc",
        "sine-48k-stereo-f32.wav",
        "sine-48k-stereo-alac.m4a",
        "sine-48k-51-alac.m4a",
        "mkv/vorbis-stereo.webm",
    ] {
        let clean = std::fs::read(format!("{path}{name}")).expect("fixture readable");
        let mut cases: Vec<Vec<u8>> = Vec::new();
        for cut in [
            8usize,
            40,
            200,
            1_000,
            4_500,
            clean.len() / 2,
            clean.len() - 1,
        ] {
            cases.push(clean[..cut.min(clean.len())].to_vec());
        }
        // A cheap deterministic generator: positions spread through the
        // headers and the payload alike, values that include the field
        // extremes a parser is most likely to trust.
        let mut state = 0x9E37_79B9u32;
        for round in 0..48 {
            let mut bytes = clean.clone();
            for _ in 0..(1 + round % 6) {
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                let at = if round % 2 == 0 {
                    state as usize % bytes.len().min(512)
                } else {
                    state as usize % bytes.len()
                };
                bytes[at] = [0x00, 0xFF, 0x7F, 0x80, (state >> 8) as u8][(state % 5) as usize];
            }
            cases.push(bytes);
        }
        for (i, bytes) in cases.into_iter().enumerate() {
            let outcome = catch_unwind(AssertUnwindSafe(|| run(bytes)));
            assert!(outcome.is_ok(), "{name}: damaged case {i} panicked");
        }
    }
}

/// The Ogg demuxer times each Vorbis packet itself, from its block size and
/// the one before, and a seek anchors on those times. They have to agree
/// with what the decoder outputs packet by packet, with no help from the
/// decoder's own clock: the clicks fixture switches between short and
/// long blocks around every transient, where a count that ignores the
/// neighbouring block goes wrong.
#[test]
fn ogg_vorbis_packet_times_match_decoded_output() {
    for name in [
        "clicks-48k-stereo.ogg",
        "sine-48k-stereo.ogg",
        "sine-48k-51-vorbis.ogg",
    ] {
        let demuxed = demux(name);
        let rate = i64::from(demuxed.sample_rate);
        let channels = demuxed.channels as usize;
        let mut decoder = VorbisDecoder::new(&demuxed.codec_private).expect("decoder");
        let mut decoded = 0i64;
        let mut sizes = std::collections::BTreeSet::new();
        for (au, pts) in &demuxed.aus {
            assert_eq!(
                decoder.submit(au, *pts).expect("submit"),
                SubmitOutcome::Accepted
            );
            let mut frames = 0i64;
            while let Some(chunk) = decoder.try_output().expect("output") {
                frames += (chunk.data.len() / channels) as i64;
            }
            if frames > 0 {
                // Microsecond stamps round, so one frame either way.
                assert!(
                    (pts * rate / 1_000_000 - decoded).abs() <= 1,
                    "{name}: packet stamped at {pts} us after {decoded} decoded frames"
                );
                sizes.insert(frames);
            }
            decoded += frames;
        }
        if name.starts_with("clicks") {
            assert!(
                sizes.len() >= 3,
                "{name}: the fixture must switch block sizes, saw {sizes:?}"
            );
        }
    }
}
