//! Each `mp4a` track's AudioSpecificConfig, byte for byte. The box
//! parser keeps three fields decoded from the config's first two bytes,
//! and a config rebuilt from those loses everything after the channel
//! configuration: for HE-AAC that is the SBR rate and the core's object
//! type, without which a decoder refuses the stream. This walks back
//! down to the `esds` for the bytes themselves.

use std::collections::HashMap;
use std::io::{Read, Seek, SeekFrom};

use crate::source::SourceReader;

/// Top-level boxes looked at before `moov`, as the prefix scan allows.
const MAX_TOP_LEVEL: usize = 64;
/// Children read from one container box.
const MAX_CHILDREN: usize = 256;
/// Largest `esds` read, well above any real one.
const MAX_ESDS_BYTES: u64 = 4096;

#[derive(Clone)]
struct Child {
    kind: [u8; 4],
    body: u64,
    end: u64,
}

/// Configs by track id, for every track whose first sample entry is
/// `mp4a` and states one. A track the walk cannot reach is left out.
pub(crate) fn audio_configs(reader: &mut SourceReader<'_>, len: u64) -> HashMap<u32, Vec<u8>> {
    let mut configs = HashMap::new();
    let top = children(reader, 0, len, MAX_TOP_LEVEL, Some(b"moov"));
    let Some(moov) = top.iter().find(|c| &c.kind == b"moov") else {
        return configs;
    };
    for trak in children(reader, moov.body, moov.end, MAX_CHILDREN, None) {
        if &trak.kind != b"trak" {
            continue;
        }
        if let Some((id, config)) = track_config(reader, &trak) {
            configs.insert(id, config);
        }
    }
    configs
}

fn track_config(reader: &mut SourceReader<'_>, trak: &Child) -> Option<(u32, Vec<u8>)> {
    let boxes = children(reader, trak.body, trak.end, MAX_CHILDREN, None);
    let tkhd = find(&boxes, b"tkhd")?;
    let mut version = [0u8; 1];
    read_at(reader, tkhd.body, &mut version)?;
    // Version, flags, then two times of four bytes each, or of eight.
    let id_at = tkhd.body + if version[0] == 1 { 20 } else { 12 };
    let mut id = [0u8; 4];
    read_at(reader, id_at, &mut id)?;

    let mut container = find(&boxes, b"mdia")?.clone();
    for kind in [b"minf", b"stbl", b"stsd"] {
        let inside = children(reader, container.body, container.end, MAX_CHILDREN, None);
        container = find(&inside, kind)?.clone();
    }
    // The first sample entry follows the full-box header and the count.
    let entry = children(reader, container.body + 8, container.end, 1, None)
        .pop()
        .filter(|entry| &entry.kind == b"mp4a")?;
    // SampleEntry and AudioSampleEntry are 28 bytes; a QuickTime sound
    // description of version 1 or 2 adds 16 or 36.
    let mut version = [0u8; 2];
    read_at(reader, entry.body + 8, &mut version)?;
    let skip = match u16::from_be_bytes(version) {
        1 => 44,
        2 => 64,
        _ => 28,
    };
    let mut inside = children(reader, entry.body + skip, entry.end, MAX_CHILDREN, None);
    if find(&inside, b"esds").is_none()
        && let Some(wave) = find(&inside, b"wave").cloned()
    {
        inside = children(reader, wave.body, wave.end, MAX_CHILDREN, None);
    }
    let esds = find(&inside, b"esds")?;
    let size = esds.end - esds.body;
    if size > MAX_ESDS_BYTES {
        return None;
    }
    let mut body = vec![0u8; size as usize];
    read_at(reader, esds.body, &mut body)?;
    let config = decoder_specific_info(&body).filter(|c| !c.is_empty())?;
    Some((u32::from_be_bytes(id), config.to_vec()))
}

fn find<'a>(boxes: &'a [Child], kind: &[u8; 4]) -> Option<&'a Child> {
    boxes.iter().find(|c| &c.kind == kind)
}

fn read_at(reader: &mut SourceReader<'_>, at: u64, buf: &mut [u8]) -> Option<()> {
    reader.seek(SeekFrom::Start(at)).ok()?;
    reader.read_exact(buf).ok()
}

