//! Test harness pieces: the recorded network-delay fixtures and the
//! arrival-schedule synthesis the Bank's sizing tests replay, the seeded
//! RNG, and the deterministic impairment source that wraps any byte source
//! in a recorded or synthetic delivery-gap schedule.

#![forbid(unsafe_code)]

pub mod impair;
pub mod phase0;
pub mod rng;
pub mod schedule;

pub use impair::{ImpairProfile, ImpairedSource, PacedSource, RealClock, WallClock};
pub use phase0::{Gap, GapCapture};
pub use rng::Xorshift64Star;
pub use schedule::{ArrivalAu, ArrivalSchedule};
