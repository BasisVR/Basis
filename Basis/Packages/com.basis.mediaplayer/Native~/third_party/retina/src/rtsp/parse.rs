// Copyright (C) The Retina Authors
// SPDX-License-Identifier: MIT OR Apache-2.0

//! Hand-rolled parser for RTSP/1.0 message heads.
//!
//! This parser is generic over [`Input`], supporting both contiguous
//! `&[u8]` (via [`Contiguous`](super::inputs::Contiguous)) and discontiguous ring-buffer views.

use std::num::NonZeroUsize;

use url::Url;

use super::{
    inputs::{Input, Slice as _},
    msg::{Data, HeaderName, HeaderValue, Headers, Message, Method, Request, Response, StatusCode},
    table::is_tchar,
};

/// Error detail carried by [`FeedError::Invalid`].
#[derive(Debug, Default, derive_more::Error)]
pub struct Invalid {
    /// Byte offset from the start of the stream where the error was detected.
    pub pos: u64,
    /// ABNF production names, innermost first.
    pub context: Vec<&'static str>,
    /// Underlying error from a library call, if any.
    #[error(source)]
    pub source: Option<Box<dyn std::error::Error + Send + Sync + 'static>>,
}

impl std::fmt::Display for Invalid {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "byte {}: ", self.pos)?;
        if self.context.is_empty() {
            f.write_str("(no context)")?;
        } else {
            for (i, label) in self.context.iter().rev().enumerate() {
                if i > 0 {
                    f.write_str(" > ")?;
                }
                f.write_str(label)?;
            }
        }
        if let Some(ref src) = self.source {
            write!(f, ": {src}")?;
        }
        Ok(())
    }
}

/// Error returned by [`Parser::feed`].
#[derive(Debug, derive_more::Display, derive_more::Error)]
pub enum FeedError {
    /// Not enough data yet; call `feed` again after receiving more bytes.
    ///
    /// The `Option<NonZeroUsize>` holds the number of additional body bytes
    /// needed, if known. This is `Some` when the message head has been fully
    /// parsed and only the body remains; `None` during head parsing.
    #[display("incomplete RTSP message")]
    #[error(ignore)]
    Incomplete(Option<NonZeroUsize>),
    /// Malformed message; the connection should be dropped.
    #[display("invalid RTSP message: {_0}")]
    Invalid(Invalid),
}

impl FeedError {
    fn invalid() -> Self {
        FeedError::Invalid(Invalid::default())
    }

    fn map_invalid(self, f: impl FnOnce(&mut Invalid)) -> Self {
        if let FeedError::Invalid(mut inv) = self {
            f(&mut inv);
            FeedError::Invalid(inv)
        } else {
            self
        }
    }

    fn with_pos(self, pos: u64) -> Self {
        self.map_invalid(|inv| inv.pos = pos)
    }

    fn context(self, label: &'static str) -> Self {
        self.map_invalid(|inv| inv.context.push(label))
    }
}

fn invalid(label: &'static str) -> FeedError {
    FeedError::Invalid(Invalid {
        pos: 0,
        context: vec![label],
        source: None,
    })
}

fn invalid_err<E: std::error::Error + Send + Sync + 'static>(
    label: &'static str,
    e: E,
) -> FeedError {
    FeedError::Invalid(Invalid {
        pos: 0,
        context: vec![label],
        source: Some(Box::new(e)),
    })
}

fn incomplete_or_invalid<'i, I: Input<'i>>(input: &I) -> FeedError {
    if input.is_partial() {
        FeedError::Incomplete(None)
    } else {
        FeedError::invalid()
    }
}

/// Builder for [`Parser`]. Obtain via [`Parser::builder`].
pub struct ParserBuilder {
    max_message_size: usize,
}

impl ParserBuilder {
    /// Sets the maximum total message size in bytes (head + body combined).
    ///
    /// Messages exceeding this limit are rejected with [`FeedError::Invalid`].
    /// Defaults to [`usize::MAX`] (no limit).
    pub fn max_message_size(mut self, n: usize) -> Self {
        self.max_message_size = n;
        self
    }

    pub fn build(self) -> Parser {
        Parser {
            max_message_size: self.max_message_size,
            state: ParserState::default(),
            stream_pos: 0,
        }
    }
}

