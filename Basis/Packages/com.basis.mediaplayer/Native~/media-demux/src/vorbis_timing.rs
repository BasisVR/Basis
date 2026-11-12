//! Vorbis packet timing without a decoder: block sizes from the
//! identification header, each mode's block flag from the end of the setup
//! header, and each packet's mode and window flags from its first bits.
//!
//! A packet's samples run between the centres of its window and the one
//! before it: a quarter of each block. That is what granule positions
//! count. lewton hands the samples out on a different split: a long block
//! followed by a short one also releases the stretch up to its right
//! window's start, `(long - short) / 4` early, and the short block after it
//! that much less. So a packet is stamped at the previous centre, plus that
//! stretch when the previous packet released it, which is exactly where
//! lewton's output for it begins.

/// What the demuxer needs of a Vorbis stream to time its packets.
pub(crate) struct VorbisTiming {
    /// Short and long block sizes, in samples.
    blocks: [i64; 2],
    /// Each mode's block flag, in mode order.
    long_modes: Vec<bool>,
    mode_bits: u32,
    /// Block size of the packet before and the stretch it released past
    /// its centre, or `None` at the start or after a seek.
    previous: Option<(i64, i64)>,
}

/// Bits needed to code values up to `v` (the Vorbis `ilog`).
fn ilog(v: u32) -> u32 {
    32 - v.leading_zeros()
}

/// Bit `i` of a Vorbis bitstream, which packs from the least significant
/// bit of each byte.
fn bit(data: &[u8], i: usize) -> Option<bool> {
    Some(data.get(i / 8)? >> (i % 8) & 1 == 1)
}

/// `n` bits from bit `at`, least significant first.
fn bits(data: &[u8], at: usize, n: usize) -> Option<u32> {
    let mut v = 0u32;
    for k in 0..n {
        v |= u32::from(bit(data, at + k)?) << k;
    }
    Some(v)
}

/// The modes' block flags, read backwards from the setup header's framing
/// bit. Each mode is a block flag, two 16-bit fields that must be zero and
/// an 8-bit mapping; the count less one sits in the six bits before them.
/// Zero fields can also occur in what precedes the modes, so the longest
/// run whose count field agrees is taken, as other parsers do.
fn mode_flags(setup: &[u8]) -> Option<Vec<bool>> {
    let total = setup.len() * 8;
    let framing = (0..total).rev().find(|&i| bit(setup, i) == Some(true))?;
    let mut flags = Vec::new();
    let mut end = framing;
    while flags.len() < 64 && end >= 41 {
        let start = end - 41;
        if bits(setup, start + 1, 16)? != 0 || bits(setup, start + 17, 16)? != 0 {
            break;
        }
        flags.push(bit(setup, start)?);
        end = start;
    }
    (1..=flags.len()).rev().find_map(|count| {
        let at = framing.checked_sub(41 * count + 6)?;
        (bits(setup, at, 6)? as usize == count - 1).then(|| {
            let mut modes = flags[..count].to_vec();
            modes.reverse();
            modes
        })
    })
}

impl VorbisTiming {
    pub(crate) fn new(ident: &[u8], setup: &[u8]) -> Option<Self> {
        let sizes = *ident.get(28)?;
        let (short, long) = (u32::from(sizes & 0x0F), u32::from(sizes >> 4));
        if !(6..=13).contains(&short) || !(short..=13).contains(&long) {
            return None;
        }
        let long_modes = mode_flags(setup)?;
        Some(Self {
            blocks: [1 << short, 1 << long],
            mode_bits: ilog(long_modes.len() as u32 - 1),
            long_modes,
            previous: None,
        })
    }

    /// Forget the previous packet: the next one decodes to nothing.
    pub(crate) fn restart(&mut self) {
        self.previous = None;
    }

