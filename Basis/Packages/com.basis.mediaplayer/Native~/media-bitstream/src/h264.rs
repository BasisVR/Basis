//! H.264 SPS reading (exp-Golomb walk): dimensions, and whether the stream
//! can reorder frames.

/// Bit reader that freezes past end-of-data instead of erroring: a
/// truncated SPS reads zeros until the dimension range check rejects it.
/// `overrun` records that it happened, for readers with no range to check.
struct BitReader<'a> {
    data: &'a [u8],
    bitpos: usize,
    overrun: bool,
}

impl BitReader<'_> {
    fn u(&mut self, n: u32) -> u32 {
        let mut v = 0u32;
        for _ in 0..n {
            let byte = self.bitpos >> 3;
            let bit = 7 - (self.bitpos & 7);
            let b = if byte < self.data.len() {
                (self.data[byte] >> bit) & 1
            } else {
                0
            };
            v = (v << 1) | u32::from(b);
            if byte < self.data.len() {
                // Freeze past the end; never overflow bitpos.
                self.bitpos += 1;
            } else {
                self.overrun = true;
            }
        }
        v
    }

    fn ue(&mut self) -> u32 {
        let mut zeros = 0u32;
        // Cap at 31: a ue(v) with >=31 leading zeros is malformed, and a
        // 32-bit shift would overflow.
        while self.u(1) == 0 && zeros < 31 {
            zeros += 1;
        }
        (1u32 << zeros) - 1 + self.u(zeros)
    }

    fn se(&mut self) -> i32 {
        let ue = self.ue();
        if ue & 1 == 1 {
            ((ue + 1) >> 1) as i32
        } else {
            -((ue >> 1) as i32)
        }
    }
}

/// The SPS fields up to the frame cropping, which every reader here needs.
struct Header {
    profile_idc: u32,
    scaling_matrix: bool,
    width: i64,
    height: i64,
}

/// The RBSP of an SPS NAL (header byte included): the header byte and
/// emulation-prevention 0x03 bytes stripped, capped at 512 bytes, which is
/// plenty for the whole SPS including its VUI. `None` when too short to be
/// one.
fn sps_rbsp(sps: &[u8]) -> Option<Vec<u8>> {
    if sps.len() < 4 {
        return None;
    }
    let mut rbsp = Vec::with_capacity(512.min(sps.len()));
    let mut zeros = 0u32;
    for &b in &sps[1..] {
        if rbsp.len() >= 512 {
            break;
        }
        if zeros >= 2 && b == 0x03 {
            zeros = 0;
            continue;
        }
        rbsp.push(b);
        zeros = if b == 0 { zeros + 1 } else { 0 };
    }
    Some(rbsp)
}

fn read_header(g: &mut BitReader<'_>) -> Option<Header> {
    let profile_idc = g.u(8);
    g.u(8); // constraint flags + reserved
    g.u(8); // level_idc
    g.ue(); // seq_parameter_set_id
    let mut scaling_matrix = false;
    if matches!(
        profile_idc,
        100 | 110 | 122 | 244 | 44 | 83 | 86 | 118 | 128
    ) {
        let chroma = g.ue();
        if chroma == 3 {
            g.u(1);
        }
        g.ue();
        g.ue();
        g.u(1);
        // Scaling lists are not walked (uncommon in an SPS); the reader is
        // left before them, so nothing after this point can be trusted.
        scaling_matrix = g.u(1) == 1;
    }
    g.ue(); // log2_max_frame_num_minus4
    let poc_type = g.ue();
    if poc_type == 0 {
        g.ue();
    } else if poc_type == 1 {
        g.u(1);
        g.se();
        g.se();
        let n = g.ue();
        if n > 255 {
            return None; // malformed; H.264 caps this cycle at 255
        }
        for _ in 0..n {
            g.se();
        }
    }
    g.ue(); // max_num_ref_frames
    g.u(1); // gaps_in_frame_num_value_allowed
    let width_mbs = i64::from(g.ue()) + 1;
    let height_map_units = i64::from(g.ue()) + 1;
    let frame_mbs_only = i64::from(g.u(1));
    if frame_mbs_only == 0 {
        g.u(1);
    }
    g.u(1); // direct_8x8_inference
    let (mut cl, mut cr, mut ct, mut cb) = (0i64, 0i64, 0i64, 0i64);
    if g.u(1) == 1 {
        cl = i64::from(g.ue());
        cr = i64::from(g.ue());
        ct = i64::from(g.ue());
        cb = i64::from(g.ue());
    }

    // Crop and MB counts are attacker-controlled ue(v); compute in i64 so a
    // malformed SPS overflows nothing before the range check rejects it.
    Some(Header {
        profile_idc,
        scaling_matrix,
        width: width_mbs * 16 - (cl + cr) * 2,
        height: height_map_units * 16 * (2 - frame_mbs_only) - (ct + cb) * 2,
    })
}