pub struct Parser {
    max_message_size: usize,
    state: ParserState,
    /// Cumulative bytes consumed from the stream across all `feed` calls.
    stream_pos: u64,
}

impl Default for Parser {
    fn default() -> Self {
        Parser::builder().build()
    }
}

#[derive(Default)]
enum ParserState {
    #[default]
    Idle,
    /// Accumulating headers for a request or response.
    Head {
        msg: Message,
        head_bytes: usize,
    },
    Body(Message, usize),
}

impl Parser {
    pub fn builder() -> ParserBuilder {
        ParserBuilder {
            max_message_size: usize::MAX,
        }
    }

    pub fn stream_pos(&self) -> u64 {
        self.stream_pos
    }

    /// Bytes of completed head lines consumed so far, while a head is being
    /// read; 0 otherwise.
    pub fn head_bytes(&self) -> usize {
        match self.state {
            ParserState::Head { head_bytes, .. } => head_bytes,
            _ => 0,
        }
    }

    /// Parses the next message from `input`.
    ///
    /// Returns `Ok(Some(...))` when a complete message has been parsed.
    /// Returns `Ok(None)` on a clean end-of-stream between messages (empty,
    /// non-partial input while idle).
    /// On `Err(Incomplete(_))`, the caller should supply more bytes and call
    /// again; `input` reflects any bytes that were stably consumed (so the
    /// caller can advance its buffer accordingly).
    /// On `Err(Invalid)`, the connection is unrecoverable; [`Invalid::pos`]
    /// gives the stream offset where the error was detected.
    pub fn feed<'i, I: Input<'i>>(
        &mut self,
        input: &mut I,
    ) -> Result<Option<(Message, I::Slice)>, FeedError> {
        let start_pos = self.stream_pos;
        let initial_len = input.len();
        let result = self.feed_inner(input);
        let consumed = (initial_len - input.len()) as u64;
        self.stream_pos += consumed;
        result.map_err(|e| e.with_pos(start_pos + consumed))
    }

    fn feed_inner<'i, I: Input<'i>>(
        &mut self,
        input: &mut I,
    ) -> Result<Option<(Message, I::Slice)>, FeedError> {
        loop {
            match std::mem::take(&mut self.state) {
                ParserState::Idle => {
                    let checkpoint = *input;
                    let before = input.len();
                    match parse_head(input) {
                        Ok(None) => return Ok(None), // clean EOF between messages
                        Ok(Some(Message::Data(data))) => {
                            let body_len = data.body_len as usize;
                            self.state = ParserState::Body(Message::Data(data), body_len);
                        }
                        Ok(Some(msg)) => {
                            self.state = ParserState::Head {
                                msg,
                                head_bytes: before - input.len(),
                            };
                        }
                        Err(FeedError::Incomplete(_)) => {
                            *input = checkpoint;
                            return Err(FeedError::Incomplete(None));
                        }
                        Err(e) => return Err(e),
                    }
                }
                ParserState::Head {
                    mut msg,
                    mut head_bytes,
                } => {
                    let context = match &msg {
                        Message::Request(_) => "request",
                        Message::Response(_) => "response",
                        Message::Data(_) => unreachable!(),
                    };
                    let checkpoint = *input;
                    let before = input.len();
                    match parse_header_line(input) {
                        Ok(Some((name, value))) => {
                            head_bytes += before - input.len();
                            if head_bytes > self.max_message_size {
                                return Err(invalid("message-too-large").context(context));
                            }
                            match &mut msg {
                                Message::Request(req) => req.headers.append(name, value),
                                Message::Response(resp) => resp.headers.append(name, value),
                                Message::Data(_) => unreachable!(),
                            }
                            self.state = ParserState::Head { msg, head_bytes };
                        }
                        Ok(None) => {
                            head_bytes += before - input.len();
                            let headers = match &msg {
                                Message::Request(req) => &req.headers,
                                Message::Response(resp) => &resp.headers,
                                Message::Data(_) => unreachable!(),
                            };
                            let body_len =
                                content_length(headers).map_err(|e| e.context(context))?;
                            let total = (head_bytes as u64).saturating_add(body_len);
                            if total > self.max_message_size as u64 {
                                return Err(invalid("message-too-large").context(context));
                            }
                            self.state = ParserState::Body(msg, body_len as usize);
                        }
                        Err(FeedError::Incomplete(_)) => {
                            *input = checkpoint;
                            self.state = ParserState::Head { msg, head_bytes };
                            return Err(FeedError::Incomplete(None));
                        }
                        Err(e) => {
                            self.state = ParserState::Head { msg, head_bytes };
                            return Err(e.context(context));
                        }
                    }
                }
                ParserState::Body(msg, body_len) => {
                    if input.len() >= body_len {
                        let body = input.next_slice(body_len);
                        return Ok(Some((msg, body)));
                    } else {
                        self.state = ParserState::Body(msg, body_len);
                        let needed = body_len - input.len();
                        return Err(FeedError::Incomplete(NonZeroUsize::new(needed)));
                    }
                }
            }
        }
    }
}

