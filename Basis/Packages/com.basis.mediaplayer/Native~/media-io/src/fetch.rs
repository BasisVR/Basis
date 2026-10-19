//! Whole-resource fetcher for playlist-driven lanes (HLS). Every fetch is
//! either a cancellable GET or a local file read for fixture playback. A
//! GET goes out on a client pinned to its host's vetted addresses, and a
//! host's client is kept until a fetch to that host fails in transport: a
//! live playlist's reloads, segments and parts reuse its open connections.
//! Each hop of a redirect is vetted and pinned the same way.
//!
//! Which of those two a fetcher can do is fixed when it is built, from
//! where the session's playlist came from, and never widens afterwards. A
//! playlist fetched over the network gets [`ResourceFetcher::remote`] and
//! has no filesystem arm at all, so no URI it names can reach the disk
//! however the URI is spelled; a playlist opened from disk gets
//! [`ResourceFetcher::local`] and keeps both, since reaching the network
//! from there is what the address gate already covers.

use std::io::Read;
use std::path::{Component, Path, PathBuf};
use std::sync::Arc;
use std::time::Duration;

use media_demux::SourceError;
use media_hls::SegmentFetcher;
use url::Url;

use crate::http::{PinnedClients, awaiting, follow};
use crate::{AddressGate, CancelToken, IoError, IoErrorKind, IoLimits};

/// The directory a disk-origin playlist's reads are confined to, kept in
/// both forms because the two screens need different ones: the path as
/// the caller gave it, which the resolved resource is a prefix-extension
/// of, and the canonicalised one, which symlinks resolve into.
struct LocalRoot {
    given: PathBuf,
    canonical: PathBuf,
}

pub struct ResourceFetcher {
    limits: IoLimits,
    gate: Arc<dyn AddressGate>,
    cancel: CancelToken,
    /// The directory local reads resolve within. `None` is a
    /// network-origin playlist, which has no local arm.
    root: Option<LocalRoot>,
    /// One pinned client per host, kept across fetches.
    clients: PinnedClients,
}

/// The file `url` names, confined to `root`, or a refusal.
///
/// Two screens. The lexical one compares components against the root as
/// given and touches the filesystem not at all, so nothing can race it;
/// it is what catches an absolute path, a drive-relative one, and a walk
/// back out through `..`. The canonicalised one then resolves symlinks
/// and re-checks, which catches a link planted inside the root that
/// points outside it.
///
/// What is opened is the canonicalised path, so defeating the second
/// screen means replacing a directory component of an already-resolved
/// path between the check and the open. Closing that window needs
/// openat-style handle-relative resolution, which is neither on stable
/// Rust for Windows (`windows_by_handle`, rust-lang/rust#63010) nor
/// reachable from a crate that forbids unsafe. The residual race is
/// accepted: without the second screen the same attacker succeeds with
/// no race to win.
fn confine(root: &LocalRoot, url: &str) -> Result<PathBuf, SourceError> {
    let path = Path::new(url);
    // Strip the root and judge only what the playlist contributed: the
    // root itself is the caller's and may legitimately carry `..` or any
    // other shape. An absolute or drive-relative resource fails to strip
    // at all, which is the refusal it deserves.
    let Ok(contributed) = path.strip_prefix(&root.given) else {
        return Err(format!("playlist resource outside the playlist's directory: {url}").into());
    };
    if contributed
        .components()
        .any(|c| !matches!(c, Component::Normal(_) | Component::CurDir))
    {
        return Err(format!("playlist resource walks out of its directory: {url}").into());
    }
    let canonical = std::fs::canonicalize(path).map_err(|e| format!("{url}: {e}"))?;
    if !canonical.starts_with(&root.canonical) {
        return Err(
            format!("playlist resource resolves outside the playlist's directory: {url}").into(),
        );
    }
    Ok(canonical)
}

impl ResourceFetcher {
    /// A fetcher for a playlist that came off the network: http(s) only,
    /// every URL address-vetted, anything else refused.
    pub fn remote(limits: IoLimits, gate: Arc<dyn AddressGate>, cancel: CancelToken) -> Self {
        Self {
            limits,
            gate,
            cancel,
            root: None,
            clients: PinnedClients::default(),
        }
    }

