//! Raw audio demuxer rows: sniffed routing, pinned AU counts against
//! ffprobe's packet view, exact pts arithmetic, metadata-frame and
//! pre-skip handling.

use media_clock::{Generation, MediaTime};
use media_demux::{
    Au, AudioCodec, ContainerKind, DemuxError, DemuxLimits, Format, MemSource, StreamEvent,
    open_auto, sniff_container,
};

fn fixture(name: &str) -> Vec<u8> {
    let path = concat!(env!("CARGO_MANIFEST_DIR"), "/../fixtures/");
    std::fs::read(format!("{path}{name}")).expect("fixture readable")
}

struct Run {
    format: Format,
    aus: Vec<Au>,
}

fn run(name: &str) -> Run {
    let bytes = fixture(name);
    let mut demuxer = open_auto(
        Box::new(MemSource(bytes)),
        DemuxLimits::default(),
        Generation::default(),
    )
    .expect("open");
    let mut format = None;
    let mut aus = Vec::new();
    loop {
        match demuxer.next_event().expect("event") {
            StreamEvent::Format(_, f) => format = Some(f),
            StreamEvent::Au(au) => aus.push(au),
            StreamEvent::Eos(_) => break,
            _ => {}
        }
    }
    Run {
        format: format.expect("a format announce"),
        aus,
    }
}

#[test]
fn sniffs_route_each_container() {
    assert_eq!(
        sniff_container(&fixture("sine-48k-stereo.flac")[..64]),
        Some(ContainerKind::Flac)
    );
    assert_eq!(
        sniff_container(&fixture("sine-48k-stereo.opus")[..64]),
        Some(ContainerKind::Ogg)
    );
    // LAME writes no ID3 by default via ffmpeg; the bare-sync path must
    // classify by layer bits.
    let mp3 = fixture("sine-48k-stereo.mp3");
    assert_eq!(
        sniff_container(&mp3[..64.min(mp3.len())]),
        Some(ContainerKind::Mp3)
    );
    assert_eq!(
        sniff_container(&fixture("sine-48k-stereo.aac")[..64]),
        Some(ContainerKind::Adts)
    );
    // An ID3v2 header routes to the MP3 demuxer.
    let mut tagged = b"ID3\x04\x00\x00\x00\x00\x00\x0a".to_vec();
    tagged.extend_from_slice(&[0u8; 32]);
    assert_eq!(sniff_container(&tagged), Some(ContainerKind::Mp3));
}

#[test]
fn flac_frames_match_ffprobe_and_carry_exact_pts() {
    let run = run("sine-48k-stereo.flac");
    let Format::Audio {
        codec,
        sample_rate,
        channels,
        codec_private,
    } = &run.format
    else {
        panic!("audio format expected");
    };
    assert_eq!(*codec, AudioCodec::Flac);
    assert_eq!(*sample_rate, 48_000);
    assert_eq!(*channels, 2);
    assert!(codec_private.starts_with(b"fLaC"));
    // ffprobe: 63 packets over 6 s.
    assert_eq!(run.aus.len(), 63);
    assert_eq!(run.aus[0].pts, MediaTime::ZERO);
    // ffmpeg's flac encoder uses 4608-sample blocks: frame 1 at 96 ms.
    assert_eq!(run.aus[1].pts, MediaTime::from_micros(96_000));
    let last = run.aus.last().expect("frames");
    assert_eq!(
        last.pts,
        MediaTime::from_micros(62 * 4608 * 1_000_000 / 48_000)
    );
}

/// The 7.1 lane. FLAC carries 8 channels through the demuxer
/// unscreened (claxon's cap is 8, the decoder takes it), while ADTS AAC
/// with channel_configuration 7 refuses typed at open, being wider than
/// the 1..=6 screen the AAC lanes share.
#[test]
fn flac_71_demuxes_eight_channels() {
    let run = run("sine-48k-71.flac");
    let Format::Audio {
        codec,
        sample_rate,
        channels,
        ..
    } = &run.format
    else {
        panic!("audio format expected");
    };
    assert_eq!(*codec, AudioCodec::Flac);
    assert_eq!(*sample_rate, 48_000);
    assert_eq!(*channels, 8);
    // ffprobe: 63 packets over 6 s, same blocking as the stereo fixture.
    assert_eq!(run.aus.len(), 63);
}

#[test]
fn adts_71_refuses_typed_on_the_channel_screen() {
    let bytes = fixture("sine-48k-71.aac");
    let result = open_auto(
        Box::new(MemSource(bytes)),
        DemuxLimits::default(),
        Generation::default(),
    );
    match result {
        Err(media_demux::DemuxError::Unsupported(what)) => {
            assert!(
                what.contains("channel configuration"),
                "refusal named {what:?}"
            );
        }
        Err(other) => panic!("expected a typed channel-screen refusal, got {other:?}"),
        Ok(_) => panic!("expected a typed channel-screen refusal, got an open demuxer"),
    }
}

