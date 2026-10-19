//! Annex-B NAL unit walking and keyframe detection.

/// Iterate the NAL units of an Annex-B stream. Each item is the NAL payload
/// (header byte included) between start codes; a trailing zero belonging to
/// a following 4-byte start code is trimmed.
pub fn nal_units(data: &[u8]) -> NalUnits<'_> {
    NalUnits { data, pos: 0 }
}

pub struct NalUnits<'a> {
    data: &'a [u8],
    pos: usize,
}

impl<'a> Iterator for NalUnits<'a> {
    type Item = &'a [u8];

    fn next(&mut self) -> Option<&'a [u8]> {
        let data = self.data;
        let mut i = self.pos;
        let start = loop {
            if i + 3 > data.len() {
                return None;
            }
            if data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1 {
                break i + 3;
            }
            i += 1;
        };

        let mut j = start;
        let mut end = data.len();
        while j + 3 <= data.len() {
            if data[j] == 0 && data[j + 1] == 0 && data[j + 2] == 1 {
                end = j;
                if end > start && data[end - 1] == 0 {
                    end -= 1;
                }
                break;
            }
            j += 1;
        }
        self.pos = end;
        Some(&data[start..end])
    }
}

pub fn h264_nal_type(first_byte: u8) -> u8 {
    first_byte & 0x1F
}

pub fn h265_nal_type(first_byte: u8) -> u8 {
    (first_byte >> 1) & 0x3F
}

/// Any IDR slice (NAL type 5) in the access unit.
pub fn h264_is_keyframe(annexb: &[u8]) -> bool {
    nal_units(annexb).any(|nal| !nal.is_empty() && h264_nal_type(nal[0]) == 5)
}

/// Any IRAP NAL (BLA..CRA, types 16..=23) in the access unit.
pub fn h265_is_keyframe(annexb: &[u8]) -> bool {
    nal_units(annexb).any(|nal| !nal.is_empty() && (16..=23).contains(&h265_nal_type(nal[0])))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn walks_three_and_four_byte_start_codes() {
        let stream = [
            0, 0, 1, 0x67, 0xAA, // 3-byte start
            0, 0, 0, 1, 0x68, 0xBB, // 4-byte start
            0, 0, 1, 0x65, 0xCC,
        ];
        let nals: Vec<&[u8]> = nal_units(&stream).collect();
        assert_eq!(nals, vec![&[0x67, 0xAA][..], &[0x68, 0xBB], &[0x65, 0xCC]]);
    }

    #[test]
    fn keyframe_detection() {
        let idr = [0u8, 0, 1, 0x09, 0x10, 0, 0, 1, 0x65, 0x88];
        let non_idr = [0u8, 0, 1, 0x41, 0x9A];
        assert!(h264_is_keyframe(&idr));
        assert!(!h264_is_keyframe(&non_idr));
        // H.265 IDR_W_RADL is type 19 -> first byte 19 << 1 = 0x26.
        let irap = [0u8, 0, 1, 0x26, 0x01];
        assert!(h265_is_keyframe(&irap));
        assert!(!h265_is_keyframe(&[0u8, 0, 1, 0x02, 0x01]));
    }

    #[test]
    fn empty_and_garbage_yield_nothing() {
        assert!(nal_units(&[]).next().is_none());
        assert!(nal_units(&[0xFF; 16]).next().is_none());
    }
}
