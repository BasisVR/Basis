// Copyright (C) The Retina Authors
// SPDX-License-Identifier: MIT OR Apache-2.0

const C_TCHAR: u8 = 1;
const C_FIELD_VCHAR_INNER: u8 = 2;

const TABLE: [u8; 128] = table();
const fn table() -> [u8; 128] {
    /// Returns if the byte is a `tchar` as defined in
    /// [RFC 7230 section 3.2.6](https://datatracker.ietf.org/doc/html/rfc7230#section-3.2.6).
    #[rustfmt::skip]
    const fn is_tchar(b: u8) -> bool {
        // tchar          = "!" / "#" / "$" / "%" / "&" / "'" / "*"
        //                / "+" / "-" / "." / "^" / "_" / "`" / "|" / "~"
        //                / DIGIT / ALPHA
        //                ; any VCHAR, except delimiters
        matches!(b,
            b'!' | b'#' | b'$' | b'%' | b'&' | b'\'' | b'*'
            | b'+' | b'-' | b'.' | b'^' | b'_' | b'`' | b'|' | b'~'
            | b'0'..=b'9' | b'a'..=b'z' | b'A'..=b'Z')
    }

    /// Returns if the byte is valid in the interior of a header field value:
    /// VCHAR / SP / HTAB.
    const fn is_field_vchar_inner(b: u8) -> bool {
        matches!(b, 0x21..=0x7E | b' ' | b'\t')
    }

    let mut table = [0u8; 128];
    let mut i = 0;
    while i < 128 {
        let b = i as u8;
        let mut flags = 0;
        if is_tchar(b) {
            flags |= C_TCHAR;
        }
        if is_field_vchar_inner(b) {
            flags |= C_FIELD_VCHAR_INNER;
        }
        table[i] = flags;
        i += 1;
    }
    table
}

pub const fn is_tchar(b: u8) -> bool {
    (b as usize) < TABLE.len() && TABLE[b as usize] & C_TCHAR != 0
}

pub const fn is_valid_token(token: &[u8]) -> bool {
    if token.is_empty() {
        return false;
    }
    let mut i = 0;
    while i < token.len() {
        if !is_tchar(token[i]) {
            return false;
        }
        i += 1;
    }
    true
}

const fn is_vchar(b: u8) -> bool {
    matches!(b, 0x21..=0x7E)
}

const fn is_field_vchar_inner(b: u8) -> bool {
    (b as usize) < TABLE.len() && TABLE[b as usize] & C_FIELD_VCHAR_INNER != 0
}

pub fn is_valid_header_value(value: &[u8]) -> bool {
    match value {
        [] => false,
        &[only] => is_vchar(only),
        &[first, ref middle @ .., last] => {
            is_vchar(first) && is_vchar(last) && middle.iter().all(|&b| is_field_vchar_inner(b))
        }
    }
}
