//! `conformance`: diff the demuxer's AU stream against the ffprobe oracle:
//! announce, count, timestamps to 1 µs, and payload MD5 for
//! every packet (raw stored payloads, so keyframes need no exemption).
//!
//! Needs `ffprobe` on PATH; the fixture set is the committed `fixtures/`
//! directory or any file/directory argument.

use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::process::{Command, ExitCode};

use md5::Digest as _;
use media_clock::Generation;
use media_demux::{AudioCodec, DemuxLimits, Demuxer, Format, Mp4Demuxer, StreamEvent, TsDemuxer};

const PTS_TOLERANCE_US: i64 = 1;

pub fn run(fixture: &Path) -> ExitCode {
    let fixtures: Vec<PathBuf> = if fixture.is_dir() {
        let mut entries: Vec<PathBuf> = std::fs::read_dir(fixture)
            .expect("fixture dir readable")
            .filter_map(|e| e.ok())
            .map(|e| e.path())
            .filter(|p| {
                p.extension()
                    .is_some_and(|ext| matches!(ext.to_str(), Some("mp4" | "mov" | "ts" | "m2ts")))
            })
            .collect();
        entries.sort();
        entries
    } else {
        vec![fixture.to_path_buf()]
    };
    if fixtures.is_empty() {
        eprintln!("conformance: no fixtures under {}", fixture.display());
        return ExitCode::FAILURE;
    }

    let mut failed = false;
    for path in &fixtures {
        match check_fixture(path) {
            Ok(summary) => println!("PASS {} ({summary})", path.display()),
            Err(reason) => {
                failed = true;
                println!("FAIL {}: {reason}", path.display());
            }
        }
    }
    if failed {
        ExitCode::FAILURE
    } else {
        ExitCode::SUCCESS
    }
}

struct AuRecord {
    pts_us: i64,
    key: bool,
    size: usize,
    md5: String,
}

