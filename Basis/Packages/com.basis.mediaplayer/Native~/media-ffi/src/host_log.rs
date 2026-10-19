//! Where this host's diagnostics go.
//!
//! `media-diag` defaults to stderr, which is the channel a console host
//! and a test run have. A plugin loaded into a player has no readable
//! one: Android discards a native process's stderr outright, and a
//! Windows player is a GUI-subsystem process whose handle goes nowhere.
//! Each platform's sink is installed from the earliest hook it offers,
//! and the install is idempotent so every entry point that could precede
//! a diagnostic can ask for it.

use std::sync::Once;

static INSTALLED: Once = Once::new();

/// Points [`media_diag::log`] at this platform's channel. Safe to call
/// from anywhere; only the first call does anything.
pub fn install() {
    INSTALLED.call_once(|| {
        #[cfg(target_os = "android")]
        media_diag::set_log_sink(media_present::android::log);
        #[cfg(windows)]
        media_diag::set_log_sink(windows_sink);
    });
}

/// `OutputDebugStringW` is what a windowed process has: DebugView and any
/// attached debugger read it. Unlike logcat there is no tag field, so the
/// line names itself. stderr is written as well, because a console host
/// and `cargo test` have only that; a duplicated line where both channels
/// exist is cheaper than silence where one does not.
#[cfg(windows)]
fn windows_sink(line: &str) {
    media_diag::stderr_sink(line);
    let wide: Vec<u16> = format!("[basis-media] {line}\n\0").encode_utf16().collect();
    // SAFETY: wide is NUL-terminated and outlives the call.
    unsafe { OutputDebugStringW(wide.as_ptr()) };
}

#[cfg(windows)]
#[link(name = "kernel32")]
unsafe extern "system" {
    fn OutputDebugStringW(output_string: *const u16);
}