#[test]
fn mp3_skips_the_xing_frame_and_counts_samples() {
    let run = run("sine-48k-stereo.mp3");
    let Format::Audio {
        codec,
        sample_rate,
        channels,
        ..
    } = &run.format
    else {
        panic!("audio format expected");
    };
    assert_eq!(*codec, AudioCodec::Mp3);
    assert_eq!(*sample_rate, 48_000);
    assert_eq!(*channels, 2);
    // ffprobe also sees 251 packets: its demuxer consumes the leading
    // Info frame as metadata exactly as this one must (the fixture's
    // stored stream has 252 frames).
    assert_eq!(run.aus.len(), 251);
    assert_eq!(run.aus[0].pts, MediaTime::ZERO);
    // The first AU is real audio, not the Info frame.
    assert_ne!(&run.aus[0].data[36..40], b"Info");
    // 1152 samples per MPEG-1 Layer III frame.
    assert_eq!(
        run.aus[1].pts,
        MediaTime::from_micros(1_152 * 1_000_000 / 48_000)
    );
}

#[test]
fn adts_strips_headers_and_reconstructs_the_asc() {
    let run = run("sine-48k-stereo.aac");
    let Format::Audio {
        codec,
        sample_rate,
        channels,
        codec_private,
    } = &run.format
    else {
        panic!("audio format expected");
    };
    assert_eq!(*codec, AudioCodec::Aac);
    assert_eq!(*sample_rate, 48_000);
    assert_eq!(*channels, 2);
    // AAC-LC (AOT 2), 48 kHz (index 3), stereo (config 2).
    assert_eq!(codec_private, &vec![0x11u8, 0x90]);
    assert_eq!(run.aus.len(), 283);
    // Payloads carry no ADTS sync of their own.
    assert!(run.aus[0].data.len() > 4);
    assert_eq!(
        run.aus[1].pts,
        MediaTime::from_micros(1_024 * 1_000_000 / 48_000)
    );
}

#[test]
fn ogg_opus_applies_pre_skip_as_negative_lead_in() {
    let run = run("sine-48k-stereo.opus");
    let Format::Audio {
        codec,
        sample_rate,
        channels,
        codec_private,
    } = &run.format
    else {
        panic!("audio format expected");
    };
    assert_eq!(*codec, AudioCodec::Opus);
    assert_eq!(*sample_rate, 48_000);
    assert_eq!(*channels, 2);
    assert!(codec_private.starts_with(b"OpusHead"));
    assert_eq!(run.aus.len(), 301);
    // Pre-skip pushes the first packets before the origin; playback time
    // reaches zero once the priming samples are consumed.
    assert!(run.aus[0].pts < MediaTime::ZERO);
    let pre_skip = i64::from(u16::from_le_bytes([codec_private[10], codec_private[11]]));
    assert_eq!(
        run.aus[0].pts,
        MediaTime::from_micros(-pre_skip * 1_000_000 / 48_000)
    );
    // 20 ms packets: consecutive pts step by exactly 960 samples.
    let step = run.aus[1].pts - run.aus[0].pts;
    assert_eq!(step, MediaTime::from_micros(20_000));
    // The stream must cross the origin and cover ~6 s of audio.
    let last = run.aus.last().expect("packets");
    assert!(last.pts > MediaTime::from_micros(5_900_000));
}

/// ffprobe's oracle for the WAV fixtures: `sine-48k-stereo.wav` is 96 000
/// frames of 16-bit stereo (384 000 data bytes) and `sine-48k-51.wav` is
/// 48 000 frames of 24-bit 5.1 (864 000). Both carry a `LIST` chunk between
/// `fmt ` and `data`, so the walk has to skip one to find the samples.
const WAV_STEREO_BYTES: usize = 384_000;
const WAV_51_BYTES: usize = 864_000;

#[test]
fn wav_sniffs_and_announces_pcm() {
    let stereo = fixture("sine-48k-stereo.wav");
    assert_eq!(sniff_container(&stereo[..64]), Some(ContainerKind::Wav));

    let run = run("sine-48k-stereo.wav");
    let Format::Audio {
        codec,
        sample_rate,
        channels,
        codec_private,
    } = &run.format
    else {
        panic!("audio format expected");
    };
    assert_eq!(*codec, AudioCodec::Pcm);
    assert_eq!(*sample_rate, 48_000);
    assert_eq!(*channels, 2);
    // Identity channel order, 16-bit, little-endian.
    assert_eq!(codec_private, &vec![0u8, 1, 1]);
}

#[test]
fn wav_extensible_reads_its_subformat_and_depth() {
    let run = run("sine-48k-51.wav");
    let Format::Audio {
        codec,
        sample_rate,
        channels,
        codec_private,
    } = &run.format
    else {
        panic!("audio format expected");
    };
    assert_eq!(*codec, AudioCodec::Pcm);
    assert_eq!(*sample_rate, 48_000);
    // WAVE_FORMAT_EXTENSIBLE hides the real tag in the SubFormat GUID and
    // the depth in the valid-bits field; both have to be read through.
    assert_eq!(*channels, 6);
    assert_eq!(codec_private, &vec![0u8, 3, 1]);
}

