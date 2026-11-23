use std::io::{self, Read, Write};

use crate::{CaptureStatus, EncodedAudio, EncodedVideo};

pub const VERSION: u32 = 1;
pub const TOKEN_LEN: usize = 16;
pub const HELLO_LEN: usize = TOKEN_LEN + 4;
pub const VIDEO: u8 = 1;
pub const AUDIO: u8 = 2;
pub const STATUS: u8 = 3;
pub const LOG: u8 = 4;
pub const CONFIG: u8 = 16;
pub const KEYFRAME: u8 = 17;
pub const BITRATE: u8 = 18;
const MAX_MESSAGE: usize = 32 << 20;

pub fn write_message(w: &mut impl Write, kind: u8, parts: &[&[u8]]) -> io::Result<()> {
    let len: usize = parts.iter().map(|p| p.len()).sum();
    let len = u32::try_from(len).map_err(|_| io::Error::other("relay message too large"))?;
    let mut header = [0u8; 5];
    header[0] = kind;
    header[1..].copy_from_slice(&len.to_le_bytes());
    w.write_all(&header)?;
    for part in parts {
        w.write_all(part)?;
    }
    w.flush()
}

pub fn read_message(r: &mut impl Read, buf: &mut Vec<u8>) -> io::Result<u8> {
    let mut header = [0u8; 5];
    r.read_exact(&mut header)?;
    let len = u32::from_le_bytes([header[1], header[2], header[3], header[4]]) as usize;
    if len > MAX_MESSAGE {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "relay message too large",
        ));
    }
    buf.resize(len, 0);
    r.read_exact(buf)?;
    Ok(header[0])
}

pub fn write_video(w: &mut impl Write, frame: &EncodedVideo) -> io::Result<()> {
    let mut head = [0u8; 17];
    head[..8].copy_from_slice(&frame.pts_us.to_le_bytes());
    head[8] = u8::from(frame.key);
    head[9..13].copy_from_slice(&frame.width.to_le_bytes());
    head[13..].copy_from_slice(&frame.height.to_le_bytes());
    write_message(w, VIDEO, &[&head, &frame.data])
}

pub fn parse_video(body: &[u8]) -> Option<EncodedVideo> {
    let head = body.get(..17)?;
    Some(EncodedVideo {
        pts_us: i64::from_le_bytes(head[..8].try_into().ok()?),
        key: head[8] != 0,
        width: u32::from_le_bytes(head[9..13].try_into().ok()?),
        height: u32::from_le_bytes(head[13..].try_into().ok()?),
        data: body[17..].to_vec(),
    })
}

pub fn write_audio(w: &mut impl Write, packet: &EncodedAudio) -> io::Result<()> {
    write_message(w, AUDIO, &[&packet.pts_us.to_le_bytes(), &packet.data])
}

pub fn parse_audio(body: &[u8]) -> Option<EncodedAudio> {
    Some(EncodedAudio {
        pts_us: i64::from_le_bytes(body.get(..8)?.try_into().ok()?),
        data: body[8..].to_vec(),
    })
}

pub fn write_status(w: &mut impl Write, status: &CaptureStatus) -> io::Result<()> {
    let json = serde_json::to_vec(status).map_err(io::Error::other)?;
    write_message(w, STATUS, &[&json])
}

pub fn parse_status(body: &[u8]) -> Option<CaptureStatus> {
    serde_json::from_slice(body).ok()
}

pub fn hello(token: &[u8; TOKEN_LEN]) -> [u8; HELLO_LEN] {
    let mut out = [0u8; HELLO_LEN];
    out[..TOKEN_LEN].copy_from_slice(token);
    out[TOKEN_LEN..].copy_from_slice(&VERSION.to_le_bytes());
    out
}

pub fn token_hex(token: &[u8; TOKEN_LEN]) -> String {
    token.iter().map(|b| format!("{b:02x}")).collect()
}

pub fn parse_token(hex: &str) -> Option<[u8; TOKEN_LEN]> {
    if hex.len() != TOKEN_LEN * 2 {
        return None;
    }
    let mut out = [0u8; TOKEN_LEN];
    for (i, byte) in out.iter_mut().enumerate() {
        *byte = u8::from_str_radix(hex.get(i * 2..i * 2 + 2)?, 16).ok()?;
    }
    Some(out)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::CaptureState;

    #[test]
    fn frames_round_trip() {
        let mut wire = Vec::new();
        let video = EncodedVideo {
            data: vec![0, 0, 0, 1, 0x65, 7],
            pts_us: 123_456,
            key: true,
            width: 1920,
            height: 1080,
        };
        write_video(&mut wire, &video).unwrap();
        write_audio(
            &mut wire,
            &EncodedAudio {
                data: vec![9, 8, 7],
                pts_us: -5,
            },
        )
        .unwrap();
        let status = CaptureStatus {
            state: CaptureState::Running,
            error: String::new(),
            width: 1280,
            height: 720,
            encoder: "VAAPI".into(),
            hardware: true,
            captured: 10,
            encoded: 9,
            dropped: 1,
            bitrate_kbps: 4000,
            audio: true,
            audio_error: String::new(),
        };
        write_status(&mut wire, &status).unwrap();
        let mut reader = wire.as_slice();
        let mut body = Vec::new();
        assert_eq!(read_message(&mut reader, &mut body).unwrap(), VIDEO);
        let back = parse_video(&body).unwrap();
        assert_eq!(
            (back.pts_us, back.key, back.width, back.height, back.data),
            (123_456, true, 1920, 1080, video.data)
        );
        assert_eq!(read_message(&mut reader, &mut body).unwrap(), AUDIO);
        let audio = parse_audio(&body).unwrap();
        assert_eq!((audio.pts_us, audio.data), (-5, vec![9, 8, 7]));
        assert_eq!(read_message(&mut reader, &mut body).unwrap(), STATUS);
        let back = parse_status(&body).unwrap();
        assert_eq!(
            (back.state, back.width, back.encoder, back.encoded),
            (CaptureState::Running, 1280, "VAAPI".to_string(), 9)
        );
        assert!(read_message(&mut reader, &mut body).is_err());
    }

    #[test]
    fn token_hex_round_trips() {
        let token = [0xab; TOKEN_LEN];
        assert_eq!(parse_token(&token_hex(&token)), Some(token));
        assert_eq!(parse_token("zz"), None);
        assert_eq!(hello(&token)[TOKEN_LEN..], VERSION.to_le_bytes());
    }

    #[test]
    fn oversized_message_is_refused() {
        let mut wire = vec![VIDEO];
        wire.extend_from_slice(&u32::MAX.to_le_bytes());
        let mut body = Vec::new();
        assert!(read_message(&mut wire.as_slice(), &mut body).is_err());
    }
}
