//! RIST receive transport: librist behind FFI, Main Profile,
//! plain + PSK-AES, serving recovered MPEG-TS as a `ByteSource`.
//!
//! The native dependency sits behind the `librist` cargo feature. With it off
//! (the default) this crate is a stub whose `open` returns a typed
//! [`RistError::NotBuilt`] refusal and nothing links.

use std::fmt;

#[cfg(feature = "librist")]
mod ffi;
#[cfg(feature = "librist")]
mod source;

#[cfg(feature = "librist")]
pub use source::RistSource;

#[derive(Debug)]
pub enum RistError {
    /// The binary was built without the `librist` feature.
    NotBuilt,
    /// The URL or its parameters are unusable.
    Config(String),
    /// librist refused to initialise or start.
    Init(String),
}

#[cfg(feature = "librist")]
impl RistError {
    pub(crate) fn config(msg: impl Into<String>) -> Self {
        Self::Config(msg.into())
    }

    pub(crate) fn init(msg: impl Into<String>) -> Self {
        Self::Init(msg.into())
    }
}

impl fmt::Display for RistError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::NotBuilt => write!(
                f,
                "RIST is not built into this binary; rebuild with the `rist` feature \
                 (librist staged by tools/build-librist.ps1)"
            ),
            Self::Config(msg) => write!(f, "rist: {msg}"),
            Self::Init(msg) => write!(f, "rist: {msg}"),
        }
    }
}

impl std::error::Error for RistError {}

/// Stub for builds without the native dependency: same signature, typed
/// refusal.
#[cfg(not(feature = "librist"))]
pub struct RistSource;

#[cfg(not(feature = "librist"))]
impl RistSource {
    pub fn open(
        _url: &str,
        _vetted: std::net::SocketAddr,
        _cancel: Box<dyn Fn() -> bool + Send>,
    ) -> Result<Self, RistError> {
        Err(RistError::NotBuilt)
    }

    pub fn library_version() -> String {
        "not built".into()
    }
}

#[cfg(not(feature = "librist"))]
impl media_demux::ByteSource for RistSource {
    fn size(&mut self) -> Result<Option<u64>, media_demux::SourceError> {
        Ok(None)
    }

    fn read_at(
        &mut self,
        _offset: u64,
        _buf: &mut [u8],
    ) -> Result<usize, media_demux::SourceError> {
        Ok(0)
    }
}
