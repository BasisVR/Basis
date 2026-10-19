//! Decode the committed fixture's AAC track through the in-box MFT
//! headless, checking the configuration it needs (raw payload,
//! HEAACWAVEINFO blob, float output ranking) against the real decoder.

#![cfg(windows)]

use decode_mf::AacDecoder;
use media_clock::Generation;
use media_decode::{AudioDecoder, SubmitOutcome};
use media_demux::{DemuxLimits, Demuxer, Format, MemSource, Mp4Demuxer, StreamEvent};

#[test]
fn fixture_aac_track_decodes_to_pcm() {
    let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../../fixtures/h264-aac-640x360-30fps.mp4");
    let bytes = std::fs::read(path).expect("fixture readable");
    let mut demux = Mp4Demuxer::open(
        Box::new(MemSource(bytes)),
        DemuxLimits::default(),
        Generation(1),
    )
    .expect("demux opens");

    // Collect the audio format + AUs.
    let mut format = None;
    let mut aus = Vec::new();
    loop {
        match demux.next_event().expect("event") {
            StreamEvent::Format(_, f @ Format::Audio { .. }) => format = Some(f),
            StreamEvent::Au(au) if !au.data.starts_with(&[0, 0, 0, 1]) => aus.push(au),
            StreamEvent::Eos(_) => break,
            _ => {}
        }
    }
    let Some(Format::Audio {
        sample_rate,
        channels,
        codec_private,
        ..
    }) = format
    else {
        panic!("no audio format");
    };
    assert_eq!(aus.len(), 283);

    let mut decoder =
        AacDecoder::new(sample_rate, channels, &codec_private).expect("AAC MFT configures");
    let (out_rate, out_channels) = decoder.output_format();
    assert_eq!(out_rate, 48000);
    assert_eq!(out_channels, 2);

    let mut total_frames = 0i64;
    let mut first_pts = None;
    let mut submitted = 0usize;
    let mut drained = false;
    loop {
        match decoder.try_output().expect("decode") {
            Some(chunk) => {
                assert_eq!(chunk.channels, 2);
                assert_eq!(chunk.sample_rate, 48000);
                first_pts.get_or_insert(chunk.pts_us);
                total_frames += chunk.data.len() as i64 / i64::from(chunk.channels);
                continue;
            }
            None if drained => break,
            None => {}
        }
        if submitted < aus.len() {
            let au = &aus[submitted];
            match decoder
                .submit(&au.data, au.pts.as_micros())
                .expect("submit")
            {
                SubmitOutcome::Accepted => submitted += 1,
                SubmitOutcome::NotAccepting => {}
            }
        } else if !drained {
            decoder.begin_drain().expect("drain");
            drained = true;
        }
    }

    // 283 AUs x 1024 samples, minus whatever the decoder holds back as
    // priming: expect within a few frames of the 6 s content.
    assert!(
        total_frames > 280 * 1024 && total_frames <= 283 * 1024,
        "unexpected frame total {total_frames}"
    );
    // The first chunk's timestamp tracks the (negative, priming) input pts.
    let first = first_pts.expect("produced output");
    assert!(first <= 0, "first pts {first}");

    let silence = total_frames == 0;
    assert!(!silence);
}