#[test]
fn wav_serves_every_data_byte_once_in_order() {
    for (name, data_bytes, frame_bytes) in [
        ("sine-48k-stereo.wav", WAV_STEREO_BYTES, 4usize),
        ("sine-48k-51.wav", WAV_51_BYTES, 18usize),
    ] {
        let run = run(name);
        let served: Vec<u8> = run
            .aus
            .iter()
            .flat_map(|au| au.data.iter().copied())
            .collect();
        assert_eq!(served.len(), data_bytes, "{name} served byte count");
        // `data` is the last chunk in both fixtures, so the tail of the file
        // is the payload: this pins the located offset as well as the order.
        let raw = fixture(name);
        assert_eq!(served, raw[raw.len() - data_bytes..], "{name} payload");

        // ~20 ms of whole frames per AU, and pts follows the sample count.
        let per_au = 960 * frame_bytes;
        assert_eq!(run.aus.len(), data_bytes / per_au, "{name} AU count");
        assert!(run.aus.iter().all(|au| au.data.len() == per_au));
        assert_eq!(run.aus[0].pts, MediaTime::ZERO);
        assert_eq!(run.aus[1].pts, MediaTime::from_micros(20_000));
        assert!(run.aus.iter().all(|au| au.key));
    }
}

#[test]
fn wav_seeks_to_the_exact_frame() {
    let raw = fixture("sine-48k-stereo.wav");
    let mut demuxer = open_auto(
        Box::new(MemSource(raw.clone())),
        DemuxLimits::default(),
        Generation::default(),
    )
    .expect("open");
    // PCM is linear, so unlike the other raw-audio lanes this one lands on
    // the requested instant rather than refusing.
    assert_eq!(
        demuxer.duration(),
        Some(MediaTime::from_micros(2_000_000)),
        "byte rate states the duration"
    );

    let landed = demuxer
        .seek(
            MediaTime::from_micros(500_000),
            Generation::default().next(),
        )
        .expect("PCM seeks");
    assert_eq!(landed, MediaTime::from_micros(500_000));

    // The format announce still leads, so the engine can configure a
    // decoder for a session that seeks before its first pull.
    let au = loop {
        match demuxer.next_event().expect("event") {
            StreamEvent::Au(au) => break au,
            StreamEvent::Format(..) => continue,
            other => panic!("unexpected {other:?} after the seek"),
        }
    };
    assert_eq!(au.pts, MediaTime::from_micros(500_000));
    // 0.5 s in at 192 000 bytes/s = 96 000 bytes past the data start.
    let data_start = raw.len() - WAV_STEREO_BYTES;
    assert_eq!(
        au.data,
        raw[data_start + 96_000..data_start + 96_000 + 3_840]
    );
}

#[test]
fn wav_refuses_what_the_pcm_adapter_cannot_play() {
    // 32-bit float is a valid WAV and deliberately outside the supported
    // set; it must refuse at open rather than half-decode.
    let mut wav = b"RIFF".to_vec();
    wav.extend_from_slice(&36u32.to_le_bytes());
    wav.extend_from_slice(b"WAVEfmt ");
    wav.extend_from_slice(&16u32.to_le_bytes());
    wav.extend_from_slice(&3u16.to_le_bytes()); // WAVE_FORMAT_IEEE_FLOAT
    wav.extend_from_slice(&2u16.to_le_bytes());
    wav.extend_from_slice(&48_000u32.to_le_bytes());
    wav.extend_from_slice(&384_000u32.to_le_bytes());
    wav.extend_from_slice(&8u16.to_le_bytes());
    wav.extend_from_slice(&32u16.to_le_bytes());
    wav.extend_from_slice(b"data");
    wav.extend_from_slice(&8u32.to_le_bytes());
    wav.extend_from_slice(&[0u8; 8]);

    assert_eq!(sniff_container(&wav), Some(ContainerKind::Wav));
    let err = open_auto(
        Box::new(MemSource(wav)),
        DemuxLimits::default(),
        Generation::default(),
    )
    .err()
    .expect("float PCM refuses");
    assert!(
        matches!(err, media_demux::DemuxError::Unsupported(_)),
        "typed refusal, got {err}"
    );
}

/// A parse error from any demuxer carries the neutral `parse:` label ahead
/// of the demuxer's own message.
#[test]
fn a_wav_parse_error_reads_as_wav() {
    let mut wav = b"RIFF".to_vec();
    wav.extend_from_slice(&20u32.to_le_bytes());
    wav.extend_from_slice(b"WAVEdata");
    wav.extend_from_slice(&8u32.to_le_bytes());
    wav.extend_from_slice(&[0u8; 8]);

    assert_eq!(sniff_container(&wav), Some(ContainerKind::Wav));
    let err = open_auto(
        Box::new(MemSource(wav)),
        DemuxLimits::default(),
        Generation::default(),
    )
    .err()
    .expect("a data chunk before fmt refuses");
    assert_eq!(err.to_string(), "parse: WAV data chunk before fmt");
}

