//! The librist-backed receiver: librist owns the UDP sockets, ARQ, GRE
//! tunnel, jitter buffer and (Main Profile) PSK-AES, and hands recovered
//! MPEG-TS payload to a data callback. This buffers it and serves it as a
//! `ByteSource`, so the engine's TS lane consumes RIST the same way as the
//! live HTTP byte sources.

use std::ffi::{CString, c_char, c_int, c_void};
use std::net::SocketAddr;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Condvar, Mutex};
use std::time::Duration;

use media_demux::{ByteSource, SourceError};
use media_diag::diag_warn;

use crate::RistError;
use crate::ffi;

/// Recovered TS held between librist's output thread and the demux thread.
/// librist has already done ARQ recovery upstream, so an overrun here means
/// the ring is undersized, not a network loss; the write side drops oldest.
const RING_BYTES: usize = 1 << 20;

/// Bytes kept from the start of the stream so the container sniff can
/// re-read offset 0 (same contract as `HttpLiveSource`).
const HEAD_CACHE: usize = 64 * 1024;

/// How long a blocked read sleeps between cancel-probe checks.
const WAIT_SLICE: Duration = Duration::from_millis(100);

struct Ring {
    buf: Vec<u8>,
    head: usize,
    fill: usize,
}

struct Shared {
    ring: Mutex<Ring>,
    ready: Condvar,
}

impl Shared {
    fn write(&self, data: &[u8]) {
        // Reached from a librist C thread: recover from poisoning rather
        // than unwind into it. Each iteration below leaves head and fill
        // consistent, so the ring is usable after an unrelated panic.
        let mut ring = self.ring.lock().unwrap_or_else(|e| e.into_inner());
        let cap = ring.buf.len();
        for &byte in data {
            let at = ring.head;
            ring.buf[at] = byte;
            ring.head = (ring.head + 1) % cap;
            if ring.fill < cap {
                ring.fill += 1;
            }
        }
        drop(ring);
        self.ready.notify_one();
    }

    fn drain(&self, out: &mut [u8]) -> usize {
        let mut ring = self.ring.lock().unwrap_or_else(|e| e.into_inner());
        let cap = ring.buf.len();
        let n = out.len().min(ring.fill);
        let tail = (ring.head + cap - ring.fill) % cap;
        for (i, slot) in out[..n].iter_mut().enumerate() {
            *slot = ring.buf[(tail + i) % cap];
        }
        ring.fill -= n;
        n
    }
}

/// Panics absorbed by the fence in `on_data`. A count rather than a flag,
/// so the throttled warning can say how many datagrams were lost this way.
static PANICS: AtomicU64 = AtomicU64::new(0);

/// librist data callback. Runs on a librist output thread, so it must be
/// thread-safe and must not stall. The block is reference-counted and must
/// be released with `rist_receiver_data_block_free2`, which happens on
/// every path. The frame is librist's and cannot unwind, so a panic is
/// absorbed here.
extern "C" fn on_data(arg: *mut c_void, mut block: *mut ffi::RistDataBlock) -> c_int {
    let outcome = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        // Both pointers come from librist; the fence catches an unwind,
        // not a bad dereference, so each is null-checked.
        if arg.is_null() {
            return;
        }
        // SAFETY: `arg` is the raw Arc<Shared> handed to
        // rist_receiver_data_callback_set2; a strong count is held for it until
        // after rist_destroy has joined librist's threads, so it is live here,
        // and it is non-null by the check above.
        let shared = unsafe { &*(arg as *const Shared) };
        // SAFETY: librist hands a valid block whose payload/payload_len describe
        // the recovered datagram; both are only read within those bounds.
        unsafe {
            if !block.is_null() && !(*block).payload.is_null() && (*block).payload_len > 0 {
                let payload =
                    std::slice::from_raw_parts((*block).payload as *const u8, (*block).payload_len);
                shared.write(payload);
            }
        }
    }));
    if outcome.is_err() {
        // Reported so a repeating panic is not mistaken for ordinary
        // datagram loss. Throttled, since the callback runs once per
        // datagram.
        let panics = PANICS.fetch_add(1, Ordering::Relaxed) + 1;
        if panics == 1 || panics.is_multiple_of(256) {
            diag_warn!("rist: {panics} panic(s) in the data callback, datagram dropped");
        }
    }
    // The free dereferences the block without checking it. The return
    // value is 0 on every path: librist cannot re-deliver a datagram, so a
    // failure code would achieve nothing.
    if !block.is_null() {
        // SAFETY: non-null per the check above; this is the block librist
        // handed over, released exactly once.
        unsafe { ffi::rist_receiver_data_block_free2(&mut block) };
    }
    0
}

