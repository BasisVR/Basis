#![cfg_attr(not(target_os = "linux"), allow(dead_code))]

pub(crate) const NAL_SLICE: u8 = 1;
pub(crate) const NAL_IDR: u8 = 5;
pub(crate) const NAL_SPS: u8 = 7;
pub(crate) const NAL_PPS: u8 = 8;
pub(crate) const SLICE_P: u8 = 0;
pub(crate) const SLICE_I: u8 = 2;
pub(crate) const LOG2_MAX_FRAME_NUM: u32 = 16;
pub(crate) const MAX_REF_FRAMES: u32 = 1;
pub(crate) const PIC_INIT_QP: u8 = 26;
pub(crate) const LOG2_MAX_MV_LENGTH: u32 = 15;
pub(crate) const ASPECT_RATIO_SQUARE: u8 = 1;
pub(crate) const BT709: u32 = 1;
pub(crate) const VIDEO_FORMAT_UNSPECIFIED: u32 = 5;

#[derive(Debug, Default)]
pub(crate) struct BitWriter {
    bytes: Vec<u8>,
    acc: u8,
    used: u32,
}

impl BitWriter {
    pub(crate) fn new() -> Self {
        Self::default()
    }

    pub(crate) fn bit(&mut self, on: bool) {
        self.acc = (self.acc << 1) | u8::from(on);
        self.used += 1;
        if self.used == 8 {
            self.bytes.push(self.acc);
            self.acc = 0;
            self.used = 0;
        }
    }

    pub(crate) fn u(&mut self, bits: u32, value: u32) {
        debug_assert!(bits <= 32);
        for i in (0..bits).rev() {
            self.bit((value >> i) & 1 != 0);
        }
    }

    fn ue_wide(&mut self, value: u64) {
        let coded = value + 1;
        let len = 64 - coded.leading_zeros();
        for _ in 1..len {
            self.bit(false);
        }
        for i in (0..len).rev() {
            self.bit((coded >> i) & 1 != 0);
        }
    }

    pub(crate) fn ue(&mut self, value: u32) {
        self.ue_wide(u64::from(value));
    }

    pub(crate) fn se(&mut self, value: i32) {
        let v = i64::from(value);
        self.ue_wide(if v > 0 {
            (2 * v - 1) as u64
        } else {
            (-2 * v) as u64
        });
    }

    pub(crate) fn bit_len(&self) -> usize {
        self.bytes.len() * 8 + self.used as usize
    }

    pub(crate) fn trailing_bits(&mut self) {
        self.bit(true);
        while self.used != 0 {
            self.bit(false);
        }
    }

    pub(crate) fn into_bytes(mut self) -> Vec<u8> {
        if self.used != 0 {
            self.bytes.push(self.acc << (8 - self.used));
        }
        self.bytes
    }
}

pub(crate) fn escape_into(rbsp: &[u8], out: &mut Vec<u8>) {
    let mut zeros = 0u32;
    for &b in rbsp {
        if zeros >= 2 && b <= 3 {
            out.push(3);
            zeros = 0;
        }
        out.push(b);
        zeros = if b == 0 { zeros + 1 } else { 0 };
    }
}

pub(crate) fn nal(ref_idc: u8, kind: u8, rbsp: &[u8]) -> Vec<u8> {
    let mut out = Vec::with_capacity(rbsp.len() + rbsp.len() / 64 + 8);
    out.extend_from_slice(&[0, 0, 0, 1, ((ref_idc & 3) << 5) | (kind & 0x1f)]);
    escape_into(rbsp, &mut out);
    out
}

pub(crate) fn pack_bits(fields: &[(u32, u32)]) -> u32 {
    let mut value = 0u32;
    let mut shift = 0u32;
    for &(v, width) in fields {
        let mask = if width >= 32 {
            u32::MAX
        } else {
            (1 << width) - 1
        };
        value |= (v & mask) << shift;
        shift += width;
    }
    debug_assert!(shift <= 32);
    value
}

const LEVELS: [(u8, u64, u32, u32); 19] = [
    (10, 1_485, 99, 64),
    (11, 3_000, 396, 192),
    (12, 6_000, 396, 384),
    (13, 11_880, 396, 768),
    (20, 11_880, 396, 2_000),
    (21, 19_800, 792, 4_000),
    (22, 20_250, 1_620, 4_000),
    (30, 40_500, 1_620, 10_000),
    (31, 108_000, 3_600, 14_000),
    (32, 216_000, 5_120, 20_000),
    (40, 245_760, 8_192, 20_000),
    (41, 245_760, 8_192, 50_000),
    (42, 522_240, 8_704, 50_000),
    (50, 589_824, 22_080, 135_000),
    (51, 983_040, 36_864, 240_000),
    (52, 2_073_600, 36_864, 240_000),
    (60, 4_177_920, 139_264, 240_000),
    (61, 8_355_840, 139_264, 480_000),
    (62, 16_711_680, 139_264, 800_000),
];

