//! Sequential live HTTP(S) source: the async I/O domain under the
//! `ByteSource` seam.
//!
//! One streaming GET; an async reader task on the shared runtime pulls
//! chunks under a per-read stall timeout and fills a bounded channel the
//! sync media path drains. Backpressure is the channel filling and TCP
//! doing the rest. Connect, redirects and every read race the session's
//! [`CancelToken`], so teardown never waits out a network timeout.
//! SSRF handling is the ranged source's (resolve once, vet all, pin the
//! connection), re-run on every redirect hop.
//!
//! The first bytes served are kept in a head cache so the container sniff
//! and a demuxer restarting from offset 0 both read them; everything past
//! the cache is strictly sequential (a live stream has no backward seeks).

use std::sync::Arc;

use bytes::Bytes;
use media_demux::{ByteSource, SourceError};
use url::Url;

use crate::cancel::CancelToken;
use crate::http::{HttpSource, REDIRECT_STATUSES, vet_url_async};
use crate::runtime::runtime;
use crate::{AddressGate, IoError, IoErrorKind, IoLimits};

/// Bytes kept from the start of the stream for sniff re-reads.
const HEAD_CACHE: usize = 64 * 1024;
/// Reader-task channel depth, in chunks (reqwest chunks are TLS-record
/// sized, so this bounds a few MiB at worst).
const CHANNEL_CHUNKS: usize = 64;

pub struct HttpLiveSource {
    rx: tokio::sync::mpsc::Receiver<Result<Bytes, IoError>>,
    /// Current chunk and the offset already served from it.
    chunk: Option<(Bytes, usize)>,
    /// Absolute offset of the next unserved byte.
    pos: u64,
    head: Vec<u8>,
    cancel: CancelToken,
    url: Url,
    ended: bool,
}

impl std::fmt::Debug for HttpLiveSource {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("HttpLiveSource")
            .field("url", &self.url.as_str())
            .field("pos", &self.pos)
            .finish_non_exhaustive()
    }
}

impl HttpLiveSource {
    /// Open the stream. Blocks the calling (opener) thread but every await
    /// inside races `cancel`, so a close during connect returns promptly.
    pub fn open(
        url: &str,
        limits: IoLimits,
        gate: Arc<dyn AddressGate>,
        cancel: CancelToken,
    ) -> Result<Self, IoError> {
        if url.len() > limits.max_url_len {
            return Err(IoError::new(IoErrorKind::Cap, "URL length cap exceeded"));
        }
        let parsed =
            Url::parse(url).map_err(|e| IoError::new(IoErrorKind::Url, format!("{url}: {e}")))?;

        // Work off a child token: the session token cancels this source,
        // but dropping this source (a reconnect discarding the dead
        // transport) must not cancel the session.
        let cancel = cancel.child();
        let open_cancel = cancel.clone();
        let open_limits = limits.clone();
        let opened = runtime().block_on(async move {
            tokio::select! {
                _ = open_cancel.cancelled() => {
                    Err(IoError::new(IoErrorKind::Connect, "open cancelled"))
                }
                opened = open_streaming(parsed, &open_limits, gate.as_ref()) => opened,
            }
        });
        let (response, final_url) = match opened {
            Ok(opened) => opened,
            Err(e) => {
                // No source is constructed, so no drop retires the child
                // token, and its forwarder task would park on the shared
                // runtime for the rest of the session. A playlist lane
                // re-opens per segment, so a failing host accumulates
                // one per refresh.
                cancel.cancel();
                return Err(e);
            }
        };

        Ok(Self::from_response(
            response,
            final_url,
            cancel,
            limits.read_stall,
        ))
    }

