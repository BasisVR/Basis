// Copyright (C) The Retina Authors
// SPDX-License-Identifier: MIT OR Apache-2.0

use bytes::Bytes;
use log::{debug, warn};
use sdp_types::Media;
use std::{net::IpAddr, num::NonZeroU16};
use url::Url;

use super::{Presentation, Stream};

/// A static payload type in the [RTP parameters
/// registry](https://www.iana.org/assignments/rtp-parameters/rtp-parameters.xhtml#rtp-parameters-1).
#[derive(Debug)]
struct StaticPayloadType {
    encoding: &'static str,
    media: &'static str,
    clock_rate: u32,
    channels: Option<NonZeroU16>,
}

/// All registered static payload types.
/// The registry is officially closed, so this list should never change.
#[rustfmt::skip]
static STATIC_PAYLOAD_TYPES: [Option<StaticPayloadType>; 35] = [
    /* 0 */ Some(StaticPayloadType {
        encoding: "pcmu",
        media: "audio",
        clock_rate: 8_000,
        channels: NonZeroU16::new(1),
    }),
    /* 1 */ None, // reserved
    /* 2 */ None, // reserved
    /* 3 */ Some(StaticPayloadType {
        encoding: "gsm",
        media: "audio",
        clock_rate: 8_000,
        channels: NonZeroU16::new(1),
    }),
    /* 4 */ Some(StaticPayloadType {
        encoding: "g723",
        media: "audio",
        clock_rate: 8_000,
        channels: NonZeroU16::new(1),
    }),
    /* 5 */ Some(StaticPayloadType {
        encoding: "dvi4",
        media: "audio",
        clock_rate: 8_000,
        channels: NonZeroU16::new(1),
    }),
    /* 6 */ Some(StaticPayloadType {
        encoding: "dvi4",
        media: "audio",
        clock_rate: 16_000,
        channels: NonZeroU16::new(1),
    }),
    /* 7 */ Some(StaticPayloadType {
        encoding: "lpc",
        media: "audio",
        clock_rate: 8_000,
        channels: NonZeroU16::new(1),
    }),
    /* 8 */ Some(StaticPayloadType {
        encoding: "pcma",
        media: "audio",
        clock_rate: 8_000,
        channels: NonZeroU16::new(1),
    }),
    /* 9 */ Some(StaticPayloadType {
        encoding: "g722",
        media: "audio",
        clock_rate: 8_000,
        channels: NonZeroU16::new(1),
    }),
    /* 10 */ Some(StaticPayloadType {
        encoding: "l16",
        media: "audio",
        clock_rate: 441_000,
        channels: NonZeroU16::new(2),
    }),
    /* 11 */ Some(StaticPayloadType {
        encoding: "l16",
        media: "audio",
        clock_rate: 441_000,
        channels: NonZeroU16::new(1),
    }),
    /* 12 */ Some(StaticPayloadType {
        encoding: "qcelp",
        media: "audio",
        clock_rate: 8_000,
        channels: NonZeroU16::new(1),
    }),
    /* 13 */ Some(StaticPayloadType {
        encoding: "cn",
        media: "audio",
        clock_rate: 8_000,
        channels: NonZeroU16::new(1),
    }),
    /* 14 */ Some(StaticPayloadType {
        encoding: "mpa",
        media: "audio",
        clock_rate: 90_000,
        channels: None,
    }),
    /* 15 */ Some(StaticPayloadType {
        encoding: "g728",
        media: "audio",
        clock_rate: 8_000,
        channels: NonZeroU16::new(1),
    }),
    /* 16 */ Some(StaticPayloadType {
        encoding: "dvi4",
        media: "audio",
        clock_rate: 11_025,
        channels: NonZeroU16::new(1),
    }),
    /* 17 */ Some(StaticPayloadType {
        encoding: "dvi4",
        media: "audio",
        clock_rate: 22_050,
        channels: NonZeroU16::new(1),
    }),
    /* 18 */ Some(StaticPayloadType {
        encoding: "g729",
        media: "audio",
        clock_rate: 8_000,
        channels: NonZeroU16::new(1),
    }),
    /* 19 */ None, // reserved
    /* 20 */ None, // unassigned
    /* 21 */ None, // unassigned
    /* 22 */ None, // unassigned
    /* 23 */ None, // unassigned
    /* 24 */ None, // unassigned
    /* 25 */ Some(StaticPayloadType {
        encoding: "celb",
        media: "video",
        clock_rate: 90_000,
        channels: None,
    }),
    /* 26 */ Some(StaticPayloadType {
        encoding: "jpeg",
        media: "video",
        clock_rate: 90_000,
        channels: None,
    }),
    /* 27 */ None, // unassigned
    /* 28 */ Some(StaticPayloadType {
        encoding: "nv",
        media: "video",
        clock_rate: 90_000,
        channels: None,
    }),
    /* 29 */ None, // unassigned
    /* 30 */ None, // unassigned
    /* 31 */ Some(StaticPayloadType {
        encoding: "h261",
        media: "video",
        clock_rate: 90_000,
        channels: None,
    }),
    /* 32 */ Some(StaticPayloadType {
        encoding: "mpv",
        media: "video",
        clock_rate: 90_000,
        channels: None,
    }),
    /* 33 */ Some(StaticPayloadType {
        encoding: "mp2t",
        // The RTP parameters registry says type AV (audio and video).
        // The MIME registration says the media type is "video".
        // https://datatracker.ietf.org/doc/html/rfc3555#section-4.2.9
        media: "video",
        clock_rate: 90_000,
        channels: None,
    }),
    /* 34 */ Some(StaticPayloadType {
        encoding: "h263",
        media: "video",
        clock_rate: 90_000,
        channels: None,
    }),
];

/// Joins a control URL to a base URL in a non-RFC-compliant but common way.
/// This matches what live555 and ffmpeg do.
///
/// See discussion at [#9](https://github.com/scottlamb/retina/issues/9).
fn join_control(base_url: &Url, control: &str) -> Result<Url, String> {
    if control == "*" {
        return Ok(base_url.clone());
    }
    if let Ok(absolute_url) = Url::parse(control) {
        return Ok(absolute_url);
    }

    Url::parse(&format!(
        "{}{}{}",
        base_url.as_str(),
        if base_url.as_str().ends_with('/') {
            ""
        } else {
            "/"
        },
        control
    ))
    .map_err(|e| format!("unable to join base url {base_url} with control url {control:?}: {e}"))
}

/// Returns the `CSeq` from an RTSP response as a `u32`, or `None` if missing/unparseable.
pub(crate) fn get_cseq(response: &crate::rtsp::msg::Response) -> Option<u32> {
    response
        .headers
        .get("CSeq")
        .and_then(|cseq| u32::from_str_radix(cseq, 10).ok())
}

