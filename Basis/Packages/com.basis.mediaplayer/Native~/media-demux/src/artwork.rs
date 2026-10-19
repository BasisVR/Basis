//! Embedded cover art, extracted but never decoded.
//!
//! The bytes travel compressed exactly as the container stored them, with
//! the MIME type the container stated, and the consumer decodes them.
//! Decoding JPEG or PNG here would put an image parser in the one place
//! that already handles untrusted bytes, to reach a picture the host
//! platform can decode itself.
//!
//! FLAC's `PICTURE` block and Ogg's `METADATA_BLOCK_PICTURE` comment are
//! the same structure (the latter is the former in base64), so one parser
//! serves both. ID3v2's `APIC` frame is its own shape.

/// One embedded picture: the container's bytes and the MIME type it stated.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Artwork {
    /// As the container states it (`image/jpeg`, `image/png`). ID3v2.2
    /// states a three-letter format code instead, mapped here.
    pub mime: String,
    pub data: Vec<u8>,
}

/// Ceiling on a single picture. Cover art runs to a few MB at most; a
/// larger claim is a hostile length field, not a photograph.
pub(crate) const MAX_ARTWORK_BYTES: usize = 16 * 1024 * 1024;

/// `PICTURE` type 3 is the front cover, which is the one to show when a
/// file carries several.
const FRONT_COVER: u32 = 3;

fn be32(data: &[u8], at: usize) -> Option<u32> {
    Some(u32::from_be_bytes(data.get(at..at + 4)?.try_into().ok()?))
}

/// Parse a FLAC `PICTURE` block body (also an Ogg `METADATA_BLOCK_PICTURE`
/// once un-base64'd). Returns the picture type alongside, so a caller
/// holding several can prefer the front cover.
pub(crate) fn parse_picture_block(body: &[u8]) -> Option<(u32, Artwork)> {
    let kind = be32(body, 0)?;
    let mime_len = be32(body, 4)? as usize;
    if mime_len > 255 {
        return None;
    }
    let mime = body.get(8..8 + mime_len)?;
    let desc_at = 8 + mime_len;
    let desc_len = be32(body, desc_at)? as usize;
    // Skip the description and the four fixed fields (width, height,
    // depth, colour count) to reach the picture itself.
    let data_len_at = desc_at
        .checked_add(4)?
        .checked_add(desc_len)?
        .checked_add(16)?;
    let data_len = be32(body, data_len_at)? as usize;
    if data_len == 0 || data_len > MAX_ARTWORK_BYTES {
        return None;
    }
    let data = body.get(data_len_at + 4..data_len_at + 4 + data_len)?;
    Some((
        kind,
        Artwork {
            mime: String::from_utf8_lossy(mime).into_owned(),
            data: data.to_vec(),
        },
    ))
}

/// Keep `candidate` over `held` when it is the front cover, or when
/// nothing is held yet.
pub(crate) fn prefer(held: &mut Option<(u32, Artwork)>, candidate: (u32, Artwork)) {
    let better = match held {
        None => true,
        Some((kind, _)) => *kind != FRONT_COVER && candidate.0 == FRONT_COVER,
    };
    if better {
        *held = Some(candidate);
    }
}

/// Decode standard base64, ignoring whitespace and padding. `None` on any
/// character outside the alphabet.
pub(crate) fn base64(text: &[u8]) -> Option<Vec<u8>> {
    let mut out = Vec::with_capacity(text.len() / 4 * 3);
    let mut acc = 0u32;
    let mut bits = 0u32;
    for &c in text {
        let value = match c {
            b'A'..=b'Z' => u32::from(c - b'A'),
            b'a'..=b'z' => u32::from(c - b'a') + 26,
            b'0'..=b'9' => u32::from(c - b'0') + 52,
            b'+' => 62,
            b'/' => 63,
            b'=' => break,
            b'\r' | b'\n' | b' ' | b'\t' => continue,
            _ => return None,
        };
        acc = (acc << 6) | value;
        bits += 6;
        if bits >= 8 {
            bits -= 8;
            out.push((acc >> bits) as u8);
            if out.len() > MAX_ARTWORK_BYTES {
                return None;
            }
        }
    }
    Some(out)
}

