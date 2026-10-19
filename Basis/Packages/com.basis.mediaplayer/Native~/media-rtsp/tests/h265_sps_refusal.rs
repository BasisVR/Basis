//! An H.265 SPS carrying an Exp-Golomb code too long to read is refused,
//! wherever in the SPS it sits. The SDP's `sprop-sps` reaches this parser
//! at DESCRIBE, for any H.265 stream the server lists. A code the reader
//! gives up on has already consumed its bits, so reading on from there
//! would take every later field from the wrong position.
//!
//! Each case builds one SPS twice: well formed, which must be accepted
//! (so the builder is known to be right), and with the one field replaced
//! by a code of 32 leading zeros, which must not.

use retina::codec::Depacketizer;

/// A real camera's VPS and PPS; only the SPS is built here.
const VPS: &str = "QAEMAf//AWAAAAMAsAAAAwAAAwBarAwAAAMABAAAAwAyqA==";
const PPS: &str = "RAHA8saNA7NA";

#[derive(Default)]
struct Bits {
    bytes: Vec<u8>,
    used: u32,
}

impl Bits {
    fn bit(&mut self, b: bool) {
        if self.used.is_multiple_of(8) {
            self.bytes.push(0);
        }
        if b {
            *self.bytes.last_mut().unwrap() |= 0x80 >> (self.used % 8);
        }
        self.used += 1;
    }

    fn n(&mut self, value: u64, count: u32) {
        for i in (0..count).rev() {
            self.bit(value >> i & 1 == 1);
        }
    }

    fn ue(&mut self, value: u32) {
        let coded = u64::from(value) + 1;
        let len = 64 - coded.leading_zeros();
        self.n(0, len - 1);
        self.n(coded, len);
    }

    /// A code the reader refuses: more leading zeros than a 32-bit value
    /// can have.
    fn ue_too_long(&mut self) {
        self.n(0, 32);
        self.bit(true);
    }

    /// RBSP trailing bits, then emulation prevention, behind an SPS header.
    fn into_sps_nal(mut self) -> Vec<u8> {
        self.bit(true);
        while !self.used.is_multiple_of(8) {
            self.bit(false);
        }
        let mut nal = vec![0x42, 0x01];
        let mut zeros = 0;
        for byte in self.bytes {
            if zeros >= 2 && byte <= 3 {
                nal.push(3);
                zeros = 0;
            }
            zeros = if byte == 0 { zeros + 1 } else { 0 };
            nal.push(byte);
        }
        nal
    }
}

#[derive(Clone, Copy, PartialEq)]
enum Fault {
    None,
    ScalingListCoefficient,
    PaletteMaxSize,
}

/// A 64x64 Main-profile SPS. `scaling` adds explicit scaling-list data,
/// `palette` an SCC extension with palette mode on; `fault` puts the
/// unreadable code into one of them.
fn sps(scaling: bool, palette: bool, fault: Fault) -> Vec<u8> {
    let mut b = Bits::default();
    b.n(0, 4); // sps_video_parameter_set_id
    b.n(0, 3); // sps_max_sub_layers_minus1
    b.bit(true); // sps_temporal_id_nesting_flag
    b.n(0x01, 8); // profile space, tier, Main
    b.n(0x6000_0000, 32); // compatibility flags
    b.n(0b1001, 4); // progressive, frame only
    b.n(0, 44);
    b.n(90, 8); // general_level_idc
    b.ue(0); // sps_seq_parameter_set_id
    b.ue(1); // chroma_format_idc 4:2:0
    b.ue(64);
    b.ue(64);
    b.bit(false); // conformance_window_flag
    b.ue(0);
    b.ue(0); // bit depths
    b.ue(4); // log2_max_pic_order_cnt_lsb_minus4
    b.bit(true); // sps_sub_layer_ordering_info_present_flag
    b.ue(1);
    b.ue(0);
    b.ue(0);
    for v in [0, 1, 0, 1, 1, 1] {
        b.ue(v); // coding and transform block sizes, hierarchy depths
    }
    b.bit(scaling); // scaling_list_enabled_flag
    if scaling {
        b.bit(true); // sps_scaling_list_data_present_flag
        for size_id in 0..4 {
            let matrices = if size_id == 3 { 2 } else { 6 };
            for matrix in 0..matrices {
                if size_id == 0 && matrix == 0 {
                    b.bit(true); // scaling_list_pred_mode_flag
                    if fault == Fault::ScalingListCoefficient {
                        b.ue_too_long();
                    } else {
                        b.ue(0);
                    }
                    for _ in 1..16 {
                        b.ue(0); // scaling_list_delta_coef
                    }
                } else {
                    b.bit(false);
                    b.ue(0); // scaling_list_pred_matrix_id_delta
                }
            }
        }
    }
    b.bit(false); // amp_enabled_flag
    b.bit(false); // sample_adaptive_offset_enabled_flag
    b.bit(false); // pcm_enabled_flag
    b.ue(0); // num_short_term_ref_pic_sets
    b.bit(false); // long_term_ref_pics_present_flag
    b.bit(false); // sps_temporal_mvp_enabled_flag
    b.bit(false); // strong_intra_smoothing_enabled_flag
    b.bit(false); // vui_parameters_present_flag
    b.bit(palette); // sps_extension_flag
    if palette {
        b.n(0b0001, 4); // range, multilayer, 3D, SCC
        b.n(0, 4); // sps_extension_4bits
        b.bit(false); // sps_curr_pic_ref_enabled_flag
        b.bit(true); // palette_mode_enabled_flag
        if fault == Fault::PaletteMaxSize {
            b.ue_too_long();
        } else {
            b.ue(63);
        }
        b.ue(0); // delta_palette_max_predictor_size
        b.bit(false); // sps_palette_predictor_initializers_present_flag
    }
    b.into_sps_nal()
}

fn base64(bytes: &[u8]) -> String {
    const ALPHABET: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut out = String::new();
    for chunk in bytes.chunks(3) {
        let n = chunk
            .iter()
            .enumerate()
            .fold(0u32, |n, (i, &b)| n | u32::from(b) << (16 - 8 * i));
        for i in 0..4 {
            if i <= chunk.len() {
                out.push(ALPHABET[(n >> (18 - 6 * i) & 63) as usize] as char);
            } else {
                out.push('=');
            }
        }
    }
    out
}

fn accepted(sps: &[u8]) -> bool {
    let params = format!(
        "profile-id=1;sprop-vps={VPS};sprop-sps={};sprop-pps={PPS}",
        base64(sps)
    );
    Depacketizer::new("video", "h265", 90_000, None, Some(&params))
        .expect("h265 depacketizer")
        .parameters()
        .is_some()
}

#[test]
fn an_unreadable_scaling_list_coefficient_refuses_the_sps() {
    assert!(accepted(&sps(true, false, Fault::None)));
    assert!(!accepted(&sps(true, false, Fault::ScalingListCoefficient)));
}

#[test]
fn an_unreadable_palette_max_size_refuses_the_sps() {
    assert!(accepted(&sps(false, true, Fault::None)));
    assert!(!accepted(&sps(false, true, Fault::PaletteMaxSize)));
}
