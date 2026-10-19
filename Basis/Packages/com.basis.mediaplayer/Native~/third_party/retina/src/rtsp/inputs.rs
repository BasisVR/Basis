// Copyright (C) The Retina Authors
// SPDX-License-Identifier: MIT OR Apache-2.0

//! Input abstractions for the RTSP parser.
//!
//! Two [`Input`] implementations are provided:
//!
//! * [`Contiguous`] — wraps a single `&[u8]`, used for contiguous buffers
//!   like `BytesMut`.
//! * [`Split`] — wraps two `&[u8]` halves, for discontiguous ring-buffer
//!   views. Reserved for future use.

use std::borrow::Cow;

use derive_more::Debug;

use crate::mostly_ascii::MostlyAscii;

/// A parsed output slice from an [`Input`].
pub trait Slice<'i>: Clone {
    fn to_cow(self) -> Cow<'i, [u8]>;
    fn to_cow_str(self) -> Result<Cow<'i, str>, std::str::Utf8Error> {
        match self.to_cow() {
            Cow::Borrowed(b) => std::str::from_utf8(b).map(Cow::Borrowed),
            Cow::Owned(b) => String::from_utf8(b)
                .map(Cow::Owned)
                .map_err(|e| e.utf8_error()),
        }
    }
}

impl<'i> Slice<'i> for &'i [u8] {
    fn to_cow(self) -> Cow<'i, [u8]> {
        Cow::Borrowed(self)
    }
}

/// Streaming byte input, usable as a checkpoint (since `Copy`).
pub trait Input<'i>: Copy {
    type Slice: Slice<'i>;

    fn len(&self) -> usize;

    fn is_empty(&self) -> bool {
        self.len() == 0
    }

    /// Returns true if more bytes may be supplied in a future `feed` call.
    fn is_partial(&self) -> bool;

    /// Returns the next byte without consuming it.
    fn peek_byte(&self) -> Option<u8>;

    /// Skips `n` bytes. Panics if `n > self.len()`.
    fn advance(&mut self, n: usize);

    /// Consumes and returns the next `n` bytes as a slice. Panics if `n > self.len()`.
    fn next_slice(&mut self, n: usize) -> Self::Slice;

    /// Returns byte at index `i`. Panics if `i >= self.len()`.
    fn byte_at(&self, i: usize) -> u8;

    /// Returns true iff the next `lit.len()` bytes equal `lit`.
    /// Panics if `self.len() < lit.len()`.
    fn starts_with_lit(&self, lit: &[u8]) -> bool;

    /// Returns the offset of the first occurrence of `b`, or `None` if absent.
    fn find_byte(&self, b: u8) -> Option<usize>;

    /// Returns the offset of the first occurrence of `a` or `b`, or `None` if absent.
    fn find_bytes2(&self, a: u8, b: u8) -> Option<usize>;

    /// Returns the offset of the first occurrence of `a`, `b`, or `c`, or `None` if absent.
    fn find_bytes3(&self, a: u8, b: u8, c: u8) -> Option<usize>;

    /// Returns the offset of the first byte satisfying `pred`, or `None` if none do.
    fn find_first<F: Fn(u8) -> bool>(&self, pred: F) -> Option<usize>;
}

// ---------------------------------------------------------------------------
// Contiguous — single &[u8] input
// ---------------------------------------------------------------------------

/// Contiguous input wrapping a single `&[u8]`.
#[derive(Copy, Clone)]
pub struct Contiguous<'i> {
    data: &'i [u8],
    partial: bool,
}

impl<'i> Contiguous<'i> {
    pub fn new(data: &'i [u8], partial: bool) -> Self {
        Self { data, partial }
    }
}