/// Parses a [MediaDescription] to a [Stream].
/// On failure, returns an error which is expected to be supplemented with
/// the [MediaDescription] debug string and packed into a `RtspResponseError`.
fn parse_media(base_url: &Url, media_description: &Media) -> Result<Stream, String> {
    let media = media_description.media.clone().into_boxed_str();

    // https://tools.ietf.org/html/rfc8866#section-5.14 says "If the <proto>
    // sub-field is "RTP/AVP" or "RTP/SAVP" the <fmt> sub-fields contain RTP
    // payload type numbers."
    // https://www.iana.org/assignments/sdp-parameters/sdp-parameters.xhtml#sdp-parameters-2
    // shows several other variants, such as "TCP/RTP/AVP". Looking for a "RTP" component
    // seems appropriate.
    // https://www.ietf.org/archive/id/draft-sheedy-mmusic-rtsp-ext-01.txt
    // adds "MP2T" as a valid proto which is used by some ISPs.
    if !media_description.proto.starts_with("RTP/")
        && !media_description.proto.contains("/RTP/")
        && !media_description.proto.contains("MP2T/")
    {
        return Err("Expected RTP-based proto".into());
    }

    // RFC 8866 continues: "When a list of payload type numbers is given,
    // this implies that all of these payload formats MAY be used in the
    // session, but the first of these formats SHOULD be used as the default
    // format for the session." Just use the first until we find a stream
    // where this isn't the right thing to do.
    let rtp_payload_type_str = media_description
        .fmt
        .split_ascii_whitespace()
        .next()
        .unwrap();
    let rtp_payload_type = u8::from_str_radix(rtp_payload_type_str, 10)
        .map_err(|_| format!("invalid RTP payload type {rtp_payload_type_str:?}"))?;
    if (rtp_payload_type & 0x80) != 0 {
        return Err(format!("invalid RTP payload type {rtp_payload_type}"));
    }

    // Capture interesting attributes.
    // RFC 8866: "For dynamic payload type assignments, the "a=rtpmap:"
    // attribute (see Section 6.6) SHOULD be used to map from an RTP payload
    // type number to a media encoding name that identifies the payload
    // format. The "a=fmtp:" attribute MAY be used to specify format
    // parameters (see Section 6.15)."
    let mut rtpmap = None;
    let mut fmtp = None;
    let mut control = None;
    let mut framerate = None;
    for a in &media_description.attributes {
        match a.attribute.as_str() {
            "rtpmap" => {
                let v = a
                    .value
                    .as_ref()
                    .ok_or_else(|| "rtpmap attribute with no value".to_string())?
                    .trim_end_matches(' ');
                // https://tools.ietf.org/html/rfc8866#section-6.6
                // rtpmap-value = payload-type SP encoding-name
                //   "/" clock-rate [ "/" encoding-params ]
                // payload-type = zero-based-integer
                // encoding-name = token
                // clock-rate = integer
                // encoding-params = channels
                // channels = integer
                //
                // At least one camera (improperly) sends a trailing space; trim this.
                let (rtpmap_payload_type, v) = v
                    .split_once(' ')
                    .ok_or_else(|| "invalid rtmap attribute".to_string())?;
                if rtpmap_payload_type == rtp_payload_type_str {
                    rtpmap = Some(v);
                }
            }
            "fmtp" => {
                // Similarly should start with payload-type SP.
                let v = a
                    .value
                    .as_ref()
                    .ok_or_else(|| "fmtp attribute with no value".to_string())?;

                if let Some((fmtp_payload_type, v)) = v.split_once(' ') {
                    if fmtp_payload_type == rtp_payload_type_str {
                        fmtp = Some(v);
                    }
                } else {
                    // Ubiquiti cameras sometimes have e.g. "a=fmtp:96": payload
                    // type only, no actual attributes. Don't fail on this.
                    warn!("ignoring invalid fmtp attribute value {:?}", v);
                }
            }
            "control" => {
                control = a
                    .value
                    .as_deref()
                    .map(|c| join_control(base_url, c))
                    .transpose()?;
            }
            "framerate" => {
                if let Some(s) = a.value.as_ref()
                    && let Ok(f) = s.parse::<f32>()
                {
                    framerate = Some(f);
                }
            }
            _ => (),
        }
    }

    let encoding_name;
    let clock_rate;
    let channels;
    match rtpmap {
        Some(rtpmap) => {
            let (e, rtpmap) = rtpmap
                .split_once('/')
                .ok_or_else(|| "invalid rtpmap attribute".to_string())?;
            encoding_name = e;
            let (clock_rate_str, channels_str) = match rtpmap.find('/') {
                None => (rtpmap, None),
                Some(i) => (&rtpmap[..i], Some(&rtpmap[i + 1..])),
            };
            clock_rate = u32::from_str_radix(clock_rate_str, 10)
                .map_err(|_| "bad clockrate in rtpmap".to_string())?;
            channels = channels_str
                .map(|c| {
                    u16::from_str_radix(c, 10)
                        .ok()
                        .and_then(NonZeroU16::new)
                        .ok_or_else(|| format!("Invalid channels specification {c:?}"))
                })
                .transpose()?;
        }
        None => {
            let type_ = STATIC_PAYLOAD_TYPES
                .get(usize::from(rtp_payload_type))
                .and_then(Option::as_ref)
                .ok_or_else(|| {
                    format!(
                        "Expected rtpmap parameter or assigned static payload type (got {rtp_payload_type})"
                    )
                })?;
            encoding_name = type_.encoding;
            clock_rate = type_.clock_rate;
            channels = type_.channels;
            if type_.media != &*media {
                return Err(format!(
                    "SDP media type {} must match RTP payload type {:#?}",
                    &media, type_
                ));
            }
        }
    }

    let encoding_name = encoding_name.to_ascii_lowercase().into_boxed_str();
    let depacketizer =
        crate::codec::Depacketizer::new(&media, &encoding_name, clock_rate, channels, fmtp);

    Ok(Stream {
        media,
        encoding_name,
        clock_rate_hz: clock_rate,
        rtp_payload_type,
        depacketizer,
        control,
        channels,
        framerate,
        state: super::StreamState::Uninit,
    })
}

use crate::mostly_ascii::MostlyAscii;

/// Parses a successful RTSP `DESCRIBE` response into a [Presentation].
/// On error, returns a string which is expected to be packed into an `RtspProtocolError`.
pub(crate) fn parse_describe(
    request_url: Url,
    response: &crate::rtsp::msg::Response,
    body: &Bytes,
) -> Result<Presentation, String> {
    match response.headers.get("Content-Type") {
        Some(v) if &**v == "application/sdp" => {}
        Some(v) => {
            return Err(format!(
                "DESCRIBE response at {} has unexpected content type {}",
                request_url.as_str(),
                v,
            ));
        }
        None => {
            warn!(
                "DESCRIBE response at {} has no content type; trying sdp anyway",
                request_url.as_str()
            );
        }
    }
    let raw_sdp = MostlyAscii::new(&body[..]);
    let sdp = sdp_types::Session::parse(raw_sdp.bytes)
        .map_err(|e| format!("Unable to parse SDP: {e}\n\n{raw_sdp:#?}",))?;

    // https://tools.ietf.org/html/rfc2326#appendix-C.1.1
    let base_url = response
        .headers
        .get("Content-Base")
        .map(|v| ("Content-Base", v))
        .or_else(|| {
            response
                .headers
                .get("Content-Location")
                .map(|v| ("Content-Location", v))
        })
        .map(|(h, v)| Url::parse(v).map_err(|e| format!("bad {h} {v:?}: {e}")))
        .unwrap_or_else(|| Ok(request_url.clone()))?;

    let mut control = None;
    let mut tool = None;
    for a in &sdp.attributes {
        if a.attribute == "control" {
            control = a
                .value
                .as_deref()
                .map(|c| join_control(&base_url, c))
                .transpose()?;
        } else if a.attribute == "tool" {
            tool = a.value.as_deref().map(super::Tool::new);
        }
    }
    let control = control.unwrap_or(request_url);

    let streams: Box<[Stream]> = sdp
        .medias
        .iter()
        .enumerate()
        .filter_map(|(i, m)| {
            parse_media(&base_url, m).map_or_else(
                |e| {
                    warn!(
                        "Ignoring unparseable stream {}: {}\nraw SDP: {:#?}",
                        i, &e, raw_sdp
                    );
                    None
                },
                Some,
            )
        })
        .collect();

    if streams.is_empty() {
        return Err(format!(
            "No parseable streams (and {} unparseable streams)",
            sdp.medias.len()
        ));
    }

    Ok(Presentation {
        streams,
        base_url,
        control,
        tool,
    })
}

