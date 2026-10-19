// Copyright (C) The Retina Authors
// SPDX-License-Identifier: MIT OR Apache-2.0

//! RTSP/1.0 message types.

use std::{borrow::Cow, collections::BTreeMap, fmt, ops::Deref};

use super::table::{is_valid_header_value, is_valid_token};
use crate::mostly_ascii::MostlyAscii;

// ---------------------------------------------------------------------------
// CaseInsensitive — ASCII-case-insensitive wrapper for Ord/Eq
// ---------------------------------------------------------------------------

/// Wrapper that compares the inner value using ASCII-case-insensitive ordering.
///
/// Works as both a sized wrapper (e.g. `CaseInsensitive<Cow<'static, str>>`)
/// and an unsized wrapper (`CaseInsensitive<str>`) for `BTreeMap` lookups via
/// the `Borrow` trait.
#[derive(Clone, Debug)]
#[repr(transparent)]
pub struct CaseInsensitive<T: ?Sized>(T);

impl CaseInsensitive<str> {
    #[allow(unsafe_code)]
    fn new(s: &str) -> &Self {
        // SAFETY: `CaseInsensitive` is `repr(transparent)` over `T`.
        unsafe { &*(s as *const str as *const Self) }
    }
}

impl<T: AsRef<str> + ?Sized> PartialEq for CaseInsensitive<T> {
    fn eq(&self, other: &Self) -> bool {
        self.0.as_ref().eq_ignore_ascii_case(other.0.as_ref())
    }
}
impl<T: AsRef<str> + ?Sized> Eq for CaseInsensitive<T> {}

impl<T: AsRef<str> + ?Sized> PartialOrd for CaseInsensitive<T> {
    fn partial_cmp(&self, other: &Self) -> Option<std::cmp::Ordering> {
        Some(self.cmp(other))
    }
}

impl<T: AsRef<str> + ?Sized> Ord for CaseInsensitive<T> {
    fn cmp(&self, other: &Self) -> std::cmp::Ordering {
        self.0
            .as_ref()
            .bytes()
            .map(|b| b.to_ascii_lowercase())
            .cmp(other.0.as_ref().bytes().map(|b| b.to_ascii_lowercase()))
    }
}

impl std::borrow::Borrow<CaseInsensitive<str>> for CaseInsensitive<Cow<'static, str>> {
    fn borrow(&self) -> &CaseInsensitive<str> {
        CaseInsensitive::new(self.0.as_ref())
    }
}

/// An RTSP/1.0 message head (parsed without body).
#[derive(Debug, Clone, PartialEq, Eq, derive_more::From)]
pub enum Message {
    Request(Request),
    Response(Response),
    Data(Data),
}

/// An RTSP/1.0 request head.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Request {
    pub method: Method,
    pub request_uri: Option<url::Url>,
    pub headers: Headers,
}

impl Request {
    pub fn write_head(&self, w: &mut impl std::io::Write) -> std::io::Result<()> {
        write!(
            w,
            "{method} {uri} RTSP/1.0\r\n",
            method = self.method,
            uri = self.request_uri.as_ref().map(|u| u.as_str()).unwrap_or("*"),
        )?;
        self.headers.write(w)?;
        Ok(())
    }
}

impl fmt::Display for Request {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            f,
            "{method} {uri} RTSP/1.0\r\n{headers}",
            method = self.method,
            uri = self.request_uri.as_ref().map(|u| u.as_str()).unwrap_or("*"),
            headers = self.headers,
        )
    }
}

/// Defines `pub const` token constants on `Self`, validated at compile time.
macro_rules! static_const_tokens {
    ($($NAME:ident => $s:literal),+ $(,)?) => {
        $(
            pub const $NAME: Self = Self(std::borrow::Cow::Borrowed(
                match $crate::rtsp::table::is_valid_token($s.as_bytes()) {
                    true => $s,
                    false => panic!("invalid token"),
                }
            ));
        )+
    };
}

// ---------------------------------------------------------------------------
// Method
// ---------------------------------------------------------------------------

