//! HTTP(S) byte source over positioned reads.
//!
//! Open resolves the host once, vets *every* returned address through the
//! gate, and pins the connection to the vetted set (`resolve_to_addrs`)
//! with Host/SNI carried by the URL, which closes the resolve-then-reconnect
//! TOCTOU by construction. Redirects are handled manually so each hop
//! re-runs the same vetting and re-pins, on every request and not only the
//! first: a server may answer any ranged read with a redirect, and each
//! read walks from the URL the caller opened rather than from wherever an
//! earlier walk ended.
//!
//! Reads on a range-capable server are chunked ranged requests, so a
//! stalled link surfaces as a typed error rather than a silent hang;
//! sequential reads ride one pooled connection, and a positioned read
//! elsewhere costs one request. A server that answers the opening probe
//! with a 200 has handed over the whole entity, so that response *is* the
//! sequential stream (forward reads discard, backward reads restart) and
//! the open costs one connection. Origins that answer this way often
//! allow no more than one.
//!
//! No total timeout bounds a body, only the client's read timeout, so every
//! request and every read also races the session's [`CancelToken`]: a
//! closing session joins the opener and demux threads this source runs on,
//! and a server that stops answering must hold neither of them.

use std::future::Future;
use std::net::SocketAddr;
use std::sync::Arc;
use std::time::Duration;

use bytes::Bytes;
use media_demux::{ByteSource, SourceError};
use media_diag::diag_log;
use url::Url;

use crate::cancel::CancelToken;
use crate::runtime::runtime;
use crate::{AddressGate, IoError, IoErrorKind, IoLimits};

pub(crate) const REDIRECT_STATUSES: [u16; 5] = [301, 302, 303, 307, 308];

/// What one URL still needs looked up before its client can be pinned.
enum VetTarget {
    /// An IP literal, vetted by the screen that produced this.
    Literal,
    Domain {
        domain: String,
        port: u16,
    },
}

/// The half of vetting that touches no network: scheme screen, and the
/// literal hosts settled against the gate on the spot.
fn vet_target(url: &Url, gate: &dyn AddressGate) -> Result<VetTarget, IoError> {
    if !matches!(url.scheme(), "http" | "https") {
        return Err(IoError::new(
            IoErrorKind::Url,
            format!("unsupported scheme: {}", url.scheme()),
        ));
    }
    let host = url
        .host()
        .ok_or_else(|| IoError::new(IoErrorKind::Url, "URL without host"))?;
    let port = url
        .port_or_known_default()
        .ok_or_else(|| IoError::new(IoErrorKind::Url, "URL without port"))?;

    match host {
        url::Host::Ipv4(ip) => {
            if !gate.permit(ip.into()) {
                return Err(IoError::new(IoErrorKind::Blocked, format!("{ip} blocked")));
            }
            Ok(VetTarget::Literal)
        }
        url::Host::Ipv6(ip) => {
            if !gate.permit(ip.into()) {
                return Err(IoError::new(IoErrorKind::Blocked, format!("{ip} blocked")));
            }
            Ok(VetTarget::Literal)
        }
        url::Host::Domain(domain) => Ok(VetTarget::Domain {
            domain: domain.to_string(),
            port,
        }),
    }
}

/// The other half: permit *every* returned address (a mixed
/// public/private answer is the rebinding shape, refused whole) and hand
/// back the pinned set for `resolve_to_addrs`.
fn vet_addrs(
    domain: String,
    addrs: Vec<SocketAddr>,
    gate: &dyn AddressGate,
) -> Result<(String, Vec<SocketAddr>), IoError> {
    if addrs.is_empty() {
        return Err(IoError::new(
            IoErrorKind::Resolve,
            format!("{domain}: no addresses"),
        ));
    }
    for addr in &addrs {
        if !gate.permit(addr.ip()) {
            return Err(IoError::new(
                IoErrorKind::Blocked,
                format!("{domain} resolves to blocked {}", addr.ip()),
            ));
        }
    }
    Ok((domain, addrs))
}