#[derive(Debug, PartialEq, Eq)]
pub(crate) struct SessionHeader {
    pub(crate) id: Box<str>,
    pub(crate) timeout_sec: u32,
}

#[derive(Debug, Eq, PartialEq)]
pub(crate) struct SetupResponse {
    pub(crate) session: SessionHeader,
    pub(crate) ssrc: Option<u32>,
    pub(crate) channel_id: Option<u8>,
    pub(crate) source: Option<IpAddr>,
    pub(crate) server_port: Option<u16>,
}

fn parse_server_port(server_port: &str) -> Result<u16, ()> {
    if let Some((a, b)) = server_port.split_once('-') {
        let a = u16::from_str_radix(a, 10).map_err(|_| ())?;
        let b = u16::from_str_radix(b, 10).map_err(|_| ())?;
        if a.checked_add(1) != Some(b) {
            // It's unclear what a non-consecutive range means.
            return Err(());
        }
        return Ok(a);
    }

    // Not returning a range is allowed by RFC 2326's grammar, but I'm not sure
    // what it means. RTSP 2.0 allows "RTCP-mux" for using a single port for
    // both RTP and RTCP, but it's only by client request, and RTSP 1.0 doesn't
    // reference this.
    Err(())
}

/// Parses a `SETUP` response.
/// `session_id` is checked for assignment or reassignment.
/// Returns an assigned interleaved channel id (implying the next channel id
/// is also assigned) or errors.
pub(crate) fn parse_setup(response: &crate::rtsp::msg::Response) -> Result<SetupResponse, String> {
    // https://datatracker.ietf.org/doc/html/rfc2326#section-12.37
    let session = response
        .headers
        .get("Session")
        .ok_or_else(|| "Missing Session header".to_string())?;
    let session_str: &str = session;
    let session = match session_str.split_once(';') {
        None => SessionHeader {
            id: session_str.into(),
            timeout_sec: 60, // default
        },
        Some((id, timeout_str)) => {
            if let Some(v) = timeout_str.trim().strip_prefix("timeout=") {
                let timeout_sec =
                    u32::from_str_radix(v, 10).map_err(|_| format!("Unparseable timeout {v}"))?;

                if timeout_sec == 0 {
                    // This would make Retina send keepalives at an absurd rate; reject.
                    return Err(format!(
                        "Invalid timeout=0 in Session header {:?}",
                        session_str
                    ));
                }
                SessionHeader {
                    id: id.into(),
                    timeout_sec,
                }
            } else {
                return Err(format!("Unparseable Session header {:?}", session_str));
            }
        }
    };
    let transport = response
        .headers
        .get("Transport")
        .ok_or_else(|| "Missing Transport header".to_string())?;
    let mut channel_id = None;
    let mut ssrc = None;
    let mut source = None;
    let mut server_port = None;
    let transport_str: &str = transport;
    for part in transport_str.split(';') {
        if let Some(v) = part.strip_prefix("ssrc=") {
            let v = v.trim();
            let v = u32::from_str_radix(v, 16).map_err(|_| format!("Unparseable ssrc {v}"))?;
            ssrc = Some(v);
        } else if let Some(interleaved) = part.strip_prefix("interleaved=") {
            let mut channels = interleaved.splitn(2, '-');
            let n = channels.next().expect("splitn returns at least one part");
            let n = u8::from_str_radix(n, 10).map_err(|_| format!("bad channel number {n}"))?;
            if let Some(m) = channels.next() {
                let m = u8::from_str_radix(m, 10)
                    .map_err(|_| format!("bad second channel number {m}"))?;
                if n.checked_add(1) != Some(m) {
                    return Err(format!("Expected adjacent channels; got {n}-{m}"));
                }
            }
            channel_id = Some(n);
        } else if let Some(s) = part.strip_prefix("source=") {
            source = Some(
                s.parse()
                    .map_err(|_| format!("Transport header has unparseable source {s:?}"))?,
            );
        } else if let Some(s) = part.strip_prefix("server_port=") {
            server_port = Some(parse_server_port(s).map_err(|()| {
                format!("Transport header {:?} has bad server_port", &**transport)
            })?);
        }
    }
    Ok(SetupResponse {
        session,
        ssrc,
        channel_id,
        source,
        server_port,
    })
}

/// Parses a `PLAY` response. The error should always be packed into a `RtspProtocolError`.
pub(crate) fn parse_play(
    response: &crate::rtsp::msg::Response,
    presentation: &mut Presentation,
) -> Result<(), String> {
    // https://tools.ietf.org/html/rfc2326#section-12.33
    let rtp_info = match response.headers.get("RTP-Info") {
        Some(rtsp_info) => rtsp_info,
        None => return Ok(()),
    };
    for s in rtp_info.split(',') {
        let s = s.trim();
        let mut parts = s.split(';');
        let url = parts
            .next()
            .expect("split always returns at least one part")
            .strip_prefix("url=")
            .ok_or_else(|| "RTP-Info missing stream URL".to_string())?;
        let url = join_control(&presentation.base_url, url)?;
        let stream = if presentation.streams.len() == 1 {
            // The server is allowed to not specify a stream control URL for
            // single-stream presentations. Additionally, some buggy
            // cameras (eg the GW Security GW4089IP) use an incorrect URL.
            // When there is a single stream in the presentation, there's no
            // ambiguity. Be "forgiving", just as RFC 2326 section 14.3 asks
            // servers to be forgiving of clients with single-stream
            // containers.
            // https://datatracker.ietf.org/doc/html/rfc2326#section-14.3
            Some(&mut presentation.streams[0])
        } else {
            presentation
                .streams
                .iter_mut()
                .find(|s| matches!(&s.control, Some(u) if u == &url))
        };
        let stream = match stream {
            Some(s) => s,
            None => {
                log::warn!("RTP-Info contains unknown stream {}", url);
                continue;
            }
        };
        let state = match &mut stream.state {
            super::StreamState::Uninit => {
                // This appears to happen for Reolink devices when we did not send a SETUP request
                // for all streams. It also happens in some of other the tests
                // here simply because I didn't include all the SETUP steps.
                debug!(
                    "PLAY response described stream {} in Uninit state",
                    stream.control.as_ref().unwrap_or(&presentation.control)
                );
                continue;
            }
            super::StreamState::Init(init) => init,
            super::StreamState::Playing { .. } => unreachable!(),
        };
        for part in parts {
            if part.is_empty() {
                continue;
            }
            let (key, value) = part
                .split_once('=')
                .ok_or_else(|| "RTP-Info param has no =".to_string())?;
            match key {
                "seq" => {
                    let seq =
                        u16::from_str_radix(value, 10).map_err(|_| format!("bad seq {value:?}"))?;
                    state.initial_seq = Some(seq);
                }
                "rtptime" => match u32::from_str_radix(value, 10) {
                    Ok(v) => state.initial_rtptime = Some(v),
                    Err(_) => warn!("Unparseable rtptime in RTP-Info header {:?}", rtp_info),
                },
                "ssrc" => {
                    let value = value.trim();
                    let ssrc = u32::from_str_radix(value, 16)
                        .map_err(|_| format!("Unparseable ssrc {value}"))?;
                    state.ssrc = Some(ssrc);
                }
                _ => {}
            }
        }
    }
    Ok(())
}

