//! Network and I/O layer: every socket the engine opens, opened
//! here. Byte sources implement the demux layer's [`ByteSource`] seam.
//!
//! SSRF policy is *bind what you resolve*: resolve once, vet every
//! address, connect to the vetted IPs with Host/SNI carried separately,
//! and re-vet on every redirect. The address gate lives only in this
//! crate; the managed layer holds policy (consent, allowlists), not
//! mechanism.

#![forbid(unsafe_code)]

mod cancel;
mod fetch;
mod file;
mod gate;
mod http;
mod live;
mod resolve;
mod runtime;

pub use cancel::CancelToken;
pub use fetch::ResourceFetcher;
pub use file::FileSource;
pub use gate::{
    AddressGate, AllowAllGate, PublicAddressGate, resolve_vetted, resolve_vetted_all,
    resolve_vetted_async,
};
pub use http::{HttpSource, vet_url_async};
pub use live::HttpLiveSource;
pub use runtime::io_runtime_handle;

use std::fmt;
use std::time::Duration;

/// Edge caps drawn from the session budget.
#[derive(Debug, Clone)]
pub struct IoLimits {
    pub max_redirects: u32,
    pub connect_timeout: Duration,
    /// Ceiling on one wait for bytes from a media source: the response
    /// head, then each read of the body. It is a stall detector on the
    /// link, not a bound on the whole exchange, because a body is read at
    /// playback pace and not at all while paused. A signalling request,
    /// whose body is small and read at once, takes it as a total instead.
    /// Prompt teardown comes from elsewhere: every request and read races
    /// the session's [`CancelToken`].
    pub request_timeout: Duration,
    /// Largest ranged request. A request that carries on where the last one
    /// ended doubles in size up to this, so playback reads long ranges and
    /// a discarded stream wastes at most this much.
    pub chunk_bytes: u64,
    /// Smallest ranged request: the open's, and the first after a jump,
    /// unless the read wants more. A reader that jumps (a fragment walk, a
    /// bisection seek) reads a few kilobytes where it lands.
    pub jump_bytes: u64,
    /// Per-read stall detector on sequential live sources: this long with
    /// no bytes at all is a dead link, surfaced as a typed error for the
    /// resilience path.
    pub read_stall: Duration,
    pub max_url_len: usize,
    /// Ceiling on a signalling response body buffered whole in memory.
    /// What this bounds is a negotiation document (SDP), not media, and
    /// the host serving it is whichever one a session's URL names.
    pub max_signalling_bytes: u64,
}

impl Default for IoLimits {
    fn default() -> Self {
        Self {
            max_redirects: 5,
            connect_timeout: Duration::from_secs(8),
            request_timeout: Duration::from_secs(20),
            chunk_bytes: 4 * 1024 * 1024,
            jump_bytes: 64 * 1024,
            read_stall: Duration::from_secs(10),
            max_url_len: 4096,
            max_signalling_bytes: 256 * 1024,
        }
    }
}

/// Structured error surface: a typo, a 404, a TLS failure, a blocked
/// address and a cap hit are distinguishable in a field report.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum IoErrorKind {
    /// Malformed or unsupported URL.
    Url,
    /// The address gate refused every resolved address.
    Blocked,
    /// DNS resolution failed.
    Resolve,
    /// TCP/TLS connect failed or timed out.
    Connect,
    /// The server answered with a non-success status.
    Http,
    /// Redirect handling failed (limit, missing/invalid Location).
    Redirect,
    /// A mid-stream read failed or timed out.
    Read,
    /// A cap from [`IoLimits`] tripped.
    Cap,
    /// Local file I/O failed.
    File,
}

#[derive(Debug)]
pub struct IoError {
    pub kind: IoErrorKind,
    /// HTTP status where one applies.
    pub status: Option<u16>,
    pub detail: String,
}

impl IoError {
    fn new(kind: IoErrorKind, detail: impl Into<String>) -> Self {
        Self {
            kind,
            status: None,
            detail: detail.into(),
        }
    }

    /// The same, with `e`'s `source()` chain flattened into the detail.
    fn from_chain(kind: IoErrorKind, e: &dyn std::error::Error) -> Self {
        Self::new(kind, error_chain(e))
    }
}

/// Flatten an error and its `source()` chain into one string.
///
/// reqwest's `Display` names only its outermost layer (that a request
/// could not be sent, and to where) and drops the resolve, connect or
/// TLS error underneath, which is the part that says what went wrong.
/// Public for the transports that run their own HTTP requests over this
/// crate's discipline (WHEP signalling).
pub fn error_chain(e: &dyn std::error::Error) -> String {
    let mut detail = e.to_string();
    let mut cause = e.source();
    while let Some(e) = cause {
        detail.push_str(": ");
        detail.push_str(&e.to_string());
        cause = e.source();
    }
    detail
}

impl fmt::Display for IoError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self.status {
            Some(status) => write!(f, "{:?} ({status}): {}", self.kind, self.detail),
            None => write!(f, "{:?}: {}", self.kind, self.detail),
        }
    }
}

impl std::error::Error for IoError {}