/// Skips any leading CRLFs, then parses the first line of the next message.
/// Returns `Ok(None)` on a clean end-of-stream (empty, non-partial input).
fn parse_head<'i, I: Input<'i>>(input: &mut I) -> Result<Option<Message>, FeedError> {
    loop {
        match input.peek_byte() {
            None => {
                return if input.is_partial() {
                    Err(FeedError::Incomplete(None))
                } else {
                    Ok(None) // clean EOF between messages
                };
            }
            Some(b'\r') => {
                if input.len() < 2 {
                    return Err(incomplete_or_invalid(input));
                }
                if input.byte_at(1) != b'\n' {
                    return Err(FeedError::invalid());
                }
                input.advance(2);
            }
            Some(b'$') => {
                input.advance(1);
                return Ok(Some(Message::Data(parse_data(input)?)));
            }
            Some(_) => {
                if input.len() >= 5 && input.starts_with_lit(b"RTSP/") {
                    return Ok(Some(Message::Response(parse_status_line(input)?)));
                } else if input.len() < 5
                    && input.is_partial()
                    && input.starts_with_lit(&b"RTSP/"[..input.len()])
                {
                    return Err(FeedError::Incomplete(None));
                } else {
                    return Ok(Some(Message::Request(parse_request_line(input)?)));
                }
            }
        }
    }
}

fn parse_data<'i, I: Input<'i>>(input: &mut I) -> Result<Data, FeedError> {
    if input.len() < 3 {
        return Err(incomplete_or_invalid(input).context("interleaved-data"));
    }
    let channel_id = input.byte_at(0);
    let body_len = (input.byte_at(1) as u16) << 8 | input.byte_at(2) as u16;
    input.advance(3);
    Ok(Data {
        channel_id,
        body_len,
    })
}

fn parse_status_line<'i, I: Input<'i>>(input: &mut I) -> Result<Response, FeedError> {
    parse_status_line_inner(input).map_err(|e| e.context("status-line"))
}

fn parse_status_line_inner<'i, I: Input<'i>>(input: &mut I) -> Result<Response, FeedError> {
    eat_literal(input, b"RTSP/1.0 ").map_err(|e| e.context("RTSP-Version"))?;
    if input.len() < 3 {
        for i in 0..input.len() {
            if !input.byte_at(i).is_ascii_digit() {
                return Err(invalid("status-code"));
            }
        }
        return Err(incomplete_or_invalid(input).context("status-code"));
    }
    let b0 = input.byte_at(0);
    let b1 = input.byte_at(1);
    let b2 = input.byte_at(2);
    if !b0.is_ascii_digit() || !b1.is_ascii_digit() || !b2.is_ascii_digit() {
        return Err(invalid("status-code"));
    }
    input.advance(3);
    let code = (b0 - b'0') as u16 * 100 + (b1 - b'0') as u16 * 10 + (b2 - b'0') as u16;
    let status_code = StatusCode::try_from(code).map_err(|e| invalid_err("status-code", e))?;
    eat_byte(input, b' ')?;
    let reason_slice = take_line(input).map_err(|e| e.context("reason-phrase"))?;
    let reason_phrase = reason_slice
        .to_cow_str()
        .map_err(|e| invalid_err("reason-phrase", e))?
        .into_owned();
    Ok(Response {
        status_code,
        reason_phrase,
        headers: Headers::default(),
    })
}