    /// Adopt an on-demand source's open body rather than connecting again
    /// to ask a question its response has already answered. The probe
    /// that settles Auto liveness is a GET whose 200 *is* the live
    /// stream, and an origin serving one client at a time has nothing
    /// left to give for the same bytes.
    ///
    /// The source comes back unchanged when there is nothing to adopt: a
    /// ranged source reads by request rather than off one body, and a
    /// source a read has already moved off the start holds bytes that
    /// only it can serve back.
    pub fn adopt(
        source: HttpSource,
        limits: &IoLimits,
        cancel: CancelToken,
    ) -> Result<Self, Box<HttpSource>> {
        let (response, url) = source.into_streaming_body()?;
        Ok(Self::from_response(
            response,
            url,
            cancel.child(),
            limits.read_stall,
        ))
    }

    /// Wrap an open body. `cancel` is this source's own token, the one its
    /// `Drop` retires, so callers pass a child of the session's.
    fn from_response(
        response: reqwest::Response,
        url: Url,
        cancel: CancelToken,
        stall: std::time::Duration,
    ) -> Self {
        let (tx, rx) = tokio::sync::mpsc::channel(CHANNEL_CHUNKS);
        runtime().spawn(read_loop(response, tx, cancel.clone(), stall));
        Self {
            rx,
            chunk: None,
            pos: 0,
            head: Vec::new(),
            cancel,
            url,
            ended: false,
        }
    }

    /// The URL the source actually reads from (after redirects).
    pub fn final_url(&self) -> &Url {
        &self.url
    }

    fn serve(&mut self, buf: &mut [u8]) -> usize {
        let Some((bytes, taken)) = self.chunk.as_mut() else {
            return 0;
        };
        let remaining = &bytes[*taken..];
        let n = remaining.len().min(buf.len());
        buf[..n].copy_from_slice(&remaining[..n]);
        if self.head.len() < HEAD_CACHE {
            let keep = (HEAD_CACHE - self.head.len()).min(n);
            self.head.extend_from_slice(&remaining[..keep]);
        }
        *taken += n;
        if *taken >= bytes.len() {
            self.chunk = None;
        }
        self.pos += n as u64;
        n
    }
}

impl ByteSource for HttpLiveSource {
    fn size(&mut self) -> Result<Option<u64>, SourceError> {
        Ok(None)
    }

    fn read_at(&mut self, offset: u64, buf: &mut [u8]) -> Result<usize, SourceError> {
        if buf.is_empty() {
            return Ok(0);
        }
        // Re-reads of the streamed head (the container sniff, a demuxer
        // starting over at 0) come from the cache.
        if offset < self.pos {
            let head_len = self.head.len() as u64;
            if offset < head_len {
                let at = offset as usize;
                let n = buf.len().min(self.head.len() - at);
                buf[..n].copy_from_slice(&self.head[at..at + n]);
                return Ok(n);
            }
            return Err(Box::new(IoError::new(
                IoErrorKind::Read,
                format!(
                    "live source is sequential: cannot re-read {offset} (stream at {})",
                    self.pos
                ),
            )));
        }
        if offset > self.pos {
            return Err(Box::new(IoError::new(
                IoErrorKind::Read,
                format!(
                    "live source is sequential: cannot skip to {offset} (stream at {})",
                    self.pos
                ),
            )));
        }

        loop {
            let n = self.serve(buf);
            if n > 0 {
                return Ok(n);
            }
            if self.ended {
                return Ok(0);
            }
            match self.rx.blocking_recv() {
                Some(Ok(bytes)) => self.chunk = Some((bytes, 0)),
                Some(Err(e)) => {
                    self.ended = true;
                    return Err(Box::new(e));
                }
                None => {
                    self.ended = true;
                    return Ok(0);
                }
            }
        }
    }
}

impl Drop for HttpLiveSource {
    fn drop(&mut self) {
        self.cancel.cancel();
    }
}