#[derive(Default)]
pub(crate) struct OptionsResponse {
    pub(crate) set_parameter_supported: bool,
    pub(crate) get_parameter_supported: bool,
}

/// Parses an `OPTIONS` response.
pub(crate) fn parse_options(
    response: &crate::rtsp::msg::Response,
) -> Result<OptionsResponse, String> {
    let mut interpreted = OptionsResponse::default();

    // RTSP/1.0 OPTIONS method: https://tools.ietf.org/html/rfc2326#section-10.1
    // HTTP/1.1 OPTIONS method: https://www.rfc-editor.org/rfc/rfc2616.html#section-9.2
    // RTSP/1.0 Public header: https://www.rfc-editor.org/rfc/rfc2326.html#section-12.28
    // HTTP/1.1 Public header: https://www.rfc-editor.org/rfc/rfc2068#section-14.35
    if let Some(public) = response.headers.get("Public") {
        for method in public.split(',') {
            let method = method.trim();
            match method {
                "SET_PARAMETER" => interpreted.set_parameter_supported = true,
                "GET_PARAMETER" => interpreted.get_parameter_supported = true,
                _ => {}
            }
        }
    }
    Ok(interpreted)
}

#[cfg(test)]
mod tests {
    use std::num::NonZeroU16;

    use bytes::Bytes;
    use url::Url;

    use crate::{StreamContext, StreamContextInner, TcpStreamContext};
    use crate::{client::StreamStateInit, codec::ParametersRef};

    use super::super::StreamState;
    use super::*;
    use crate::testutil::{init_logging, response};

    fn parse_describe(
        raw_url: &str,
        raw_response: &'static [u8],
    ) -> Result<super::Presentation, String> {
        let url = Url::parse(raw_url).unwrap();
        let (resp, body) = response(raw_response);
        super::parse_describe(url, &resp, &body)
    }

    fn dummy_stream_state_init(ssrc: Option<u32>) -> StreamState {
        StreamState::Init(StreamStateInit {
            ssrc,
            initial_seq: None,
            initial_rtptime: None,
            ctx: StreamContext(StreamContextInner::Tcp(TcpStreamContext {
                rtp_channel_id: 0,
            })),
            udp_sockets: None,
        })
    }

    /// Longse cameras, for whatever reason, have trailing whitespace in their `CSeq` header lines.
    ///
    /// This test ensures this is correctly stripped. RTSP follows HTTP's lead for request/response
    /// parsing (see [RFC 2326 section
    /// 4.1](https://www.rfc-editor.org/rfc/rfc2326.html#page-19)), and HTTP [RFC
    /// 9110 section 5.5](https://www.rfc-editor.org/rfc/rfc9110#name-field-values) says the following:
    ///
    /// > A field value does not include leading or trailing whitespace. When a
    /// > specific version of HTTP allows such whitespace to appear in a message,
    /// > a field parsing implementation MUST exclude such whitespace prior to
    /// > evaluating the field value.
    ///
    /// Our parser strips OWS around header values.
    #[test]
    fn longse_cseq() {
        init_logging();
        let (response, _body) = response(include_bytes!("testdata/longse_unauthorized.txt"));
        assert_eq!(super::get_cseq(&response), Some(1));
    }

    fn sdp_response(body: &'static [u8]) -> (crate::rtsp::msg::Response, Bytes) {
        use crate::rtsp::msg::*;
        (
            Response {
                status_code: StatusCode::OK,
                reason_phrase: "OK".into(),
                headers: [(
                    HeaderName::CONTENT_TYPE,
                    HeaderValue::try_from("application/sdp").unwrap(),
                )]
                .into(),
            },
            Bytes::from_static(body),
        )
    }

    #[test]
    fn anvpiz_sdp() {
        init_logging();
        let url = Url::parse("rtsp://127.0.0.1/").unwrap();
        let (response, body) = sdp_response(include_bytes!("testdata/anpviz_sdp.txt"));
        super::parse_describe(url, &response, &body).unwrap();
    }

    #[test]
    fn geovision_sdp() {
        init_logging();
        let url = Url::parse("rtsp://127.0.0.1/").unwrap();
        let (response, body) = sdp_response(include_bytes!("testdata/geovision_sdp.txt"));
        super::parse_describe(url, &response, &body).unwrap();
    }

    #[test]
    fn ubiquiti_sdp() {
        init_logging();
        let url = Url::parse("rtsp://127.0.0.1/").unwrap();
        let (response, body) = sdp_response(include_bytes!("testdata/ubiquiti_sdp.txt"));
        let d = super::parse_describe(url, &response, &body).unwrap();
        assert_eq!(d.streams.len(), 3);
    }

    /// Parses SDP taken from a TP-LINK TL-IPC44AW-COLOR 4.0 camera running firmware
    /// FW: 1.0.10 Build 220330 Rel.35741n, which has a stream with a string where an RTP
    /// payload description is expected.
    /// https://github.com/scottlamb/moonfire-nvr/issues/238
    #[test]
    fn tplink_sdp() {
        init_logging();
        let url = Url::parse("rtsp://127.0.0.1/").unwrap();
        let (response, body) = sdp_response(include_bytes!("testdata/tplink_sdp.txt"));
        let p = super::parse_describe(url, &response, &body).unwrap();
        assert_eq!(p.streams.len(), 2);
    }

