//! The shared I/O runtime: async is confined to the network edge,
//! one small tokio runtime shared across sessions. The media path stays
//! synchronous and consumes bounded channels this side fills.

use std::sync::OnceLock;

use tokio::runtime::Runtime;

pub(crate) fn runtime() -> &'static Runtime {
    static RUNTIME: OnceLock<Runtime> = OnceLock::new();
    RUNTIME.get_or_init(|| {
        tokio::runtime::Builder::new_multi_thread()
            .worker_threads(2)
            .thread_name("bm-io")
            .enable_io()
            .enable_time()
            .build()
            .expect("build I/O runtime")
    })
}

/// Handle to the shared I/O runtime for crates that host their own async
/// sessions on it (media-rtsp).
pub fn io_runtime_handle() -> tokio::runtime::Handle {
    runtime().handle().clone()
}