    /// For one audio packet: where its output starts relative to the
    /// previous centre, and how far it moves the centre. `None` for a
    /// packet that is not audio or names a mode the stream lacks.
    pub(crate) fn packet(&mut self, packet: &[u8]) -> Option<(i64, i64)> {
        if bit(packet, 0)? {
            return None;
        }
        let mode = bits(packet, 1, self.mode_bits as usize)? as usize;
        let long = *self.long_modes.get(mode)?;
        let at = 1 + self.mode_bits as usize;
        let (block, released) = if long {
            let next_long = bit(packet, at + 1)?;
            let [short, long] = self.blocks;
            (long, if next_long { 0 } else { (long - short) / 4 })
        } else {
            (self.blocks[0], 0)
        };
        let (offset, advance) = match self.previous {
            Some((previous, released)) => (released, previous / 4 + block / 4),
            None => (0, 0),
        };
        self.previous = Some((block, released));
        Some((offset, advance))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Pack values least significant bit first, as Vorbis does.
    fn pack(fields: &[(u32, usize)]) -> Vec<u8> {
        let mut out = Vec::new();
        let mut n = 0usize;
        for &(v, width) in fields {
            for k in 0..width {
                if n.is_multiple_of(8) {
                    out.push(0);
                }
                if v >> k & 1 == 1 {
                    *out.last_mut().unwrap() |= 1 << (n % 8);
                }
                n += 1;
            }
        }
        out
    }

    /// A setup header tail: some preceding bits, the mode count, the modes
    /// and the framing bit.
    fn setup(prefix: &[(u32, usize)], modes: &[bool]) -> Vec<u8> {
        let mut fields = prefix.to_vec();
        fields.push((modes.len() as u32 - 1, 6));
        for (i, &long) in modes.iter().enumerate() {
            fields.extend([(u32::from(long), 1), (0, 16), (0, 16), (i as u32, 8)]);
        }
        fields.push((1, 1));
        pack(&fields)
    }

    #[test]
    fn modes_read_back_from_the_framing_bit() {
        let s = setup(&[(0x2D, 7), (0x1F3, 9)], &[false, true]);
        assert_eq!(mode_flags(&s), Some(vec![false, true]));
        let s = setup(&[(0xFFFF, 16)], &[true, false, true, false]);
        assert_eq!(mode_flags(&s), Some(vec![true, false, true, false]));
        assert_eq!(mode_flags(&[]), None);
        assert_eq!(mode_flags(&[0, 0, 0]), None);
    }

    fn ident(short: u8, long: u8) -> Vec<u8> {
        let mut i = vec![0u8; 30];
        i[28] = short | (long << 4);
        i
    }

    /// Long-long-short-short-long: centres move by a quarter of each block,
    /// and the long block before the short one releases its extra stretch
    /// early, which the packet after it is stamped past.
    #[test]
    fn packets_time_from_their_neighbours() {
        let mut t = VorbisTiming::new(&ident(8, 11), &setup(&[], &[false, true])).expect("timing");
        let long = |next_long: bool| pack(&[(0, 1), (1, 1), (1, 1), (u32::from(next_long), 1)]);
        let short = pack(&[(0, 1), (0, 1)]);
        assert_eq!(t.packet(&long(true)), Some((0, 0)));
        assert_eq!(t.packet(&long(false)), Some((0, 1024)));
        assert_eq!(t.packet(&short), Some((448, 512 + 64)));
        assert_eq!(t.packet(&short), Some((0, 128)));
        assert_eq!(t.packet(&long(true)), Some((0, 64 + 512)));
        t.restart();
        assert_eq!(t.packet(&short), Some((0, 0)));
        // A header packet, and a mode past the table, are not timed.
        assert_eq!(t.packet(&[1]), None);
        let mut three =
            VorbisTiming::new(&ident(8, 11), &setup(&[], &[false, true, false])).expect("timing");
        assert_eq!(three.packet(&pack(&[(0, 1), (3, 2)])), None);
    }

    #[test]
    fn implausible_block_sizes_are_refused() {
        let s = setup(&[], &[false]);
        assert!(VorbisTiming::new(&ident(5, 11), &s).is_none());
        assert!(VorbisTiming::new(&ident(11, 8), &s).is_none());
        assert!(VorbisTiming::new(&ident(8, 14), &s).is_none());
        assert!(VorbisTiming::new(&[0; 10], &s).is_none());
    }
}