fn check_fixture(path: &Path) -> Result<String, String> {
    // Our side: raw stored payloads, decode order per track. TS leaves the
    // demuxer as stored already (Annex-B video, and ADTS audio in raw
    // mode); MP4 needs raw mode to skip the Annex-B conversion.
    let is_ts = path
        .extension()
        .is_some_and(|ext| ext == "ts" || ext == "m2ts");
    let source = crate::open_source(&path.to_string_lossy(), true)?;
    let mut demux: Box<dyn Demuxer> = if is_ts {
        let mut demux = TsDemuxer::open(source, DemuxLimits::default(), Generation(0))
            .map_err(|e| format!("demux open: {e}"))?;
        demux.set_emit_raw_audio(true);
        Box::new(demux)
    } else {
        let mut demux = Mp4Demuxer::open(source, DemuxLimits::default(), Generation(0))
            .map_err(|e| format!("demux open: {e}"))?;
        demux.set_emit_raw_video(true);
        Box::new(demux)
    };

    let mut records: Vec<(media_demux::TrackId, AuRecord)> = Vec::new();
    let mut our_dims = (0u32, 0u32);
    let mut our_audio_fmt = (0u32, 0u32);
    let mut our_audio_codec = None;
    loop {
        match demux.next_event().map_err(|e| format!("demux: {e}"))? {
            StreamEvent::Format(
                _,
                Format::Video {
                    display_width,
                    display_height,
                    ..
                },
            ) => our_dims = (display_width, display_height),
            StreamEvent::Format(
                _,
                Format::Audio {
                    codec,
                    sample_rate,
                    channels,
                    ..
                },
            ) => {
                our_audio_fmt = (sample_rate, channels);
                our_audio_codec = Some(codec);
            }
            StreamEvent::Au(au) => {
                let record = AuRecord {
                    pts_us: au.pts.as_micros(),
                    key: au.key,
                    size: au.data.len(),
                    md5: format!("{:x}", md5::Md5::digest(&au.data)),
                };
                records.push((au.track, record));
            }
            StreamEvent::Eos(_) => break,
            _ => {}
        }
    }
    // Track identity is only final at EOS for TS (the PMT names the PIDs
    // mid-stream), so partition after the pull loop.
    let video_track = demux.video_track();
    let audio_track = demux.audio_track();
    let mut ours_video: Vec<AuRecord> = Vec::new();
    let mut ours_audio: Vec<AuRecord> = Vec::new();
    for (track, record) in records {
        if Some(track) == video_track {
            ours_video.push(record);
        } else if Some(track) == audio_track {
            ours_audio.push(record);
        }
    }

    // Oracle side.
    let oracle = ffprobe(path)?;
    let shifts = if is_ts {
        HashMap::new()
    } else {
        ffmpeg_presentation_shifts(path)
    };
    let shift_of =
        |track: Option<media_demux::TrackId>| track.and_then(|t| shifts.get(&t.0)).copied();
    let mut checks = Vec::new();

    if let Some(stream) = oracle.streams.iter().find(|s| s.codec_type == "video") {
        if stream.codec_name != "h264" {
            return Err(format!("oracle video codec {}", stream.codec_name));
        }
        if (stream.width.unwrap_or(0), stream.height.unwrap_or(0)) != our_dims {
            return Err(format!(
                "announce: video {}x{} vs oracle {}x{}",
                our_dims.0,
                our_dims.1,
                stream.width.unwrap_or(0),
                stream.height.unwrap_or(0)
            ));
        }
        let packets = without_trailing_discards(oracle.packets_for(stream.index));
        diff_stream("video", &ours_video, &packets, true, shift_of(video_track))?;
        checks.push(format!("video {} AUs", ours_video.len()));
    } else if !ours_video.is_empty() {
        return Err("we demuxed video the oracle does not see".into());
    }

    if let Some(stream) = oracle.streams.iter().find(|s| s.codec_type == "audio") {
        let lpcm = our_audio_codec == Some(AudioCodec::Pcm);
        let codec_ok = match stream.codec_name.as_str() {
            "aac" => our_audio_codec == Some(AudioCodec::Aac),
            "mp3" => our_audio_codec == Some(AudioCodec::Mp3),
            "pcm_bluray" => lpcm,
            other => return Err(format!("oracle audio codec {other}")),
        };
        if !codec_ok {
            return Err(format!(
                "announce: audio codec {our_audio_codec:?} vs oracle {}",
                stream.codec_name
            ));
        }
        let oracle_fmt = (
            stream
                .sample_rate
                .as_deref()
                .and_then(|s| s.parse().ok())
                .unwrap_or(0u32),
            stream.channels.unwrap_or(0),
        );
        if oracle_fmt != our_audio_fmt {
            return Err(format!(
                "announce: audio {}Hz/{}ch vs oracle {}Hz/{}ch",
                our_audio_fmt.0, our_audio_fmt.1, oracle_fmt.0, oracle_fmt.1
            ));
        }
        if lpcm {
            // LPCM has no canonical packetisation (we emit per PES, ffmpeg
            // re-chunks), so per-packet comparisons are meaningless, but
            // emitting nothing at all is still a failure.
            if ours_audio.is_empty() {
                return Err("no LPCM frames emitted".into());
            }
            checks.push(format!(
                "audio {} LPCM frames (announce only)",
                ours_audio.len()
            ));
        } else {
            let packets = without_trailing_discards(oracle.packets_for(stream.index));
            diff_stream("audio", &ours_audio, &packets, false, shift_of(audio_track))?;
            checks.push(format!("audio {} AUs", ours_audio.len()));
        }
    } else if !ours_audio.is_empty() {
        return Err("we demuxed audio the oracle does not see".into());
    }

    Ok(checks.join(", "))
}

/// Where an edit list ends a track early, FFmpeg keeps samples past the
/// end, flagged discard, up to a cut-off of its own. Once every sample it
/// shows is behind them, none of those is a reference for one it shows, and
/// the demuxer stops at the last sample shown, so they are not compared.
fn without_trailing_discards(mut packets: Vec<OraclePacket>) -> Vec<OraclePacket> {
    while packets
        .last()
        .is_some_and(|p| p.flags.as_deref().is_some_and(|f| f.contains('D')))
    {
        packets.pop();
    }
    packets
}

fn diff_stream(
    kind: &str,
    ours: &[AuRecord],
    oracle: &[OraclePacket],
    check_keys: bool,
    ffmpeg_shift: Option<i64>,
) -> Result<(), String> {
    if ours.len() != oracle.len() {
        return Err(format!(
            "{kind} count: {} vs oracle {}",
            ours.len(),
            oracle.len()
        ));
    }
    let ffmpeg_shift = ffmpeg_shift.unwrap_or(0);
    for (i, (au, packet)) in ours.iter().zip(oracle).enumerate() {
        let oracle_pts = packet.pts_us();
        if (au.pts_us + ffmpeg_shift - oracle_pts).abs() > PTS_TOLERANCE_US {
            return Err(format!(
                "{kind} pts[{i}]: {} vs oracle {oracle_pts}",
                au.pts_us
            ));
        }
        if au.size != packet.size_bytes() {
            return Err(format!(
                "{kind} size[{i}]: {} vs oracle {}",
                au.size,
                packet.size_bytes()
            ));
        }
        if let Some(hash) = packet.md5()
            && au.md5 != hash
        {
            return Err(format!("{kind} md5[{i}]: {} vs oracle {hash}", au.md5));
        }
        if check_keys {
            let oracle_key = packet.flags.as_deref().is_some_and(|f| f.contains('K'));
            if au.key != oracle_key {
                return Err(format!(
                    "{kind} keyframe[{i}]: {} vs oracle {oracle_key}",
                    au.key
                ));
            }
        }
    }
    Ok(())
}