#[test]
fn wav_handles_the_shapes_a_writer_leaves_behind() {
    fn riff(chunks: &[u8]) -> Vec<u8> {
        let mut out = b"RIFF".to_vec();
        out.extend_from_slice(&((4 + chunks.len()) as u32).to_le_bytes());
        out.extend_from_slice(b"WAVE");
        out.extend_from_slice(chunks);
        out
    }
    fn fmt16() -> Vec<u8> {
        let mut c = b"fmt ".to_vec();
        c.extend_from_slice(&16u32.to_le_bytes());
        c.extend_from_slice(&1u16.to_le_bytes());
        c.extend_from_slice(&2u16.to_le_bytes());
        c.extend_from_slice(&48_000u32.to_le_bytes());
        c.extend_from_slice(&192_000u32.to_le_bytes());
        c.extend_from_slice(&4u16.to_le_bytes());
        c.extend_from_slice(&16u16.to_le_bytes());
        c
    }

    // A live capture writes 0xFFFFFFFF for the data size and never comes
    // back to fix it: the stated size is a hint, EOF is the truth.
    let mut streaming = fmt16();
    streaming.extend_from_slice(b"data");
    streaming.extend_from_slice(&u32::MAX.to_le_bytes());
    streaming.extend_from_slice(&vec![0u8; 3_840 * 3]);
    let mut demuxer = open_auto(
        Box::new(MemSource(riff(&streaming))),
        DemuxLimits::default(),
        Generation::default(),
    )
    .expect("open");
    assert_eq!(demuxer.duration(), None, "no duration without a real size");
    let mut aus = 0;
    loop {
        match demuxer.next_event().expect("event") {
            StreamEvent::Au(_) => aus += 1,
            StreamEvent::Eos(_) => break,
            _ => {}
        }
    }
    assert_eq!(aus, 3);

    // A chunk claiming more than the metadata budget must trip the cap
    // rather than walk.
    let mut hostile = fmt16();
    hostile.extend_from_slice(b"JUNK");
    hostile.extend_from_slice(&0xFFFF_FFF0u32.to_le_bytes());
    let err = open_auto(
        Box::new(MemSource(riff(&hostile))),
        DemuxLimits::default(),
        Generation::default(),
    )
    .err()
    .expect("the chunk walk is bounded");
    assert!(
        matches!(err, media_demux::DemuxError::Cap(_)),
        "expected a cap refusal, got {err}"
    );
}

#[test]
fn mp3_seeks_by_its_xing_table() {
    use media_clock::Generation;

    let raw = fixture("sine-48k-stereo.mp3");
    let mut demuxer = open_auto(
        Box::new(MemSource(raw)),
        DemuxLimits::default(),
        Generation::default(),
    )
    .expect("open");

    // The Xing frame count states the length: 251 frames of 1152 samples
    // at 48 kHz. ffprobe reports 6.000 s for the same file because it
    // applies the encoder's delay and padding; the frame count is what
    // the seek arithmetic is keyed on, so that is what is reported.
    assert_eq!(
        demuxer.duration(),
        Some(MediaTime::from_micros(251 * 1152 * 1_000_000 / 48_000))
    );

    for target_ms in [0i64, 1_500, 4_000] {
        let target = MediaTime::from_micros(target_ms * 1000);
        let landed = demuxer
            .seek(target, Generation::default().next())
            .expect("MP3 seeks");
        assert_eq!(landed, target, "the landing is reported at the request");

        let au = loop {
            match demuxer.next_event().expect("event") {
                StreamEvent::Au(au) => break au,
                StreamEvent::Format(..) => continue,
                other => panic!("unexpected {other:?} after seeking to {target_ms} ms"),
            }
        };
        // Frames carry no index, so the landing is an estimate: hold it to
        // a frame or two of the request rather than to the sample.
        let drift = (au.pts.as_micros() - target.as_micros()).abs();
        assert!(
            drift <= 100_000,
            "seek to {target_ms} ms landed at {} us",
            au.pts.as_micros()
        );
        // Whatever it lands on must be a real frame, not a false sync
        // inside a payload.
        assert!(au.data.len() > 4 && au.data[0] == 0xFF);
    }
}

/// The committed FLAC fixtures carry STREAMINFO, VORBIS_COMMENT and
/// PADDING but no SEEKTABLE (ffmpeg writes none), so this is the
/// bisection arm, and the demanding one: the 7.1 fixture's frames are
/// ~52 kB each against the stereo fixture's ~1.3 kB.
#[test]
fn flac_seeks_by_bisecting_its_frame_headers() {
    use media_clock::Generation;

    for name in ["sine-48k-stereo.flac", "sine-48k-71.flac"] {
        let mut demuxer = open_auto(
            Box::new(MemSource(fixture(name))),
            DemuxLimits::default(),
            Generation::default(),
        )
        .expect("open");

        // STREAMINFO's total sample count: 6 s at 48 kHz.
        assert_eq!(
            demuxer.duration(),
            Some(MediaTime::from_micros(6_000_000)),
            "{name} duration"
        );

        for target_ms in [0i64, 1_500, 3_000, 5_500] {
            let target = MediaTime::from_micros(target_ms * 1000);
            let landed = demuxer
                .seek(target, Generation::default().next())
                .expect("FLAC seeks");

            let au = loop {
                match demuxer.next_event().expect("event") {
                    StreamEvent::Au(au) => break au,
                    StreamEvent::Format(..) => continue,
                    other => panic!("unexpected {other:?} after seeking {name} to {target_ms} ms"),
                }
            };
            // Frame headers state their own sample number, so the landing
            // is reported exactly where playback resumes rather than at
            // the request.
            assert_eq!(au.pts, landed, "{name} at {target_ms} ms");
            // Keyframe-clean means at or before the target, and within the
            // one frame that covers it: 4608 samples = 96 ms.
            assert!(
                au.pts <= target && target.as_micros() - au.pts.as_micros() < 96_000,
                "{name}: seek to {target_ms} ms landed at {} us",
                au.pts.as_micros()
            );
            // Whatever it lands on must be a real frame, not a false sync
            // inside a payload.
            assert!(au.data.len() > 4 && au.data[0] == 0xFF && au.data[1] & 0xFE == 0xF8);
        }

        // Past the end clamps to the last frame rather than erroring.
        let landed = demuxer
            .seek(
                MediaTime::from_micros(60_000_000),
                Generation::default().next(),
            )
            .expect("FLAC seeks past the end");
        assert!(landed < MediaTime::from_micros(6_000_000), "{name} clamped");
    }
}