fn parse_request_line<'i, I: Input<'i>>(input: &mut I) -> Result<Request, FeedError> {
    parse_request_line_inner(input).map_err(|e| e.context("request-line"))
}

fn parse_request_line_inner<'i, I: Input<'i>>(input: &mut I) -> Result<Request, FeedError> {
    let method_slice = take_token(input).map_err(|e| e.context("method"))?;
    let method = Method::try_from(method_slice.to_cow().into_owned())
        .map_err(|e| invalid_err("method", e))?;
    eat_byte(input, b' ')?;
    let request_uri = if input.peek_byte() == Some(b'*') {
        input.advance(1);
        None
    } else {
        let uri_str = take_vchar(input)
            .map_err(|e| e.context("Request-URI"))?
            .to_cow_str()
            .map_err(|e| invalid_err("Request-URI", e))?;
        if uri_str.is_empty() {
            return Err(invalid("Request-URI"));
        }
        Some(Url::parse(&uri_str).map_err(|e| invalid_err("Request-URI", e))?)
    };
    eat_literal(input, b" RTSP/1.0\r\n").map_err(|e| e.context("RTSP-Version"))?;
    Ok(Request {
        method,
        request_uri,
        headers: Headers::default(),
    })
}

fn parse_header_line<'i, I: Input<'i>>(
    input: &mut I,
) -> Result<Option<(HeaderName, HeaderValue)>, FeedError> {
    match input.peek_byte() {
        None => Err(incomplete_or_invalid(input)),
        Some(b'\r') => {
            if input.len() < 2 {
                return Err(incomplete_or_invalid(input));
            }
            if input.byte_at(1) != b'\n' {
                return Err(FeedError::invalid());
            }
            input.advance(2);
            Ok(None)
        }
        Some(_) => Ok(Some(parse_header_pair(input)?)),
    }
}

fn parse_header_pair<'i, I: Input<'i>>(
    input: &mut I,
) -> Result<(HeaderName, HeaderValue), FeedError> {
    parse_header_pair_inner(input).map_err(|e| e.context("message-header"))
}

fn parse_header_pair_inner<'i, I: Input<'i>>(
    input: &mut I,
) -> Result<(HeaderName, HeaderValue), FeedError> {
    let colon_pos = match input.find_bytes3(b':', b'\r', b'\n') {
        None => return Err(incomplete_or_invalid(input).context("field-name")),
        Some(n) => n,
    };
    if colon_pos == 0 || input.byte_at(colon_pos) != b':' {
        return Err(invalid("field-name"));
    }
    let name_slice = input.next_slice(colon_pos);
    input.advance(1); // consume ':'

    // Skip OWS after colon.
    match input.find_first(|b| !matches!(b, b' ' | b'\t')) {
        Some(n) => input.advance(n),
        None => return Err(incomplete_or_invalid(input).context("field-value")),
    }

    let crlf_pos = match input.find_bytes2(b'\r', b'\n') {
        None => return Err(incomplete_or_invalid(input).context("field-value")),
        Some(n) => n,
    };
    if input.byte_at(crlf_pos) != b'\r' {
        return Err(invalid("field-value"));
    }
    if input.len() <= crlf_pos + 1 {
        return Err(incomplete_or_invalid(input).context("field-value"));
    }
    if input.byte_at(crlf_pos + 1) != b'\n' {
        return Err(invalid("field-value"));
    }
    let value_slice = input.next_slice(crlf_pos);
    input.advance(2); // consume '\r\n'

    // Strip trailing OWS.
    let value_cow = value_slice.to_cow();
    let trimmed_len = value_cow.len()
        - value_cow
            .iter()
            .rev()
            .take_while(|&&b| b == b' ' || b == b'\t')
            .count();
    if trimmed_len == 0 {
        return Err(invalid("field-value"));
    }

    let name = HeaderName::try_from(name_slice.to_cow().into_owned())
        .map_err(|e| invalid_err("field-name", e))?;
    let value = HeaderValue::try_from(value_cow[..trimmed_len].to_vec())
        .map_err(|e| invalid_err("field-value", e))?;
    Ok((name, value))
}