impl<'i> Input<'i> for Contiguous<'i> {
    type Slice = &'i [u8];

    fn len(&self) -> usize {
        self.data.len()
    }

    fn is_partial(&self) -> bool {
        self.partial
    }

    fn peek_byte(&self) -> Option<u8> {
        self.data.first().copied()
    }

    fn advance(&mut self, n: usize) {
        self.data = &self.data[n..];
    }

    fn next_slice(&mut self, n: usize) -> &'i [u8] {
        let (ret, rest) = self.data.split_at(n);
        self.data = rest;
        ret
    }

    fn byte_at(&self, i: usize) -> u8 {
        self.data[i]
    }

    fn starts_with_lit(&self, lit: &[u8]) -> bool {
        self.data.starts_with(lit)
    }

    fn find_byte(&self, b: u8) -> Option<usize> {
        memchr::memchr(b, self.data)
    }

    fn find_bytes2(&self, a: u8, b: u8) -> Option<usize> {
        memchr::memchr2(a, b, self.data)
    }

    fn find_bytes3(&self, a: u8, b: u8, c: u8) -> Option<usize> {
        memchr::memchr3(a, b, c, self.data)
    }

    fn find_first<F: Fn(u8) -> bool>(&self, pred: F) -> Option<usize> {
        self.data.iter().position(|&b| pred(b))
    }
}

// ---------------------------------------------------------------------------
// Split — discontiguous two-slice input (for ring buffers)
// ---------------------------------------------------------------------------

/// Discontiguous input from two slices, as from a ring buffer.
#[derive(Copy, Clone, Debug)]
#[debug(
    "{first:?}<split>{second:?}",
    first = MostlyAscii { bytes: self.first, escape_newline: true },
    second = MostlyAscii { bytes: self.second, escape_newline: true }
)]
pub struct Split<'i> {
    first: &'i [u8],
    second: &'i [u8], // empty if first is empty.
    partial: bool,
}

impl<'i> Split<'i> {
    pub fn new(first: &'i [u8], second: &'i [u8], partial: bool) -> Self {
        if first.is_empty() {
            Self {
                first: second,
                second: &[],
                partial,
            }
        } else {
            Self {
                first,
                second,
                partial,
            }
        }
    }
}

impl<'i> Slice<'i> for Split<'i> {
    fn to_cow(self) -> Cow<'i, [u8]> {
        if self.second.is_empty() {
            Cow::Borrowed(self.first)
        } else {
            let mut v = Vec::with_capacity(self.first.len() + self.second.len());
            v.extend_from_slice(self.first);
            v.extend_from_slice(self.second);
            Cow::Owned(v)
        }
    }
}

impl<'i> Input<'i> for Split<'i> {
    type Slice = Split<'i>;

    fn len(&self) -> usize {
        self.first.len() + self.second.len()
    }

    fn is_partial(&self) -> bool {
        self.partial
    }

    fn peek_byte(&self) -> Option<u8> {
        self.first.first().copied()
    }

    fn advance(&mut self, n: usize) {
        if let Some(beyond_first) = n.checked_sub(self.first.len()) {
            self.first = &std::mem::take(&mut self.second)[beyond_first..];
        } else {
            self.first = &self.first[n..];
        }
    }

    fn next_slice(&mut self, offset: usize) -> Self::Slice {
        if let Some(beyond_first) = offset.checked_sub(self.first.len()) {
            let ret = Split {
                first: self.first,
                second: &self.second[..beyond_first],
                partial: false,
            };
            self.first = &std::mem::take(&mut self.second)[beyond_first..];
            ret
        } else {
            let (ret, rest) = self.first.split_at(offset);
            self.first = rest;
            Split {
                first: ret,
                second: &[],
                partial: false,
            }
        }
    }

    fn byte_at(&self, i: usize) -> u8 {
        if let Some(beyond_first) = i.checked_sub(self.first.len()) {
            self.second[beyond_first]
        } else {
            self.first[i]
        }
    }

    fn starts_with_lit(&self, lit: &[u8]) -> bool {
        debug_assert!(self.first.len() + self.second.len() >= lit.len());
        self.first
            .iter()
            .chain(self.second.iter())
            .zip(lit.iter())
            .all(|(a, b)| a == b)
    }

    fn find_byte(&self, b: u8) -> Option<usize> {
        memchr::memchr(b, self.first)
            .or_else(|| memchr::memchr(b, self.second).map(|o| o + self.first.len()))
    }

    fn find_bytes2(&self, a: u8, b: u8) -> Option<usize> {
        memchr::memchr2(a, b, self.first)
            .or_else(|| memchr::memchr2(a, b, self.second).map(|o| o + self.first.len()))
    }

    fn find_bytes3(&self, a: u8, b: u8, c: u8) -> Option<usize> {
        memchr::memchr3(a, b, c, self.first)
            .or_else(|| memchr::memchr3(a, b, c, self.second).map(|o| o + self.first.len()))
    }

    fn find_first<F: Fn(u8) -> bool>(&self, pred: F) -> Option<usize> {
        self.first.iter().position(|&b| pred(b)).or_else(|| {
            self.second
                .iter()
                .position(|&b| pred(b))
                .map(|i| i + self.first.len())
        })
    }
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