/// CRC-8 over a FLAC frame header, polynomial 0x07.
fn flac_crc8(data: &[u8]) -> u8 {
    let mut crc = 0u8;
    for &b in data {
        crc ^= b;
        for _ in 0..8 {
            crc = if crc & 0x80 != 0 {
                (crc << 1) ^ 0x07
            } else {
                crc << 1
            };
        }
    }
    crc
}

/// Absolute offsets of the fixture's frames, found the way any FLAC reader
/// finds them: a validated header at each sync. Deliberately independent
/// of the demuxer, so the seek points the next test hands it are derived
/// from the file rather than from the code under test.
fn flac_frame_offsets(data: &[u8], first_frame: usize) -> Vec<usize> {
    let mut offsets = Vec::new();
    let mut i = first_frame;
    while i + 16 < data.len() {
        if data[i] == 0xFF && data[i + 1] & 0xFE == 0xF8 {
            // Fixed block size, single-byte frame number, no extension
            // bytes: the shape ffmpeg writes for these fixtures.
            let bs_bits = data[i + 2] >> 4;
            let chan = data[i + 3] >> 4;
            if bs_bits != 0 && chan <= 10 && data[i + 3] & 1 == 0 && data[i + 4] < 0x80 {
                let len = 5;
                if flac_crc8(&data[i..i + len]) == data[i + len] {
                    offsets.push(i);
                    i += 16;
                    continue;
                }
            }
        }
        i += 1;
    }
    offsets
}

/// Rebuild a FLAC with a SEEKTABLE spliced in after STREAMINFO. ffmpeg's
/// muxer writes none and the reference encoder is not a build dependency,
/// so without this the arm that takes its byte position from the file
/// rather than bisecting for it would never run.
fn flac_with_seektable(src: &[u8], every_n_frames: usize) -> Vec<u8> {
    assert_eq!(&src[..4], b"fLaC");
    let mut blocks: Vec<(u8, Vec<u8>)> = Vec::new();
    let mut i = 4usize;
    let mut max_block = 0u32;
    loop {
        let last = src[i] & 0x80 != 0;
        let kind = src[i] & 0x7F;
        let len =
            usize::from(src[i + 1]) << 16 | usize::from(src[i + 2]) << 8 | usize::from(src[i + 3]);
        let body = src[i + 4..i + 4 + len].to_vec();
        if kind == 0 {
            max_block = u32::from(body[2]) << 8 | u32::from(body[3]);
        }
        assert_ne!(kind, 3, "fixture already carries a SEEKTABLE");
        blocks.push((kind, body));
        i += 4 + len;
        if last {
            break;
        }
    }
    let first_frame = i;

    let offsets = flac_frame_offsets(src, first_frame);
    assert!(offsets.len() > 8, "expected a walkable frame run");
    let mut table = Vec::new();
    for (n, &off) in offsets.iter().enumerate().step_by(every_n_frames) {
        let sample = (n as u64) * u64::from(max_block);
        table.extend_from_slice(&sample.to_be_bytes());
        table.extend_from_slice(&((off - first_frame) as u64).to_be_bytes());
        table.extend_from_slice(&(max_block as u16).to_be_bytes());
    }

    let mut out = b"fLaC".to_vec();
    let rebuilt: Vec<(u8, Vec<u8>)> = std::iter::once(blocks[0].clone())
        .chain(std::iter::once((3u8, table)))
        .chain(blocks[1..].iter().cloned())
        .collect();
    let count = rebuilt.len();
    for (idx, (kind, body)) in rebuilt.into_iter().enumerate() {
        out.push(kind | if idx == count - 1 { 0x80 } else { 0 });
        out.extend_from_slice(&(body.len() as u32).to_be_bytes()[1..]);
        out.extend_from_slice(&body);
    }
    out.extend_from_slice(&src[first_frame..]);
    out
}

/// A source that counts the positioned reads it serves. Over a ranged HTTP
/// source each one is a round trip, so the count is the cost the seek
/// actually pays in the field.
struct CountingSource {
    inner: MemSource,
    reads: std::sync::Arc<std::sync::atomic::AtomicUsize>,
}