/// Find the cover art in an ID3v2 tag body (everything past the 10-byte
/// header). Handles v2.2's `PIC` and v2.3/v2.4's `APIC`.
///
/// A tag carrying the unsynchronisation flag is skipped rather than read:
/// the scheme inserts bytes throughout the tag, so a picture lifted out of
/// one without reversing it would be corrupt, and the art is not worth
/// carrying that reversal for.
pub(crate) fn from_id3v2(major: u8, flags: u8, body: &[u8]) -> Option<Artwork> {
    if flags & 0x80 != 0 {
        return None;
    }
    let mut at = 0usize;
    // An extended header sits before the frames and states its own size.
    if flags & 0x40 != 0 {
        let size = if major >= 4 {
            syncsafe(body, 0)? as usize
        } else {
            // v2.3 states the size of what follows the size field.
            be32(body, 0)? as usize + 4
        };
        at = size;
    }

    let (id_len, size_len) = if major >= 3 { (4usize, 4usize) } else { (3, 3) };
    let mut held: Option<(u32, Artwork)> = None;
    while at + id_len + size_len + 2 <= body.len() {
        let id = body.get(at..at + id_len)?;
        if id.iter().all(|&b| b == 0) {
            break; // padding
        }
        let size = match (major, size_len) {
            // Only v2.4 states frame sizes syncsafe; v2.3 uses a plain u32.
            (4, _) => syncsafe(body, at + id_len)? as usize,
            (_, 4) => be32(body, at + id_len)? as usize,
            _ => {
                let s = body.get(at + id_len..at + id_len + 3)?;
                (usize::from(s[0]) << 16) | (usize::from(s[1]) << 8) | usize::from(s[2])
            }
        };
        let header = id_len + size_len + if major >= 3 { 2 } else { 0 };
        let frame = body.get(at + header..at + header + size)?;
        if (id == b"APIC" || id == b"PIC")
            && let Some(art) = parse_apic(major, frame)
        {
            prefer(&mut held, art);
        }
        at = at.checked_add(header)?.checked_add(size)?;
    }
    held.map(|(_, art)| art)
}

fn syncsafe(data: &[u8], at: usize) -> Option<u32> {
    let b = data.get(at..at + 4)?;
    if b.iter().any(|&x| x & 0x80 != 0) {
        return None;
    }
    Some(
        (u32::from(b[0]) << 21)
            | (u32::from(b[1]) << 14)
            | (u32::from(b[2]) << 7)
            | u32::from(b[3]),
    )
}