#[cfg(test)]
mod tests {
    use super::*;

    /// Helper: parse a line from `data` using the given `Input` constructor,
    /// verifying it returns `b"foo"`.
    fn check_take_line<'i, I: Input<'i>>(mut input: I) {
        use crate::rtsp::parse::tests::take_line_for_test;
        let line = take_line_for_test(&mut input).unwrap();
        assert_eq!(line.to_cow().as_ref(), b"foo");
    }

    #[test]
    fn contiguous_take_line() {
        let data = b"foo\r\nbar";
        check_take_line(Contiguous::new(data, false));
    }

    #[test]
    fn split_take_line_all_splits() {
        let data = &b"foo\r\nbar"[..];
        for split in 0..data.len() {
            let (first, second) = data.split_at(split);
            check_take_line(Split::new(first, second, false));
        }
    }

    #[test]
    fn contiguous_find_bytes() {
        let input = Contiguous::new(b"hello:world", false);
        assert_eq!(input.find_byte(b':'), Some(5));
        assert_eq!(input.find_bytes2(b':', b'x'), Some(5));
        assert_eq!(input.find_bytes3(b'x', b'y', b':'), Some(5));
        assert_eq!(input.find_first(|b| b == b'o'), Some(4));
    }

    #[test]
    fn split_find_bytes_across_boundary() {
        // Put the colon in the second half.
        let input = Split::new(b"hello", b":world", false);
        assert_eq!(input.find_byte(b':'), Some(5));
        assert_eq!(input.find_bytes2(b':', b'x'), Some(5));
        assert_eq!(input.find_bytes3(b'x', b'y', b':'), Some(5));
        assert_eq!(input.find_first(|b| b == b'w'), Some(6));
    }

    #[test]
    fn contiguous_advance_and_next_slice() {
        let mut input = Contiguous::new(b"abcdef", false);
        input.advance(2);
        assert_eq!(input.len(), 4);
        let s = input.next_slice(3);
        assert_eq!(s, b"cde");
        assert_eq!(input.len(), 1);
        assert_eq!(input.peek_byte(), Some(b'f'));
    }

    #[test]
    fn split_advance_across_boundary() {
        let mut input = Split::new(b"ab", b"cdef", false);
        assert_eq!(input.len(), 6);
        input.advance(3); // past first, into second
        assert_eq!(input.len(), 3);
        assert_eq!(input.peek_byte(), Some(b'd'));
    }

    #[test]
    fn split_next_slice_across_boundary() {
        let mut input = Split::new(b"ab", b"cdef", false);
        let s = input.next_slice(4); // spans both halves
        let cow = s.to_cow();
        assert_eq!(cow.as_ref(), b"abcd");
        assert_eq!(input.len(), 2);
    }

    #[test]
    fn contiguous_starts_with_lit() {
        let input = Contiguous::new(b"RTSP/1.0 200 OK", false);
        assert!(input.starts_with_lit(b"RTSP/"));
        assert!(!input.starts_with_lit(b"HTTP/"));
    }

    #[test]
    fn split_starts_with_lit_across_boundary() {
        let input = Split::new(b"RT", b"SP/1.0", false);
        assert!(input.starts_with_lit(b"RTSP/"));
        assert!(!input.starts_with_lit(b"HTTP/"));
    }

    #[test]
    fn contiguous_slice_to_cow_str() {
        let mut input = Contiguous::new(b"hello", false);
        let s: &[u8] = input.next_slice(5);
        // Since it's &[u8], we need to call Slice::to_cow_str.
        let cow_str = Slice::to_cow_str(s).unwrap();
        assert_eq!(&*cow_str, "hello");
    }

    #[test]
    fn split_slice_to_cow_str() {
        let mut input = Split::new(b"hel", b"lo", false);
        let s = input.next_slice(5);
        let cow_str = s.to_cow_str().unwrap();
        assert_eq!(&*cow_str, "hello");
    }
}