/// The boxes laid end to end in `from..end`, stopping after `stop` when
/// it is found. A header that does not fit ends the walk.
fn children(
    reader: &mut SourceReader<'_>,
    from: u64,
    end: u64,
    limit: usize,
    stop: Option<&[u8; 4]>,
) -> Vec<Child> {
    let mut found = Vec::new();
    let mut pos = from;
    while found.len() < limit && end.saturating_sub(pos) >= 8 {
        let mut header = [0u8; 8];
        if read_at(reader, pos, &mut header).is_none() {
            break;
        }
        let size = u64::from(u32::from_be_bytes([
            header[0], header[1], header[2], header[3],
        ]));
        let kind = [header[4], header[5], header[6], header[7]];
        let (body, size) = match size {
            0 => (pos + 8, end - pos),
            1 => {
                let mut large = [0u8; 8];
                if end - pos < 16 || read_at(reader, pos + 8, &mut large).is_none() {
                    break;
                }
                (pos + 16, u64::from_be_bytes(large))
            }
            size => (pos + 8, size),
        };
        let Some(next) = pos.checked_add(size) else {
            break;
        };
        if next < body || next > end {
            break;
        }
        found.push(Child {
            kind,
            body,
            end: next,
        });
        if stop == Some(&kind) {
            break;
        }
        pos = next;
    }
    found
}

/// One descriptor (ISO/IEC 14496-1 8.3.3): its tag, its body, and what
/// follows it.
fn descriptor(bytes: &[u8]) -> Option<(u8, &[u8], &[u8])> {
    let (&tag, mut rest) = bytes.split_first()?;
    let mut len = 0usize;
    for _ in 0..4 {
        let (&byte, after) = rest.split_first()?;
        rest = after;
        len = (len << 7) | usize::from(byte & 0x7F);
        if byte & 0x80 == 0 {
            break;
        }
    }
    (len <= rest.len()).then(|| (tag, &rest[..len], &rest[len..]))
}