/// An RTSP method name, which must be a valid token as defined in
/// [RFC 7230 section 3.2.6](https://datatracker.ietf.org/doc/html/rfc7230#section-3.2.6).
#[derive(Clone, PartialEq, Eq)]
pub struct Method(Cow<'static, str>);

impl Method {
    /// Returns a `Method` from a static string, or an error if it's not a valid token.
    pub const fn from_static(name: &'static str) -> Result<Self, &'static str> {
        if !is_valid_token(name.as_bytes()) {
            return Err("invalid RTSP method token");
        }
        Ok(Self(Cow::Borrowed(name)))
    }

    static_const_tokens! {
        DESCRIBE => "DESCRIBE",
        GET_PARAMETER => "GET_PARAMETER",
        OPTIONS => "OPTIONS",
        PLAY => "PLAY",
        SETUP => "SETUP",
        SET_PARAMETER => "SET_PARAMETER",
        TEARDOWN => "TEARDOWN",
    }
}

impl fmt::Display for Method {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.0)
    }
}

impl fmt::Debug for Method {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "Method({:?})", &*self.0)
    }
}

impl std::borrow::Borrow<str> for Method {
    fn borrow(&self) -> &str {
        self.0.as_ref()
    }
}

impl Deref for Method {
    type Target = str;
    fn deref(&self) -> &Self::Target {
        self.0.as_ref()
    }
}

impl TryFrom<&'_ [u8]> for Method {
    type Error = InvalidMethodError;
    fn try_from(v: &'_ [u8]) -> Result<Method, InvalidMethodError> {
        Self::try_from(v.to_owned())
    }
}

impl TryFrom<Vec<u8>> for Method {
    type Error = InvalidMethodError;
    fn try_from(v: Vec<u8>) -> Result<Method, InvalidMethodError> {
        if !is_valid_token(&v) {
            return Err(InvalidMethodError(v));
        }
        let v = String::from_utf8(v).expect("valid token => utf-8");
        Ok(Method(Cow::Owned(v)))
    }
}

impl TryFrom<&'_ str> for Method {
    type Error = InvalidMethodError;
    fn try_from(v: &'_ str) -> Result<Method, InvalidMethodError> {
        Self::try_from(v.as_bytes().to_owned())
    }
}

impl TryFrom<String> for Method {
    type Error = InvalidMethodError;
    fn try_from(v: String) -> Result<Method, InvalidMethodError> {
        if !is_valid_token(v.as_bytes()) {
            return Err(InvalidMethodError(v.into_bytes()));
        }
        Ok(Self(Cow::Owned(v)))
    }
}

#[derive(Clone, Debug, derive_more::Display, derive_more::Error)]
#[display("invalid RTSP method token {:?}", MostlyAscii { bytes: _0, escape_newline: true })]
pub struct InvalidMethodError(#[error(not(source))] Vec<u8>);

/// Compile-time-validated method literal.
#[allow(unused_macros)]
macro_rules! rtsp_method {
    ($s:literal) => {{
        const M: $crate::rtsp::msg::Method =
            $crate::rtsp::msg::Method(std::borrow::Cow::Borrowed(valid_token!($s)));
        M
    }};
}

// ---------------------------------------------------------------------------
// Response
// ---------------------------------------------------------------------------

/// An RTSP/1.0 response head.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Response {
    pub status_code: StatusCode,
    pub reason_phrase: String,
    pub headers: Headers,
}

impl Response {
    pub fn write_head(&self, w: &mut impl std::io::Write) -> std::io::Result<()> {
        write!(
            w,
            "RTSP/1.0 {:03} {}\r\n",
            self.status_code.0, &self.reason_phrase,
        )?;
        self.headers.write(w)?;
        Ok(())
    }
}

impl fmt::Display for Response {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            f,
            "RTSP/1.0 {:03} {}\r\n{}",
            self.status_code.0, &self.reason_phrase, self.headers,
        )
    }
}

// ---------------------------------------------------------------------------
// StatusCode
// ---------------------------------------------------------------------------

#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub struct StatusCode(u16);

impl StatusCode {
    pub const OK: StatusCode = StatusCode(200);
    pub const UNAUTHORIZED: StatusCode = StatusCode(401);
    pub const SESSION_NOT_FOUND: StatusCode = StatusCode(454);
    pub const INTERNAL_SERVER_ERROR: StatusCode = StatusCode(500);

    #[inline]
    pub fn as_u16(self) -> u16 {
        self.0
    }

    pub fn is_success(self) -> bool {
        (200..300).contains(&self.0)
    }
}

impl From<StatusCode> for u16 {
    fn from(s: StatusCode) -> u16 {
        s.0
    }
}

impl TryFrom<u16> for StatusCode {
    type Error = InvalidStatusCode;