/// `APIC` body: text encoding, MIME (or a v2.2 three-letter format code),
/// picture type, description, then the picture.
fn parse_apic(major: u8, frame: &[u8]) -> Option<(u32, Artwork)> {
    let encoding = *frame.first()?;
    let (mime, at) = if major >= 3 {
        let end = frame.iter().skip(1).position(|&b| b == 0)? + 1;
        (
            String::from_utf8_lossy(frame.get(1..end)?).into_owned(),
            end + 1,
        )
    } else {
        // v2.2 states "JPG"/"PNG" rather than a MIME type.
        let code = frame.get(1..4)?;
        let mime = match code {
            b"PNG" => "image/png",
            b"JPG" => "image/jpeg",
            _ => return None,
        };
        (mime.to_string(), 4)
    };
    let kind = u32::from(*frame.get(at)?);
    let desc_at = at + 1;
    // UTF-16 descriptions terminate on a null *pair*, and only on an even
    // boundary: a lone zero byte is half a character.
    let data_at = if encoding == 1 || encoding == 2 {
        let mut i = desc_at;
        loop {
            let pair = frame.get(i..i + 2)?;
            if pair == [0, 0] {
                break i + 2;
            }
            i += 2;
        }
    } else {
        frame.iter().skip(desc_at).position(|&b| b == 0)? + desc_at + 1
    };
    let data = frame.get(data_at..)?;
    if data.is_empty() || data.len() > MAX_ARTWORK_BYTES {
        return None;
    }
    Some((
        kind,
        Artwork {
            mime,
            data: data.to_vec(),
        },
    ))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn picture_block(kind: u32, mime: &str, desc: &str, data: &[u8]) -> Vec<u8> {
        let mut out = kind.to_be_bytes().to_vec();
        out.extend_from_slice(&(mime.len() as u32).to_be_bytes());
        out.extend_from_slice(mime.as_bytes());
        out.extend_from_slice(&(desc.len() as u32).to_be_bytes());
        out.extend_from_slice(desc.as_bytes());
        out.extend_from_slice(&[0u8; 16]); // width, height, depth, colours
        out.extend_from_slice(&(data.len() as u32).to_be_bytes());
        out.extend_from_slice(data);
        out
    }

    #[test]
    fn picture_block_reads_past_its_variable_fields() {
        let block = picture_block(3, "image/png", "front", b"\x89PNGbytes");
        let (kind, art) = parse_picture_block(&block).expect("parses");
        assert_eq!(kind, FRONT_COVER);
        assert_eq!(art.mime, "image/png");
        assert_eq!(art.data, b"\x89PNGbytes");
    }

    /// The description sits between two length-prefixed fields, so a
    /// non-empty one is what catches an offset walked wrongly.
    #[test]
    fn picture_block_survives_a_long_description() {
        let block = picture_block(0, "image/jpeg", &"d".repeat(500), b"jpegdata");
        let (_, art) = parse_picture_block(&block).expect("parses");
        assert_eq!(art.data, b"jpegdata");
    }

    #[test]
    fn a_truncated_or_hostile_picture_block_is_refused_not_panicked() {
        let block = picture_block(3, "image/png", "x", b"data");
        for cut in 0..block.len() {
            let _ = parse_picture_block(&block[..cut]);
        }
        // A length field past the cap must not become an allocation.
        let mut hostile = picture_block(3, "image/png", "", b"d");
        let at = hostile.len() - 5;
        hostile[at..at + 4].copy_from_slice(&u32::MAX.to_be_bytes());
        assert!(parse_picture_block(&hostile).is_none());
    }

    #[test]
    fn the_front_cover_wins_over_other_picture_types() {
        let mut held = None;
        prefer(
            &mut held,
            (
                4,
                Artwork {
                    mime: "a".into(),
                    data: vec![1],
                },
            ),
        );
        prefer(
            &mut held,
            (
                FRONT_COVER,
                Artwork {
                    mime: "b".into(),
                    data: vec![2],
                },
            ),
        );
        prefer(
            &mut held,
            (
                5,
                Artwork {
                    mime: "c".into(),
                    data: vec![3],
                },
            ),
        );
        assert_eq!(held.expect("held").1.data, vec![2]);
    }

    #[test]
    fn base64_decodes_and_refuses_rubbish() {
        assert_eq!(base64(b"aGVsbG8=").expect("decodes"), b"hello");
        // Line breaks appear in tags written by hand.
        assert_eq!(base64(b"aGVs\nbG8=").expect("decodes"), b"hello");
        assert!(base64(b"not!base64").is_none());
    }

    fn id3_frame(id: &[u8], body: &[u8], syncsafe_size: bool) -> Vec<u8> {
        let mut out = id.to_vec();
        let n = body.len() as u32;
        if syncsafe_size {
            out.extend_from_slice(&[
                ((n >> 21) & 0x7F) as u8,
                ((n >> 14) & 0x7F) as u8,
                ((n >> 7) & 0x7F) as u8,
                (n & 0x7F) as u8,
            ]);
        } else {
            out.extend_from_slice(&n.to_be_bytes());
        }
        out.extend_from_slice(&[0, 0]);
        out.extend_from_slice(body);
        out
    }

    /// APIC: encoding, null-terminated MIME, picture type, null-terminated
    /// description, then the picture.
    fn apic(mime: &str, desc: &[u8], encoding: u8, data: &[u8]) -> Vec<u8> {
        let mut body = vec![encoding];
        body.extend_from_slice(mime.as_bytes());
        body.push(0);
        body.push(3);
        body.extend_from_slice(desc);
        body.extend_from_slice(if encoding == 1 { &[0, 0][..] } else { &[0][..] });
        body.extend_from_slice(data);
        body
    }

    #[test]
    fn id3v23_and_v24_frame_sizes_are_read_differently() {
        let body = apic("image/jpeg", b"cover", 0, b"jpegdata");
        // v2.3 states a plain u32; v2.4 states it syncsafe. Reading one as
        // the other walks straight off the end of the frame.
        let v23 = id3_frame(b"APIC", &body, false);
        let v24 = id3_frame(b"APIC", &body, true);
        assert_eq!(from_id3v2(3, 0, &v23).expect("v2.3").data, b"jpegdata");
        assert_eq!(from_id3v2(4, 0, &v24).expect("v2.4").data, b"jpegdata");
    }

    #[test]
    fn a_utf16_description_terminates_on_a_null_pair() {
        // UTF-16 "hi" contains no lone null, but a single-null scan would
        // stop inside it and hand back the wrong bytes.
        let desc = b"h\x00i\x00";
        let body = apic("image/png", desc, 1, b"pngdata");
        let tag = id3_frame(b"APIC", &body, false);
        assert_eq!(from_id3v2(3, 0, &tag).expect("parses").data, b"pngdata");
    }

    #[test]
    fn id3v22_states_a_format_code_rather_than_a_mime_type() {
        let mut body = vec![0u8];
        body.extend_from_slice(b"JPG");
        body.push(3);
        body.push(0); // empty description
        body.extend_from_slice(b"jpegdata");
        let mut tag = b"PIC".to_vec();
        let n = body.len() as u32;
        tag.extend_from_slice(&[(n >> 16) as u8, (n >> 8) as u8, n as u8]);
        tag.extend_from_slice(&body);
        let art = from_id3v2(2, 0, &tag).expect("v2.2 parses");
        assert_eq!(art.mime, "image/jpeg");
        assert_eq!(art.data, b"jpegdata");
    }

    /// Unsynchronisation rewrites bytes throughout the tag, so a picture
    /// lifted out without reversing it would be corrupt. Refusing is the
    /// contract.
    #[test]
    fn an_unsynchronised_tag_yields_no_art() {
        let body = apic("image/jpeg", b"", 0, b"jpegdata");
        let tag = id3_frame(b"APIC", &body, false);
        assert!(from_id3v2(3, 0x80, &tag).is_none());
    }

    #[test]
    fn a_tag_of_arbitrary_bytes_never_panics() {
        for len in 0..64usize {
            let bytes: Vec<u8> = (0..len).map(|i| (i * 7 + 13) as u8).collect();
            for major in 2..=4u8 {
                for flags in [0u8, 0x40, 0x80] {
                    let _ = from_id3v2(major, flags, &bytes);
                }
            }
        }
    }
}