/// FFmpeg's MP4 reader presents a track later by its largest negative
/// composition offset (`dts_shift`), so that no sample presents before it
/// decodes; the demuxer keeps the file's own times. The offsets are read
/// from the file's `ctts` and `trun` boxes as FFmpeg's `mov_read_ctts` and
/// `mov_read_trun` read them: signed, whatever the box's version, since
/// this predicts ffprobe rather than reading the spec. The shift is given
/// per track ID in microseconds; a track stating no negative offset is
/// absent and compared as it is.
fn ffmpeg_presentation_shifts(path: &Path) -> HashMap<u32, i64> {
    let Ok(data) = std::fs::read(path) else {
        return HashMap::new();
    };
    let mut timescales = HashMap::new();
    let mut lowest: HashMap<u32, i64> = HashMap::new();
    for (kind, body) in boxes(&data) {
        match kind {
            b"moov" => {
                for (kind, trak) in boxes(body) {
                    if kind != b"trak" {
                        continue;
                    }
                    let Some(id) = find(trak, &[b"tkhd"]).and_then(|b| full_box_u32(b, 12, 20))
                    else {
                        continue;
                    };
                    if let Some(scale) =
                        find(trak, &[b"mdia", b"mdhd"]).and_then(|b| full_box_u32(b, 12, 20))
                    {
                        timescales.insert(id, scale);
                    }
                    if let Some(ctts) = find(trak, &[b"mdia", b"minf", b"stbl", b"ctts"]) {
                        let count = be32(ctts, 4).unwrap_or(0) as usize;
                        for i in 0..count {
                            let Some(offset) = be32(ctts, 12 + 8 * i) else {
                                break;
                            };
                            lower(&mut lowest, id, i64::from(offset as i32));
                        }
                    }
                }
            }
            b"moof" => {
                for (kind, traf) in boxes(body) {
                    if kind != b"traf" {
                        continue;
                    }
                    let Some(id) = find(traf, &[b"tfhd"]).and_then(|b| be32(b, 4)) else {
                        continue;
                    };
                    for (kind, trun) in boxes(traf) {
                        if kind != b"trun" {
                            continue;
                        }
                        let flags = be32(trun, 0).unwrap_or(0) & 0x00ff_ffff;
                        let count = be32(trun, 4).unwrap_or(0) as usize;
                        if flags & 0x800 == 0 {
                            continue;
                        }
                        let mut at = 8
                            + 4 * usize::from(flags & 0x1 != 0)
                            + 4 * usize::from(flags & 0x4 != 0);
                        let before = 4 * [0x100, 0x200, 0x400]
                            .iter()
                            .filter(|&&field| flags & field != 0)
                            .count();
                        for _ in 0..count {
                            at += before;
                            let Some(offset) = be32(trun, at) else {
                                break;
                            };
                            lower(&mut lowest, id, i64::from(offset as i32));
                            at += 4;
                        }
                    }
                }
            }
            _ => {}
        }
    }
    lowest
        .into_iter()
        .filter(|&(_, offset)| offset < 0)
        .filter_map(|(id, offset)| {
            let scale = i64::from(*timescales.get(&id)?);
            (scale > 0).then(|| (id, (-offset * 1_000_000 + scale / 2) / scale))
        })
        .collect()
}

fn lower(lowest: &mut HashMap<u32, i64>, id: u32, offset: i64) {
    let entry = lowest.entry(id).or_insert(offset);
    *entry = (*entry).min(offset);
}