/// Scheme/host vetting: resolve once under the resolver ceiling, vet the
/// whole answer, and hand back the pinned set for `resolve_to_addrs`.
/// `None` means the host was an IP literal, already vetted. The resolve
/// is a real await point, so a `select!` racing this against a cancel
/// token can observe the cancel. Public for transports that run their own
/// HTTP requests over the same discipline (WHEP signalling).
pub async fn vet_url_async(
    url: &Url,
    gate: &dyn AddressGate,
) -> Result<Option<(String, Vec<SocketAddr>)>, IoError> {
    match vet_target(url, gate)? {
        VetTarget::Literal => Ok(None),
        VetTarget::Domain { domain, port } => {
            let addrs = crate::resolve::resolve_async(&domain, port).await?;
            vet_addrs(domain, addrs, gate).map(Some)
        }
    }
}

pub struct HttpSource {
    /// The URL the caller opened. Every request after the open starts its
    /// walk here rather than at [`Self::url`]: a redirector handing out
    /// short-lived signed targets has to be asked again for a fresh one,
    /// and the target a previous chain settled on is the thing that
    /// expires first.
    origin: Url,
    /// Where the latest walk landed, for reporting only.
    url: Url,
    clients: PinnedClients,
    gate: Arc<dyn AddressGate>,
    limits: IoLimits,
    len: Option<u64>,
    ranges: bool,
    /// Whether the server will serve byte ranges at all, which is a wider
    /// question than [`Self::ranges`]: that one records a 206 we actually
    /// got and so drives the reads, while a server can answer 200 to a
    /// range request and still advertise `Accept-Ranges: bytes`.
    rangeable: bool,
    /// Whether the response stated any length, the total or just this
    /// body's. Chunked delivery with no length at all is the live shape.
    finite: bool,
    stream: Option<StreamState>,
    /// Size of the latest ranged request, which the next one doubles when
    /// it carries on from where this one ended.
    window: u64,
    cancel: CancelToken,
}

/// A response with at most this much left on the wire is read to its end
/// before the next request, rather than dropped: dropping a response part
/// way closes its connection, and the next request would pay a new one and
/// a TLS handshake to save a few kilobytes. A read this far ahead of the
/// open response reads on to it for the same reason.
const REUSE_BYTES: u64 = 128 * 1024;

/// How long reading a response to its end may take before the connection
/// is dropped after all. A server that has stopped sending is not worth
/// waiting on for the sake of reusing its connection.
const REUSE_WAIT: Duration = Duration::from_millis(500);

struct StreamState {
    response: reqwest::Response,
    pos: u64,
    /// Exclusive end of the current ranged chunk; `None` for an unbounded
    /// sequential body.
    end: Option<u64>,
    /// The chunk being served and how far into it the reads have got. A
    /// body yields whole chunks rather than filling a caller's buffer, so
    /// the remainder is held here between reads.
    chunk: Option<(Bytes, usize)>,
}

impl StreamState {
    fn new(response: reqwest::Response, pos: u64, end: Option<u64>) -> Self {
        Self {
            response,
            pos,
            end,
            chunk: None,
        }
    }

    /// Copy out of the held chunk; `0` once it is spent.
    fn serve(&mut self, buf: &mut [u8]) -> usize {
        let Some((bytes, taken)) = self.chunk.as_mut() else {
            return 0;
        };
        let remaining = &bytes[*taken..];
        let n = remaining.len().min(buf.len());
        buf[..n].copy_from_slice(&remaining[..n]);
        *taken += n;
        if *taken >= bytes.len() {
            self.chunk = None;
        }
        self.pos += n as u64;
        n
    }

    /// One read's worth of body, `0` at the end of it.
    fn read(&mut self, cancel: &CancelToken, buf: &mut [u8]) -> Result<usize, IoError> {
        // Guarded here as well as at the trait boundary: this is the loop
        // that reads a zero from `serve` as a spent chunk, and it should
        // not depend on every caller having checked first.
        if buf.is_empty() {
            return Ok(0);
        }
        loop {
            let n = self.serve(buf);
            if n > 0 {
                return Ok(n);
            }
            let next = awaiting(cancel, IoErrorKind::Read, "read cancelled", async {
                self.response
                    .chunk()
                    .await
                    .map_err(|e| IoError::from_chain(IoErrorKind::Read, &e))
            })?;
            match next {
                Some(bytes) if !bytes.is_empty() => self.chunk = Some((bytes, 0)),
                // An empty chunk is not the end of the body.
                Some(_) => {}
                None => return Ok(0),
            }
        }
    }
}

