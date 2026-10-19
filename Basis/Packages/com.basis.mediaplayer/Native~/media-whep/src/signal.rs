//! WHEP signalling (draft-ietf-wish-whep), hand-rolled: one POST
//! carrying the SDP offer, two answer flows, PATCH for the counter-offer
//! flow, DELETE on teardown. Every request runs the media-io connect
//! discipline — the endpoint host is resolved once, every address vetted
//! through the engine's one gate, and the client pinned to the vetted
//! set; redirects re-vet and re-pin per hop. The gate here covers the
//! signalling URLs only; media-path addresses (ICE candidates) are vetted
//! at the transmit boundary in the session driver.

use std::sync::Arc;
use std::time::Duration;

use media_io::{AddressGate, IoLimits};
use url::Url;

/// How the server answered the offer (negotiation shape is
/// diagnosable, never silent).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AnswerFlow {
    /// `201 Created` with the SDP answer in the body.
    Direct,
    /// `406 Not Acceptable` with a server counter-offer; our answer went
    /// back via `PATCH` to the resource URL.
    CounterOffer,
}

impl std::fmt::Display for AnswerFlow {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Direct => write!(f, "201+answer"),
            Self::CounterOffer => write!(f, "406+counter-offer"),
        }
    }
}

#[derive(Debug)]
pub enum WhepError {
    Url(String),
    /// The gate refused an address, resolution failed, or the transport
    /// failed underneath the request.
    Io(media_io::IoError),
    /// The server answered with an unexpected status.
    Http {
        status: u16,
        detail: String,
    },
    /// The response violated the WHEP shape (missing Location, empty
    /// SDP body, redirect loop).
    Protocol(String),
    /// The session was closed while signalling was still in flight.
    Cancelled,
}

impl std::fmt::Display for WhepError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Url(detail) => write!(f, "whep url: {detail}"),
            Self::Io(e) => write!(f, "whep signalling: {e}"),
            Self::Http { status, detail } => write!(f, "whep signalling ({status}): {detail}"),
            Self::Protocol(detail) => write!(f, "whep protocol: {detail}"),
            Self::Cancelled => write!(f, "whep signalling cancelled"),
        }
    }
}

impl std::error::Error for WhepError {}

/// What the POST negotiated. The SDP bodies are handed back verbatim;
/// the session layer owns their interpretation (str0m).
#[derive(Debug)]
pub enum PostOutcome {
    /// The common flow: the body is the server's SDP answer.
    Answer {
        resource: Url,
        ice_servers: Vec<String>,
        answer_sdp: String,
    },
    /// The counter-offer flow: the body is the server's SDP offer; ours
    /// goes back via [`patch_answer`].
    CounterOffer {
        resource: Url,
        ice_servers: Vec<String>,
        offer_sdp: String,
    },
}

const REDIRECT_STATUSES: [u16; 5] = [301, 302, 303, 307, 308];
/// Teardown DELETE: bounded so a dead server cannot hold a closing
/// session (fire-and-forget from the demuxer's drop).
pub(crate) const DELETE_TIMEOUT: Duration = Duration::from_secs(3);

/// Map a `whep://` / `wheps://` URL onto its HTTP signalling endpoint
/// (the ws/wss convention: `whep` is plain HTTP, `wheps` is HTTPS).
pub fn signalling_url(url: &str) -> Result<Url, WhepError> {
    let (scheme, rest) = match url.split_once("://") {
        Some(("whep", rest)) => ("http", rest),
        Some(("wheps", rest)) => ("https", rest),
        _ => return Err(WhepError::Url("expected whep:// or wheps://".into())),
    };
    let http = format!("{scheme}://{rest}");
    let parsed = Url::parse(&http).map_err(|e| WhepError::Url(format!("{url}: {e}")))?;
    if parsed.host_str().is_none() {
        return Err(WhepError::Url("whep url without host".into()));
    }
    Ok(parsed)
}

/// A reqwest client pinned to the vetted addresses of one URL's host —
/// the async twin of media-io's blocking connect discipline. The vet is
/// awaited rather than blocked on: these futures share a two-worker
/// runtime with every reader task in the process, and the host being
/// resolved is whichever one a `Location:` header last named.
async fn pinned_client(
    url: &Url,
    limits: &IoLimits,
    gate: &dyn AddressGate,
) -> Result<reqwest::Client, WhepError> {
    let mut builder = reqwest::Client::builder()
        .redirect(reqwest::redirect::Policy::none())
        .connect_timeout(limits.connect_timeout)
        .timeout(limits.request_timeout)
        .no_proxy()
        .user_agent("basis-media/0.1");
    if let Some((domain, addrs)) = media_io::vet_url_async(url, gate)
        .await
        .map_err(WhepError::Io)?
    {
        builder = builder.resolve_to_addrs(&domain, &addrs);
    }
    builder
        .build()
        .map_err(|e| WhepError::Protocol(format!("http client: {}", media_io::error_chain(&e))))
}