/// The boxes laid end to end in `data`, as (type, body); a box running
/// past the end, or stating a size shorter than its own header, stops the
/// list.
fn boxes(data: &[u8]) -> impl Iterator<Item = (&[u8], &[u8])> {
    let mut at = 0usize;
    std::iter::from_fn(move || {
        let size = be32(data, at)? as usize;
        let kind = data.get(at + 4..at + 8)?;
        let (body, next) = match size {
            1 => {
                let large = u64::from_be_bytes(data.get(at + 8..at + 16)?.try_into().ok()?);
                (at + 16, at.checked_add(usize::try_from(large).ok()?)?)
            }
            0 => (at + 8, data.len()),
            size => (at + 8, at.checked_add(size)?),
        };
        if next < body || next > data.len() {
            return None;
        }
        at = next;
        Some((kind, &data[body..next]))
    })
}

/// The body of the box at `path` below `data`.
fn find<'a>(data: &'a [u8], path: &[&[u8; 4]]) -> Option<&'a [u8]> {
    let (first, rest) = path.split_first()?;
    let (_, body) = boxes(data).find(|(kind, _)| kind == first)?;
    if rest.is_empty() {
        Some(body)
    } else {
        find(body, rest)
    }
}

/// A full box's field at `v0` bytes into its body, or at `v1` in a
/// version 1 box.
fn full_box_u32(body: &[u8], v0: usize, v1: usize) -> Option<u32> {
    be32(body, if *body.first()? == 1 { v1 } else { v0 })
}

fn be32(data: &[u8], at: usize) -> Option<u32> {
    Some(u32::from_be_bytes(data.get(at..at + 4)?.try_into().ok()?))
}

#[derive(serde::Deserialize)]
struct OracleOutput {
    #[serde(default)]
    packets: Vec<OraclePacket>,
    #[serde(default)]
    streams: Vec<OracleStream>,
}

#[derive(serde::Deserialize)]
struct OraclePacket {
    stream_index: u32,
    pts_time: Option<String>,
    size: String,
    data_hash: Option<String>,
    flags: Option<String>,
}

#[derive(serde::Deserialize)]
struct OracleStream {
    index: u32,
    codec_type: String,
    codec_name: String,
    width: Option<u32>,
    height: Option<u32>,
    sample_rate: Option<String>,
    channels: Option<u32>,
}

impl OracleOutput {
    fn packets_for(&self, stream_index: u32) -> Vec<OraclePacket> {
        self.packets
            .iter()
            .filter(|p| p.stream_index == stream_index)
            .map(|p| OraclePacket {
                stream_index: p.stream_index,
                pts_time: p.pts_time.clone(),
                size: p.size.clone(),
                data_hash: p.data_hash.clone(),
                flags: p.flags.clone(),
            })
            .collect()
    }
}

impl OraclePacket {
    fn pts_us(&self) -> i64 {
        self.pts_time
            .as_deref()
            .and_then(|s| s.parse::<f64>().ok())
            .map(|s| (s * 1e6).round() as i64)
            .unwrap_or(i64::MIN)
    }

    fn size_bytes(&self) -> usize {
        self.size.parse().unwrap_or(0)
    }

    fn md5(&self) -> Option<String> {
        self.data_hash
            .as_deref()
            .and_then(|h| h.split(':').nth(1))
            .map(str::to_ascii_lowercase)
    }
}

fn ffprobe(path: &Path) -> Result<OracleOutput, String> {
    let output = Command::new("ffprobe")
        .args([
            "-v",
            "error",
            "-show_streams",
            "-show_packets",
            "-show_data_hash",
            "md5",
            "-of",
            "json",
        ])
        .arg(path)
        .output()
        .map_err(|e| format!("ffprobe not runnable: {e}"))?;
    if !output.status.success() {
        return Err(format!(
            "ffprobe failed: {}",
            String::from_utf8_lossy(&output.stderr)
        ));
    }
    serde_json::from_slice(&output.stdout).map_err(|e| format!("ffprobe json: {e}"))
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A size too small for the header it sits in, in either width, ends
    /// the list there rather than slicing a body that ends before it
    /// begins.
    #[test]
    fn a_box_shorter_than_its_header_stops_the_list() {
        let mut short = 4u32.to_be_bytes().to_vec();
        short.extend_from_slice(b"free");
        short.extend_from_slice(&[0; 8]);
        assert_eq!(boxes(&short).count(), 0);

        let mut large = 1u32.to_be_bytes().to_vec();
        large.extend_from_slice(b"free");
        large.extend_from_slice(&8u64.to_be_bytes());
        large.extend_from_slice(&[0; 8]);
        assert_eq!(boxes(&large).count(), 0);

        let mut whole = 8u32.to_be_bytes().to_vec();
        whole.extend_from_slice(b"free");
        assert_eq!(boxes(&whole).count(), 1, "an empty box is still a box");
    }
}