impl std::fmt::Debug for HttpSource {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("HttpSource")
            .field("url", &self.url.as_str())
            .field("len", &self.len)
            .field("ranges", &self.ranges)
            .finish_non_exhaustive()
    }
}

impl HttpSource {
    pub fn open(
        url: &str,
        limits: IoLimits,
        gate: Arc<dyn AddressGate>,
        cancel: CancelToken,
    ) -> Result<Self, IoError> {
        if url.len() > limits.max_url_len {
            return Err(IoError::new(IoErrorKind::Cap, "URL length cap exceeded"));
        }
        let origin =
            Url::parse(url).map_err(|e| IoError::new(IoErrorKind::Url, format!("{url}: {e}")))?;

        let mut clients = PinnedClients::default();
        let window = limits.jump_bytes.clamp(1, limits.chunk_bytes.max(1));
        let probe_end = window - 1;
        let (current, response) = awaiting(
            &cancel,
            IoErrorKind::Connect,
            "open cancelled",
            follow(
                &mut clients,
                &origin,
                &limits,
                gate.as_ref(),
                Some((0, probe_end)),
                IoErrorKind::Connect,
            ),
        )?;

        let status = response.status().as_u16();
        if !response.status().is_success() {
            return Err(IoError {
                kind: IoErrorKind::Http,
                status: Some(status),
                detail: format!("GET {current}"),
            });
        }

        if status == 206 {
            require_part_at(&response, 0, &current, IoErrorKind::Http)?;
            // On a 206 the total can only come from Content-Range: a
            // range-capping proxy makes Content-Length the part, not
            // the whole. A 206 that states no total still paces as
            // on-demand, it just reports an unknown size.
            let len = content_range_total(&response);
            let finite = len.is_some() || response.content_length().is_some();
            let end = part_end(&response, 0).map(|end| end.min(window));
            return Ok(Self {
                origin,
                url: current,
                clients,
                gate,
                limits,
                len,
                ranges: true,
                rangeable: true,
                finite,
                stream: Some(StreamState::new(response, 0, end)),
                window,
                cancel,
            });
        }

        // No range support: a 200 answers with the whole entity from
        // byte 0, so this response is the sequential stream and the
        // open is done on the one connection it already holds.
        let len = response.content_length();
        // A server can decline this range request and still honour
        // ranges generally; the advertised header is the second arm.
        let advertises_ranges = response
            .headers()
            .get("accept-ranges")
            .and_then(|v| v.to_str().ok())
            .is_some_and(|v| v.trim().eq_ignore_ascii_case("bytes"));
        Ok(Self {
            origin,
            url: current,
            clients,
            gate,
            limits,
            len,
            ranges: false,
            rangeable: advertises_ranges,
            finite: len.is_some(),
            stream: Some(StreamState::new(response, 0, None)),
            window,
            cancel,
        })
    }

    /// Where the latest redirect walk landed. Reads do not start here;
    /// each one walks from the URL the caller opened.
    pub fn final_url(&self) -> &Url {
        &self.url
    }

    /// Whether this source reads as on-demand rather than a live edge:
    /// finite and rangeable. A known total is not required: demanding one
    /// would misclassify servers that serve ranges without stating a total.
    pub fn is_seekable(&self) -> bool {
        self.finite && self.rangeable
    }

    /// Surrender the open body so a live lane can adopt it instead of
    /// connecting again. `Err(self)` when there is nothing to hand over:
    /// a ranged source reads by request rather than off one body, and a
    /// source a read has moved off the start holds bytes that would have
    /// to be handed over with it.
    pub(crate) fn into_streaming_body(mut self) -> Result<(reqwest::Response, Url), Box<Self>> {
        let adoptable = !self.ranges
            && self
                .stream
                .as_ref()
                .is_some_and(|s| s.pos == 0 && s.chunk.is_none());
        if !adoptable {
            return Err(Box::new(self));
        }
        let stream = self.stream.take().expect("adoptable implies a stream");
        Ok((stream.response, self.url))
    }