/// POST the SDP offer to the endpoint. Follows redirects (re-vetting
/// each hop), returns the answer or counter-offer flow.
pub async fn post_offer(
    endpoint: &Url,
    offer_sdp: &str,
    limits: &IoLimits,
    gate: &Arc<dyn AddressGate>,
) -> Result<PostOutcome, WhepError> {
    let mut current = endpoint.clone();
    for _hop in 0..=limits.max_redirects {
        let client = pinned_client(&current, limits, gate.as_ref()).await?;
        let response = client
            .post(current.clone())
            .header("Content-Type", "application/sdp")
            .body(offer_sdp.to_string())
            .send()
            .await
            .map_err(|e| {
                WhepError::Protocol(format!("POST {current}: {}", media_io::error_chain(&e)))
            })?;

        let status = response.status().as_u16();
        if REDIRECT_STATUSES.contains(&status) {
            let location = header_str(&response, "location")
                .ok_or_else(|| WhepError::Protocol(format!("{status} without Location")))?;
            current = current
                .join(&location)
                .map_err(|e| WhepError::Protocol(format!("redirect {location}: {e}")))?;
            continue;
        }

        let ice_servers = ice_servers_from_links(
            response
                .headers()
                .get_all("link")
                .iter()
                .filter_map(|v| v.to_str().ok()),
        );
        // The resource URL is server-controlled; it is re-vetted by the
        // pinned client build on every later PATCH/DELETE.
        let resource = header_str(&response, "location")
            .map(|l| {
                current
                    .join(&l)
                    .map_err(|e| WhepError::Protocol(format!("Location {l}: {e}")))
            })
            .transpose()?;

        match status {
            201 => {
                let resource =
                    resource.ok_or_else(|| WhepError::Protocol("201 without Location".into()))?;
                let answer_sdp = sdp_body(response, limits).await?;
                return Ok(PostOutcome::Answer {
                    resource,
                    ice_servers,
                    answer_sdp,
                });
            }
            406 => {
                let resource =
                    resource.ok_or_else(|| WhepError::Protocol("406 without Location".into()))?;
                let offer_sdp = sdp_body(response, limits).await?;
                return Ok(PostOutcome::CounterOffer {
                    resource,
                    ice_servers,
                    offer_sdp,
                });
            }
            _ => {
                return Err(WhepError::Http {
                    status,
                    detail: format!("POST {current}"),
                });
            }
        }
    }
    Err(WhepError::Protocol(format!(
        "redirect cap ({}) exceeded",
        limits.max_redirects
    )))
}

/// PATCH our SDP answer to the resource URL (the counter-offer flow's
/// second half).
pub async fn patch_answer(
    resource: &Url,
    answer_sdp: &str,
    limits: &IoLimits,
    gate: &Arc<dyn AddressGate>,
) -> Result<(), WhepError> {
    let client = pinned_client(resource, limits, gate.as_ref()).await?;
    let response = client
        .patch(resource.clone())
        .header("Content-Type", "application/sdp")
        .body(answer_sdp.to_string())
        .send()
        .await
        .map_err(|e| {
            WhepError::Protocol(format!("PATCH {resource}: {}", media_io::error_chain(&e)))
        })?;
    let status = response.status().as_u16();
    if (200..300).contains(&status) {
        Ok(())
    } else {
        Err(WhepError::Http {
            status,
            detail: format!("PATCH {resource}"),
        })
    }
}

/// DELETE the session resource: the WHEP draft's teardown obligation. Errors
/// are reported, not retried: the server reaps dead sessions anyway.
///
/// [`DELETE_TIMEOUT`] is applied twice over: once as reqwest's own
/// connect/request bound, and once around the whole call, which is the
/// only arm that covers the resolve preceding the request.
pub async fn delete_resource(resource: &Url, gate: &Arc<dyn AddressGate>) -> Result<(), WhepError> {
    tokio::time::timeout(DELETE_TIMEOUT, async {
        let limits = IoLimits {
            connect_timeout: DELETE_TIMEOUT,
            request_timeout: DELETE_TIMEOUT,
            ..IoLimits::default()
        };
        let client = pinned_client(resource, &limits, gate.as_ref()).await?;
        client.delete(resource.clone()).send().await.map_err(|e| {
            WhepError::Protocol(format!("DELETE {resource}: {}", media_io::error_chain(&e)))
        })?;
        Ok(())
    })
    .await
    .map_err(|_| WhepError::Protocol(format!("DELETE {resource}: teardown timed out")))?
}

