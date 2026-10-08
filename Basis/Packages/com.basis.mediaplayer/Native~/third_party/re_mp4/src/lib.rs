//! `re_mp4` is a Rust library to parse ISO-MP4 files.
//!
//! This package contains MPEG-4 specifications defined in parts:
//!    * ISO/IEC 14496-12 - ISO Base Media File Format (`QuickTime`, MPEG-4, etc)
//!    * ISO/IEC 14496-14 - MP4 file format
//!    * ISO/IEC 14496-17 - Streaming text format
//!

mod error;
pub use error::Error;

pub type Result<T> = std::result::Result<T, Error>;

mod types;
pub use types::*;

mod mp4box;
pub use mp4box::*;

mod reader;
pub use reader::{Mp4, Sample, Track};

pub use types::{TrackId, TrackKind};