    /// One GET walked from the URL the caller opened, every hop vetted as
    /// the open's were.
    fn request(&mut self, range: Option<(u64, u64)>) -> Result<reqwest::Response, IoError> {
        let (landed, response) = awaiting(
            &self.cancel,
            IoErrorKind::Read,
            "read cancelled",
            follow(
                &mut self.clients,
                &self.origin,
                &self.limits,
                self.gate.as_ref(),
                range,
                IoErrorKind::Read,
            ),
        )?;
        self.url = landed;
        Ok(response)
    }

    /// `want` is the size of the read that asked, which a request after a
    /// jump is sized to when it wants more than the floor.
    fn reopen_at(&mut self, offset: u64, want: usize) -> Result<(), IoError> {
        let carries_on = self.stream.as_ref().is_some_and(|s| s.pos == offset);
        self.retire_stream();
        if self.ranges {
            let chunk = self.limits.chunk_bytes.max(1);
            let floor = (want as u64).max(self.limits.jump_bytes).clamp(1, chunk);
            self.window = if carries_on {
                self.window.saturating_mul(2).clamp(floor, chunk)
            } else {
                floor
            };
            let last = offset.saturating_add(self.window - 1);
            let response = self.request(Some((offset, last)))?;
            let status = response.status().as_u16();
            // With no total stated, a range past the end is how the end of
            // the file shows itself: no stream, and the read returns 0.
            if status == 416 && self.len.is_none() {
                return Ok(());
            }
            if status != 206 {
                return Err(IoError {
                    kind: IoErrorKind::Read,
                    status: Some(status),
                    detail: format!("ranged GET at {offset} for {}", self.url),
                });
            }
            require_part_at(&response, offset, &self.url, IoErrorKind::Read)?;
            let end = part_end(&response, offset);
            self.stream = Some(StreamState::new(response, offset, end));
            return Ok(());
        }

        // Sequential fallback: restart and discard forward to the offset.
        let response = self.request(None)?;
        if !response.status().is_success() {
            return Err(IoError {
                kind: IoErrorKind::Read,
                status: Some(response.status().as_u16()),
                detail: format!("GET {}", self.url),
            });
        }
        let mut stream = StreamState::new(response, 0, None);
        discard_until(&mut stream, &self.cancel, offset)?;
        self.stream = Some(stream);
        Ok(())
    }

    /// Let go of the open response, reading it to its end first when little
    /// of it is left, so its connection serves the next request.
    fn retire_stream(&mut self) {
        let Some(mut stream) = self.stream.take() else {
            return;
        };
        let Some(end) = stream.end.filter(|_| self.ranges) else {
            return;
        };
        let held = stream
            .chunk
            .as_ref()
            .map_or(0, |(bytes, taken)| (bytes.len() - taken) as u64);
        if end.saturating_sub(stream.pos + held) > REUSE_BYTES {
            return;
        }
        let cancel = &self.cancel;
        runtime().block_on(async {
            let drain = async { while let Ok(Some(_)) = stream.response.chunk().await {} };
            tokio::select! {
                _ = cancel.cancelled() => {}
                _ = tokio::time::timeout(REUSE_WAIT, drain) => {}
            }
        });
    }
}

impl ByteSource for HttpSource {
    fn size(&mut self) -> Result<Option<u64>, SourceError> {
        Ok(self.len)
    }