    #[test]
    fn dahua_h264_aac_onvif() {
        init_logging();
        // DESCRIBE.
        let prefix =
            "rtsp://192.168.5.111:554/cam/realmonitor?channel=1&subtype=1&unicast=true&proto=Onvif";
        let mut p = parse_describe(
            prefix,
            include_bytes!("testdata/dahua_describe_h264_aac_onvif.txt"),
        )
        .unwrap();
        assert_eq!(p.control.as_str(), &(prefix.to_string() + "/"));

        assert_eq!(p.streams.len(), 3);

        // H.264 video stream.
        assert_eq!(
            p.streams[0].control.as_ref().unwrap().as_str(),
            &(prefix.to_string() + "/trackID=0")
        );
        assert_eq!(p.streams[0].media(), "video");
        assert_eq!(p.streams[0].encoding_name(), "h264");
        assert_eq!(p.streams[0].rtp_payload_type, 96);
        assert_eq!(p.streams[0].clock_rate_hz, 90_000);
        match p.streams[0].parameters().unwrap() {
            ParametersRef::Video(v) => {
                assert_eq!(v.rfc6381_codec(), "avc1.64001E");
                assert_eq!(v.pixel_dimensions(), (704, 480));
                assert_eq!(v.pixel_aspect_ratio(), None);
                assert_eq!(v.frame_rate(), Some((2, 30)));
            }
            _ => panic!(),
        }

        // .mp4 audio stream.
        assert_eq!(
            p.streams[1].control.as_ref().unwrap().as_str(),
            &(prefix.to_string() + "/trackID=1")
        );
        assert_eq!(p.streams[1].media(), "audio");
        assert_eq!(p.streams[1].encoding_name(), "mpeg4-generic");
        assert_eq!(p.streams[1].rtp_payload_type, 97);
        assert_eq!(p.streams[1].clock_rate_hz, 48_000);
        match p.streams[1].parameters() {
            Some(ParametersRef::Audio(_)) => {}
            _ => panic!(),
        }

        // ONVIF parameters stream.
        assert_eq!(
            p.streams[2].control.as_ref().unwrap().as_str(),
            &(prefix.to_string() + "/trackID=4")
        );
        assert_eq!(p.streams[2].media(), "application");
        assert_eq!(p.streams[2].encoding_name(), "vnd.onvif.metadata");
        assert_eq!(p.streams[2].rtp_payload_type, 107);
        assert_eq!(p.streams[2].clock_rate_hz, 90_000);
        assert!(matches!(
            p.streams[2].parameters(),
            Some(ParametersRef::Message(_))
        ));

        // SETUP.
        let setup_response = response(include_bytes!("testdata/dahua_setup.txt"));
        let setup_response = super::parse_setup(&setup_response.0).unwrap();
        assert_eq!(
            setup_response.session,
            SessionHeader {
                id: "634214675641".into(),
                timeout_sec: 60
            }
        );
        assert_eq!(setup_response.channel_id, Some(0));
        assert_eq!(setup_response.ssrc, Some(0x30a98ee7));
        p.streams[0].state = dummy_stream_state_init(Some(0x30a98ee7));

        // PLAY.
        super::parse_play(
            &response(include_bytes!("testdata/dahua_play.txt")).0,
            &mut p,
        )
        .unwrap();
        match &p.streams[0].state {
            StreamState::Init(s) => {
                assert_eq!(s.initial_seq, Some(47121));
                assert_eq!(s.initial_rtptime, Some(3475222385));
            }
            _ => panic!(),
        };
        // The other streams don't get filled in because they're in state Uninit.

        // OPTIONS.
        let opts = super::parse_options(&response(include_bytes!("testdata/dahua_options.txt")).0)
            .unwrap();
        assert!(opts.set_parameter_supported);
    }

    #[test]
    fn dahua_h265_pcma() {
        init_logging();
        let p = parse_describe(
            "rtsp://192.168.5.111:554/cam/realmonitor?channel=1&subtype=2",
            include_bytes!("testdata/dahua_describe_h265_pcma.txt"),
        )
        .unwrap();

        // Abridged test; similar to the other Dahua test.
        assert_eq!(p.streams.len(), 2);
        assert_eq!(p.streams[0].media(), "video");
        assert_eq!(p.streams[0].encoding_name(), "h265");
        assert_eq!(p.streams[0].rtp_payload_type, 98);

        if cfg!(feature = "h265") {
            assert!(p.streams[0].parameters().is_some());
            assert_eq!(p.streams[1].media(), "audio");
            assert_eq!(p.streams[1].encoding_name(), "pcma");
            assert_eq!(p.streams[1].rtp_payload_type, 8);
            match p.streams[1].parameters().unwrap() {
                ParametersRef::Audio(_) => {}
                _ => panic!(),
            };
        } else {
            assert!(p.streams[0].parameters().is_none());
        }
    }

    #[test]
    fn hikvision() {
        init_logging();
        // DESCRIBE.
        let prefix = "rtsp://192.168.5.106:554/Streaming/Channels/101";
        let mut p = parse_describe(
            &(prefix.to_string() + "?transportmode=unicast&Profile=Profile_1"),
            include_bytes!("testdata/hikvision_describe.txt"),
        )
        .unwrap();
        assert_eq!(
            p.base_url.as_str(),
            "rtsp://192.168.5.106:554/Streaming/Channels/101/"
        );

        assert_eq!(p.streams.len(), 2);

        // H.264 video stream.
        assert_eq!(
            p.streams[0].control.as_ref().unwrap().as_str(),
            &(prefix.to_string() + "/trackID=1?transportmode=unicast&profile=Profile_1")
        );
        assert_eq!(p.streams[0].media(), "video");
        assert_eq!(p.streams[0].encoding_name(), "h264");
        assert_eq!(p.streams[0].rtp_payload_type, 96);
        assert_eq!(p.streams[0].clock_rate_hz, 90_000);
        match p.streams[0].parameters().unwrap() {
            ParametersRef::Video(v) => {
                assert_eq!(v.rfc6381_codec(), "avc1.4D0029");
                assert_eq!(v.pixel_dimensions(), (1920, 1080));
                assert_eq!(v.pixel_aspect_ratio(), None);
                assert_eq!(v.frame_rate(), Some((2_000, 60_000)));
            }
            _ => panic!(),
        }

        // ONVIF parameters stream.
        assert_eq!(
            p.streams[1].control.as_ref().unwrap().as_str(),
            &(prefix.to_string() + "/trackID=3?transportmode=unicast&profile=Profile_1")
        );
        assert_eq!(p.streams[1].media(), "application");
        assert_eq!(p.streams[1].encoding_name(), "vnd.onvif.metadata");
        assert_eq!(p.streams[1].rtp_payload_type, 107);
        assert_eq!(p.streams[1].clock_rate_hz, 90_000);
        assert!(matches!(
            p.streams[1].parameters(),
            Some(ParametersRef::Message(_))
        ));

        // SETUP.
        let setup_response = response(include_bytes!("testdata/hikvision_setup.txt"));
        let setup_response = super::parse_setup(&setup_response.0).unwrap();
        assert_eq!(
            setup_response.session,
            SessionHeader {
                id: "708345999".into(),
                timeout_sec: 60
            }
        );
        assert_eq!(setup_response.channel_id, Some(0));
        assert_eq!(setup_response.ssrc, Some(0x4cacc3d1));
        p.streams[0].state = dummy_stream_state_init(Some(0x4cacc3d1));

        // PLAY.
        super::parse_play(
            &response(include_bytes!("testdata/hikvision_play.txt")).0,
            &mut p,
        )
        .unwrap();
        match &p.streams[0].state {
            StreamState::Init(state) => {
                assert_eq!(state.initial_seq, Some(24104));
                assert_eq!(state.initial_rtptime, Some(1270711678));
            }
            _ => panic!(),
        }
        // The other stream isn't filled in because it's in state Uninit.
    }