async fn open_streaming(
    mut url: Url,
    limits: &IoLimits,
    gate: &dyn AddressGate,
) -> Result<(reqwest::Response, Url), IoError> {
    for _hop in 0..=limits.max_redirects {
        let mut builder = reqwest::Client::builder()
            .redirect(reqwest::redirect::Policy::none())
            .connect_timeout(limits.connect_timeout)
            .no_proxy()
            .user_agent("basis-media/0.1");
        if let Some((domain, addrs)) = vet_url_async(&url, gate).await? {
            builder = builder.resolve_to_addrs(&domain, &addrs);
        }
        let client = builder
            .build()
            .map_err(|e| IoError::from_chain(IoErrorKind::Connect, &e))?;

        let response = client
            .get(url.clone())
            .send()
            .await
            .map_err(|e| IoError::from_chain(IoErrorKind::Connect, &e))?;

        let status = response.status().as_u16();
        if REDIRECT_STATUSES.contains(&status) {
            let location = response
                .headers()
                .get("location")
                .and_then(|v| v.to_str().ok())
                .ok_or_else(|| {
                    IoError::new(IoErrorKind::Redirect, format!("{status} without Location"))
                })?;
            url = url
                .join(location)
                .map_err(|e| IoError::new(IoErrorKind::Redirect, format!("{location}: {e}")))?;
            continue;
        }
        if !response.status().is_success() {
            return Err(IoError {
                kind: IoErrorKind::Http,
                status: Some(status),
                detail: format!("GET {url}"),
            });
        }
        return Ok((response, url));
    }
    Err(IoError::new(
        IoErrorKind::Redirect,
        format!("redirect cap ({}) exceeded", limits.max_redirects),
    ))
}

async fn read_loop(
    mut response: reqwest::Response,
    tx: tokio::sync::mpsc::Sender<Result<Bytes, IoError>>,
    cancel: CancelToken,
    stall: std::time::Duration,
) {
    loop {
        let next = tokio::time::timeout(stall, response.chunk());
        let outcome = tokio::select! {
            _ = cancel.cancelled() => return,
            outcome = next => outcome,
        };
        match outcome {
            Err(_elapsed) => {
                let _ = tx
                    .send(Err(IoError::new(
                        IoErrorKind::Read,
                        format!("no bytes for {}ms (stall)", stall.as_millis()),
                    )))
                    .await;
                return;
            }
            Ok(Ok(Some(bytes))) => {
                if !bytes.is_empty() && tx.send(Ok(bytes)).await.is_err() {
                    return; // consumer gone
                }
            }
            // Clean end of body: closing the channel is EOS.
            Ok(Ok(None)) => return,
            Ok(Err(e)) => {
                let _ = tx
                    .send(Err(IoError::from_chain(IoErrorKind::Read, &e)))
                    .await;
                return;
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    struct BlockAll;
    impl AddressGate for BlockAll {
        fn permit(&self, _ip: std::net::IpAddr) -> bool {
            false
        }
    }

    /// A failing open still has a child token to retire; nothing else
    /// will, because the source that would have owned it was never
    /// built.
    #[test]
    fn a_failed_open_leaves_no_forwarder_behind() {
        // Tied together on purpose: a flat threshold beside a loop count
        // lets a leak on one open in five pass unnoticed.
        const OPENS: usize = 50;
        const TOLERATED: usize = OPENS / 5;

        let session = CancelToken::new();
        let before = runtime().metrics().num_alive_tasks();
        for _ in 0..OPENS {
            HttpLiveSource::open(
                "http://10.0.0.1/stream.ts",
                IoLimits::default(),
                Arc::new(BlockAll),
                session.clone(),
            )
            .map(|_| ())
            .expect_err("the gate refuses every address");
        }
        // Polled rather than read once: a cancelled forwarder still has to
        // be scheduled before it exits, the multi-threaded runtime's count
        // is documented as approximate, and sibling rows share the
        // process-wide runtime. The leak guarded against is permanent, so
        // a bounded wait cannot hide one; it only stops a scheduling delay
        // reading as one.
        let mut grew = usize::MAX;
        for _ in 0..20 {
            grew = runtime().metrics().num_alive_tasks().saturating_sub(before);
            if grew < TOLERATED {
                break;
            }
            std::thread::sleep(std::time::Duration::from_millis(50));
        }
        assert!(
            grew < TOLERATED,
            "{grew} forwarder tasks outlived their {OPENS} opens"
        );
    }
}
