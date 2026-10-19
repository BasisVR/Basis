//! Elementary-stream bitstream parsing shared by the demux layer: Annex-B
//! NAL walking, H.264 SPS dimensions, keyframe detection, ADTS headers and
//! the AudioSpecificConfig. The ue(v) shift cap, the bit reader that
//! freezes past end-of-data and the i64 crop arithmetic each guard a crash
//! that fuzzing found.
//!
//! Everything here parses attacker-controlled bytes.

#![forbid(unsafe_code)]

mod adts;
mod annexb;
mod asc;
mod cea608;
mod h264;
mod sei;
mod user_data;

pub use adts::{AAC_RATES, AdtsHeader, aac_channels_from_config, build_asc, parse_adts};
pub use annexb::{h264_is_keyframe, h264_nal_type, h265_is_keyframe, h265_nal_type, nal_units};
pub use asc::{AudioSpecificConfig, parse_asc, strip_inert_sbr};
pub use cea608::{CaptionCue, CaptionScanner, a53_cc_triples};
pub use h264::{sps_dimensions, sps_reorder_free};
pub use sei::{scan_au_sei, sei_messages, unescape_rbsp};
pub use user_data::{EPOCH_SLACK_US, SeiUserData, UserDataScanner};