    #[test]
    fn reolink() {
        init_logging();
        // DESCRIBE.
        let mut p = parse_describe(
            "rtsp://192.168.5.206:554/h264Preview_01_main",
            include_bytes!("testdata/reolink_describe.txt"),
        )
        .unwrap();
        let p2 = parse_describe(
            "rtsp://192.168.5.206:554/h264Preview_01_main",
            include_bytes!("testdata/reolink_describe_control_first.txt"),
        )
        .unwrap();
        assert_eq!(p.control, p2.control);
        assert_eq!(p.tool, p2.tool);
        let base = "rtsp://192.168.5.206/h264Preview_01_main/";
        assert_eq!(p.control.as_str(), base);
        assert_eq!(
            p.tool.as_deref(),
            Some("LIVE555 Streaming Media v2013.04.08")
        );

        assert_eq!(p.streams.len(), 2);

        // H.264 video stream.
        assert_eq!(
            p.streams[0].control.as_ref().unwrap().as_str(),
            &(base.to_string() + "trackID=1")
        );
        assert_eq!(p.streams[0].media(), "video");
        assert_eq!(p.streams[0].encoding_name(), "h264");
        assert_eq!(p.streams[0].rtp_payload_type, 96);
        assert_eq!(p.streams[0].clock_rate_hz, 90_000);
        match p.streams[0].parameters().unwrap() {
            ParametersRef::Video(v) => {
                assert_eq!(v.rfc6381_codec(), "avc1.640033");
                assert_eq!(v.pixel_dimensions(), (2560, 1440));
                assert_eq!(v.pixel_aspect_ratio(), None);
                assert_eq!(v.frame_rate(), None);
            }
            _ => panic!(),
        };

        // audio stream
        assert_eq!(
            p.streams[1].control.as_ref().unwrap().as_str(),
            &(base.to_string() + "trackID=2")
        );
        assert_eq!(p.streams[1].media(), "audio");
        assert_eq!(p.streams[1].encoding_name(), "mpeg4-generic");
        assert_eq!(p.streams[1].rtp_payload_type, 97);
        assert_eq!(p.streams[1].clock_rate_hz, 16_000);
        match p.streams[1].parameters() {
            Some(ParametersRef::Audio(_)) => {}
            _ => panic!(),
        }

        // SETUP.
        let setup_response = response(include_bytes!("testdata/reolink_setup.txt"));
        let setup_response = super::parse_setup(&setup_response.0).unwrap();
        assert_eq!(
            setup_response.session,
            SessionHeader {
                id: "F8F8E425".into(),
                timeout_sec: 60
            }
        );
        assert_eq!(setup_response.channel_id, Some(0));
        assert_eq!(setup_response.ssrc, None);
        p.streams[0].state = dummy_stream_state_init(None);
        p.streams[1].state = dummy_stream_state_init(None);

        // PLAY.
        super::parse_play(
            &response(include_bytes!("testdata/reolink_play.txt")).0,
            &mut p,
        )
        .unwrap();
        match &p.streams[0].state {
            StreamState::Init(state) => {
                assert_eq!(state.initial_seq, Some(16852));
                assert_eq!(state.initial_rtptime, Some(1070938629));
            }
            _ => panic!(),
        };
        match &p.streams[1].state {
            StreamState::Init(state) => {
                assert_eq!(state.initial_rtptime, Some(3075976528));
                assert_eq!(state.ssrc, Some(0x9fc9fff8));
            }
            _ => panic!(),
        };
    }

    #[test]
    fn bunny() {
        init_logging();
        // This is a public test server for Wowza Streaming Engine.
        // https://www.wowza.com/html/mobile.html

        // DESCRIBE.
        let prefix = "rtsp://wowzaec2demo.streamlock.net/vod/mp4:BigBuckBunny_115k.mov";
        let mut p = parse_describe(prefix, include_bytes!("testdata/bunny_describe.txt")).unwrap();
        assert_eq!(p.control.as_str(), &(prefix.to_string() + "/"));

        assert_eq!(p.streams.len(), 2);

        // audio stream
        assert_eq!(
            p.streams[0].control.as_ref().unwrap().as_str(),
            &(prefix.to_string() + "/trackID=1")
        );
        assert_eq!(p.streams[0].media(), "audio");
        assert_eq!(p.streams[0].encoding_name(), "mpeg4-generic");
        assert_eq!(p.streams[0].rtp_payload_type, 96);
        assert_eq!(p.streams[0].clock_rate_hz, 12_000);
        assert_eq!(p.streams[0].channels, NonZeroU16::new(2));
        match p.streams[0].parameters() {
            Some(ParametersRef::Audio(_)) => {}
            _ => panic!(),
        }

        // H.264 video stream.
        assert_eq!(
            p.streams[1].control.as_ref().unwrap().as_str(),
            &(prefix.to_string() + "/trackID=2")
        );
        assert_eq!(p.streams[1].media(), "video");
        assert_eq!(p.streams[1].encoding_name(), "h264");
        assert_eq!(p.streams[1].rtp_payload_type, 97);
        assert_eq!(p.streams[1].clock_rate_hz, 90_000);
        match p.streams[1].parameters().unwrap() {
            ParametersRef::Video(v) => {
                assert_eq!(v.rfc6381_codec(), "avc1.42C01E");
                assert_eq!(v.pixel_dimensions(), (240, 160));
                assert_eq!(v.pixel_aspect_ratio(), None);
                assert_eq!(v.frame_rate(), Some((2, 48)));
            }
            _ => panic!(),
        }

        // SETUP.
        let setup_response = response(include_bytes!("testdata/bunny_setup.txt"));
        let setup_response = super::parse_setup(&setup_response.0).unwrap();
        assert_eq!(
            setup_response.session,
            SessionHeader {
                id: "1642021126".into(),
                timeout_sec: 60
            }
        );
        assert_eq!(setup_response.channel_id, Some(0));
        assert_eq!(setup_response.ssrc, None);
        p.streams[0].state = dummy_stream_state_init(None);
        p.streams[1].state = dummy_stream_state_init(None);

        // PLAY.
        super::parse_play(
            &response(include_bytes!("testdata/bunny_play.txt")).0,
            &mut p,
        )
        .unwrap();
        match &p.streams[1].state {
            StreamState::Init(state) => {
                assert_eq!(state.initial_rtptime, Some(0));
                assert_eq!(state.initial_seq, Some(1));
                assert_eq!(state.ssrc, None);
            }
            _ => panic!(),
        };
    }

    #[test]
    fn missing_contenttype_describe() {
        let prefix = "rtsp://192.168.1.101/live/test";
        parse_describe(
            prefix,
            include_bytes!("testdata/missing_content_type_describe.txt"),
        )
        .unwrap();
    }

