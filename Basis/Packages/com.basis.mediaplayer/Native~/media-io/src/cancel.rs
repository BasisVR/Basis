//! Cancellation for connects and reads: teardown never waits out a
//! network timeout. The engine holds the token; sources and their reader
//! tasks race everything against it.

use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};

use tokio::sync::Notify;

#[derive(Clone, Default)]
pub struct CancelToken(Arc<Inner>);

#[derive(Default)]
struct Inner {
    flag: AtomicBool,
    notify: Notify,
}

impl CancelToken {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn cancel(&self) {
        self.0.flag.store(true, Ordering::Release);
        self.0.notify.notify_waiters();
    }

    pub fn is_cancelled(&self) -> bool {
        self.0.flag.load(Ordering::Acquire)
    }

    /// A token that cancels when this one does, but whose own `cancel`
    /// does not propagate upwards — a source drops its child on teardown
    /// without ending the session-wide token it was built from.
    pub fn child(&self) -> CancelToken {
        let child = CancelToken::new();
        let parent = self.clone();
        let forward = child.clone();
        crate::runtime::runtime().spawn(async move {
            tokio::select! {
                _ = parent.cancelled() => forward.cancel(),
                _ = forward.cancelled() => {}
            }
        });
        child
    }

    /// Resolves when (or after) `cancel` is called.
    pub(crate) async fn cancelled(&self) {
        loop {
            let notified = self.0.notify.notified();
            tokio::pin!(notified);
            // Register interest before the flag check so a cancel between
            // check and await cannot be missed.
            notified.as_mut().enable();
            if self.is_cancelled() {
                return;
            }
            notified.await;
        }
    }
}