/// Width and height from an SPS NAL (header byte included), or `None` when
/// the SPS is malformed or the result lands outside (0, 8192].
pub fn sps_dimensions(sps: &[u8]) -> Option<(u32, u32)> {
    let rbsp = sps_rbsp(sps)?;
    let mut g = BitReader {
        data: &rbsp,
        bitpos: 0,
        overrun: false,
    };
    let Header { width, height, .. } = read_header(&mut g)?;
    if width <= 0 || height <= 0 || width > 8192 || height > 8192 {
        return None;
    }
    Some((width as u32, height as u32))
}

/// Whether an SPS NAL (header byte included) rules out frame reordering, so
/// pictures leave the decoder in the order they arrive. Baseline has no B
/// slices; any other profile has to say so with a VUI
/// `max_num_reorder_frames` of 0. Without that field the decoder may reorder
/// up to its whole picture buffer (H.264 E.2.1), so the answer is `false`.
/// `None` when the SPS is truncated or malformed, or carries scaling lists,
/// which are not walked.
pub fn sps_reorder_free(sps: &[u8]) -> Option<bool> {
    let rbsp = sps_rbsp(sps)?;
    let mut g = BitReader {
        data: &rbsp,
        bitpos: 0,
        overrun: false,
    };
    let header = read_header(&mut g)?;
    if g.overrun {
        return None;
    }
    if header.profile_idc == 66 {
        return Some(true);
    }
    if header.scaling_matrix {
        return None;
    }
    if g.u(1) == 0 {
        // No VUI at all.
        return (!g.overrun).then_some(false);
    }
    if g.u(1) == 1 && g.u(8) == 255 {
        // aspect_ratio_idc Extended_SAR: sar_width, sar_height.
        g.u(16);
        g.u(16);
    }
    if g.u(1) == 1 {
        g.u(1); // overscan_appropriate
    }
    if g.u(1) == 1 {
        g.u(3); // video_format
        g.u(1); // video_full_range
        if g.u(1) == 1 {
            g.u(24); // colour primaries, transfer, matrix
        }
    }
    if g.u(1) == 1 {
        g.ue(); // chroma_sample_loc_type_top_field
        g.ue(); // chroma_sample_loc_type_bottom_field
    }
    if g.u(1) == 1 {
        g.u(32); // num_units_in_tick
        g.u(32); // time_scale
        g.u(1); // fixed_frame_rate
    }
    let nal_hrd = g.u(1) == 1;
    if nal_hrd {
        skip_hrd(&mut g)?;
    }
    let vcl_hrd = g.u(1) == 1;
    if vcl_hrd {
        skip_hrd(&mut g)?;
    }
    if nal_hrd || vcl_hrd {
        g.u(1); // low_delay_hrd
    }
    g.u(1); // pic_struct_present
    let reorder = if g.u(1) == 1 {
        g.u(1); // motion_vectors_over_pic_boundaries
        g.ue(); // max_bytes_per_pic_denom
        g.ue(); // max_bits_per_mb_denom
        g.ue(); // log2_max_mv_length_horizontal
        g.ue(); // log2_max_mv_length_vertical
        Some(g.ue())
    } else {
        None
    };
    if g.overrun {
        return None;
    }
    Some(reorder == Some(0))
}

