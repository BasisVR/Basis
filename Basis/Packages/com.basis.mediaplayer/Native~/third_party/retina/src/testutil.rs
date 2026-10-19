// Copyright (C) The Retina Authors
// SPDX-License-Identifier: MIT OR Apache-2.0

#[cfg(test)]
use std::sync::Once;
use std::sync::OnceLock;

#[cfg(test)]
use bytes::Bytes;
use derive_more::Debug;
use pretty_hex::{HexConfig, PrettyHex as _};

#[cfg(test)]
static INIT_LOGGING: Once = Once::new();
static FINDER: OnceLock<memchr::memmem::Finder> = OnceLock::new();

pub const NONASCII_HEX_CONFIG: HexConfig = HexConfig {
    title: true,
    ascii: false,
    width: 16,
    group: 4,
    chunk: 1,
    max_bytes: usize::MAX,
    display_offset: 0,
};

#[allow(dead_code)]
#[derive(Debug)]
pub struct InvalidAvccFrame<'i> {
    reason: &'static str,

    #[debug("{:?}", data.hex_conf(NONASCII_HEX_CONFIG))]
    data: &'i [u8],
}

pub fn validate_avcc_frame(data: &[u8]) -> Result<(), InvalidAvccFrame<'_>> {
    let finder = FINDER.get_or_init(|| memchr::memmem::Finder::new(&[0, 0][..]));
    let mut remaining = data;
    while let Some((len, rest)) = remaining.split_first_chunk() {
        let len = u32::from_be_bytes(*len);
        let Some((nal, rest)) = rest.split_at_checked(len as usize) else {
            return Err(InvalidAvccFrame {
                reason: "ends mid-NAL",
                data,
            });
        };
        let body = match nal {
            [] => {
                return Err(InvalidAvccFrame {
                    reason: "NAL has zero length",
                    data,
                });
            }
            [_hdr, .., 0] => {
                return Err(InvalidAvccFrame {
                    reason: "NAL has trailing zero",
                    data,
                });
            }
            [_hdr, body @ ..] => body,
        };
        for i in finder.find_iter(body) {
            if body.get(i + 2).is_some_and(|b| [0, 1, 2].contains(b)) {
                return Err(InvalidAvccFrame {
                    reason: "NAL body has forbidden Annex B byte sequence",
                    data,
                });
            }
        }
        remaining = rest;
    }
    if !remaining.is_empty() {
        return Err(InvalidAvccFrame {
            reason: "frame ends mid-NAL length prefix",
            data,
        });
    }
    Ok(())
}

#[cfg(test)]
pub(crate) struct HexDebug(pub(crate) Vec<u8>);

#[cfg(test)]
impl std::cmp::PartialEq for HexDebug {
    fn eq(&self, other: &Self) -> bool {
        self.0 == other.0
    }
}

#[cfg(test)]
impl std::cmp::Eq for HexDebug {}

#[cfg(test)]
impl std::fmt::Debug for HexDebug {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        pretty_hex::pretty_hex_write(f, &self.0)
    }
}

#[cfg(test)]
macro_rules! assert_eq_hex {
    ($left:expr, $right:expr) => {{
        pretty_assertions::assert_eq!(
            $crate::testutil::HexDebug(Vec::from(AsRef::<[u8]>::as_ref(&$left))),
            $crate::testutil::HexDebug(Vec::from(AsRef::<[u8]>::as_ref(&$right))),
        );
    }};
}

#[cfg(test)]
macro_rules! assert_eq_hexes {
    ($left:expr, $right:expr) => {{
        let left = $left
            .iter()
            .map(|x| $crate::testutil::HexDebug(Vec::from(AsRef::<[u8]>::as_ref(x))))
            .collect::<Vec<_>>();
        let right = $right
            .iter()
            .map(|x| $crate::testutil::HexDebug(Vec::from(AsRef::<[u8]>::as_ref(x))))
            .collect::<Vec<_>>();
        pretty_assertions::assert_eq!(left, right);
    }};
}

#[cfg(test)]
pub(crate) use {assert_eq_hex, assert_eq_hexes};

#[cfg(test)]
pub(crate) fn init_logging() {
    use std::str::FromStr as _;
    INIT_LOGGING.call_once(|| {
        let h = mylog::Builder::new()
            .is_test(true)
            .format(
                ::std::env::var("RUST_FORMAT")
                    .map_err(|_| ())
                    .and_then(|s| mylog::Format::from_str(&s))
                    .unwrap_or(mylog::Format::Google),
            )
            .spec(::std::env::var("RUST_LOG").as_deref().unwrap_or("info"))
            .build();
        h.install().unwrap();
    })
}

#[cfg(test)]
pub(crate) fn response(raw: &'static [u8]) -> (crate::rtsp::msg::Response, Bytes) {
    use crate::rtsp::inputs::{Contiguous, Input as _, Slice as _};
    let mut parser = crate::rtsp::parse::Parser::default();
    let mut input = Contiguous::new(raw, false);
    let (msg, body_slice) = parser.feed(&mut input).unwrap().unwrap();
    assert!(input.is_empty(), "not all bytes consumed");
    match msg {
        crate::rtsp::msg::Message::Response(r) => {
            let body_cow = body_slice.to_cow();
            let body = Bytes::copy_from_slice(&body_cow);
            (r, body)
        }
        _ => panic!("unexpected message type"),
    }
}
