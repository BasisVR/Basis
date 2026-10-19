//! Fixture rows for the software decoders. Each decoder is driven with the
//! AUs its real demuxer produces from a committed sine fixture, and the PCM
//! out is checked for count and content.

use decode_sw::{FlacDecoder, OpusDecoder};
use media_clock::Generation;
use media_decode::{AudioDecoder, PcmChunk, SubmitOutcome};
use media_demux::{DemuxLimits, Format, MemSource, StreamEvent, open_auto};

struct Demuxed {
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
    loop {
        match demuxer.next_event().expect("event") {
            StreamEvent::Format(
                _,
                Format::Audio {
                    codec_private: p, ..
                },
            ) => codec_private = p,
            StreamEvent::Au(au) => aus.push((au.data, au.pts.as_micros())),
            StreamEvent::Eos(_) => break,
            _ => {}
        }
    }
    Demuxed { codec_private, aus }
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