    /// Simulates a negative `rtptime` value in the `PLAY` response, as returned by the OMNY M5S2A
    /// 2812 in [scottlamb/moonfire-nvr#224](https://github.com/scottlamb/moonfire-nvr/issues/224).
    ///
    /// The rest of the data is copied from the `bunny` test above.
    ///
    /// This is currently treated as if the `rtptime` parameter were absent.
    #[test]
    fn bad_rtptime() {
        init_logging();
        let prefix = "rtsp://wowzaec2demo.streamlock.net/vod/mp4:BigBuckBunny_115k.mov";
        let mut p = parse_describe(prefix, include_bytes!("testdata/bunny_describe.txt")).unwrap();
        p.streams[0].state = dummy_stream_state_init(None);
        super::parse_play(
            &response(include_bytes!("testdata/bad_rtptime.txt")).0,
            &mut p,
        )
        .unwrap();
        match &p.streams[0].state {
            StreamState::Init(state) => {
                assert_eq!(state.initial_rtptime, None);
                assert_eq!(state.initial_seq, Some(1));
                assert_eq!(state.ssrc, None);
            }
            _ => panic!(),
        };
    }

    #[test]
    fn foscam() {
        init_logging();
        // DESCRIBE.
        let prefix = "rtsp://192.168.5.107:65534/videoMain";
        let p = parse_describe(prefix, include_bytes!("testdata/foscam_describe.txt")).unwrap();
        assert_eq!(p.control.as_str(), &(prefix.to_string() + "/"));
        assert_eq!(
            p.tool.as_deref(),
            Some("LIVE555 Streaming Media v2014.02.10")
        );

        assert_eq!(p.streams.len(), 2);

        // H.264 video stream.
        assert_eq!(
            p.streams[0].control.as_ref().unwrap().as_str(),
            &(prefix.to_string() + "/track1")
        );
        assert_eq!(p.streams[0].media(), "video");
        assert_eq!(p.streams[0].encoding_name(), "h264");
        assert_eq!(p.streams[0].rtp_payload_type, 96);
        assert_eq!(p.streams[0].clock_rate_hz, 90_000);
        match p.streams[0].parameters().unwrap() {
            ParametersRef::Video(v) => {
                assert_eq!(v.rfc6381_codec(), "avc1.4D001F");
                assert_eq!(v.pixel_dimensions(), (1280, 720));
                assert_eq!(v.pixel_aspect_ratio(), None);
                assert_eq!(v.frame_rate(), None);
            }
            _ => panic!(),
        }

        // audio stream
        assert_eq!(
            p.streams[1].control.as_ref().unwrap().as_str(),
            &(prefix.to_string() + "/track2")
        );
        assert_eq!(p.streams[1].media(), "audio");
        assert_eq!(p.streams[1].encoding_name(), "pcmu");
        assert_eq!(p.streams[1].rtp_payload_type, 0);
        assert_eq!(p.streams[1].clock_rate_hz, 8_000);
        assert_eq!(p.streams[1].channels, NonZeroU16::new(1));
        match p.streams[1].parameters().unwrap() {
            ParametersRef::Audio(_) => {}
            _ => panic!(),
        };
    }

    #[test]
    fn vstarcam() {
        init_logging();
        // DESCRIBE.
        let prefix = "rtsp://192.168.1.198:10554/tcp/av0_0";
        let p = parse_describe(prefix, include_bytes!("testdata/vstarcam_describe.txt")).unwrap();
        assert_eq!(p.control.as_str(), &(prefix.to_string()));

        assert_eq!(p.streams.len(), 2);

        // H.264 video stream.
        assert_eq!(
            p.streams[0].control.as_ref().unwrap().as_str(),
            &(prefix.to_string() + "/track0")
        );

        assert_eq!(p.streams[0].media(), "video");

        assert_eq!(p.streams[0].encoding_name(), "h264");
        assert_eq!(p.streams[0].rtp_payload_type, 96);
        assert_eq!(p.streams[0].clock_rate_hz, 90_000);

        match p.streams[0].parameters().unwrap() {
            ParametersRef::Video(v) => {
                assert_eq!(v.rfc6381_codec(), "avc1.4D002A");
                assert_eq!(v.pixel_dimensions(), (1920, 1080));
                assert_eq!(v.pixel_aspect_ratio(), None);
                assert_eq!(v.frame_rate(), Some((2, 15)));
            }
            _ => panic!(),
        }

        // audio stream
        assert_eq!(
            p.streams[1].control.as_ref().unwrap().as_str(),
            &(prefix.to_string() + "/track1")
        );
        assert_eq!(p.streams[1].media(), "audio");
        assert_eq!(p.streams[1].encoding_name(), "pcma");
        assert_eq!(p.streams[1].rtp_payload_type, 8);
        assert_eq!(p.streams[1].clock_rate_hz, 8_000);
        assert_eq!(p.streams[1].channels, NonZeroU16::new(1));
        match p.streams[1].parameters().unwrap() {
            ParametersRef::Audio(_) => {}
            _ => panic!(),
        };
    }

    /// [GW Security GW4089IP](https://github.com/scottlamb/moonfire-nvr/wiki/Cameras:-GW-Security#gw4089ip),
    /// main stream (high-res, audio).
    #[test]
    fn gw_main() {
        init_logging();
        // DESCRIBE.
        let base = "rtsp://192.168.1.110:5050/H264?channel=1&subtype=0&unicast=true&proto=Onvif";
        let mut p = parse_describe(base, include_bytes!("testdata/gw_main_describe.txt")).unwrap();
        assert_eq!(p.control.as_str(), base);

        assert_eq!(p.streams.len(), 2);

        // H.264 video stream.
        assert_eq!(
            p.streams[0].control.as_ref().unwrap().as_str(),
            "rtsp://192.168.1.110:5050/H264?channel=1&subtype=0&unicast=true&proto=Onvif/video"
        );
        assert_eq!(p.streams[0].media(), "video");
        assert_eq!(p.streams[0].encoding_name(), "h264");
        assert_eq!(p.streams[0].rtp_payload_type, 96);
        assert_eq!(p.streams[0].clock_rate_hz, 90_000);
        let Some(ParametersRef::Video(v)) = p.streams[0].parameters() else {
            panic!();
        };
        assert_eq!(v.rfc6381_codec(), "avc1.4D002A");

        // audio stream
        assert_eq!(
            p.streams[1].control.as_ref().unwrap().as_str(),
            "rtsp://192.168.1.110:5050/H264?channel=1&subtype=0&unicast=true&proto=Onvif/audio"
        );
        assert_eq!(p.streams[1].media(), "audio");
        assert_eq!(p.streams[1].encoding_name(), "pcmu"); // rtpmap wins over static list.
        assert_eq!(p.streams[1].rtp_payload_type, 8);
        assert_eq!(p.streams[1].clock_rate_hz, 8_000);
        assert_eq!(p.streams[1].channels, NonZeroU16::new(1));
        match p.streams[1].parameters().unwrap() {
            ParametersRef::Audio(_) => {}
            _ => panic!(),
        };

        // SETUP.
        let setup_response = response(include_bytes!("testdata/gw_main_setup_video.txt"));
        let setup_response = super::parse_setup(&setup_response.0).unwrap();
        assert_eq!(
            setup_response.session,
            SessionHeader {
                id: "9a90de54".into(),
                timeout_sec: 60
            }
        );
        assert_eq!(setup_response.channel_id, Some(0));
        assert_eq!(setup_response.ssrc, None);
        p.streams[0].state = dummy_stream_state_init(None);

        let setup_response = response(include_bytes!("testdata/gw_main_setup_audio.txt"));
        let setup_response = super::parse_setup(&setup_response.0).unwrap();
        assert_eq!(
            setup_response.session,
            SessionHeader {
                id: "9a90de54".into(),
                timeout_sec: 60
            }
        );
        assert_eq!(setup_response.channel_id, Some(2));
        assert_eq!(setup_response.ssrc, None);
        p.streams[1].state = dummy_stream_state_init(None);

        // PLAY.
        super::parse_play(
            &response(include_bytes!("testdata/gw_main_play.txt")).0,
            &mut p,
        )
        .unwrap();
        // The RTP-Info url= isn't in the expected format, so its contents are skipped.
        match &p.streams[0].state {
            StreamState::Init(s) => {
                assert_eq!(s.initial_seq, None);
                assert_eq!(s.initial_rtptime, None);
            }
            _ => panic!(),
        };
        match &p.streams[1].state {
            StreamState::Init(s) => {
                assert_eq!(s.initial_seq, None);
                assert_eq!(s.initial_rtptime, None);
            }
            _ => panic!(),
        };
    }

