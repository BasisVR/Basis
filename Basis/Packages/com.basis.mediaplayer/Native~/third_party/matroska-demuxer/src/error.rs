//! Demux errors.

use std::error::Error;

use crate::element_id::ElementId;

/// Errors that can occur when demuxing Matroska files.
#[derive(Debug)]
pub enum DemuxError {
    /// A `std::io::Error`.
    IoError(std::io::Error),
    /// A `std::string::FromUtf8Error`.
    FromUtf8Error(std::string::FromUtf8Error),
    /// A `TryFromIntError`.
    TryFromIntError(std::num::TryFromIntError),
    /// An invalid EBML Element ID was found.
    InvalidEbmlElementId,
    /// An invalid EBML data size was found.
    InvalidEbmlDataSize,
    /// An invalid EBML header was found.
    InvalidEbmlHeader(String),
    /// Wrong float size.
    WrongFloatSize(u64),
    /// Wrong integer size.
    WrongIntegerSize(u64),
    /// Wrong date size.
    WrongDateSize(u64),
    /// Unsupported DocType.
    UnsupportedDocType(String),
    /// Unsupported DocTypeReadVersion.
    UnsupportedDocTypeReadVersion(u64),
    /// Unexpected element found.
    UnexpectedElement((ElementId, ElementId)),
    /// Unexpected data type found.
    UnexpectedDataType,
    /// Can't find the expected element.
    ElementNotFound(ElementId),
    /// Can't find a cluster element.
    CantFindCluster,
    /// A value that should not be zero was zero.
    NonZeroValueIsZero(ElementId),
    /// A value that should be positive is not positive.
    PositiveValueIsNotPositive,
    /// A value for duration is already set for this frame, only one BlockDuration is prelevant for one frame.
    FrameAlreadyHasDuration(u64),
    /// An element or frame declared more data than is available in the stream.
    ElementSizeExceedsStream {
        /// The size declared by the element.
        declared: u64,
        /// The number of bytes actually remaining in the stream.
        available: u64,
    },
    /// A block declared a size that is inconsistent with its own contents.
    InvalidBlockSize,
    /// An element or frame declared more data than the reader will hold at once.
    ElementSizeExceedsLimit {
        /// The size declared by the element.
        declared: u64,
        /// The most the reader will allocate for it.
        limit: u64,
    },
}

impl std::fmt::Display for DemuxError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            DemuxError::IoError(err) => {
                write!(f, "{:?}", err.source())
            }
            DemuxError::FromUtf8Error(err) => {
                write!(f, "{:?}", err.source())
            }
            DemuxError::TryFromIntError(err) => {
                write!(f, "{:?}", err.source())
            }
            DemuxError::InvalidEbmlElementId => {
                write!(f, "invalid EBML Element ID was found")
            }
            DemuxError::InvalidEbmlDataSize => {
                write!(f, "invalid EBML data size was found")
            }
            DemuxError::InvalidEbmlHeader(message) => {
                write!(f, "invalid EBML header: {}", message)
            }
            DemuxError::WrongFloatSize(size) => {
                write!(
                    f,
                    "floats need to be either 4 or 7 bytes. Found size of: {}",
                    size
                )
            }
            DemuxError::WrongIntegerSize(size) => {
                write!(
                    f,
                    "integers can be at most 8 bytes. Found size of: {}",
                    size
                )
            }
            DemuxError::WrongDateSize(size) => {
                write!(f, "date can be at most 8 bytes. Found size of: {}", size)
            }
            DemuxError::UnsupportedDocType(doctype) => {
                write!(
                    f,
                    "unsupported DocType. Only 'matroska' and 'webm' are supported': {}",
                    doctype
                )
            }
            DemuxError::UnsupportedDocTypeReadVersion(version) => {
                write!(f, "unsupported DocTypeReadVersion: {}", version)
            }
            DemuxError::UnexpectedElement((expected, found)) => {
                write!(
                    f,
                    "unexpected element found. Expected: {:?} Found: {:?}",
                    expected, found
                )
            }
            DemuxError::UnexpectedDataType => {
                write!(f, "unexpected data type found")
            }
            DemuxError::ElementNotFound(element_id) => {
                write!(f, "can't find Element: {:?}", element_id)
            }
            DemuxError::CantFindCluster => {
                write!(f, "can't find the first cluster element")
            }
            DemuxError::NonZeroValueIsZero(element_id) => {
                write!(
                    f,
                    "a value that should not be zero was zero: {:?}",
                    element_id
                )
            }
            DemuxError::PositiveValueIsNotPositive => {
                write!(f, "a value that should be positive is not positive")
            }
            DemuxError::FrameAlreadyHasDuration(previous_duration) => {
                write!(
                    f,
                    "a duration `{previous_duration}` is already set for current Frame"
                )
            }
            DemuxError::ElementSizeExceedsStream {
                declared,
                available,
            } => {
                write!(
                    f,
                    "element declared size of {declared} bytes but only {available} bytes are available"
                )
            }
            DemuxError::InvalidBlockSize => {
                write!(f, "a block declared a size inconsistent with its contents")
            }
            DemuxError::ElementSizeExceedsLimit { declared, limit } => {
                write!(
                    f,
                    "element declared size of {declared} bytes but at most {limit} bytes are read"
                )
            }
        }
    }
}

impl From<std::io::Error> for DemuxError {
    fn from(err: std::io::Error) -> DemuxError {
        DemuxError::IoError(err)
    }
}

impl From<std::string::FromUtf8Error> for DemuxError {
    fn from(err: std::string::FromUtf8Error) -> DemuxError {
        DemuxError::FromUtf8Error(err)
    }
}

impl From<std::num::TryFromIntError> for DemuxError {
    fn from(err: std::num::TryFromIntError) -> DemuxError {
        DemuxError::TryFromIntError(err)
    }
}

impl Error for DemuxError {
    fn source(&self) -> Option<&(dyn Error + 'static)> {
        match *self {
            DemuxError::IoError(ref e) => Some(e),
            DemuxError::FromUtf8Error(ref e) => Some(e),
            DemuxError::TryFromIntError(ref e) => Some(e),
            _ => None,
        }
    }
}
