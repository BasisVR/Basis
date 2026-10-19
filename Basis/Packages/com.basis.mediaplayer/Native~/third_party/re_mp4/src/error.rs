use thiserror::Error;

use crate::mp4box::BoxType;

#[derive(Error, Debug)]
pub enum Error {
    #[error("{0}")]
    Io(#[from] std::io::Error),

    #[error("{0}")]
    InvalidData(&'static str),

    #[error("{0} not found")]
    BoxNotFound(BoxType),

    #[error("{0} and {1} not found")]
    Box2NotFound(BoxType, BoxType),

    #[error("trak[{0}] not found")]
    TrakNotFound(u32),

    #[error("trak[{0}].{1} not found")]
    BoxInTrakNotFound(u32, BoxType),

    #[error("traf[{0}].{1} not found")]
    BoxInTrafNotFound(u32, BoxType),

    #[error("trak[{0}].stbl.{1} not found")]
    BoxInStblNotFound(u32, BoxType),

    #[error("trak[{0}].stbl.{1}.entry[{2}] not found")]
    EntryInStblNotFound(u32, BoxType, u32),

    #[error("traf[{0}].trun.{1}.entry[{2}] not found")]
    EntryInTrunNotFound(u32, BoxType, u32),

    #[error("{0} version {1} is not supported")]
    UnsupportedBoxVersion(BoxType, u8),
}