    /// [GW Security GW4089IP](https://github.com/scottlamb/moonfire-nvr/wiki/Cameras:-GW-Security#gw4089ip),
    /// sub stream (low-res, no audio).
    #[test]
    fn gw_sub() {
        init_logging();
        // DESCRIBE.
        let base = "rtsp://192.168.1.110:5049/H264?channel=1&subtype=1&unicast=true&proto=Onvif";
        let mut p = parse_describe(base, include_bytes!("testdata/gw_sub_describe.txt")).unwrap();
        assert_eq!(
            p.control.as_str(),
            "rtsp://192.168.1.110:5049/H264?channel=1&subtype=1&unicast=true&proto=Onvif"
        );
        assert_eq!(p.streams.len(), 1);

        // H.264 video stream.
        assert_eq!(
            p.streams[0].control.as_ref().unwrap().as_str(),
            "rtsp://192.168.1.110:5049/H264?channel=1&subtype=1&unicast=true&proto=Onvif/video"
        );
        assert_eq!(p.streams[0].media(), "video");
        assert_eq!(p.streams[0].encoding_name(), "h264");
        assert_eq!(p.streams[0].rtp_payload_type, 96);
        assert_eq!(p.streams[0].clock_rate_hz, 90_000);
        let Some(ParametersRef::Video(v)) = p.streams[0].parameters() else {
            panic!();
        };
        assert_eq!(v.rfc6381_codec(), "avc1.4D001E");

        // SETUP.
        let setup_response = response(include_bytes!("testdata/gw_sub_setup.txt"));
        let setup_response = super::parse_setup(&setup_response.0).unwrap();
        assert_eq!(
            setup_response.session,
            SessionHeader {
                id: "9b0d0e54".into(),
                timeout_sec: 60
            }
        );
        assert_eq!(setup_response.channel_id, Some(0));
        assert_eq!(setup_response.ssrc, None);
        p.streams[0].state = dummy_stream_state_init(None);

        // PLAY.
        super::parse_play(
            &response(include_bytes!("testdata/gw_sub_play.txt")).0,
            &mut p,
        )
        .unwrap();
        match &p.streams[0].state {
            StreamState::Init(s) => {
                assert_eq!(s.initial_seq, Some(273));
                assert_eq!(s.initial_rtptime, Some(1621810809));
            }
            _ => panic!(),
        };
    }

    /// Tests parsing SDP from `the macro-video rtsp server`, with missing origin line.
    #[test]
    fn macrovideo() {
        init_logging();
        // DESCRIBE.
        let p = parse_describe(
            "rtsp://camera",
            include_bytes!("testdata/macrovideo_describe.txt"),
        )
        .unwrap();
        assert_eq!(p.streams.len(), 1);
    }

    /// Tests parsing SDP from an "IPCAM C9F0SeZ3N0PbL0" [sic], as quoted in
    /// [imoonfire-nvr#213](https://github.com/scottlamb/moonfire-nvr/issues/213).
    ///
    /// This camera notably sends a trailing space in its `rtpmap` attribute.
    #[test]
    fn ipcam() {
        init_logging();
        // DESCRIBE.
        let p = parse_describe(
            "rtsp://camera",
            include_bytes!("testdata/ipcam_describe.txt"),
        )
        .unwrap();
        assert_eq!(p.streams.len(), 1);
    }

    /// Tests that a trailing semicolon in the `RTP-Info` header is ignored.
    /// Reproduces <https://github.com/scottlamb/retina/issues/117>.
    ///
    /// The Laureii camera sends, e.g.:
    /// `RTP-Info: url=rtsp://192.168.1.19:554/0/main/trackID=0;seq=0;rtptime=0;`
    #[test]
    fn rtp_info_trailing_semicolon() {
        init_logging();
        let base = "rtsp://192.168.1.110:5049/H264?channel=1&subtype=1&unicast=true&proto=Onvif";
        let mut p = parse_describe(base, include_bytes!("testdata/gw_sub_describe.txt")).unwrap();
        p.streams[0].state = dummy_stream_state_init(None);
        super::parse_play(
            &response(include_bytes!("testdata/laureii_play.txt")).0,
            &mut p,
        )
        .unwrap();
        match &p.streams[0].state {
            StreamState::Init(s) => {
                assert_eq!(s.initial_seq, Some(0));
                assert_eq!(s.initial_rtptime, Some(0));
            }
            _ => panic!(),
        };
    }

    /// Some Hikvision cameras send SSRC with a leading space in the
    /// Transport header (e.g. `ssrc= d6d6627`). Ensure we trim whitespace
    /// before parsing.
    #[test]
    fn hikvision_ssrc_with_leading_space() {
        init_logging();
        let setup_response = response(include_bytes!("testdata/hikvision_setup_ssrc_space.txt"));
        let r = parse_setup(&setup_response.0).unwrap();
        assert_eq!(
            r,
            SetupResponse {
                source: None,
                session: SessionHeader {
                    id: "708886412".into(),
                    timeout_sec: 60,
                },
                channel_id: Some(0),
                ssrc: Some(0x0d6d6627),
                server_port: None,
            }
        );
    }

    #[test]
    fn luckfox_setup_tcp() {
        init_logging();
        let setup_response = response(include_bytes!("testdata/luckfox_rkipc_setup_tcp.txt"));
        let r = parse_setup(&setup_response.0).unwrap();
        assert_eq!(
            r,
            SetupResponse {
                source: None,
                session: SessionHeader {
                    id: "12345678".into(),
                    timeout_sec: 60,
                },
                channel_id: Some(0),
                ssrc: Some(0x22345684),
                server_port: None,
            }
        );
    }

    #[test]
    fn luckfox_setup_udp() {
        init_logging();
        let setup_response = response(include_bytes!("testdata/luckfox_rkipc_setup_udp.txt"));
        let r = parse_setup(&setup_response.0).unwrap();
        assert_eq!(
            r,
            SetupResponse {
                source: None,
                session: SessionHeader {
                    id: "12345678".into(),
                    timeout_sec: 60,
                },
                channel_id: None,
                ssrc: Some(0x22345685),
                server_port: Some(49152),
            }
        );
    }
}