    fn read_at(&mut self, offset: u64, buf: &mut [u8]) -> Result<usize, SourceError> {
        // `serve` reports a zero-byte copy as a spent chunk, so without this
        // the loop below would replace the chunk it is still holding, lose
        // its unread remainder and go on pulling until the body ends.
        if buf.is_empty() {
            return Ok(0);
        }
        if let Some(len) = self.len
            && offset >= len
        {
            return Ok(0);
        }
        let cancel = self.cancel.clone();
        // Three passes at most: a positioned stream that has reached its
        // chunk boundary reopens once and reads again, and a read or a
        // skip that fails on a ranged source is answered once with a fresh
        // request.
        let mut reopened_after_failure = false;
        for _ in 0..3 {
            let usable = match &self.stream {
                Some(s) if s.pos == offset => s.end.is_none_or(|end| offset < end),
                Some(s) if !self.ranges => offset >= s.pos,
                Some(s) => {
                    offset > s.pos
                        && offset - s.pos <= REUSE_BYTES
                        && s.end.is_some_and(|end| offset < end)
                }
                None => false,
            };
            if !usable {
                self.reopen_at(offset, buf.len())?;
            } else if let Some(stream) = &mut self.stream
                && offset > stream.pos
                && let Err(e) = discard_until(stream, &cancel, offset)
            {
                self.stream = None;
                // A skip reads on through a response that can die like any
                // other; asking again from the skip's target costs what the
                // jump would have cost without the skip.
                if self.ranges && !reopened_after_failure && !cancel.is_cancelled() {
                    reopened_after_failure = true;
                    diag_log!("skip to byte {offset} failed ({e}); reopening there");
                    continue;
                }
                return Err(e.into());
            }

            // No stream after a reopen is a 416 past the end of a file whose
            // total was never stated.
            let Some(stream) = self.stream.as_mut() else {
                return Ok(0);
            };
            // A failed read retires the response with it. Left installed,
            // it would pass the check above, and a later read at the same
            // position would poll a `chunk()` future that was dropped
            // mid-poll. The latching cancel token would also prevent that,
            // but this path should not depend on it.
            let n = match stream.read(&cancel, buf) {
                Ok(n) => n,
                Err(e) => {
                    self.stream = None;
                    // A connection is expendable where the server serves
                    // ranges: one that died under a paused session, or was
                    // closed by a server tired of a reader that had stopped,
                    // is replaced by a request for the same byte. A failed
                    // read copied nothing out, so `offset` is still where
                    // the caller is. Once per call, so a dead link costs
                    // one more connect and read timeout and then surfaces.
                    if self.ranges && !reopened_after_failure && !cancel.is_cancelled() {
                        reopened_after_failure = true;
                        diag_log!("read failed at byte {offset} ({e}); reopening there");
                        continue;
                    }
                    return Err(e.into());
                }
            };
            if n > 0 {
                return Ok(n);
            }
            // Chunk exhausted at the boundary: reopen, keeping the spent
            // stream so the next request knows it carries on. Only a known
            // total says the boundary is the end of the file; with none
            // stated, the next request finds out.
            let pos = stream.pos;
            if stream.end == Some(pos) && self.len.is_none_or(|len| pos < len) {
                continue;
            }
            return Ok(0);
        }
        Ok(0)
    }
}

/// Drive one network operation on the shared runtime, racing the session's
/// cancel token. The live source gets that race from the `select!` around
/// its own open; this lane stays synchronous all the way down to the demux
/// thread, so the race belongs at each call instead.
pub(crate) fn awaiting<T>(
    cancel: &CancelToken,
    kind: IoErrorKind,
    detail: &'static str,
    work: impl Future<Output = Result<T, IoError>>,
) -> Result<T, IoError> {
    runtime().block_on(async {
        tokio::select! {
            _ = cancel.cancelled() => Err(IoError::new(kind, detail)),
            outcome = work => outcome,
        }
    })
}

/// The pinned client for each host a source or playlist fetcher has been
/// sent to. A ranged source asks again every chunk, and every request
/// walks from the URL the caller opened: without these a redirected source
/// would pay a resolve and a handshake per hop per chunk. A client held
/// here only ever connects to the addresses that were vetted when it was
/// built.
#[derive(Default)]
pub(crate) struct PinnedClients {
    by_host: Vec<(String, reqwest::Client)>,
    /// The host most recently handed a client since `clear_last`: where a
    /// failure happened, whichever hop of a redirect walk it was on.
    last: Option<String>,
}