/// Step over hrd_parameters (H.264 E.1.2).
fn skip_hrd(g: &mut BitReader<'_>) -> Option<()> {
    let cpb_count = g.ue();
    if cpb_count > 31 {
        return None;
    }
    g.u(4); // bit_rate_scale
    g.u(4); // cpb_size_scale
    for _ in 0..=cpb_count {
        g.ue(); // bit_rate_value_minus1
        g.ue(); // cpb_size_value_minus1
        g.u(1); // cbr
    }
    g.u(20); // four 5-bit delay and offset lengths
    Some(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_the_fixture_sps() {
        // The repo's 640x360 fixture's SPS (High profile, with cropping and
        // emulation-prevention bytes in the VUI).
        let sps = [
            0x67, 0x64, 0x00, 0x1e, 0xac, 0xd9, 0x40, 0xa0, 0x2f, 0xf9, 0x70, 0x11, 0x00, 0x00,
            0x03, 0x00, 0x01, 0x00, 0x00, 0x03, 0x00, 0x3c, 0x0f, 0x16, 0x2d, 0x96,
        ];
        assert_eq!(sps_dimensions(&sps), Some((640, 360)));
    }

    // SPSs written by ffmpeg's libx264 from testsrc2 at 640x360. The expected
    // reorder depth is ffmpeg's own `trace_headers` reading of each, not
    // this parser's.
    const BASELINE: [u8; 25] = [
        0x67, 0x42, 0xc0, 0x1e, 0xd9, 0x00, 0xa0, 0x2f, 0xf9, 0x70, 0x11, 0x00, 0x00, 0x03, 0x00,
        0x01, 0x00, 0x00, 0x03, 0x00, 0x3c, 0x0f, 0x16, 0x2e, 0x48,
    ];
    // High, `-bf 0`: max_num_reorder_frames 0.
    const HIGH_NO_B: [u8; 26] = [
        0x67, 0x64, 0x00, 0x1e, 0xac, 0xb2, 0x01, 0x40, 0x5f, 0xf2, 0xe0, 0x22, 0x00, 0x00, 0x03,
        0x00, 0x02, 0x00, 0x00, 0x03, 0x00, 0x78, 0x1e, 0x2c, 0x5c, 0x90,
    ];
    // High, `-bf 2`: max_num_reorder_frames 2. Also the 640x360 fixture's SPS.
    const HIGH_B: [u8; 26] = [
        0x67, 0x64, 0x00, 0x1e, 0xac, 0xd9, 0x40, 0xa0, 0x2f, 0xf9, 0x70, 0x11, 0x00, 0x00, 0x03,
        0x00, 0x01, 0x00, 0x00, 0x03, 0x00, 0x3c, 0x0f, 0x16, 0x2d, 0x96,
    ];
    // Main, `-bf 3`: max_num_reorder_frames 2.
    const MAIN_B: [u8; 24] = [
        0x67, 0x4d, 0x40, 0x1e, 0xec, 0xa0, 0x50, 0x17, 0xfc, 0xb8, 0x08, 0x80, 0x00, 0x00, 0x03,
        0x00, 0x80, 0x00, 0x00, 0x1e, 0x07, 0x8b, 0x16, 0xcb,
    ];

    #[test]
    fn baseline_and_a_zero_reorder_depth_rule_out_reordering() {
        assert_eq!(sps_reorder_free(&BASELINE), Some(true));
        assert_eq!(sps_reorder_free(&HIGH_NO_B), Some(true));
    }

    #[test]
    fn a_stream_with_b_frames_can_reorder() {
        assert_eq!(sps_reorder_free(&HIGH_B), Some(false));
        assert_eq!(sps_reorder_free(&MAIN_B), Some(false));
    }

    #[test]
    fn a_truncated_vui_says_nothing() {
        // Cut inside the VUI, before max_num_reorder_frames: the frozen
        // reader's zeros must not pass for an answer.
        assert_eq!(sps_reorder_free(&HIGH_NO_B[..20]), None);
        assert_eq!(sps_reorder_free(&[0x67, 0x64, 0x00]), None);
    }

    #[test]
    fn the_new_walk_still_reads_the_dimensions() {
        for sps in [&BASELINE[..], &HIGH_NO_B, &HIGH_B, &MAIN_B] {
            assert_eq!(sps_dimensions(sps), Some((640, 360)));
        }
    }

    #[test]
    fn rejects_truncated() {
        assert_eq!(sps_dimensions(&[]), None);
        assert_eq!(sps_dimensions(&[0x67, 0xFF]), None);
        // A truncated High-profile SPS freezes the bit reader at zero and
        // fails the dimension range check instead of overrunning.
        assert_eq!(sps_dimensions(&[0x67, 0x64, 0x00]), None);
    }
}