    fn try_from(value: u16) -> Result<Self, Self::Error> {
        if !(100..600).contains(&value) {
            return Err(InvalidStatusCode(value));
        }
        Ok(StatusCode(value))
    }
}

impl fmt::Display for StatusCode {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}", self.0)
    }
}

#[derive(Copy, Clone, Debug, derive_more::Display, derive_more::Error)]
#[display("status code {_0} outside the valid range 100..=599")]
pub struct InvalidStatusCode(#[error(not(source))] u16);

// ---------------------------------------------------------------------------
// Headers
// ---------------------------------------------------------------------------

#[derive(Clone, Default, Debug, PartialEq, Eq)]
pub struct Headers(BTreeMap<HeaderName, HeaderValue>);

impl Headers {
    /// Looks up a header by name (case-insensitive).
    pub fn get(&self, name: &str) -> Option<&HeaderValue> {
        self.0.get(CaseInsensitive::new(name))
    }

    /// Appends the given value; if there is an existing value for the same key,
    /// the new value is appended with `", "` between.
    pub fn append(&mut self, name: HeaderName, value: HeaderValue) {
        match self.0.entry(name) {
            std::collections::btree_map::Entry::Occupied(mut e) => {
                e.get_mut().0.extend([", ", &value]);
            }
            std::collections::btree_map::Entry::Vacant(e) => {
                e.insert(value);
            }
        }
    }

    /// Inserts the given value, overwriting any existing value.
    pub fn insert(&mut self, name: HeaderName, value: HeaderValue) {
        self.0.insert(name, value);
    }

    #[inline]
    pub fn iter(&self) -> impl Iterator<Item = (&HeaderName, &HeaderValue)> {
        self.0.iter()
    }

    /// Writes all header lines followed by the blank termination line.
    pub fn write(&self, w: &mut impl std::io::Write) -> std::io::Result<()> {
        for (name, value) in self.0.iter() {
            write!(w, "{name}: {value}\r\n")?;
        }
        w.write_all(b"\r\n")
    }
}

impl fmt::Display for Headers {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        for (name, value) in self.0.iter() {
            write!(f, "{name}: {value}\r\n")?;
        }
        f.write_str("\r\n")
    }
}

impl<I: IntoIterator<Item = (HeaderName, HeaderValue)>> From<I> for Headers {
    fn from(i: I) -> Self {
        let mut out = Self::default();
        for (name, value) in i {
            out.append(name, value);
        }
        out
    }
}

// ---------------------------------------------------------------------------
// HeaderName
// ---------------------------------------------------------------------------

/// An RTSP header name, which must be a valid token.
/// Compared case-insensitively.
#[derive(Clone)]
pub struct HeaderName(Cow<'static, str>);

impl HeaderName {
    /// Returns a `HeaderName` from a static string, or an error if it's not a valid token.
    pub const fn from_static(name: &'static str) -> Result<Self, &'static str> {
        if !is_valid_token(name.as_bytes()) {
            return Err("invalid RTSP header name token");
        }
        Ok(Self(Cow::Borrowed(name)))
    }

    static_const_tokens! {
        ACCEPT => "Accept",
        AUTHORIZATION => "Authorization",
        CONTENT_TYPE => "Content-Type",
        CSEQ => "CSeq",
        PUBLIC => "Public",
        RANGE => "Range",
        RTP_INFO => "RTP-Info",
        SESSION => "Session",
        TRANSPORT => "Transport",
        USER_AGENT => "User-Agent",
        WWW_AUTHENTICATE => "WWW-Authenticate",
    }
}

impl fmt::Display for HeaderName {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.0)
    }
}

impl fmt::Debug for HeaderName {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "HeaderName({:?})", &*self.0)
    }
}

impl std::borrow::Borrow<CaseInsensitive<str>> for HeaderName {
    fn borrow(&self) -> &CaseInsensitive<str> {
        CaseInsensitive::new(self.0.as_ref())
    }
}

impl Deref for HeaderName {
    type Target = str;
    fn deref(&self) -> &Self::Target {
        self.0.as_ref()
    }
}

impl PartialEq for HeaderName {
    fn eq(&self, other: &Self) -> bool {
        self.0.eq_ignore_ascii_case(&other.0)
    }
}
impl Eq for HeaderName {}

impl PartialOrd for HeaderName {
    fn partial_cmp(&self, other: &Self) -> Option<std::cmp::Ordering> {
        Some(self.cmp(other))
    }
}