    /// A fetcher for a playlist opened from disk beside `root` (the
    /// fixture lane). http(s) URIs still fetch and are still vetted.
    /// Fails when `root` cannot be canonicalised, since a root that does
    /// not resolve is not one reads can be judged against.
    pub fn local(
        root: &Path,
        limits: IoLimits,
        gate: Arc<dyn AddressGate>,
        cancel: CancelToken,
    ) -> Result<Self, IoError> {
        let canonical = std::fs::canonicalize(root)
            .map_err(|e| IoError::new(IoErrorKind::File, format!("{}: {e}", root.display())))?;
        Ok(Self {
            limits,
            gate,
            cancel,
            root: Some(LocalRoot {
                given: root.to_path_buf(),
                canonical,
            }),
            clients: PinnedClients::default(),
        })
    }
}

impl ResourceFetcher {
    fn fetch_remote(&mut self, url: &str, cap: u64) -> Result<Vec<u8>, IoError> {
        let origin =
            Url::parse(url).map_err(|e| IoError::new(IoErrorKind::Url, format!("{url}: {e}")))?;
        let Self {
            limits,
            gate,
            cancel,
            clients,
            ..
        } = self;
        clients.clear_last();
        let fetched = awaiting(cancel, IoErrorKind::Read, "fetch cancelled", async {
            let (at, mut response) = follow(
                &mut *clients,
                &origin,
                limits,
                gate.as_ref(),
                None,
                IoErrorKind::Connect,
            )
            .await?;
            let status = response.status();
            if !status.is_success() {
                return Err(IoError {
                    kind: IoErrorKind::Http,
                    status: Some(status.as_u16()),
                    detail: format!("GET {at}"),
                });
            }
            if let Some(stated) = response.content_length()
                && stated > cap
            {
                return Err(IoError::new(
                    IoErrorKind::Cap,
                    format!("resource exceeds the {cap}-byte cap: {url}"),
                ));
            }
            let mut bytes = Vec::new();
            while let Some(chunk) = response
                .chunk()
                .await
                .map_err(|e| IoError::from_chain(IoErrorKind::Read, &e))?
            {
                if bytes.len() as u64 + chunk.len() as u64 > cap {
                    return Err(IoError::new(
                        IoErrorKind::Cap,
                        format!("resource exceeds the {cap}-byte cap: {url}"),
                    ));
                }
                bytes.extend_from_slice(&chunk);
            }
            Ok(bytes)
        });
        // A host whose pinned addresses stop answering may have moved; the
        // next fetch to it resolves and vets it afresh.
        if let Err(e) = &fetched
            && matches!(e.kind, IoErrorKind::Connect | IoErrorKind::Read)
            && !cancel.is_cancelled()
        {
            clients.forget_last();
        }
        fetched
    }
}

impl SegmentFetcher for ResourceFetcher {
    fn fetch(&mut self, url: &str, cap: u64) -> Result<Vec<u8>, SourceError> {
        if self.cancel.is_cancelled() {
            return Err("fetch cancelled".into());
        }
        if url.starts_with("http://") || url.starts_with("https://") {
            self.fetch_remote(url, cap)
                .map_err(|e| Box::new(e) as SourceError)
        } else {
            let Some(root) = self.root.as_ref() else {
                return Err(format!(
                    "a playlist fetched over the network may not name a local resource: {url}"
                )
                .into());
            };
            let path = confine(root, url)?;
            // Length off the handle, not the name: a second resolution
            // leaves the target free to grow or be swapped between the
            // two calls, so the read is bounded whatever the size said.
            let file = std::fs::File::open(&path).map_err(|e| format!("{url}: {e}"))?;
            let stated = file.metadata().map_err(|e| format!("{url}: {e}"))?.len();
            if stated > cap {
                return Err(format!("resource exceeds the {cap}-byte cap: {url}").into());
            }
            let mut bytes = Vec::new();
            file.take(cap.saturating_add(1))
                .read_to_end(&mut bytes)
                .map_err(|e| format!("{url}: {e}"))?;
            if bytes.len() as u64 > cap {
                return Err(format!("resource exceeds the {cap}-byte cap: {url}").into());
            }
            Ok(bytes)
        }
    }

    fn wait(&mut self, duration: Duration) {
        // Sliced so teardown never waits out a full refresh interval.
        let mut remaining = duration;
        let slice = Duration::from_millis(50);
        while remaining > Duration::ZERO && !self.cancel.is_cancelled() {
            let step = remaining.min(slice);
            std::thread::sleep(step);
            remaining = remaining.saturating_sub(step);
        }
    }
}