impl media_demux::ByteSource for CountingSource {
    fn size(&mut self) -> Result<Option<u64>, media_demux::SourceError> {
        self.inner.size()
    }

    fn read_at(&mut self, offset: u64, buf: &mut [u8]) -> Result<usize, media_demux::SourceError> {
        self.reads
            .fetch_add(1, std::sync::atomic::Ordering::Relaxed);
        self.inner.read_at(offset, buf)
    }
}

/// The SEEKTABLE arm: a file that states its byte positions must land on
/// them directly, not bisect. The landing has to match the tableless file's
/// to the sample (the table is a shortcut to the same frame), and it must
/// cost fewer reads, which is the reason to honour it.
#[test]
fn flac_seeks_by_its_seektable_when_the_file_carries_one() {
    use media_clock::Generation;
    use std::sync::Arc;
    use std::sync::atomic::{AtomicUsize, Ordering};

    let plain = fixture("sine-48k-stereo.flac");
    let tabled = flac_with_seektable(&plain, 4);

    let landings: Vec<(MediaTime, usize)> = [plain.clone(), tabled]
        .into_iter()
        .map(|bytes| {
            let reads = Arc::new(AtomicUsize::new(0));
            let mut demuxer = open_auto(
                Box::new(CountingSource {
                    inner: MemSource(bytes),
                    reads: Arc::clone(&reads),
                }),
                DemuxLimits::default(),
                Generation::default(),
            )
            .expect("open");
            assert_eq!(demuxer.duration(), Some(MediaTime::from_micros(6_000_000)));
            reads.store(0, Ordering::Relaxed);
            let landed = demuxer
                .seek(
                    MediaTime::from_micros(4_000_000),
                    Generation::default().next(),
                )
                .expect("FLAC seeks");
            let au = loop {
                match demuxer.next_event().expect("event") {
                    StreamEvent::Au(au) => break au,
                    StreamEvent::Format(..) => continue,
                    other => panic!("unexpected {other:?}"),
                }
            };
            assert_eq!(au.pts, landed);
            (landed, reads.load(Ordering::Relaxed))
        })
        .collect();

    let (bisected, bisect_reads) = landings[0];
    let (tabled_landing, tabled_reads) = landings[1];
    assert_eq!(
        tabled_landing, bisected,
        "the table must reach the same frame the bisection does"
    );
    assert!(
        tabled_reads < bisect_reads,
        "the table has to save reads to be worth honouring \
         (table {tabled_reads}, bisection {bisect_reads})"
    );
}

/// A FLAC `PICTURE` block, which is also the payload of Ogg's
/// `METADATA_BLOCK_PICTURE`.
fn picture_block(mime: &str, data: &[u8]) -> Vec<u8> {
    let mut out = 3u32.to_be_bytes().to_vec(); // front cover
    out.extend_from_slice(&(mime.len() as u32).to_be_bytes());
    out.extend_from_slice(mime.as_bytes());
    out.extend_from_slice(&0u32.to_be_bytes()); // no description
    out.extend_from_slice(&[0u8; 16]); // width, height, depth, colours
    out.extend_from_slice(&(data.len() as u32).to_be_bytes());
    out.extend_from_slice(data);
    out
}

/// Splice a metadata block into a FLAC ahead of its frames.
fn flac_with_block(src: &[u8], kind: u8, body: &[u8]) -> Vec<u8> {
    let mut blocks: Vec<(u8, Vec<u8>)> = Vec::new();
    let mut i = 4usize;
    loop {
        let last = src[i] & 0x80 != 0;
        let k = src[i] & 0x7F;
        let len =
            usize::from(src[i + 1]) << 16 | usize::from(src[i + 2]) << 8 | usize::from(src[i + 3]);
        blocks.push((k, src[i + 4..i + 4 + len].to_vec()));
        i += 4 + len;
        if last {
            break;
        }
    }
    blocks.push((kind, body.to_vec()));
    let mut out = b"fLaC".to_vec();
    let count = blocks.len();
    for (idx, (k, body)) in blocks.into_iter().enumerate() {
        out.push(k | if idx == count - 1 { 0x80 } else { 0 });
        out.extend_from_slice(&(body.len() as u32).to_be_bytes()[1..]);
        out.extend_from_slice(&body);
    }
    out.extend_from_slice(&src[i..]);
    out
}

/// The art has to survive the whole open path, not just the parser: for
/// FLAC that means the metadata walk still finds STREAMINFO and the first
/// frame with a picture block sitting between them.
#[test]
fn flac_carries_its_embedded_cover_art() {
    use media_clock::Generation;

    let art = b"\x89PNG\r\n\x1a\nfake png body";
    let bytes = flac_with_block(
        &fixture("sine-48k-stereo.flac"),
        6,
        &picture_block("image/png", art),
    );
    let mut demuxer = open_auto(
        Box::new(MemSource(bytes)),
        DemuxLimits::default(),
        Generation::default(),
    )
    .expect("open");

    let found = demuxer.artwork().expect("art surfaced");
    assert_eq!(found.mime, "image/png");
    assert_eq!(found.data, art);
    // The picture must not disturb the audio it sits beside.
    assert_eq!(demuxer.duration(), Some(MediaTime::from_micros(6_000_000)));
    let mut aus = 0;
    loop {
        match demuxer.next_event().expect("event") {
            StreamEvent::Au(_) => aus += 1,
            StreamEvent::Eos(_) => break,
            _ => {}
        }
    }
    assert_eq!(aus, 63);
}