impl PinnedClients {
    async fn for_url(
        &mut self,
        url: &Url,
        limits: &IoLimits,
        gate: &dyn AddressGate,
    ) -> Result<reqwest::Client, IoError> {
        // The screen that needs no network runs for a known host too: the
        // scheme belongs to the URL, not to the host.
        vet_target(url, gate)?;
        let host = url.host_str().unwrap_or_default();
        self.last = Some(host.to_string());
        if let Some((_, client)) = self.by_host.iter().find(|(known, _)| known == host) {
            return Ok(client.clone());
        }
        let client = build_pinned_client(url, limits, gate).await?;
        // One walk's worth of hosts is the most a source uses at once.
        if self.by_host.len() > limits.max_redirects as usize {
            self.by_host.remove(0);
        }
        self.by_host.push((host.to_string(), client.clone()));
        Ok(client)
    }

    pub(crate) fn clear_last(&mut self) {
        self.last = None;
    }

    /// Drop the client of the host last handed one, so that host is
    /// resolved and vetted afresh next time.
    pub(crate) fn forget_last(&mut self) {
        if let Some(host) = self.last.take() {
            self.by_host.retain(|(known, _)| *known != host);
        }
    }
}

/// Send one GET to `origin` and follow what it answers, one hop at a time
/// so that each target is vetted and pinned before anything is sent to it.
/// Returns the first response that is not a redirect, whatever its status,
/// with the URL that gave it.
pub(crate) async fn follow(
    clients: &mut PinnedClients,
    origin: &Url,
    limits: &IoLimits,
    gate: &dyn AddressGate,
    range: Option<(u64, u64)>,
    kind: IoErrorKind,
) -> Result<(Url, reqwest::Response), IoError> {
    let mut current = origin.clone();
    for _hop in 0..=limits.max_redirects {
        let client = clients.for_url(&current, limits, gate).await?;
        let response = send_get(&client, &current, range, kind).await?;

        let status = response.status().as_u16();
        if !REDIRECT_STATUSES.contains(&status) {
            return Ok((current, response));
        }
        let location = response
            .headers()
            .get("location")
            .and_then(|v| v.to_str().ok())
            .ok_or_else(|| {
                IoError::new(IoErrorKind::Redirect, format!("{status} without Location"))
            })?;
        current = current
            .join(location)
            .map_err(|e| IoError::new(IoErrorKind::Redirect, format!("{location}: {e}")))?;
    }
    Err(IoError::new(
        IoErrorKind::Redirect,
        format!("redirect cap ({}) exceeded", limits.max_redirects),
    ))
}

/// One GET. No request carries a total timeout: a body is read at the
/// pace its consumer plays it, and not at all while the session is
/// paused, so the length of an exchange says nothing about the link. The
/// client's read timeout bounds each wait for bytes instead, the wait for
/// the response head included, and the session's cancel token ends any
/// of it early.
async fn send_get(
    client: &reqwest::Client,
    url: &Url,
    range: Option<(u64, u64)>,
    kind: IoErrorKind,
) -> Result<reqwest::Response, IoError> {
    let mut request = client.get(url.clone());
    if let Some((first, last)) = range {
        request = request.header("Range", format!("bytes={first}-{last}"));
    }
    request
        .send()
        .await
        .map_err(|e| IoError::from_chain(kind, &e))
}

async fn build_pinned_client(
    url: &Url,
    limits: &IoLimits,
    gate: &dyn AddressGate,
) -> Result<reqwest::Client, IoError> {
    let mut builder = reqwest::Client::builder()
        .redirect(reqwest::redirect::Policy::none())
        .connect_timeout(limits.connect_timeout)
        // Per read rather than per request: it measures a wait for bytes,
        // so a link that has gone quiet surfaces as a typed error while a
        // consumer that has stopped reading costs nothing.
        .read_timeout(limits.request_timeout)
        .no_proxy()
        .user_agent("basis-media/0.1");

    if let Some((domain, addrs)) = vet_url_async(url, gate).await? {
        builder = builder.resolve_to_addrs(&domain, &addrs);
    }

    builder
        .build()
        .map_err(|e| IoError::from_chain(IoErrorKind::Connect, &e))
}