/// The DecoderSpecificInfo inside an `esds` body: the ES descriptor, its
/// DecoderConfig descriptor, and the config within that.
fn decoder_specific_info(esds: &[u8]) -> Option<&[u8]> {
    let (tag, es, _) = descriptor(esds.get(4..)?)?;
    if tag != 0x03 {
        return None;
    }
    // ES_ID, then flags for the optional fields ahead of the
    // sub-descriptors: a depended-on ES_ID, a URL, an OCR ES_ID.
    let flags = *es.get(2)?;
    let mut at = 3;
    if flags & 0x80 != 0 {
        at += 2;
    }
    if flags & 0x40 != 0 {
        at += 1 + usize::from(*es.get(at)?);
    }
    if flags & 0x20 != 0 {
        at += 2;
    }
    let mut rest = es.get(at..)?;
    while !rest.is_empty() {
        let (tag, body, next) = descriptor(rest)?;
        if tag == 0x04 {
            // Object type, stream type, buffer size and two bitrates.
            let mut inner = body.get(13..)?;
            while !inner.is_empty() {
                let (tag, body, next) = descriptor(inner)?;
                if tag == 0x05 {
                    return Some(body);
                }
                inner = next;
            }
            return None;
        }
        rest = next;
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::MemSource;
    use crate::source::CachedSource;

    fn boxed(kind: &[u8; 4], body: &[u8]) -> Vec<u8> {
        let mut out = ((body.len() + 8) as u32).to_be_bytes().to_vec();
        out.extend_from_slice(kind);
        out.extend_from_slice(body);
        out
    }

    /// `esds` body as ffmpeg writes it: four-byte descriptor lengths.
    fn esds_body(config: &[u8]) -> Vec<u8> {
        let wide = |tag: u8, body: &[u8]| {
            let mut out = vec![tag, 0x80, 0x80, 0x80, body.len() as u8];
            out.extend_from_slice(body);
            out
        };
        let mut dec = vec![0x40, 0x15, 0, 0, 0, 0, 1, 0x77, 0x84, 0, 1, 0x77, 0x84];
        dec.extend(wide(0x05, config));
        let mut es = vec![0, 1, 0];
        es.extend(wide(0x04, &dec));
        es.extend(wide(0x06, &[0x02]));
        let mut body = vec![0, 0, 0, 0];
        body.extend(wide(0x03, &es));
        body
    }

    fn movie(tracks: &[(u32, u16, Vec<u8>)]) -> Vec<u8> {
        let mut moov = Vec::new();
        for (id, version, entry_tail) in tracks {
            let mut tkhd = vec![0u8; 12];
            tkhd.extend_from_slice(&id.to_be_bytes());
            tkhd.extend_from_slice(&[0u8; 64]);
            let mut entry = vec![0u8; 8];
            entry.extend_from_slice(&version.to_be_bytes());
            entry.extend_from_slice(&[0u8; 18]);
            entry.extend(vec![
                0u8;
                match version {
                    1 => 16,
                    2 => 36,
                    _ => 0,
                }
            ]);
            entry.extend_from_slice(entry_tail);
            let mut stsd = vec![0, 0, 0, 0, 0, 0, 0, 1];
            stsd.extend(boxed(b"mp4a", &entry));
            let stbl = boxed(b"stbl", &boxed(b"stsd", &stsd));
            let minf = boxed(b"minf", &[boxed(b"smhd", &[0; 8]), stbl].concat());
            let mdia = boxed(b"mdia", &[boxed(b"mdhd", &[0; 24]), minf].concat());
            moov.extend(boxed(b"trak", &[boxed(b"tkhd", &tkhd), mdia].concat()));
        }
        [
            boxed(b"ftyp", b"isom\0\0\0\0"),
            boxed(b"mdat", &[0xAA; 32]),
            boxed(b"moov", &moov),
        ]
        .concat()
    }

    fn configs_of(bytes: Vec<u8>) -> HashMap<u32, Vec<u8>> {
        let len = bytes.len() as u64;
        let mut src = CachedSource::new(Box::new(MemSource(bytes)));
        let mut reader = SourceReader::new(&mut src, len, 1 << 20);
        audio_configs(&mut reader, len)
    }

    #[test]
    fn keeps_every_byte_of_each_track_config() {
        let he_aac = [0x2B, 0xB2, 0x08, 0x00];
        let lc = [0x11, 0x90, 0x56, 0xE5, 0x00];
        let configs = configs_of(movie(&[
            (1, 0, boxed(b"esds", &esds_body(&he_aac))),
            (7, 0, boxed(b"esds", &esds_body(&lc))),
        ]));
        assert_eq!(configs.get(&1).map(Vec::as_slice), Some(&he_aac[..]));
        assert_eq!(configs.get(&7).map(Vec::as_slice), Some(&lc[..]));
    }

    #[test]
    fn reads_quicktime_sound_descriptions() {
        let config = [0x12, 0x10];
        let in_wave = boxed(
            b"wave",
            &[boxed(b"frma", b"mp4a"), boxed(b"esds", &esds_body(&config))].concat(),
        );
        let configs = configs_of(movie(&[
            (1, 1, in_wave),
            (2, 2, boxed(b"esds", &esds_body(&config))),
        ]));
        assert_eq!(configs.len(), 2);
        assert_eq!(configs[&1], config);
        assert_eq!(configs[&2], config);
    }

    #[test]
    fn reads_one_byte_lengths_and_optional_es_fields() {
        // ES_ID 1 with a three-byte URL, then a DecoderConfig of 13 bytes
        // and a two-byte config.
        let mut es = vec![0, 1, 0x40, 3, b'a', b'b', b'c', 0x04, 17, 0x40, 0x15];
        es.extend([0u8; 11]);
        es.extend([0x05, 2, 0x12, 0x10]);
        let mut body = vec![0, 0, 0, 0, 0x03, es.len() as u8];
        body.extend(&es);
        assert_eq!(decoder_specific_info(&body), Some(&[0x12, 0x10][..]));
    }

    #[test]
    fn a_malformed_tree_yields_nothing() {
        let good = esds_body(&[0x12, 0x10]);
        for cut in 0..good.len() {
            assert_eq!(decoder_specific_info(&good[..cut]), None, "cut at {cut}");
        }
        let mut wrong_tag = good.clone();
        wrong_tag[4] = 0x02;
        assert_eq!(decoder_specific_info(&wrong_tag), None);
        // A box overrunning its parent ends the walk without a config.
        let mut bytes = movie(&[(1, 0, boxed(b"esds", &good))]);
        let at = bytes.windows(4).position(|w| w == b"esds").unwrap() - 4;
        bytes[at..at + 4].copy_from_slice(&0xFFFF_u32.to_be_bytes());
        assert!(configs_of(bytes).is_empty());
    }
}