fn content_length(headers: &Headers) -> Result<u64, FeedError> {
    match headers.get("Content-Length") {
        None => Ok(0),
        Some(v) => v
            .parse::<u64>()
            .map_err(|e| invalid_err("Content-Length", e)),
    }
}

// ---------------------------------------------------------------------------
// Primitive helpers
// ---------------------------------------------------------------------------

fn take_line<'i, I: Input<'i>>(input: &mut I) -> Result<I::Slice, FeedError> {
    let cr_pos = match input.find_byte(b'\r') {
        None => return Err(incomplete_or_invalid(input)),
        Some(n) => n,
    };
    if input.len() <= cr_pos + 1 {
        return Err(incomplete_or_invalid(input));
    }
    if input.byte_at(cr_pos + 1) != b'\n' {
        return Err(FeedError::invalid());
    }
    let line = input.next_slice(cr_pos);
    input.advance(2); // consume '\r\n'
    Ok(line)
}

fn take_token<'i, I: Input<'i>>(input: &mut I) -> Result<I::Slice, FeedError> {
    let len = input.len();
    match input.find_first(|b| !is_tchar(b)) {
        Some(0) => Err(FeedError::invalid()),
        Some(n) => Ok(input.next_slice(n)),
        None if len == 0 => Err(incomplete_or_invalid(input)),
        None if input.is_partial() => Err(FeedError::Incomplete(None)),
        None => Ok(input.next_slice(len)),
    }
}

fn take_vchar<'i, I: Input<'i>>(input: &mut I) -> Result<I::Slice, FeedError> {
    let len = input.len();
    match input.find_first(|b| !matches!(b, 0x21..=0x7E)) {
        Some(n) => Ok(input.next_slice(n)),
        None if input.is_partial() => Err(FeedError::Incomplete(None)),
        None => Ok(input.next_slice(len)),
    }
}

fn eat_literal<'i, I: Input<'i>>(input: &mut I, lit: &[u8]) -> Result<(), FeedError> {
    if input.len() < lit.len() {
        if !input.starts_with_lit(&lit[..input.len()]) {
            return Err(FeedError::invalid());
        }
        return Err(incomplete_or_invalid(input));
    }
    if !input.starts_with_lit(lit) {
        return Err(FeedError::invalid());
    }
    input.advance(lit.len());
    Ok(())
}

fn eat_byte<'i, I: Input<'i>>(input: &mut I, b: u8) -> Result<(), FeedError> {
    match input.peek_byte() {
        None => Err(incomplete_or_invalid(input)),
        Some(x) if x != b => Err(FeedError::invalid()),
        Some(_) => {
            input.advance(1);
            Ok(())
        }
    }
}

#[cfg(test)]
pub(crate) mod tests {
    use url::Url;

    use crate::rtsp::inputs::Split;
    use crate::rtsp::msg::{HeaderName, Message, Method, Request};

    use super::*;

    /// Exposed for `inputs::tests` to test `take_line` with different `Input` impls.
    pub(crate) fn take_line_for_test<'i, I: Input<'i>>(
        input: &mut I,
    ) -> Result<I::Slice, FeedError> {
        take_line(input)
    }