/// Owns the librist receiver; drop order is destroy (joins librist's
/// threads), then the logging settings the receiver used, then the callback's
/// Arc strong count.
struct Receiver {
    ctx: *mut ffi::RistCtx,
    log: *mut ffi::RistLoggingSettings,
    callback_arg: *const Shared,
}

// SAFETY: the raw pointers are owning handles used from one thread at a time;
// librist contexts are not thread-affine.
unsafe impl Send for Receiver {}

impl Drop for Receiver {
    fn drop(&mut self) {
        // SAFETY: ctx/log are the handles created at open (either may be null
        // on a failed open). rist_destroy joins librist's threads, so the
        // callback cannot run after it returns and the Arc count released
        // last cannot be observed by the callback.
        unsafe {
            if !self.ctx.is_null() {
                ffi::rist_destroy(self.ctx);
            }
            if !self.log.is_null() {
                ffi::rist_logging_settings_free2(&mut self.log);
            }
            if !self.callback_arg.is_null() {
                drop(Arc::from_raw(self.callback_arg));
            }
        }
    }
}

pub struct RistSource {
    shared: Arc<Shared>,
    _receiver: Receiver,
    cancel: Box<dyn Fn() -> bool + Send>,
    head: Vec<u8>,
    /// Absolute offset of the next unserved byte.
    pos: u64,
}

impl RistSource {
    /// Open a Main-Profile receiver for `url` (`rist://host:port?query`;
    /// secret, aes-type and buffer ride in the query, parsed by librist).
    ///
    /// `vetted` is the address-gate-vetted socket address for the URL's host;
    /// librist is pinned to it rather than re-resolving the hostname, closing
    /// the window between the SSRF check and librist's connect.
    pub fn open(
        url: &str,
        vetted: SocketAddr,
        cancel: Box<dyn Fn() -> bool + Send>,
    ) -> Result<Self, RistError> {
        let parsed =
            url::Url::parse(url).map_err(|e| RistError::config(format!("rist url: {e}")))?;
        let host = parsed
            .host_str()
            .ok_or_else(|| RistError::config("rist url without host"))?;
        let port = parsed
            .port()
            .ok_or_else(|| RistError::config("rist url requires an explicit port"))?;

        // Reconstruct the URL for librist's parser. It wants
        // "rist://host:port[?query]" with no path: a trailing '/' makes it
        // mis-parse the host and port.
        let librist_url = match parsed.query() {
            Some(query) => format!("rist://{host}:{port}?{query}"),
            None => format!("rist://{host}:{port}"),
        };
        let librist_url = CString::new(librist_url)
            .map_err(|_| RistError::config("rist url contains a NUL byte"))?;

        let shared = Arc::new(Shared {
            ring: Mutex::new(Ring {
                buf: vec![0u8; RING_BYTES],
                head: 0,
                fill: 0,
            }),
            ready: Condvar::new(),
        });

        let mut receiver = Receiver {
            ctx: std::ptr::null_mut(),
            log: std::ptr::null_mut(),
            callback_arg: std::ptr::null(),
        };

        // librist's init path logs through a logging-settings object plus a
        // global log mutex. Passing NULL leaves that mutex uninitialised, and
        // librist's bundled Windows pthread shim doesn't lazily init it, so
        // the first internal log call faults. Create real (disabled) settings
        // first, then hand them to the receiver.
        // SAFETY: out-pointer call; on success `log` is a live settings
        // object owned by `receiver` (freed after rist_destroy in Drop).
        let rc = unsafe {
            ffi::rist_logging_set(
                &mut receiver.log,
                ffi::RIST_LOG_DISABLE,
                None,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            )
        };
        if rc != 0 || receiver.log.is_null() {
            return Err(RistError::init("failed to init librist logging settings"));
        }

        // SAFETY: out-pointer create with the live logging settings above.
        let rc = unsafe {
            ffi::rist_receiver_create(&mut receiver.ctx, ffi::RIST_PROFILE_MAIN, receiver.log)
        };
        if rc != 0 || receiver.ctx.is_null() {
            return Err(RistError::init("failed to create librist receiver"));
        }

        // librist parses host/port plus encryption (secret/aes-type) and
        // buffer depth from the URL query into the peer config.
        let mut peer_cfg: *mut ffi::RistPeerConfig = std::ptr::null_mut();
        // SAFETY: librist_url is a valid NUL-terminated string; peer_cfg is
        // an out pointer librist allocates into (freed below either way).
        let rc = unsafe { ffi::rist_parse_address2(librist_url.as_ptr(), &mut peer_cfg) };
        if rc < 0 || peer_cfg.is_null() {
            // SAFETY: free tolerates the null case; rc<0 with a non-null
            // config still needs the free.
            unsafe { ffi::rist_peer_config_free2(&mut peer_cfg) };
            return Err(RistError::config("invalid rist address or parameters"));
        }

        // Pin librist to the vetted address. Setting address_family routes
        // librist onto its manual-sockdata path, which treats address as a
        // literal (no re-resolution) but takes the port from physical_port
        // rather than the address string, and rist_parse_address2 never
        // populates physical_port. Carry the URL port across explicitly, or
        // librist resolves the literal against port 0 and sends nowhere.
        let ip_literal = vetted.ip().to_string();
        // SAFETY: peer_cfg is the live config from rist_parse_address2; the
        // ip literal (max 45 chars) fits the 256-byte address field with its
        // NUL terminator.
        unsafe {
            (*peer_cfg).initiate_conn = 1;
            let bytes = ip_literal.as_bytes();
            debug_assert!(bytes.len() < ffi::RIST_MAX_STRING_LONG);
            for (i, &b) in bytes.iter().enumerate() {
                (*peer_cfg).address[i] = b as c_char;
            }
            (*peer_cfg).address[bytes.len()] = 0;
            (*peer_cfg).address_family = if vetted.is_ipv4() {
                ffi::AF_INET
            } else {
                ffi::AF_INET6
            };
            (*peer_cfg).physical_port = port;
        }

        let mut peer: *mut ffi::RistPeer = std::ptr::null_mut();
        // SAFETY: live ctx + populated config; librist copies the config, so
        // freeing it right after is the documented pattern.
        let peer_rc = unsafe { ffi::rist_peer_create(receiver.ctx, &mut peer, peer_cfg) };
        // SAFETY: peer_cfg came from rist_parse_address2 and is done with.
        unsafe { ffi::rist_peer_config_free2(&mut peer_cfg) };
        if peer_rc != 0 {
            return Err(RistError::init("failed to add librist peer"));
        }

        let callback_arg = Arc::into_raw(Arc::clone(&shared));
        receiver.callback_arg = callback_arg;
        // SAFETY: ctx is live; callback_arg stays valid until Receiver::drop
        // releases it after rist_destroy.
        let rc = unsafe {
            ffi::rist_receiver_data_callback_set2(
                receiver.ctx,
                on_data,
                callback_arg as *mut c_void,
            )
        };
        if rc != 0 {
            return Err(RistError::init("failed to set librist data callback"));
        }

        // SAFETY: ctx is live and fully configured.
        if unsafe { ffi::rist_start(receiver.ctx) } != 0 {
            return Err(RistError::init("failed to start librist receiver"));
        }

        Ok(Self {
            shared,
            _receiver: receiver,
            cancel,
            head: Vec::new(),
            pos: 0,
        })
    }