fn content_range_total(response: &reqwest::Response) -> Option<u64> {
    let value = response.headers().get("content-range")?.to_str().ok()?;
    let total = value.rsplit('/').next()?;
    total.parse().ok()
}

/// Refuse a 206 whose part does not start at `offset`. The status does not
/// say where the part starts: a proxy that rewrites ranges answers 206
/// too, and so does a `multipart/byteranges` body, and either would be
/// served as the bytes at `offset`.
fn require_part_at(
    response: &reqwest::Response,
    offset: u64,
    url: &Url,
    kind: IoErrorKind,
) -> Result<(), IoError> {
    if content_range_first(response) == Some(offset) {
        return Ok(());
    }
    let stated = response
        .headers()
        .get("content-range")
        .map(|v| String::from_utf8_lossy(v.as_bytes()).into_owned());
    Err(IoError::new(
        kind,
        format!("ranged GET at {offset} for {url} answered with Content-Range {stated:?}"),
    ))
}

/// The first byte a 206 says its body starts at, out of
/// `bytes <first>-<last>/<total>`. `None` for anything that is not that
/// shape with coherent bounds; the total may be `*`.
fn content_range_first(response: &reqwest::Response) -> Option<u64> {
    let value = response.headers().get("content-range")?.to_str().ok()?;
    parse_content_range(value).map(|(first, _)| first)
}

/// Where a 206's part ends, exclusive. `Content-Length` is optional on a
/// chunked 206, but `Content-Range` states the part either way.
fn part_end(response: &reqwest::Response, offset: u64) -> Option<u64> {
    let stated = response
        .headers()
        .get("content-range")
        .and_then(|v| v.to_str().ok())
        .and_then(parse_content_range)
        .and_then(|(_, last)| last.checked_add(1));
    stated.or_else(|| {
        response
            .content_length()
            .and_then(|n| offset.checked_add(n))
    })
}

/// `(first, last)` out of `bytes <first>-<last>/<total>`, inclusive. A last
/// byte with no offset after it has no exclusive end, so it is refused.
fn parse_content_range(value: &str) -> Option<(u64, u64)> {
    let (unit, spec) = value.trim().split_once(' ')?;
    if !unit.eq_ignore_ascii_case("bytes") {
        return None;
    }
    let (range, total) = spec.split_once('/')?;
    let (first, last) = range.split_once('-')?;
    let number = |s: &str| -> Option<u64> {
        // `parse` alone takes a leading `+`, which the grammar does not.
        s.bytes()
            .all(|b| b.is_ascii_digit())
            .then(|| s.parse().ok())?
    };
    let (first, last) = (number(first)?, number(last)?);
    let coherent = match total {
        "*" => first <= last && last < u64::MAX,
        total => first <= last && last < number(total)?,
    };
    coherent.then_some((first, last))
}

fn discard_until(
    stream: &mut StreamState,
    cancel: &CancelToken,
    offset: u64,
) -> Result<(), IoError> {
    let mut scratch = [0u8; 64 * 1024];
    while stream.pos < offset {
        let want = scratch.len().min((offset - stream.pos) as usize);
        let n = stream.read(cancel, &mut scratch[..want])?;
        if n == 0 {
            return Err(IoError::new(
                IoErrorKind::Read,
                format!("source ended at {} before offset {offset}", stream.pos),
            ));
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    fn first(value: &str) -> Option<u64> {
        super::parse_content_range(value).map(|(first, _)| first)
    }

    #[test]
    fn content_range_states_its_first_byte_or_nothing() {
        assert_eq!(first("bytes 0-99/200"), Some(0));
        assert_eq!(first(" BYTES 4096-8191/* "), Some(4096));
        for refused in [
            "",
            "bytes",
            "bytes */200",
            "bytes 5-4/200",
            "bytes 0-200/200",
            "bytes +5-9/200",
            "bytes -9/200",
            "items 0-99/200",
            "not-a-range/123",
            "bytes 0-99",
            "bytes 0-18446744073709551615/*",
        ] {
            assert_eq!(first(refused), None, "{refused:?}");
        }
    }
}