impl Ord for HeaderName {
    fn cmp(&self, other: &Self) -> std::cmp::Ordering {
        CaseInsensitive::new(self.0.as_ref()).cmp(CaseInsensitive::new(other.0.as_ref()))
    }
}

impl TryFrom<&'_ [u8]> for HeaderName {
    type Error = InvalidHeaderNameError;
    fn try_from(v: &'_ [u8]) -> Result<HeaderName, InvalidHeaderNameError> {
        Self::try_from(v.to_owned())
    }
}

impl TryFrom<Vec<u8>> for HeaderName {
    type Error = InvalidHeaderNameError;
    fn try_from(v: Vec<u8>) -> Result<HeaderName, InvalidHeaderNameError> {
        if !is_valid_token(&v) {
            return Err(InvalidHeaderNameError(v));
        }
        let v = String::from_utf8(v).expect("valid token => utf-8");
        Ok(HeaderName(Cow::Owned(v)))
    }
}

impl TryFrom<&'_ str> for HeaderName {
    type Error = InvalidHeaderNameError;
    fn try_from(v: &'_ str) -> Result<HeaderName, InvalidHeaderNameError> {
        Self::try_from(v.as_bytes().to_owned())
    }
}

impl TryFrom<String> for HeaderName {
    type Error = InvalidHeaderNameError;
    fn try_from(v: String) -> Result<HeaderName, InvalidHeaderNameError> {
        if !is_valid_token(v.as_bytes()) {
            return Err(InvalidHeaderNameError(v.into_bytes()));
        }
        Ok(Self(Cow::Owned(v)))
    }
}

#[derive(Clone, Debug, derive_more::Display, derive_more::Error)]
#[display("invalid RTSP header name token {:?}", MostlyAscii { bytes: _0, escape_newline: true })]
pub struct InvalidHeaderNameError(#[error(not(source))] Vec<u8>);

/// Compile-time-validated header name literal.
#[allow(unused_macros)]
macro_rules! rtsp_header {
    ($s:literal) => {{
        const H: $crate::rtsp::msg::HeaderName =
            $crate::rtsp::msg::HeaderName(std::borrow::Cow::Borrowed(valid_token!($s)));
        H
    }};
}

// ---------------------------------------------------------------------------
// HeaderValue
// ---------------------------------------------------------------------------

#[derive(Clone, PartialEq, Eq)]
pub struct HeaderValue(String);

impl fmt::Display for HeaderValue {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.0)
    }
}

impl fmt::Debug for HeaderValue {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{:?}", &self.0)
    }
}

impl Deref for HeaderValue {
    type Target = str;
    fn deref(&self) -> &str {
        &self.0
    }
}

impl TryFrom<String> for HeaderValue {
    type Error = InvalidHeaderValueError;
    fn try_from(v: String) -> Result<HeaderValue, InvalidHeaderValueError> {
        if !is_valid_header_value(v.as_bytes()) {
            return Err(InvalidHeaderValueError);
        }
        Ok(Self(v))
    }
}

impl TryFrom<&str> for HeaderValue {
    type Error = InvalidHeaderValueError;
    fn try_from(v: &str) -> Result<HeaderValue, InvalidHeaderValueError> {
        if !is_valid_header_value(v.as_bytes()) {
            return Err(InvalidHeaderValueError);
        }
        Ok(Self(v.to_owned()))
    }
}

impl TryFrom<Vec<u8>> for HeaderValue {
    type Error = InvalidHeaderValueError;
    fn try_from(v: Vec<u8>) -> Result<HeaderValue, InvalidHeaderValueError> {
        if !is_valid_header_value(&v) {
            return Err(InvalidHeaderValueError);
        }
        Ok(Self(
            String::from_utf8(v).expect("valid header value => UTF-8"),
        ))
    }
}

#[derive(Clone, Debug, derive_more::Display, derive_more::Error)]
#[display("invalid RTSP header value")]
pub struct InvalidHeaderValueError;

// ---------------------------------------------------------------------------
// Data
// ---------------------------------------------------------------------------

/// An RTSP interleaved data frame header (the `$` prefix).
#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub struct Data {
    pub channel_id: u8,
    pub body_len: u16,
}

impl Data {
    pub fn write(&self, w: &mut impl std::io::Write) -> std::io::Result<()> {
        let body_len = self.body_len.to_be_bytes();
        w.write_all(&[b'$', self.channel_id, body_len[0], body_len[1]])
    }
}

// ---------------------------------------------------------------------------
// OwnedMessage — for sending over the wire
// ---------------------------------------------------------------------------