pub(crate) fn level_idc(width_mbs: u32, height_mbs: u32, fps: u32, kbps: u32) -> u8 {
    let fs = width_mbs * height_mbs;
    let mbps = u64::from(fs) * u64::from(fps.max(1));
    LEVELS
        .iter()
        .find(|&&(_, max_mbps, max_fs, max_br)| {
            fs <= max_fs
                && width_mbs * width_mbs <= 8 * max_fs
                && height_mbs * height_mbs <= 8 * max_fs
                && mbps <= max_mbps
                && kbps <= max_br
        })
        .map_or(62, |l| l.0)
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum Profile {
    ConstrainedBaseline,
    Main,
}

impl Profile {
    pub(crate) fn idc(self) -> u8 {
        match self {
            Self::ConstrainedBaseline => 66,
            Self::Main => 77,
        }
    }

    pub(crate) fn cabac(self) -> bool {
        self == Self::Main
    }

    pub(crate) fn name(self) -> &'static str {
        match self {
            Self::ConstrainedBaseline => "Constrained Baseline",
            Self::Main => "Main",
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) struct Sps {
    pub profile: Profile,
    pub level_idc: u8,
    pub width: u32,
    pub height: u32,
    pub fps: u32,
}

impl Sps {
    pub(crate) fn new(profile: Profile, width: u32, height: u32, fps: u32, kbps: u32) -> Self {
        let fps = fps.max(1);
        let level_idc = level_idc(width.div_ceil(16), height.div_ceil(16), fps, kbps);
        Self {
            profile,
            level_idc,
            width,
            height,
            fps,
        }
    }

    pub(crate) fn width_mbs(&self) -> u32 {
        self.width.div_ceil(16)
    }

    pub(crate) fn height_mbs(&self) -> u32 {
        self.height.div_ceil(16)
    }

    pub(crate) fn crop_right(&self) -> u32 {
        (self.width_mbs() * 16 - self.width) / 2
    }

    pub(crate) fn crop_bottom(&self) -> u32 {
        (self.height_mbs() * 16 - self.height) / 2
    }

    pub(crate) fn cropped(&self) -> bool {
        self.crop_right() != 0 || self.crop_bottom() != 0
    }

    pub(crate) fn num_units_in_tick(&self) -> u32 {
        1
    }

    pub(crate) fn time_scale(&self) -> u32 {
        self.fps * 2
    }

    pub(crate) fn rbsp(&self) -> Vec<u8> {
        let mut w = BitWriter::new();
        w.u(8, u32::from(self.profile.idc()));
        let cb = self.profile == Profile::ConstrainedBaseline;
        w.bit(cb);
        w.bit(cb);
        w.u(4, 0);
        w.u(2, 0);
        w.u(8, u32::from(self.level_idc));
        w.ue(0);
        w.ue(LOG2_MAX_FRAME_NUM - 4);
        w.ue(2);
        w.ue(MAX_REF_FRAMES);
        w.bit(false);
        w.ue(self.width_mbs() - 1);
        w.ue(self.height_mbs() - 1);
        w.bit(true);
        w.bit(true);
        w.bit(self.cropped());
        if self.cropped() {
            w.ue(0);
            w.ue(self.crop_right());
            w.ue(0);
            w.ue(self.crop_bottom());
        }
        w.bit(true);
        w.bit(true);
        w.u(8, u32::from(ASPECT_RATIO_SQUARE));
        w.bit(false);
        w.bit(true);
        w.u(3, VIDEO_FORMAT_UNSPECIFIED);
        w.bit(false);
        w.bit(true);
        w.u(8, BT709);
        w.u(8, BT709);
        w.u(8, BT709);
        w.bit(false);
        w.bit(true);
        w.u(32, self.num_units_in_tick());
        w.u(32, self.time_scale());
        w.bit(false);
        w.bit(false);
        w.bit(false);
        w.bit(false);
        w.bit(true);
        w.bit(true);
        w.ue(0);
        w.ue(0);
        w.ue(LOG2_MAX_MV_LENGTH);
        w.ue(LOG2_MAX_MV_LENGTH);
        w.ue(0);
        w.ue(MAX_REF_FRAMES);
        w.trailing_bits();
        w.into_bytes()
    }

    pub(crate) fn pps_rbsp(&self) -> Vec<u8> {
        let mut w = BitWriter::new();
        w.ue(0);
        w.ue(0);
        w.bit(self.profile.cabac());
        w.bit(false);
        w.ue(0);
        w.ue(0);
        w.ue(0);
        w.bit(false);
        w.u(2, 0);
        w.se(i32::from(PIC_INIT_QP) - 26);
        w.se(0);
        w.se(0);
        w.bit(true);
        w.bit(false);
        w.bit(false);
        w.trailing_bits();
        w.into_bytes()
    }

    pub(crate) fn sps_nal(&self) -> Vec<u8> {
        nal(3, NAL_SPS, &self.rbsp())
    }

    pub(crate) fn pps_nal(&self) -> Vec<u8> {
        nal(3, NAL_PPS, &self.pps_rbsp())
    }

    pub(crate) fn slice_header(&self, frame: &Frame) -> (Vec<u8>, u32) {
        let mut w = BitWriter::new();
        let slice_type = frame.slice_type();
        w.ue(0);
        w.ue(u32::from(slice_type));
        w.ue(0);
        w.u(LOG2_MAX_FRAME_NUM, frame.frame_num);
        if frame.idr {
            w.ue(u32::from(frame.idr_pic_id));
        } else {
            w.bit(false);
            w.bit(false);
        }
        if frame.idr {
            w.bit(false);
            w.bit(false);
        } else {
            w.bit(false);
        }
        if self.profile.cabac() && slice_type != SLICE_I {
            w.ue(0);
        }
        w.se(0);
        w.ue(0);
        w.se(0);
        w.se(0);
        let tail = (8 - (w.bit_len() % 8) as u32) % 8;
        let (ref_idc, kind) = if frame.idr {
            (3, NAL_IDR)
        } else {
            (2, NAL_SLICE)
        };
        let out = nal(ref_idc, kind, &w.into_bytes());
        let bits = out.len() as u32 * 8 - tail;
        (out, bits)
    }

    pub(crate) fn va_seq_fields(&self) -> u32 {
        pack_bits(&[
            (1, 2),
            (1, 1),
            (0, 1),
            (0, 1),
            (1, 1),
            (LOG2_MAX_FRAME_NUM - 4, 4),
            (2, 2),
            (0, 4),
            (0, 1),
        ])
    }

    pub(crate) fn va_vui_fields(&self) -> u32 {
        pack_bits(&[
            (1, 1),
            (1, 1),
            (1, 1),
            (LOG2_MAX_MV_LENGTH, 5),
            (LOG2_MAX_MV_LENGTH, 5),
            (0, 1),
            (0, 1),
            (1, 1),
        ])
    }

    pub(crate) fn va_pic_fields(&self, idr: bool) -> u32 {
        pack_bits(&[
            (u32::from(idr), 1),
            (1, 2),
            (u32::from(self.profile.cabac()), 1),
            (0, 1),
            (0, 2),
            (0, 1),
            (0, 1),
            (1, 1),
            (0, 1),
            (0, 1),
            (0, 1),
        ])
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) struct Frame {
    pub idr: bool,
    pub frame_num: u32,
    pub poc: i32,
    pub idr_pic_id: u16,
}

impl Frame {
    pub(crate) fn slice_type(&self) -> u8 {
        if self.idr { SLICE_I } else { SLICE_P }
    }
}

#[derive(Debug, Clone)]
pub(crate) struct Gop {
    period: u32,
    since_idr: u32,
    idr_pic_id: u16,
    started: bool,
}

impl Gop {
    pub(crate) fn new(period: u32) -> Self {
        Self {
            period: period.max(1),
            since_idr: 0,
            idr_pic_id: 0,
            started: false,
        }
    }

    pub(crate) fn period(&self) -> u32 {
        self.period
    }

    pub(crate) fn restart(&mut self) {
        self.since_idr = self.period;
    }

    pub(crate) fn next(&mut self, force_idr: bool) -> Frame {
        let idr = force_idr
            || !self.started
            || self.since_idr >= self.period
            || self.since_idr >= 1 << 30;
        if idr {
            if self.started {
                self.idr_pic_id = self.idr_pic_id.wrapping_add(1);
            }
            self.started = true;
            self.since_idr = 0;
        }
        let frame = Frame {
            idr,
            frame_num: self.since_idr & ((1 << LOG2_MAX_FRAME_NUM) - 1),
            poc: (self.since_idr * 2) as i32,
            idr_pic_id: self.idr_pic_id,
        };
        self.since_idr += 1;
        frame
    }
}

#[allow(clippy::too_many_arguments)]
pub(crate) fn copy_nv12(
    src: &[u8],
    width: usize,
    height: usize,
    dst: &mut [u8],
    y_offset: usize,
    y_pitch: usize,
    uv_offset: usize,
    uv_pitch: usize,
    fill_width: usize,
    fill_height: usize,
) -> bool {
    let fill_width = fill_width & !1;
    let chroma_rows = fill_height / 2;
    if width < 2
        || height < 2
        || !width.is_multiple_of(2)
        || !height.is_multiple_of(2)
        || src.len() < width * height * 3 / 2
        || fill_width < width
        || fill_height < height
        || y_pitch < fill_width
        || uv_pitch < fill_width
        || y_offset + (fill_height - 1) * y_pitch + fill_width > dst.len()
        || uv_offset + (chroma_rows - 1) * uv_pitch + fill_width > dst.len()
    {
        return false;
    }
    for row in 0..fill_height {
        let line = &src[row.min(height - 1) * width..][..width];
        let out = &mut dst[y_offset + row * y_pitch..][..fill_width];
        out[..width].copy_from_slice(line);
        out[width..].fill(line[width - 1]);
    }
    let chroma = &src[width * height..];
    for row in 0..chroma_rows {
        let line = &chroma[row.min(height / 2 - 1) * width..][..width];
        let out = &mut dst[uv_offset + row * uv_pitch..][..fill_width];
        out[..width].copy_from_slice(line);
        let (u, v) = (line[width - 2], line[width - 1]);
        for pair in out[width..].as_chunks_mut::<2>().0 {
            pair[0] = u;
            pair[1] = v;
        }
    }
    true
}

#[cfg(test)]
mod tests {
    use super::*;

    struct BitReader<'a> {
        data: &'a [u8],
        pos: usize,
    }

    impl<'a> BitReader<'a> {
        fn new(data: &'a [u8]) -> Self {
            Self { data, pos: 0 }
        }

        fn bit(&mut self) -> bool {
            let b = (self.data[self.pos / 8] >> (7 - self.pos % 8)) & 1 != 0;
            self.pos += 1;
            b
        }

        fn u(&mut self, bits: u32) -> u32 {
            (0..bits).fold(0, |acc, _| (acc << 1) | u32::from(self.bit()))
        }

        fn ue_wide(&mut self) -> u64 {
            let mut zeros = 0;
            while !self.bit() {
                zeros += 1;
            }
            (1u64 << zeros) - 1 + u64::from(self.u(zeros))
        }

        fn ue(&mut self) -> u32 {
            self.ue_wide() as u32
        }

        fn se(&mut self) -> i32 {
            let k = self.ue_wide() as i64;
            if k % 2 == 1 {
                ((k + 1) / 2) as i32
            } else {
                (-(k / 2)) as i32
            }
        }

        fn trailing(&mut self) -> bool {
            self.bit() && (self.pos..self.data.len() * 8).all(|_| !self.bit())
        }
    }

    fn unescape(data: &[u8]) -> Vec<u8> {
        let mut out = Vec::new();
        let mut zeros = 0;
        for &b in data {
            if zeros >= 2 && b == 3 {
                zeros = 0;
                continue;
            }
            out.push(b);
            zeros = if b == 0 { zeros + 1 } else { 0 };
        }
        out
    }

    fn bits_of(f: impl FnOnce(&mut BitWriter)) -> String {
        let mut w = BitWriter::new();
        f(&mut w);
        let len = w.bit_len();
        let bytes = w.into_bytes();
        (0..len)
            .map(|i| {
                if (bytes[i / 8] >> (7 - i % 8)) & 1 != 0 {
                    '1'
                } else {
                    '0'
                }
            })
            .collect()
    }

    #[test]
    fn fixed_width_bits() {
        assert_eq!(bits_of(|w| w.u(3, 5)), "101");
        assert_eq!(bits_of(|w| w.u(8, 0x4d)), "01001101");
        assert_eq!(bits_of(|w| w.u(0, 7)), "");
        assert_eq!(
            bits_of(|w| w.u(32, 0x8000_0001)),
            format!("1{}1", "0".repeat(30))
        );
        assert_eq!(bits_of(|w| w.u(16, 0xffff)), "1".repeat(16));
    }

    #[test]
    fn exp_golomb_unsigned() {
        let table = [
            (0, "1"),
            (1, "010"),
            (2, "011"),
            (3, "00100"),
            (4, "00101"),
            (5, "00110"),
            (6, "00111"),
            (7, "0001000"),
            (8, "0001001"),
            (14, "0001111"),
            (15, "000010000"),
            (119, "0000001111000"),
            (255, "00000000100000000"),
        ];
        for (v, bits) in table {
            assert_eq!(bits_of(|w| w.ue(v)), bits, "ue({v})");
        }
        let max = bits_of(|w| w.ue(u32::MAX));
        assert_eq!(max.len(), 65);
        assert_eq!(&max[..33], &format!("{}1", "0".repeat(32)));
        assert!(max[33..].chars().all(|c| c == '0'));
    }

    #[test]
    fn exp_golomb_signed() {
        let table = [
            (0, "1"),
            (1, "010"),
            (-1, "011"),
            (2, "00100"),
            (-2, "00101"),
            (3, "00110"),
            (-3, "00111"),
            (-26, "00000110101"),
        ];
        for (v, bits) in table {
            assert_eq!(bits_of(|w| w.se(v)), bits, "se({v})");
        }
        for v in [i32::MIN, i32::MIN + 1, -1000, 1000, i32::MAX] {
            let mut w = BitWriter::new();
            w.se(v);
            let bytes = w.into_bytes();
            assert_eq!(BitReader::new(&bytes).se(), v);
        }
    }

    #[test]
    fn exp_golomb_roundtrip() {
        let values: Vec<u32> = (0..2000)
            .chain([65_535, 65_536, 1 << 20, u32::MAX - 1])
            .collect();
        let mut w = BitWriter::new();
        for &v in &values {
            w.ue(v);
            w.se(v as i32 / 3 - 300);
        }
        let bytes = w.into_bytes();
        let mut r = BitReader::new(&bytes);
        for &v in &values {
            assert_eq!(r.ue(), v);
            assert_eq!(r.se(), v as i32 / 3 - 300);
        }
    }

    #[test]
    fn trailing_bits_align() {
        assert_eq!(
            {
                let mut w = BitWriter::new();
                w.u(3, 0b101);
                w.trailing_bits();
                assert_eq!(w.bit_len() % 8, 0);
                w.into_bytes()
            },
            vec![0b1011_0000]
        );
        let mut w = BitWriter::new();
        w.u(8, 0xaa);
        w.trailing_bits();
        assert_eq!(w.into_bytes(), vec![0xaa, 0x80]);
        let mut w = BitWriter::new();
        w.u(7, 0);
        w.trailing_bits();
        assert_eq!(w.into_bytes(), vec![0x01]);
        let mut w = BitWriter::new();
        w.u(5, 0b11111);
        assert_eq!(w.bit_len(), 5);
        assert_eq!(w.into_bytes(), vec![0b1111_1000]);
    }

    #[test]
    fn emulation_prevention() {
        let cases: [(&[u8], &[u8]); 9] = [
            (&[0, 0, 0], &[0, 0, 3, 0]),
            (&[0, 0, 1], &[0, 0, 3, 1]),
            (&[0, 0, 2], &[0, 0, 3, 2]),
            (&[0, 0, 3], &[0, 0, 3, 3]),
            (&[0, 0, 4], &[0, 0, 4]),
            (&[0, 0, 0, 0, 0], &[0, 0, 3, 0, 0, 3, 0]),
            (&[0, 0, 3, 0, 0, 1], &[0, 0, 3, 3, 0, 0, 3, 1]),
            (&[1, 0, 2, 0, 0], &[1, 0, 2, 0, 0]),
            (
                &[0xff, 0, 0, 0xff, 0, 0, 0, 1],
                &[0xff, 0, 0, 0xff, 0, 0, 3, 0, 1],
            ),
        ];
        for (raw, escaped) in cases {
            let mut out = Vec::new();
            escape_into(raw, &mut out);
            assert_eq!(out, escaped, "{raw:?}");
            assert_eq!(unescape(&out), raw);
        }
        let mut raw = Vec::new();
        for i in 0..4096u32 {
            raw.push(if i % 7 < 4 { 0 } else { (i % 5) as u8 });
        }
        let mut out = Vec::new();
        escape_into(&raw, &mut out);
        assert_eq!(unescape(&out), raw);
        for window in out.windows(3) {
            assert!(
                !(window[0] == 0 && window[1] == 0 && window[2] <= 2),
                "{window:?}"
            );
        }
        let n = nal(3, NAL_SPS, &[0, 0, 1]);
        assert_eq!(n, vec![0, 0, 0, 1, 0x67, 0, 0, 3, 1]);
        assert_eq!(nal(2, NAL_SLICE, &[9])[4], 0x41);
        assert_eq!(nal(3, NAL_IDR, &[9])[4], 0x65);
        assert_eq!(nal(3, NAL_PPS, &[9])[4], 0x68);
    }

    #[test]
    fn levels() {
        assert_eq!(level_idc(120, 68, 30, 8_000), 40);
        assert_eq!(level_idc(120, 68, 30, 30_000), 41);
        assert_eq!(level_idc(120, 68, 60, 8_000), 42);
        assert_eq!(level_idc(80, 45, 30, 4_000), 31);
        assert_eq!(level_idc(80, 45, 60, 4_000), 32);
        assert_eq!(level_idc(240, 135, 30, 20_000), 51);
        assert_eq!(level_idc(240, 135, 60, 20_000), 52);
        assert_eq!(level_idc(256, 256, 120, 100_000), 61);
        assert_eq!(level_idc(11, 9, 15, 64), 10);
        assert_eq!(level_idc(22, 18, 30, 500), 13);
        assert_eq!(level_idc(256, 4, 1, 100), 40);
        assert_eq!(level_idc(1000, 1000, 120, 1), 62);
    }

    fn sps_1080p_main() -> Sps {
        Sps::new(Profile::Main, 1920, 1080, 30, 8_000)
    }

    #[test]
    fn known_1080p_main_sps() {
        let sps = sps_1080p_main();
        assert_eq!(sps.level_idc, 40);
        assert_eq!((sps.width_mbs(), sps.height_mbs()), (120, 68));
        assert_eq!((sps.crop_right(), sps.crop_bottom()), (0, 4));
        assert_eq!(
            sps.rbsp(),
            vec![
                0x4d, 0x00, 0x28, 0x8d, 0x68, 0x07, 0x80, 0x22, 0x7e, 0x5c, 0x05, 0xa8, 0x08, 0x08,
                0x0a, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x78, 0x1e, 0x10, 0x08, 0x54,
            ]
        );
        assert_eq!(
            sps.sps_nal(),
            vec![
                0x00, 0x00, 0x00, 0x01, 0x67, 0x4d, 0x00, 0x28, 0x8d, 0x68, 0x07, 0x80, 0x22, 0x7e,
                0x5c, 0x05, 0xa8, 0x08, 0x08, 0x0a, 0x00, 0x00, 0x03, 0x00, 0x02, 0x00, 0x00, 0x03,
                0x00, 0x78, 0x1e, 0x10, 0x08, 0x54,
            ]
        );
        assert!(crate::has_parameter_sets(&sps.sps_nal()));
    }

    #[test]
    fn known_pps() {
        assert_eq!(
            sps_1080p_main().pps_nal(),
            vec![0, 0, 0, 1, 0x68, 0xee, 0x3c, 0x80]
        );
        let cb = Sps::new(Profile::ConstrainedBaseline, 1280, 720, 30, 3_000);
        assert_eq!(cb.pps_nal(), vec![0, 0, 0, 1, 0x68, 0xce, 0x3c, 0x80]);
    }

    struct ParsedSps {
        profile_idc: u32,
        constraints: u32,
        level_idc: u32,
        log2_max_frame_num: u32,
        poc_type: u32,
        max_num_ref_frames: u32,
        width: u32,
        height: u32,
        crop: [u32; 4],
        colour: [u32; 3],
        full_range: bool,
        time_scale: u32,
        num_units_in_tick: u32,
        max_dec_frame_buffering: u32,
        num_reorder_frames: u32,
    }

    fn parse_sps(nal_unit: &[u8]) -> ParsedSps {
        assert_eq!(&nal_unit[..5], &[0, 0, 0, 1, 0x67]);
        let rbsp = unescape(&nal_unit[5..]);
        let mut r = BitReader::new(&rbsp);
        let profile_idc = r.u(8);
        let constraints = r.u(8);
        let level_idc = r.u(8);
        assert_eq!(r.ue(), 0);
        let log2_max_frame_num = r.ue() + 4;
        let poc_type = r.ue();
        assert_eq!(poc_type, 2);
        let max_num_ref_frames = r.ue();
        assert!(!r.bit());
        let w_mbs = r.ue() + 1;
        let h_mbs = r.ue() + 1;
        assert!(r.bit());
        assert!(r.bit());
        let mut crop = [0; 4];
        if r.bit() {
            for c in &mut crop {
                *c = r.ue();
            }
        }
        assert!(r.bit());
        assert!(r.bit());
        assert_eq!(r.u(8), 1);
        assert!(!r.bit());
        assert!(r.bit());
        assert_eq!(r.u(3), 5);
        let full_range = r.bit();
        assert!(r.bit());
        let colour = [r.u(8), r.u(8), r.u(8)];
        assert!(!r.bit());
        assert!(r.bit());
        let num_units_in_tick = r.u(32);
        let time_scale = r.u(32);
        assert!(!r.bit());
        assert!(!r.bit());
        assert!(!r.bit());
        assert!(!r.bit());
        assert!(r.bit());
        assert!(r.bit());
        assert_eq!(r.ue(), 0);
        assert_eq!(r.ue(), 0);
        assert_eq!(r.ue(), 15);
        assert_eq!(r.ue(), 15);
        let num_reorder_frames = r.ue();
        let max_dec_frame_buffering = r.ue();
        assert!(r.trailing());
        ParsedSps {
            profile_idc,
            constraints,
            level_idc,
            log2_max_frame_num,
            poc_type,
            max_num_ref_frames,
            width: w_mbs * 16 - 2 * (crop[0] + crop[1]),
            height: h_mbs * 16 - 2 * (crop[2] + crop[3]),
            crop,
            colour,
            full_range,
            time_scale,
            num_units_in_tick,
            max_dec_frame_buffering,
            num_reorder_frames,
        }
    }

    #[test]
    fn sps_roundtrip_and_cropping() {
        for (w, h, crop) in [
            (1920, 1080, [0, 0, 0, 4]),
            (1280, 720, [0, 0, 0, 0]),
            (1366, 768, [0, 5, 0, 0]),
            (1002, 998, [0, 3, 0, 5]),
            (2, 2, [0, 7, 0, 7]),
            (3440, 1440, [0, 0, 0, 0]),
            (4096, 2160, [0, 0, 0, 0]),
            (1918, 1078, [0, 1, 0, 5]),
        ] {
            for profile in [Profile::Main, Profile::ConstrainedBaseline] {
                let sps = Sps::new(profile, w, h, 60, 6_000);
                let p = parse_sps(&sps.sps_nal());
                assert_eq!((p.width, p.height), (w, h), "{w}x{h}");
                assert_eq!(p.crop, crop, "{w}x{h}");
                assert_eq!(sps.cropped(), crop != [0; 4]);
                assert_eq!(p.profile_idc, u32::from(profile.idc()));
                assert_eq!(
                    p.constraints,
                    if profile == Profile::ConstrainedBaseline {
                        0xc0
                    } else {
                        0
                    }
                );
                assert_eq!(p.level_idc, u32::from(sps.level_idc));
                assert_eq!(p.log2_max_frame_num, LOG2_MAX_FRAME_NUM);
                assert_eq!(p.poc_type, 2);
                assert_eq!(p.max_num_ref_frames, 1);
                assert_eq!(p.colour, [1, 1, 1]);
                assert!(!p.full_range);
                assert_eq!((p.num_units_in_tick, p.time_scale), (1, 120));
                assert_eq!((p.num_reorder_frames, p.max_dec_frame_buffering), (0, 1));
            }
        }
    }

    fn parse_slice(sps: &Sps, data: &[u8], bits: u32) -> (u8, u32, Option<u32>, usize) {
        assert_eq!(&data[..4], &[0, 0, 0, 1]);
        let header = data[4];
        let rbsp = unescape(&data[5..]);
        let mut r = BitReader::new(&rbsp);
        assert_eq!(r.ue(), 0);
        let slice_type = r.ue() as u8;
        assert_eq!(r.ue(), 0);
        let frame_num = r.u(LOG2_MAX_FRAME_NUM);
        let idr = header & 0x1f == NAL_IDR;
        let idr_pic_id = idr.then(|| r.ue());
        if slice_type == SLICE_P {
            assert!(!r.bit());
            assert!(!r.bit());
        }
        if idr {
            assert!(!r.bit());
            assert!(!r.bit());
        } else {
            assert!(!r.bit());
        }
        if sps.profile.cabac() && slice_type != SLICE_I {
            assert_eq!(r.ue(), 0);
        }
        assert_eq!(r.se(), 0);
        assert_eq!(r.ue(), 0);
        assert_eq!(r.se(), 0);
        assert_eq!(r.se(), 0);
        assert_eq!(
            rbsp.len(),
            data.len() - 5,
            "no escapes expected in these headers"
        );
        assert_eq!(r.pos + 40, bits as usize);
        (slice_type, frame_num, idr_pic_id, r.pos)
    }

    #[test]
    fn slice_headers() {
        let sps = sps_1080p_main();
        let idr = Frame {
            idr: true,
            frame_num: 0,
            poc: 0,
            idr_pic_id: 0,
        };
        let (data, bits) = sps.slice_header(&idr);
        assert_eq!(data, vec![0, 0, 0, 1, 0x65, 0xb8, 0x00, 0x04, 0xf0]);
        assert_eq!(bits, 32 + 8 + 28);
        assert_eq!(parse_slice(&sps, &data, bits), (SLICE_I, 0, Some(0), 28));
        let p = Frame {
            idr: false,
            frame_num: 7,
            poc: 14,
            idr_pic_id: 0,
        };
        let (data, bits) = sps.slice_header(&p);
        assert_eq!(data[4], 0x41);
        assert_eq!(parse_slice(&sps, &data, bits).0, SLICE_P);
        assert_eq!(parse_slice(&sps, &data, bits).1, 7);
        let cb = Sps::new(Profile::ConstrainedBaseline, 1280, 720, 30, 3_000);
        let (data, bits) = cb.slice_header(&p);
        assert_eq!(parse_slice(&cb, &data, bits).3, 3 + 16 + 3 + 4);
        let (data, bits) = cb.slice_header(&Frame {
            idr: true,
            frame_num: 0,
            poc: 0,
            idr_pic_id: 300,
        });
        assert_eq!(parse_slice(&cb, &data, bits).2, Some(300));
    }

    #[test]
    fn gop_sequence() {
        let mut gop = Gop::new(4);
        let frames: Vec<Frame> = (0..9).map(|i| gop.next(i == 6)).collect();
        let idr: Vec<bool> = frames.iter().map(|f| f.idr).collect();
        assert_eq!(
            idr,
            [true, false, false, false, true, false, true, false, false]
        );
        let nums: Vec<u32> = frames.iter().map(|f| f.frame_num).collect();
        assert_eq!(nums, [0, 1, 2, 3, 0, 1, 0, 1, 2]);
        let pocs: Vec<i32> = frames.iter().map(|f| f.poc).collect();
        assert_eq!(pocs, [0, 2, 4, 6, 0, 2, 0, 2, 4]);
        let ids: Vec<u16> = frames.iter().map(|f| f.idr_pic_id).collect();
        assert_eq!(ids, [0, 0, 0, 0, 1, 1, 2, 2, 2]);
        gop.restart();
        assert!(gop.next(false).idr);
        let mut gop = Gop::new(0);
        assert_eq!(gop.period(), 1);
        assert!(gop.next(false).idr && gop.next(false).idr);
        let mut long = Gop::new(u32::MAX);
        long.next(false);
        for _ in 0..70_000 {
            let f = long.next(false);
            assert!(!f.idr);
            assert!(f.frame_num < 1 << LOG2_MAX_FRAME_NUM);
        }
        let f = long.next(false);
        assert_eq!(f.frame_num, 70_001 % 65_536);
        assert_eq!(f.poc, 140_002);
    }

    #[test]
    fn va_bitfields_match_libva() {
        assert_eq!(
            pack_bits(&[
                (1, 2),
                (1, 1),
                (0, 1),
                (0, 1),
                (1, 1),
                (12, 4),
                (2, 2),
                (0, 4),
                (0, 1)
            ]),
            0xb25
        );
        assert_eq!(
            pack_bits(&[
                (0, 2),
                (0, 1),
                (1, 1),
                (1, 1),
                (0, 1),
                (0, 4),
                (0, 2),
                (9, 4),
                (1, 1)
            ]),
            0x19018
        );
        assert_eq!(
            pack_bits(&[
                (0, 1),
                (0, 1),
                (0, 1),
                (0, 5),
                (3, 5),
                (1, 1),
                (1, 1),
                (0, 1)
            ]),
            0x6300
        );
        assert_eq!(
            pack_bits(&[
                (0, 1),
                (0, 2),
                (0, 1),
                (1, 1),
                (2, 2),
                (1, 1),
                (1, 1),
                (0, 1),
                (1, 1),
                (1, 1),
                (1, 1)
            ]),
            0x1dd0
        );
        assert_eq!(pack_bits(&[(1, 1)]), 0x1);
        assert_eq!(
            pack_bits(&[
                (0, 1),
                (1, 1),
                (0, 1),
                (2, 4),
                (0, 8),
                (0, 1),
                (0, 1),
                (0, 1),
                (1, 2)
            ]),
            0x40012
        );
        assert_eq!(pack_bits(&[(0xff, 4), (u32::MAX, 28)]), u32::MAX);
        assert_eq!(pack_bits(&[(0xff, 4), (0, 4)]), 0xf);
        let sps = sps_1080p_main();
        assert_eq!(sps.va_seq_fields(), 0xb25);
        assert_eq!(sps.va_vui_fields(), 0x8f7f);
        assert_eq!(sps.va_pic_fields(true), 0x20b);
        assert_eq!(sps.va_pic_fields(false), 0x20a);
        let cb = Sps::new(Profile::ConstrainedBaseline, 640, 480, 30, 1_000);
        assert_eq!(cb.va_pic_fields(false), 0x202);
        assert_eq!(cb.va_pic_fields(true), 0x203);
    }

    #[test]
    fn nv12_copy_pads_edges() {
        let (w, h) = (4usize, 2usize);
        let src: Vec<u8> = (0..(w * h * 3 / 2) as u8).collect();
        let (fill_w, fill_h, pitch) = (8usize, 4usize, 10usize);
        let uv_offset = pitch * fill_h + 3;
        let mut dst = vec![0xeeu8; uv_offset + pitch * (fill_h / 2)];
        assert!(copy_nv12(
            &src, w, h, &mut dst, 0, pitch, uv_offset, pitch, fill_w, fill_h
        ));
        assert_eq!(&dst[..8], &[0, 1, 2, 3, 3, 3, 3, 3]);
        assert_eq!(&dst[8..10], &[0xee, 0xee]);
        assert_eq!(&dst[pitch..pitch + 8], &[4, 5, 6, 7, 7, 7, 7, 7]);
        assert_eq!(&dst[2 * pitch..2 * pitch + 8], &[4, 5, 6, 7, 7, 7, 7, 7]);
        assert_eq!(&dst[3 * pitch..3 * pitch + 8], &[4, 5, 6, 7, 7, 7, 7, 7]);
        assert_eq!(
            &dst[uv_offset..uv_offset + 8],
            &[8, 9, 10, 11, 10, 11, 10, 11]
        );
        assert_eq!(
            &dst[uv_offset + pitch..uv_offset + pitch + 8],
            &[8, 9, 10, 11, 10, 11, 10, 11]
        );
        assert!(!copy_nv12(
            &src, w, h, &mut dst, 0, 6, uv_offset, pitch, fill_w, fill_h
        ));
        assert!(!copy_nv12(
            &src[..5],
            w,
            h,
            &mut dst,
            0,
            pitch,
            uv_offset,
            pitch,
            fill_w,
            fill_h
        ));
        assert!(!copy_nv12(
            &src,
            w,
            h,
            &mut dst[..uv_offset + pitch],
            0,
            pitch,
            uv_offset,
            pitch,
            fill_w,
            fill_h
        ));
        assert!(!copy_nv12(
            &src, w, h, &mut dst, 0, pitch, uv_offset, pitch, 2, fill_h
        ));
        let mut exact = vec![0u8; w * h * 3 / 2];
        assert!(copy_nv12(&src, w, h, &mut exact, 0, w, w * h, w, w, h));
        assert_eq!(exact, src);
    }
}