/// MP3's tag is skipped by the frame walk, so the art has to be lifted out
/// before the skip and the skip has to still land on the first frame.
#[test]
fn mp3_carries_art_from_its_id3_tag() {
    use media_clock::Generation;

    let art = b"jpeg body bytes";
    let mut frame = vec![0u8]; // latin-1 text encoding
    frame.extend_from_slice(b"image/jpeg");
    frame.push(0);
    frame.push(3); // front cover
    frame.push(0); // empty description
    frame.extend_from_slice(art);

    let mut body = b"APIC".to_vec();
    body.extend_from_slice(&(frame.len() as u32).to_be_bytes()); // v2.3 plain size
    body.extend_from_slice(&[0, 0]);
    body.extend_from_slice(&frame);

    let n = body.len() as u32;
    let mut tag = b"ID3\x03\x00\x00".to_vec();
    tag.extend_from_slice(&[
        ((n >> 21) & 0x7F) as u8,
        ((n >> 14) & 0x7F) as u8,
        ((n >> 7) & 0x7F) as u8,
        (n & 0x7F) as u8,
    ]);
    tag.extend_from_slice(&body);
    tag.extend_from_slice(&fixture("sine-48k-stereo.mp3"));

    let mut demuxer = open_auto(
        Box::new(MemSource(tag)),
        DemuxLimits::default(),
        Generation::default(),
    )
    .expect("open");
    let found = demuxer.artwork().expect("art surfaced");
    assert_eq!(found.mime, "image/jpeg");
    assert_eq!(found.data, art);

    // The tagged file must still demux exactly as the untagged one does.
    let mut aus = 0;
    loop {
        match demuxer.next_event().expect("event") {
            StreamEvent::Au(_) => aus += 1,
            StreamEvent::Eos(_) => break,
            _ => {}
        }
    }
    assert_eq!(aus, 251);
}

/// Splice a `udta/meta/ilst/covr` chain onto the end of an MP4's `moov`.
/// Safe only because these fixtures put `moov` last: growing it cannot
/// move `mdat`, so the sample offsets the tables state stay true.
fn mp4_with_cover(src: &[u8], picture: &[u8]) -> Vec<u8> {
    fn atom(kind: &[u8; 4], body: &[u8]) -> Vec<u8> {
        let mut out = ((body.len() + 8) as u32).to_be_bytes().to_vec();
        out.extend_from_slice(kind);
        out.extend_from_slice(body);
        out
    }

    // `data`: a type indicator (13 = image) and four reserved bytes.
    let mut data_body = 13u32.to_be_bytes().to_vec();
    data_body.extend_from_slice(&0u32.to_be_bytes());
    data_body.extend_from_slice(picture);
    let covr = atom(b"covr", &atom(b"data", &data_body));
    let ilst = atom(b"ilst", &covr);

    // `hdlr` must state the `mdir` handler or the metadata is not the
    // iTunes flavour and carries no item list.
    let mut hdlr_body = vec![0u8; 8]; // version/flags, predefined
    hdlr_body.extend_from_slice(b"mdir");
    hdlr_body.extend_from_slice(b"appl");
    hdlr_body.extend_from_slice(&[0u8; 9]);
    let hdlr = atom(b"hdlr", &hdlr_body);

    // `meta` is a full box: version and flags before its children.
    let mut meta_body = vec![0u8; 4];
    meta_body.extend_from_slice(&hdlr);
    meta_body.extend_from_slice(&ilst);
    let udta = atom(b"udta", &atom(b"meta", &meta_body));

    // Find moov, append udta inside it, and grow its stated size.
    let mut at = 0usize;
    while at + 8 <= src.len() {
        let size = u32::from_be_bytes(src[at..at + 4].try_into().expect("sliced 4")) as usize;
        let kind = &src[at + 4..at + 8];
        if kind == b"moov" {
            let end = at + size;
            let mut out = src[..end].to_vec();
            out.extend_from_slice(&udta);
            let grown = ((size + udta.len()) as u32).to_be_bytes();
            out[at..at + 4].copy_from_slice(&grown);
            out.extend_from_slice(&src[end..]);
            return out;
        }
        if size < 8 {
            break;
        }
        at += size;
    }
    panic!("no moov in the fixture");
}

/// MP4's art sits in the iTunes metadata atom, which the box parser
/// already walks. This pins that it reaches the surface, and that the
/// format is sniffed from the bytes rather than trusted, since `covr`
/// states only "image".
#[test]
fn mp4_carries_cover_art_from_its_itunes_atom() {
    use media_clock::Generation;

    // A PNG signature and a little body: `covr` states only "image", so
    // the magic is what the format is read from.
    let png: &[u8] = &[
        0x89, b'P', b'N', b'G', 0x0D, 0x0A, 0x1A, 0x0A, b'b', b'o', b'd', b'y',
    ];
    let bytes = mp4_with_cover(&fixture("aac-48k-stereo.m4a"), png);
    let mut demuxer = open_auto(
        Box::new(MemSource(bytes)),
        DemuxLimits::default(),
        Generation::default(),
    )
    .expect("open");

    let found = demuxer.artwork().expect("art surfaced");
    assert_eq!(found.mime, "image/png");
    assert_eq!(found.data, png);

    // The added metadata must not disturb the samples it sits beside.
    let mut aus = 0;
    loop {
        match demuxer.next_event().expect("event") {
            StreamEvent::Au(_) => aus += 1,
            StreamEvent::Eos(_) => break,
            _ => {}
        }
    }
    assert!(aus > 0, "audio still demuxes past the added metadata");
}

