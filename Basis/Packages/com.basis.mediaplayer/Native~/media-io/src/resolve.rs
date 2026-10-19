//! Hostname resolution under a ceiling, off the reactor.
//!
//! Two properties every resolve in this crate needs, neither of which
//! `std::net::ToSocketAddrs` gives on its own.
//!
//! Bounded: the platform resolver runs its own retry schedule, seconds
//! to tens of seconds against a delegated but non-responsive nameserver,
//! and neither [`IoLimits::connect_timeout`](crate::IoLimits) nor
//! `request_timeout` covers any of it: both are client settings that
//! start once an address set exists. A hostile hostname costs
//! [`RESOLVE_TIMEOUT`] here instead.
//!
//! Interruptible: `to_socket_addrs` blocks the calling thread, and a
//! blocking syscall inside a poll cannot be pre-empted, so a `select!`
//! racing a cancel token against it never polls its cancel branch.
//! Running it on the blocking pool makes the await point real.
//!
//! The timeout bounds the *wait*, not the syscall: nothing cancels a
//! `getaddrinfo` once it has started, so a timed-out lookup keeps its
//! pool thread until the platform resolver gives up. [`IN_FLIGHT`]
//! bounds that, and it is why the lookup is spawned by hand rather than
//! through `tokio::net::lookup_host`, which offers nowhere to hold the
//! permit.

use std::net::{SocketAddr, ToSocketAddrs};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::time::Duration;

use media_diag::diag_log;
use tokio::sync::Semaphore;

use crate::runtime::runtime;
use crate::{IoError, IoErrorKind};

/// Ceiling on one hostname resolution. Generous against a slow-but-real
/// nameserver, short enough that six redirect hops of a blackholed one
/// stay under the engine's reconnect budget.
pub(crate) const RESOLVE_TIMEOUT: Duration = Duration::from_secs(5);

/// Ceiling on system-resolver calls in flight at once.
///
/// Well above legitimate demand (a session resolves one host at a time,
/// and the hops within one open are sequential) and far below tokio's
/// blocking pool, so a peer naming blackholed hosts cannot pile up stuck
/// threads until the pool starves. Once the permits are out, a further
/// resolve waits for one under the same [`RESOLVE_TIMEOUT`]: a bounded
/// refusal for that caller rather than an unbounded cost for the process.
static IN_FLIGHT: Semaphore = Semaphore::const_new(16);

/// Lookups that have had to wait for a slot. A queued resolve that then
/// succeeds reports nothing, so this counter is what shows process-wide
/// pressure before anything fails.
static QUEUED: AtomicU64 = AtomicU64::new(0);

/// Resolve `host:port`, or fail typed. Callers hold an async context;
/// the lookup itself lands on the blocking pool, leaving this task's
/// worker free to poll whatever else the enclosing `select!` is racing.
pub(crate) async fn resolve_async(host: &str, port: u16) -> Result<Vec<SocketAddr>, IoError> {
    let owned = host.to_string();
    resolve_gated(&IN_FLIGHT, RESOLVE_TIMEOUT, host, move || {
        (owned.as_str(), port)
            .to_socket_addrs()
            .map(|addrs| addrs.collect::<Vec<_>>())
    })
    .await
}

/// The gating, the ceiling and the pool hop, over whatever `lookup`
/// actually does. It is a parameter so a row can substitute a lookup it
/// controls the timing of; the caller above supplies the real one.
async fn resolve_gated<F>(
    in_flight: &'static Semaphore,
    ceiling: Duration,
    host: &str,
    lookup: F,
) -> Result<Vec<SocketAddr>, IoError>
where
    F: FnOnce() -> std::io::Result<Vec<SocketAddr>> + Send + 'static,
{
    // Which half of the ceiling a timeout was spent in. Both refuse at
    // the same deadline but they are different faults: the host's
    // nameserver, or this process already holding every slot for lookups
    // the platform has not given up on.
    let queued = Arc::new(AtomicBool::new(false));
    let waiting = Arc::clone(&queued);
    let gated = async move {
        let permit = match in_flight.try_acquire() {
            Ok(permit) => permit,
            Err(_) => {
                waiting.store(true, Ordering::SeqCst);
                // Logged on the first queued lookup and every 64th after:
                // a line per resolve would flood the log under exactly the
                // load being reported.
                let queued = QUEUED.fetch_add(1, Ordering::Relaxed) + 1;
                if queued == 1 || queued.is_multiple_of(64) {
                    diag_log!(
                        "resolver slots all in use, {queued} lookups have queued for one so far"
                    );
                }
                let permit = in_flight
                    .acquire()
                    .await
                    .expect("the resolve semaphore is never closed");
                waiting.store(false, Ordering::SeqCst);
                permit
            }
        };
        tokio::task::spawn_blocking(move || {
            // Held by the syscall rather than by the caller's future:
            // dropping a timed-out lookup does not stop the resolver, so
            // the permit has to come back when the resolver does or the
            // bound would mean nothing.
            let _permit = permit;
            lookup()
        })
        .await
    };

    match tokio::time::timeout(ceiling, gated).await {
        Ok(Ok(Ok(addrs))) => Ok(addrs),
        Ok(Ok(Err(e))) => Err(IoError::new(IoErrorKind::Resolve, format!("{host}: {e}"))),
        Ok(Err(joined)) => Err(IoError::new(
            IoErrorKind::Resolve,
            format!("{host}: resolver task failed: {joined}"),
        )),
        Err(_elapsed) if queued.load(Ordering::SeqCst) => Err(IoError::new(
            IoErrorKind::Resolve,
            format!(
                "{host}: no resolver slot within {}s, every one held by a lookup \
                 the platform has not given up on",
                ceiling.as_secs_f32()
            ),
        )),
        Err(_elapsed) => Err(IoError::new(
            IoErrorKind::Resolve,
            format!("{host}: no answer within {}s", ceiling.as_secs_f32()),
        )),
    }
}