    /// Simulates two feed calls: partial first, then the remainder.
    fn two_feed<'a>(
        parser: &mut Parser,
        first: &'a [u8],
        second: &'a [u8],
    ) -> Result<Option<(Message, Split<'a>)>, FeedError> {
        let mut p1 = Split::new(first, &[], true);
        match parser.feed(&mut p1) {
            Ok(r) => Ok(r),
            Err(FeedError::Incomplete(_)) => {
                let consumed = first.len() - p1.len();
                let mut p2 = Split::new(&first[consumed..], second, false);
                parser.feed(&mut p2)
            }
            Err(e) => Err(e),
        }
    }

    #[test]
    fn test_take_line() {
        let data = &b"foo\r\nbar"[..];
        for split in 0..data.len() {
            let (first, second) = data.split_at(split);
            let mut input = Split::new(first, second, false);
            match take_line(&mut input) {
                Err(e) => panic!("failed at split point {}: {:?}", split, e),
                Ok(line) => {
                    assert_eq!(
                        line.to_cow().as_ref(),
                        b"foo",
                        "failed at split point {}",
                        split
                    );
                }
            }
        }
    }

    #[test]
    fn parse_request() {
        let data = include_bytes!("testdata/request.txt");
        let expected = Request {
            method: Method::try_from("DESCRIBE").unwrap(),
            request_uri: Some(Url::parse("rtsp://192.168.5.225/h264Preview_01_main").unwrap()),
            headers: [
                (
                    HeaderName::try_from("Accept").unwrap(),
                    "application/sdp".try_into().unwrap(),
                ),
                (
                    HeaderName::try_from("CSeq").unwrap(),
                    "1".try_into().unwrap(),
                ),
                (
                    HeaderName::try_from("User-Agent").unwrap(),
                    "Retina mp4 example".try_into().unwrap(),
                ),
            ]
            .into(),
        };
        for split in 0..data.len() {
            let (first, second) = data.split_at(split);

            // Discontiguous ring-buffer view, single feed.
            let mut input = Split::new(first, second, false);
            let (Message::Request(_), body) = Parser::default()
                .feed(&mut input)
                .unwrap_or_else(|e| panic!("ring-buf failed at split {split}: {e:?}"))
                .unwrap_or_else(|| panic!("unexpected EOF at split {split}"))
            else {
                panic!()
            };
            assert!(body.to_cow().is_empty(), "split {split}");

            // Two separate feeds: partial first, complete second.
            let (Message::Request(_), body) = two_feed(&mut Parser::default(), first, second)
                .unwrap_or_else(|e| panic!("two-feed failed at split {split}: {e:?}"))
                .unwrap_or_else(|| panic!("two-feed unexpected EOF at split {split}"))
            else {
                panic!()
            };
            assert!(body.to_cow().is_empty(), "split {split}");
        }
        // Verify the parsed content matches the expected request (spot-check one split).
        let mut input = Split::new(data, &[], false);
        let (Message::Request(req), _) = Parser::default().feed(&mut input).unwrap().unwrap()
        else {
            panic!()
        };
        assert_eq!(req, expected);
    }

    #[test]
    fn parse_response() {
        let data = include_bytes!("testdata/response.txt");
        for split in 0..data.len() {
            let (first, second) = data.split_at(split);

            // Discontiguous ring-buffer view, single feed.
            let mut input = Split::new(first, second, false);
            Parser::default()
                .feed(&mut input)
                .unwrap_or_else(|e| panic!("ring-buf failed at split {split}: {e:?}"))
                .unwrap_or_else(|| panic!("unexpected EOF at split {split}"));

            // Two separate feeds: partial first, complete second.
            two_feed(&mut Parser::default(), first, second)
                .unwrap_or_else(|e| panic!("two-feed failed at split {split}: {e:?}"))
                .unwrap_or_else(|| panic!("two-feed unexpected EOF at split {split}"));
        }
    }

    #[test]
    fn parse_data() {
        let data = &b"\r\n$\x03\x00\x0512345"[..];
        for split in 0..data.len() {
            let (first, second) = data.split_at(split);

            // Discontiguous ring-buffer view, single feed.
            let mut input = Split::new(first, second, false);
            let (Message::Data(d), body) = Parser::default()
                .feed(&mut input)
                .unwrap_or_else(|e| panic!("ring-buf failed at split {split}: {e:?}"))
                .unwrap_or_else(|| panic!("unexpected EOF at split {split}"))
            else {
                panic!()
            };
            assert!(
                matches!(
                    d,
                    Data {
                        channel_id: 3,
                        body_len: 5
                    }
                ),
                "split {split}"
            );
            assert_eq!(body.to_cow().as_ref(), b"12345", "split {split}");

            // Two separate feeds: partial first, complete second.
            let (Message::Data(d), body) = two_feed(&mut Parser::default(), first, second)
                .unwrap_or_else(|e| panic!("two-feed failed at split {split}: {e:?}"))
                .unwrap_or_else(|| panic!("two-feed unexpected EOF at split {split}"))
            else {
                panic!()
            };
            assert!(
                matches!(
                    d,
                    Data {
                        channel_id: 3,
                        body_len: 5
                    }
                ),
                "split {split}"
            );
            assert_eq!(body.to_cow().as_ref(), b"12345", "split {split}");
        }
    }

    #[test]
    fn clean_eof() {
        let mut input = Split::new(&[], &[], false);
        let result = Parser::default().feed(&mut input);
        assert!(
            matches!(result, Ok(None)),
            "expected Ok(None) for clean EOF, got {result:?}",
        );
    }

    #[test]
    fn error_display_rtsp_version_mismatch() {
        let data = b"RTSP/2.0 200 OK\r\n\r\n";
        let mut input = Split::new(data, &[], false);
        let err = Parser::default().feed(&mut input).unwrap_err();
        assert_eq!(
            err.to_string(),
            "invalid RTSP message: byte 0: status-line > RTSP-Version",
        );
    }

    #[test]
    fn error_display_bad_url() {
        let data = b"DESCRIBE not-a-url RTSP/1.0\r\n\r\n";
        let mut input = Split::new(data, &[], false);
        let err = Parser::default().feed(&mut input).unwrap_err();
        let FeedError::Invalid(ref inv) = err else {
            panic!("expected Invalid, got {err:?}");
        };
        assert_eq!(inv.pos, 18, "wrong position; full error: {err}");
        assert!(
            inv.context == ["Request-URI", "request-line"],
            "wrong context: {:?}",
            inv.context
        );
        assert!(inv.source.is_some(), "expected URL parse error as source");
    }

    #[test]
    fn max_message_size() {
        let data = b"GET * RTSP/1.0\r\nContent-Length: 5\r\n\r\nhello";
        let total = data.len();

        let mut input = Split::new(data, &[], false);
        Parser::builder()
            .max_message_size(total)
            .build()
            .feed(&mut input)
            .expect("should succeed at exact limit")
            .expect("unexpected EOF");

        let mut input = Split::new(data, &[], false);
        let err = Parser::builder()
            .max_message_size(total - 1)
            .build()
            .feed(&mut input)
            .unwrap_err();
        let FeedError::Invalid(ref inv) = err else {
            panic!("expected Invalid, got {err:?}");
        };
        assert_eq!(inv.context, ["message-too-large", "request"]);
    }

    #[test]
    fn fail_eagerly() {
        let data = b"A b";
        let mut input = Split::new(data, &[], false);
        let Err(FeedError::Invalid(inv)) = Parser::builder().build().feed(&mut input) else {
            panic!();
        };
        let msg = inv.to_string();
        assert!(
            msg.contains("Request-URI"),
            "expected Request-URI failure, got {msg}"
        );
    }

    #[test]
    fn incomplete_body_reports_needed() {
        // Parse a response with Content-Length: 100 but only supply the head.
        let data = b"RTSP/1.0 200 OK\r\nContent-Length: 100\r\n\r\n";
        let mut input = Split::new(data, &[], true);
        let mut parser = Parser::default();
        let err = parser.feed(&mut input).unwrap_err();
        match err {
            FeedError::Incomplete(Some(n)) => assert_eq!(n.get(), 100),
            other => panic!("expected Incomplete(Some(100)), got {other:?}"),
        }
    }

    #[test]
    fn incomplete_data_body_reports_needed() {
        // Interleaved data: $\x00\x00\x05 but no body bytes.
        let data = b"$\x00\x00\x05";
        let mut input = Split::new(data, &[], true);
        let mut parser = Parser::default();
        let err = parser.feed(&mut input).unwrap_err();
        match err {
            FeedError::Incomplete(Some(n)) => assert_eq!(n.get(), 5),
            other => panic!("expected Incomplete(Some(5)), got {other:?}"),
        }
    }

    #[test]
    fn incomplete_head_reports_none() {
        // Partial status line.
        let data = b"RTSP/1.0 200";
        let mut input = Split::new(data, &[], true);
        let mut parser = Parser::default();
        let err = parser.feed(&mut input).unwrap_err();
        match err {
            FeedError::Incomplete(None) => {}
            other => panic!("expected Incomplete(None), got {other:?}"),
        }
    }
}