/// Containers that carry no picture must say so rather than inventing one.
#[test]
fn a_file_without_art_reports_none() {
    use media_clock::Generation;

    for name in [
        "sine-48k-stereo.flac",
        "sine-48k-stereo.mp3",
        "sine-48k-stereo.opus",
        "aac-48k-stereo.m4a",
    ] {
        let demuxer = open_auto(
            Box::new(MemSource(fixture(name))),
            DemuxLimits::default(),
            Generation::default(),
        )
        .expect("open");
        assert!(
            demuxer.artwork().is_none(),
            "{name} reported art it has none of"
        );
    }
}

/// ADTS states neither a length nor a frame count, so both the duration
/// and the landing are estimates off the leading frames' byte rate. The
/// fixture is CBR, so the estimate should be close.
#[test]
fn adts_seeks_by_its_byte_rate_estimate() {
    use media_clock::Generation;

    let mut demuxer = open_auto(
        Box::new(MemSource(fixture("sine-48k-stereo.aac"))),
        DemuxLimits::default(),
        Generation::default(),
    )
    .expect("open");

    // 283 frames of 1024 samples at 48 kHz is 6.037 s of audio; the byte
    // estimate has to land within a frame or two of that.
    let duration = demuxer.duration().expect("a duration from the byte rate");
    let stored = 283 * 1_024 * 1_000_000 / 48_000;
    assert!(
        (duration.as_micros() - stored).abs() < 100_000,
        "duration estimated at {} us against {stored} us stored",
        duration.as_micros()
    );

    for target_ms in [0i64, 1_500, 4_000] {
        let target = MediaTime::from_micros(target_ms * 1000);
        let landed = demuxer
            .seek(target, Generation::default().next())
            .expect("ADTS seeks");
        // The stream restarts on a frame boundary, so the request is
        // rounded down to one: 1024 samples is 21.3 ms.
        assert!(
            landed <= target && target.as_micros() - landed.as_micros() < 21_334,
            "seek to {target_ms} ms reported {} us",
            landed.as_micros()
        );

        let au = loop {
            match demuxer.next_event().expect("event") {
                StreamEvent::Au(au) => break au,
                StreamEvent::Format(..) => continue,
                other => panic!("unexpected {other:?} after seeking to {target_ms} ms"),
            }
        };
        assert_eq!(au.pts, landed, "the timeline re-anchors on the landing");
        // The payload is a stripped raw AAC frame, so its own bytes carry
        // no sync word to check; what pins the landing as a real frame is
        // that the walk continues cleanly from it.
        assert!(!au.data.is_empty());
        let next = match demuxer.next_event().expect("event") {
            StreamEvent::Au(au) => au,
            other => panic!("unexpected {other:?} after the landing frame"),
        };
        assert_eq!(
            next.pts - au.pts,
            MediaTime::from_micros(1_024 * 1_000_000 / 48_000)
        );
    }
}

#[test]
fn ogg_opus_seeks_by_granule_bisection() {
    use media_clock::Generation;

    let mut demuxer = open_auto(
        Box::new(MemSource(fixture("sine-48k-stereo.opus"))),
        DemuxLimits::default(),
        Generation::default(),
    )
    .expect("open");

    // The last page's granule states the length. ffprobe reports 6.0065 s
    // for the same file because it counts the priming samples; those never
    // reach the output, so the playable duration is the one reported,
    // the same pre-skip convention the packet timestamps use.
    let duration = demuxer.duration().expect("a duration from the tail page");
    assert_eq!(duration, MediaTime::from_micros(6_000_000));

    for target_ms in [500i64, 2_000, 4_000] {
        let target = MediaTime::from_micros(target_ms * 1000);
        let landed = demuxer
            .seek(target, Generation::default().next())
            .expect("Ogg seeks");
        assert_eq!(landed, target, "the landing is reported at the request");

        let au = loop {
            match demuxer.next_event().expect("event") {
                StreamEvent::Au(au) => break au,
                StreamEvent::Format(..) => continue,
                other => panic!("unexpected {other:?} after seeking to {target_ms} ms"),
            }
        };
        assert_eq!(au.pts, target);
        assert!(!au.data.is_empty());
    }

    // Past the end lands cleanly rather than erroring: the bisection has
    // no page beyond the last one, which is a refusal, not a panic.
    let past = demuxer.seek(
        MediaTime::from_micros(60_000_000),
        Generation::default().next(),
    );
    assert!(past.is_ok() || matches!(past, Err(DemuxError::Parse(_))));
}