    /// The linked librist version string, for the transport diag line.
    pub fn library_version() -> String {
        // SAFETY: librist_version returns a static NUL-terminated string.
        unsafe {
            std::ffi::CStr::from_ptr(ffi::librist_version())
                .to_string_lossy()
                .into_owned()
        }
    }

    /// Blocking sequential read: waits until librist delivers bytes, the
    /// cancel probe fires (served as end-of-source), or forever if the sender
    /// stays quiet. A silent RIST sender is indistinguishable from a slow
    /// one, and librist keeps the session alive underneath.
    fn read_next(&mut self, buf: &mut [u8]) -> usize {
        loop {
            if (self.cancel)() {
                return 0;
            }
            let n = self.shared.drain(buf);
            if n > 0 {
                return n;
            }
            // Poison is recovered, as in the writer and the drain: the
            // ring is always left consistent, so a panic elsewhere should
            // not fail every later read.
            let ring = self.shared.ring.lock().unwrap_or_else(|e| e.into_inner());
            // Re-check under the lock so a write between drain and lock
            // cannot be slept through.
            if ring.fill == 0 {
                let _unused = self
                    .shared
                    .ready
                    .wait_timeout(ring, WAIT_SLICE)
                    .unwrap_or_else(|e| e.into_inner());
            }
        }
    }
}

impl ByteSource for RistSource {
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
            return Err(format!(
                "rist source is sequential: cannot re-read {offset} (stream at {})",
                self.pos
            )
            .into());
        }
        if offset > self.pos {
            return Err(format!(
                "rist source is sequential: cannot skip to {offset} (stream at {})",
                self.pos
            )
            .into());
        }

        let n = self.read_next(buf);
        if n > 0 {
            if self.head.len() < HEAD_CACHE {
                let keep = (HEAD_CACHE - self.head.len()).min(n);
                self.head.extend_from_slice(&buf[..keep]);
            }
            self.pos += n as u64;
        }
        Ok(n)
    }
}
