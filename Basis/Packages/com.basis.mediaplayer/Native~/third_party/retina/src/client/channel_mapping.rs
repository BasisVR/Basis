// Copyright (C) The Retina Authors
// SPDX-License-Identifier: MIT OR Apache-2.0

//! Track RTSP interleaved channel->stream assignments.

use std::num::NonZeroU8;

#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub enum ChannelType {
    Rtp,
    Rtcp,
}

#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub struct ChannelMapping {
    pub stream_i: usize,
    pub channel_type: ChannelType,
}

/// Mapping of the 256 possible RTSP interleaved channels to stream indices and
/// RTP/RTCP. Assumptions:
///
/// *   We only need to support 255 possible streams in a presentation. If
///     there are more than 128, we couldn't actually stream them all at once
///     anyway with one RTP and one RTCP channel per stream.
/// *   We'll always assign even channels numbers as RTP and their odd
///     successors as RTCP for the same stream. This seems reasonable given
///     that there is no clear way to assign a single channel in the RTSP spec.
///     [RFC 2326 section 10.12](https://tools.ietf.org/html/rfc2326#section-10.12)
///     says that `interleaved=n` also assigns channel `n+1`, and it's ambiguous
///     what `interleaved=n-m` does when `m > n+1` (section 10.12 suggests it
///     assigns only `n` and `m`; section 12.39 the suggests full range `[n,
///     m]`) or when `n==m`. We'll get into trouble if an RTSP server insists on
///     specifying an odd `n`, but that just seems obstinate.
///
/// These assumptions let us keep the full mapping with little space and an
/// efficient lookup operation.
#[derive(Default)]
pub struct ChannelMappings(smallvec::SmallVec<[Option<NonZeroU8>; 16]>);

impl ChannelMappings {
    /// Returns the next unassigned even channel id, or `None` if all assigned.
    pub fn next_unassigned(&self) -> Option<u8> {
        if let Some(i) = self.0.iter().position(Option::is_none) {
            return Some((i as u8) << 1);
        }
        if self.0.len() < 128 {
            return Some((self.0.len() as u8) << 1);
        }
        None
    }

    /// Assigns an even channel id (to RTP) and its odd successor (to RTCP) or errors.
    pub fn assign(&mut self, channel_id: u8, stream_i: usize) -> Result<(), String> {
        if (channel_id & 1) != 0 {
            return Err(format!("Can't assign odd channel id {channel_id}"));
        }
        if stream_i >= 255 {
            return Err(format!(
                "Can't assign channel to stream id {stream_i} because it's >= 255"
            ));
        }
        let i = usize::from(channel_id >> 1);
        if i >= self.0.len() {
            self.0.resize(i + 1, None);
        }
        let c = &mut self.0[i];
        if let Some(c) = c {
            return Err(format!(
                "Channel id {} is already assigned to stream {}; won't reassign to stream {}",
                channel_id,
                c.get() - 1,
                channel_id
            ));
        }
        *c = Some(NonZeroU8::new((stream_i + 1) as u8).expect("[0, 255) + 1 is non-zero"));
        Ok(())
    }

    /// Looks up a channel id's mapping.
    pub fn lookup(&self, channel_id: u8) -> Option<ChannelMapping> {
        let i = usize::from(channel_id >> 1);
        if i >= self.0.len() {
            return None;
        }
        self.0[i].map(|c| ChannelMapping {
            stream_i: usize::from(c.get() - 1),
            channel_type: match (channel_id & 1) != 0 {
                false => ChannelType::Rtp,
                true => ChannelType::Rtcp,
            },
        })
    }
}

impl std::fmt::Debug for ChannelMappings {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_map()
            .entries(self.0.iter().enumerate().filter_map(|(i, v)| {
                v.map(|v| (format!("{}-{}", i << 1, (i << 1) + 1), v.get() - 1))
            }))
            .finish()
    }
}

#[cfg(test)]
mod tests {
    use super::{ChannelMapping, ChannelType};

    #[test]
    fn channel_mappings() {
        let mut mappings = super::ChannelMappings::default();
        assert_eq!(mappings.next_unassigned().unwrap(), 0);
        assert_eq!(mappings.lookup(0), None);
        mappings.assign(0, 42).unwrap();
        mappings.assign(0, 43).unwrap_err();
        mappings.assign(1, 43).unwrap_err();
        assert_eq!(
            mappings.lookup(0),
            Some(ChannelMapping {
                stream_i: 42,
                channel_type: ChannelType::Rtp,
            })
        );
        assert_eq!(
            mappings.lookup(1),
            Some(ChannelMapping {
                stream_i: 42,
                channel_type: ChannelType::Rtcp,
            })
        );
        assert_eq!(mappings.next_unassigned().unwrap(), 2);
        mappings.assign(9, 26).unwrap_err();
        mappings.assign(8, 26).unwrap();
        assert_eq!(
            mappings.lookup(8),
            Some(ChannelMapping {
                stream_i: 26,
                channel_type: ChannelType::Rtp,
            })
        );
        assert_eq!(
            mappings.lookup(9),
            Some(ChannelMapping {
                stream_i: 26,
                channel_type: ChannelType::Rtcp,
            })
        );
        assert_eq!(mappings.next_unassigned().unwrap(), 2);
    }
}
