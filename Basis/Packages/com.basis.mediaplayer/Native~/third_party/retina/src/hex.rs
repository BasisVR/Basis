// Copyright (C) The Retina Authors
// SPDX-License-Identifier: MIT OR Apache-2.0

//! Quick wrapper around `pretty-hex` to limit output.

use pretty_hex::PrettyHex;

pub struct LimitedHex<'a> {
    inner: &'a [u8],
    max_bytes: usize,
}

impl<'a> LimitedHex<'a> {
    pub fn new(inner: &'a [u8], max_bytes: usize) -> Self {
        Self { inner, max_bytes }
    }
}

impl std::fmt::Display for LimitedHex<'_> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(
            f,
            "{:#}",
            self.inner.hex_conf(pretty_hex::HexConfig {
                max_bytes: self.max_bytes,
                width: 0,
                group: 0,
                chunk: 0,
                ..Default::default()
            })
        )
    }
}

impl std::fmt::Debug for LimitedHex<'_> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(
            f,
            "{:#?}",
            self.inner.hex_conf(pretty_hex::HexConfig {
                max_bytes: self.max_bytes,
                ..Default::default()
            })
        )
    }
}