fn header_str(response: &reqwest::Response, name: &str) -> Option<String> {
    response
        .headers()
        .get(name)
        .and_then(|v| v.to_str().ok())
        .map(str::to_string)
}

/// Read the SDP body, bounded by `limits.max_signalling_bytes`. The cap
/// comes off the chunks as they arrive rather than off a stated length,
/// because a chunked answer states none: the request timeout bounds how
/// long the server may write for, not how much lands in memory while it
/// does.
async fn sdp_body(mut response: reqwest::Response, limits: &IoLimits) -> Result<String, WhepError> {
    let cap = limits.max_signalling_bytes;
    let mut buf = Vec::new();
    while let Some(chunk) = response.chunk().await.map_err(|e| {
        WhepError::Protocol(format!("reading SDP body: {}", media_io::error_chain(&e)))
    })? {
        if buf.len() as u64 + chunk.len() as u64 > cap {
            return Err(WhepError::Protocol(format!(
                "SDP body exceeds the {cap}-byte cap"
            )));
        }
        buf.extend_from_slice(&chunk);
    }
    let body = String::from_utf8_lossy(&buf).into_owned();
    if body.trim().is_empty() {
        return Err(WhepError::Protocol("empty SDP body".into()));
    }
    Ok(body)
}

/// Extract `rel="ice-server"` target URIs from `Link` header values
/// (RFC 8288 shape, parsed leniently: hostile or malformed values yield
/// fewer entries, never an error). The engine surfaces these as
/// diagnostics only. No STUN/TURN gathering runs on them: a receive-only
/// client that initiates every connectivity check needs no srflx
/// candidate (the server learns our mapped address peer-reflexively),
/// and TURN relaying is not supported.
pub fn ice_servers_from_links<'a>(values: impl Iterator<Item = &'a str>) -> Vec<String> {
    let mut servers = Vec::new();
    for value in values {
        for part in split_top_level(value) {
            let part = part.trim();
            let Some(rest) = part.strip_prefix('<') else {
                continue;
            };
            let Some((uri, params)) = rest.split_once('>') else {
                continue;
            };
            let is_ice = params.split(';').any(|p| {
                let p = p.trim();
                p.strip_prefix("rel=")
                    .map(|rel| rel.trim_matches('"').eq_ignore_ascii_case("ice-server"))
                    .unwrap_or(false)
            });
            if is_ice && !uri.is_empty() {
                servers.push(uri.to_string());
            }
        }
    }
    servers
}

/// Split a Link header value on commas that sit outside `<...>` and
/// outside quoted strings.
fn split_top_level(value: &str) -> Vec<&str> {
    let mut parts = Vec::new();
    let (mut depth, mut quoted, mut start) = (0u32, false, 0usize);
    for (i, c) in value.char_indices() {
        match c {
            '"' => quoted = !quoted,
            '<' if !quoted => depth = depth.saturating_add(1),
            '>' if !quoted => depth = depth.saturating_sub(1),
            ',' if !quoted && depth == 0 => {
                parts.push(&value[start..i]);
                start = i + 1;
            }
            _ => {}
        }
    }
    parts.push(&value[start..]);
    parts
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn scheme_mapping() {
        assert_eq!(
            signalling_url("whep://host:8889/stream/whep")
                .unwrap()
                .as_str(),
            "http://host:8889/stream/whep"
        );
        assert_eq!(
            signalling_url("wheps://host/live").unwrap().as_str(),
            "https://host/live"
        );
        assert!(signalling_url("http://host/live").is_err());
        assert!(signalling_url("whep://").is_err());
    }

    #[test]
    fn link_parse_basic() {
        let links =
            ice_servers_from_links([r#"<stun:stun.example.net>; rel="ice-server""#].into_iter());
        assert_eq!(links, vec!["stun:stun.example.net"]);
    }

    #[test]
    fn link_parse_list_and_noise() {
        let links = ice_servers_from_links(
            [
                r#"<stun:a.example:3478>; rel="ice-server", <https://spec.example>; rel="describedby""#,
                r#"<turn:b.example?transport=udp>; rel=ice-server; username="u,ser"; credential="p""#,
                "garbage",
                "<>; rel=\"ice-server\"",
            ]
            .into_iter(),
        );
        assert_eq!(
            links,
            vec!["stun:a.example:3478", "turn:b.example?transport=udp"]
        );
    }

    #[test]
    fn link_parse_hostile_never_panics() {
        for hostile in [
            "<<<<>>>>,,,\"",
            "\u{0}<x>;rel=ice-server",
            "<x>;rel=",
            ",",
            "<",
            ">",
            "<a>;rel=\"ICE-SERVER\"",
        ] {
            let _ = ice_servers_from_links([hostile].into_iter());
        }
    }
}