/// A complete message (head + body) for encoding/sending.
#[derive(Clone, Debug)]
pub enum OwnedMessage {
    Request { head: Request, body: bytes::Bytes },
    Response { head: Response, body: bytes::Bytes },
    Data { channel_id: u8, body: bytes::Bytes },
}

impl OwnedMessage {
    /// Returns the method of a Request message.
    /// Panics if this is not a Request.
    pub fn method(&self) -> &Method {
        match self {
            OwnedMessage::Request { head, .. } => &head.method,
            _ => panic!("not a request"),
        }
    }

    /// Returns the method name as a `&str`. Panics if not a Request.
    pub fn method_str(&self) -> &str {
        self.method()
    }

    /// Returns a mutable reference to the request headers.
    /// Panics if this is not a Request.
    pub fn headers_mut(&mut self) -> &mut Headers {
        match self {
            OwnedMessage::Request { head, .. } => &mut head.headers,
            _ => panic!("not a request"),
        }
    }

    /// Returns the request URI as a string, or "*" if none.
    pub fn request_uri_str(&self) -> &str {
        match self {
            OwnedMessage::Request { head, .. } => {
                head.request_uri.as_ref().map(|u| u.as_str()).unwrap_or("*")
            }
            _ => panic!("not a request"),
        }
    }

    pub fn write(&self, w: &mut impl std::io::Write) -> std::io::Result<()> {
        match self {
            OwnedMessage::Request { head, body } => {
                head.write_head(w)?;
                w.write_all(body)?;
            }
            OwnedMessage::Response { head, body } => {
                head.write_head(w)?;
                w.write_all(body)?;
            }
            OwnedMessage::Data { channel_id, body } => {
                let len = body.len() as u16;
                w.write_all(&[b'$', *channel_id, (len >> 8) as u8, len as u8])?;
                w.write_all(body)?;
            }
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn hdr_name(s: &str) -> HeaderName {
        HeaderName::try_from(s).unwrap()
    }

    fn hdr_value(s: &str) -> HeaderValue {
        HeaderValue::try_from(s).unwrap()
    }

    #[test]
    fn get_case_insensitive() {
        let mut h = Headers::default();
        h.insert(HeaderName::CSEQ, hdr_value("1"));
        assert_eq!(h.get("CSeq").unwrap().deref(), "1");
        assert_eq!(h.get("cseq").unwrap().deref(), "1");
        assert_eq!(h.get("CSEQ").unwrap().deref(), "1");
        assert!(h.get("Content-Type").is_none());
    }

    #[test]
    fn insert_overwrites() {
        let mut h = Headers::default();
        h.insert(HeaderName::SESSION, hdr_value("old"));
        h.insert(HeaderName::SESSION, hdr_value("new"));
        assert_eq!(h.get("Session").unwrap().deref(), "new");
    }

    #[test]
    fn append_joins_with_comma() {
        let mut h = Headers::default();
        h.append(HeaderName::PUBLIC, hdr_value("DESCRIBE"));
        h.append(HeaderName::PUBLIC, hdr_value("SETUP"));
        h.append(HeaderName::PUBLIC, hdr_value("PLAY"));
        assert_eq!(h.get("Public").unwrap().deref(), "DESCRIBE, SETUP, PLAY");
    }

    #[test]
    fn append_case_insensitive_merges() {
        let mut h = Headers::default();
        h.append(hdr_name("Accept"), hdr_value("a"));
        h.append(hdr_name("accept"), hdr_value("b"));
        // Both should be merged under a single key.
        assert_eq!(h.get("ACCEPT").unwrap().deref(), "a, b");
        assert_eq!(h.iter().count(), 1);
    }

    #[test]
    fn from_iterator() {
        let h = Headers::from([
            (HeaderName::CSEQ, hdr_value("1")),
            (HeaderName::SESSION, hdr_value("abc")),
        ]);
        assert_eq!(h.get("cseq").unwrap().deref(), "1");
        assert_eq!(h.get("session").unwrap().deref(), "abc");
    }

    #[test]
    fn display_format() {
        let h = Headers::from([
            (HeaderName::CSEQ, hdr_value("1")),
            (HeaderName::SESSION, hdr_value("abc")),
        ]);
        let s = h.to_string();
        assert!(s.contains("CSeq: 1\r\n"));
        assert!(s.contains("Session: abc\r\n"));
        assert!(s.ends_with("\r\n\r\n"));
    }
}