/// The blocking lanes' form: same ceiling, driven on the shared I/O
/// runtime. Callers must be opener or media threads: a runtime worker
/// would panic on the `block_on`.
///
/// `resolve_vetted` is public, so this is checked here: an async caller
/// then fails in a test rather than on tokio's own assertion in the field.
pub(crate) fn resolve_blocking(host: &str, port: u16) -> Result<Vec<SocketAddr>, IoError> {
    debug_assert!(
        tokio::runtime::Handle::try_current().is_err(),
        "resolve_blocking parks its thread; an async caller wants resolve_vetted_async"
    );
    runtime().block_on(resolve_async(host, port))
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Arc;
    use std::sync::atomic::{AtomicBool, Ordering};
    use std::sync::mpsc;

    /// How long a lookup waits to be released before giving up. Only a
    /// broken build gets here, and it has to end in a failed assertion
    /// rather than a hung suite.
    const RELEASE_GRACE: Duration = Duration::from_secs(2);

    /// The property the cancel races upstream depend on: polling a
    /// resolve returns, so whatever the enclosing `select!` is racing it
    /// against gets polled too. A resolve that blocks its thread instead
    /// completes on its first poll and the sibling branch never runs; the
    /// `biased` order makes that distinguishable here.
    ///
    /// The lookup is held until the sibling has run, so the row rests on
    /// ordering rather than timing: nothing asks the host's resolver
    /// anything, and a pool thread that starts early cannot finish ahead
    /// of the branch it should have yielded to.
    #[test]
    fn a_resolve_yields_before_it_answers() {
        static GATE: Semaphore = Semaphore::const_new(1);
        let rt = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .expect("runtime");
        rt.block_on(async {
            let sibling_ran = Arc::new(AtomicBool::new(false));
            let flag = Arc::clone(&sibling_ran);
            let (release, held) = mpsc::channel::<()>();

            let outcome = tokio::select! {
                biased;
                outcome = resolve_gated(&GATE, RESOLVE_TIMEOUT, "held", move || {
                    let _ = held.recv_timeout(RELEASE_GRACE);
                    Ok(vec!["127.0.0.1:8080".parse().expect("literal")])
                }) => outcome,
                _ = async move {
                    flag.store(true, Ordering::SeqCst);
                    let _ = release.send(());
                    std::future::pending::<()>().await;
                } => unreachable!("the pending branch cannot finish"),
            };

            assert!(outcome.is_ok(), "the released lookup answers: {outcome:?}");
            assert!(
                sibling_ran.load(Ordering::SeqCst),
                "the resolve never yielded, so the sibling branch was never polled"
            );
        });
    }

    /// No lookup starts without a permit, and the wait for one is inside
    /// the same ceiling, so a caller queued behind stuck lookups is
    /// refused rather than parked.
    #[test]
    fn a_resolve_without_a_permit_gives_up_under_the_same_ceiling() {
        static GATE: Semaphore = Semaphore::const_new(1);
        let rt = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .expect("runtime");
        rt.block_on(async {
            let _held = GATE.acquire().await.expect("the only permit");
            let ran = Arc::new(AtomicBool::new(false));
            let marker = Arc::clone(&ran);

            let started = std::time::Instant::now();
            let err = resolve_gated(&GATE, Duration::from_millis(50), "queued", move || {
                marker.store(true, Ordering::SeqCst);
                Ok(Vec::new())
            })
            .await
            .expect_err("nothing may start without a permit");

            assert!(!ran.load(Ordering::SeqCst), "the lookup ran unpermitted");
            assert_eq!(err.kind, IoErrorKind::Resolve);
            assert!(
                err.detail.contains("no resolver slot within"),
                "the refusal names the saturation rather than the nameserver: {err}"
            );
            assert!(started.elapsed() < Duration::from_secs(1));
        });
    }

    /// The blocking lanes call in from opener and media threads, never
    /// from a runtime worker; driving the shared runtime from one has to
    /// work rather than panic on a nested `block_on`. A literal keeps
    /// the host's resolver out of it.
    #[test]
    fn the_blocking_form_drives_the_shared_runtime() {
        let addrs = resolve_blocking("127.0.0.1", 8080).expect("literal resolves");
        assert_eq!(addrs, vec!["127.0.0.1:8080".parse::<SocketAddr>().unwrap()]);
    }
}
