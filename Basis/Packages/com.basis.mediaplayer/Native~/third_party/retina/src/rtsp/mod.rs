// Copyright (C) The Retina Authors
// SPDX-License-Identifier: MIT OR Apache-2.0

//! RTSP/1.0 message types and parser.
//!
//! This is a hand-rolled streaming parser designed for use with both
//! contiguous and ring-buffer inputs. For now, only contiguous `&[u8]`
//! input is used; ring-buffer support will come later.

#[allow(dead_code)] // Several items are used only in tests or reserved for future steps.
pub mod inputs;
pub mod msg;
pub mod parse;
pub(crate) mod table;
