#![cfg_attr(not(target_os = "linux"), allow(dead_code))]

pub(crate) const TYPE_NONE: u32 = 1;
pub(crate) const TYPE_BOOL: u32 = 2;
pub(crate) const TYPE_ID: u32 = 3;
pub(crate) const TYPE_INT: u32 = 4;
pub(crate) const TYPE_LONG: u32 = 5;
pub(crate) const TYPE_RECTANGLE: u32 = 10;
pub(crate) const TYPE_FRACTION: u32 = 11;
pub(crate) const TYPE_ARRAY: u32 = 13;
pub(crate) const TYPE_OBJECT: u32 = 15;
pub(crate) const TYPE_CHOICE: u32 = 19;

pub(crate) const OBJECT_FORMAT: u32 = 0x40003;
pub(crate) const OBJECT_PARAM_BUFFERS: u32 = 0x40004;

pub(crate) const PARAM_ENUM_FORMAT: u32 = 3;
pub(crate) const PARAM_FORMAT: u32 = 4;
pub(crate) const PARAM_BUFFERS: u32 = 5;

pub(crate) const FORMAT_MEDIA_TYPE: u32 = 1;
pub(crate) const FORMAT_MEDIA_SUBTYPE: u32 = 2;
pub(crate) const FORMAT_AUDIO_FORMAT: u32 = 0x10001;
pub(crate) const FORMAT_AUDIO_RATE: u32 = 0x10003;
pub(crate) const FORMAT_AUDIO_CHANNELS: u32 = 0x10004;
pub(crate) const FORMAT_AUDIO_POSITION: u32 = 0x10005;
pub(crate) const FORMAT_VIDEO_FORMAT: u32 = 0x20001;
pub(crate) const FORMAT_VIDEO_MODIFIER: u32 = 0x20002;
pub(crate) const FORMAT_VIDEO_SIZE: u32 = 0x20003;
pub(crate) const FORMAT_VIDEO_FRAMERATE: u32 = 0x20004;

pub(crate) const MEDIA_TYPE_AUDIO: u32 = 1;
pub(crate) const MEDIA_TYPE_VIDEO: u32 = 2;
pub(crate) const MEDIA_SUBTYPE_RAW: u32 = 1;

pub(crate) const VIDEO_FORMAT_RGBX: u32 = 7;
pub(crate) const VIDEO_FORMAT_BGRX: u32 = 8;
pub(crate) const VIDEO_FORMAT_RGBA: u32 = 11;
pub(crate) const VIDEO_FORMAT_BGRA: u32 = 12;

#[cfg(target_endian = "little")]
pub(crate) const AUDIO_FORMAT_F32: u32 = 0x11b;
#[cfg(target_endian = "big")]
pub(crate) const AUDIO_FORMAT_F32: u32 = 0x11c;
pub(crate) const AUDIO_CHANNEL_FL: u32 = 3;
pub(crate) const AUDIO_CHANNEL_FR: u32 = 4;

pub(crate) const BUFFERS_DATA_TYPE: u32 = 6;
pub(crate) const DATA_MEM_PTR: u32 = 1;
pub(crate) const DATA_MEM_FD: u32 = 2;
pub(crate) const CHUNK_FLAG_CORRUPTED: i32 = 1;

pub(crate) const MAX_VIDEO_DIMENSION: u32 = 8192;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum ChoiceKind {
    None,
    Range,
    Step,
    Enum,
    Flags,
}

impl ChoiceKind {
    fn raw(self) -> u32 {
        match self {
            ChoiceKind::None => 0,
            ChoiceKind::Range => 1,
            ChoiceKind::Step => 2,
            ChoiceKind::Enum => 3,
            ChoiceKind::Flags => 4,
        }
    }

    fn from_raw(raw: u32) -> Option<Self> {
        Some(match raw {
            0 => ChoiceKind::None,
            1 => ChoiceKind::Range,
            2 => ChoiceKind::Step,
            3 => ChoiceKind::Enum,
            4 => ChoiceKind::Flags,
            _ => return None,
        })
    }
}

#[derive(Clone, Debug, PartialEq)]
pub(crate) enum Pod {
    None,
    Bool(bool),
    Id(u32),
    Int(i32),
    Long(i64),
    Rectangle(u32, u32),
    Fraction(u32, u32),
    Array(u32, Vec<Pod>),
    Choice(ChoiceKind, u32, Vec<Pod>),
    Object(Object),
    Other(u32, Vec<u8>),
}

#[derive(Clone, Debug, PartialEq)]
pub(crate) struct Object {
    pub type_: u32,
    pub id: u32,
    pub props: Vec<Prop>,
}

#[derive(Clone, Debug, PartialEq)]
pub(crate) struct Prop {
    pub key: u32,
    pub flags: u32,
    pub value: Pod,
}

pub(crate) fn prop(key: u32, value: Pod) -> Prop {
    Prop {
        key,
        flags: 0,
        value,
    }
}

fn round_up(n: usize) -> usize {
    n.div_ceil(8) * 8
}

fn put(out: &mut Vec<u8>, v: u32) {
    out.extend_from_slice(&v.to_ne_bytes());
}

fn pad(out: &mut Vec<u8>) {
    out.resize(round_up(out.len()), 0);
}

fn u32_at(bytes: &[u8], at: usize) -> Option<u32> {
    let b = bytes.get(at..at.checked_add(4)?)?;
    Some(u32::from_ne_bytes([b[0], b[1], b[2], b[3]]))
}

fn i64_at(bytes: &[u8], at: usize) -> Option<i64> {
    let b = bytes.get(at..at.checked_add(8)?)?;
    let mut raw = [0u8; 8];
    raw.copy_from_slice(b);
    Some(i64::from_ne_bytes(raw))
}

impl Pod {
    pub(crate) fn type_id(&self) -> u32 {
        match self {
            Pod::None => TYPE_NONE,
            Pod::Bool(_) => TYPE_BOOL,
            Pod::Id(_) => TYPE_ID,
            Pod::Int(_) => TYPE_INT,
            Pod::Long(_) => TYPE_LONG,
            Pod::Rectangle(..) => TYPE_RECTANGLE,
            Pod::Fraction(..) => TYPE_FRACTION,
            Pod::Array(..) => TYPE_ARRAY,
            Pod::Choice(..) => TYPE_CHOICE,
            Pod::Object(_) => TYPE_OBJECT,
            Pod::Other(t, _) => *t,
        }
    }

    fn write_children(out: &mut Vec<u8>, hint: u32, values: &[Pod]) {
        let mut bodies = Vec::with_capacity(values.len());
        for value in values {
            let mut body = Vec::new();
            value.write_body(&mut body);
            bodies.push(body);
        }
        let child_size = bodies.first().map_or(0, Vec::len);
        let child_type = values.first().map_or(hint, Pod::type_id);
        debug_assert!(
            values
                .iter()
                .zip(&bodies)
                .all(|(v, b)| v.type_id() == child_type && b.len() == child_size)
        );
        put(out, child_size as u32);
        put(out, child_type);
        for body in bodies {
            out.extend_from_slice(&body);
        }
    }

    fn write_body(&self, out: &mut Vec<u8>) {
        match self {
            Pod::None => {}
            Pod::Bool(v) => put(out, u32::from(*v)),
            Pod::Id(v) => put(out, *v),
            Pod::Int(v) => out.extend_from_slice(&v.to_ne_bytes()),
            Pod::Long(v) => out.extend_from_slice(&v.to_ne_bytes()),
            Pod::Rectangle(w, h) | Pod::Fraction(w, h) => {
                put(out, *w);
                put(out, *h);
            }
            Pod::Array(child_type, values) => Self::write_children(out, *child_type, values),
            Pod::Choice(kind, flags, values) => {
                put(out, kind.raw());
                put(out, *flags);
                Self::write_children(out, TYPE_NONE, values);
            }
            Pod::Object(object) => {
                put(out, object.type_);
                put(out, object.id);
                for p in &object.props {
                    put(out, p.key);
                    put(out, p.flags);
                    p.value.encode_into(out);
                }
            }
            Pod::Other(_, body) => out.extend_from_slice(body),
        }
    }

    pub(crate) fn encode_into(&self, out: &mut Vec<u8>) {
        let start = out.len();
        put(out, 0);
        put(out, self.type_id());
        self.write_body(out);
        let size = (out.len() - start - 8) as u32;
        out[start..start + 4].copy_from_slice(&size.to_ne_bytes());
        pad(out);
    }

    pub(crate) fn encode(&self) -> Vec<u8> {
        let mut out = Vec::new();
        self.encode_into(&mut out);
        out
    }

    fn parse_children(body: &[u8]) -> Option<(u32, Vec<Pod>)> {
        let child_size = u32_at(body, 0)? as usize;
        let child_type = u32_at(body, 4)?;
        if child_size == 0 {
            return Some((child_type, Vec::new()));
        }
        let values = body[8..]
            .chunks_exact(child_size)
            .map(|c| Self::parse_body(child_type, c))
            .collect::<Option<Vec<_>>>()?;
        Some((child_type, values))
    }

    fn parse_body(type_: u32, body: &[u8]) -> Option<Pod> {
        Some(match type_ {
            TYPE_NONE => Pod::None,
            TYPE_BOOL => Pod::Bool(u32_at(body, 0)? != 0),
            TYPE_ID => Pod::Id(u32_at(body, 0)?),
            TYPE_INT => Pod::Int(u32_at(body, 0)? as i32),
            TYPE_LONG => Pod::Long(i64_at(body, 0)?),
            TYPE_RECTANGLE => Pod::Rectangle(u32_at(body, 0)?, u32_at(body, 4)?),
            TYPE_FRACTION => Pod::Fraction(u32_at(body, 0)?, u32_at(body, 4)?),
            TYPE_ARRAY => {
                let (child_type, values) = Self::parse_children(body)?;
                Pod::Array(child_type, values)
            }
            TYPE_CHOICE => {
                let kind = ChoiceKind::from_raw(u32_at(body, 0)?)?;
                let flags = u32_at(body, 4)?;
                Pod::Choice(kind, flags, Self::parse_children(body.get(8..)?)?.1)
            }
            TYPE_OBJECT => {
                let type_ = u32_at(body, 0)?;
                let id = u32_at(body, 4)?;
                let mut props = Vec::new();
                let mut at = 8usize;
                while at + 16 <= body.len() {
                    let key = u32_at(body, at)?;
                    let flags = u32_at(body, at + 4)?;
                    let size = u32_at(body, at + 8)? as usize;
                    let value_type = u32_at(body, at + 12)?;
                    let value = body.get(at + 16..(at + 16).checked_add(size)?)?;
                    props.push(Prop {
                        key,
                        flags,
                        value: Self::parse_body(value_type, value)?,
                    });
                    at += round_up(16 + size);
                }
                Pod::Object(Object { type_, id, props })
            }
            _ => Pod::Other(type_, body.to_vec()),
        })
    }

    pub(crate) fn parse(bytes: &[u8]) -> Option<(Pod, usize)> {
        let size = u32_at(bytes, 0)? as usize;
        let type_ = u32_at(bytes, 4)?;
        let body = bytes.get(8..8usize.checked_add(size)?)?;
        let pod = Self::parse_body(type_, body)?;
        Some((pod, round_up(8 + size).min(bytes.len())))
    }

    pub(crate) fn fixed(&self) -> Option<&Pod> {
        match self {
            Pod::Choice(_, _, values) => values.first(),
            other => Some(other),
        }
    }

    pub(crate) fn id(&self) -> Option<u32> {
        match self.fixed()? {
            Pod::Id(v) => Some(*v),
            _ => None,
        }
    }

    pub(crate) fn int(&self) -> Option<i32> {
        match self.fixed()? {
            Pod::Int(v) => Some(*v),
            _ => None,
        }
    }

    pub(crate) fn rectangle(&self) -> Option<(u32, u32)> {
        match self.fixed()? {
            Pod::Rectangle(w, h) => Some((*w, *h)),
            _ => None,
        }
    }

    pub(crate) fn fraction(&self) -> Option<(u32, u32)> {
        match self.fixed()? {
            Pod::Fraction(n, d) => Some((*n, *d)),
            _ => None,
        }
    }
}

impl Object {
    pub(crate) fn get(&self, key: u32) -> Option<&Pod> {
        self.props.iter().find(|p| p.key == key).map(|p| &p.value)
    }
}

fn parse_object(bytes: &[u8], type_: u32) -> Option<Object> {
    match Pod::parse(bytes)?.0 {
        Pod::Object(object) if object.type_ == type_ => Some(object),
        _ => None,
    }
}

pub(crate) fn video_enum_format(formats: &[u32], size: (u32, u32), fps: u32) -> Vec<u8> {
    let clamp = |v: u32| v.clamp(1, MAX_VIDEO_DIMENSION);
    let default_size = if size.0 == 0 || size.1 == 0 {
        (1920, 1080)
    } else {
        (clamp(size.0), clamp(size.1))
    };
    let fps = fps.max(1);
    let mut choices = Vec::with_capacity(formats.len() + 1);
    choices.extend(formats.first().map(|f| Pod::Id(*f)));
    choices.extend(formats.iter().map(|f| Pod::Id(*f)));
    Pod::Object(Object {
        type_: OBJECT_FORMAT,
        id: PARAM_ENUM_FORMAT,
        props: vec![
            prop(FORMAT_MEDIA_TYPE, Pod::Id(MEDIA_TYPE_VIDEO)),
            prop(FORMAT_MEDIA_SUBTYPE, Pod::Id(MEDIA_SUBTYPE_RAW)),
            prop(
                FORMAT_VIDEO_FORMAT,
                Pod::Choice(ChoiceKind::Enum, 0, choices),
            ),
            prop(
                FORMAT_VIDEO_SIZE,
                Pod::Choice(
                    ChoiceKind::Range,
                    0,
                    vec![
                        Pod::Rectangle(default_size.0, default_size.1),
                        Pod::Rectangle(1, 1),
                        Pod::Rectangle(MAX_VIDEO_DIMENSION, MAX_VIDEO_DIMENSION),
                    ],
                ),
            ),
            prop(
                FORMAT_VIDEO_FRAMERATE,
                Pod::Choice(
                    ChoiceKind::Range,
                    0,
                    vec![
                        Pod::Fraction(fps, 1),
                        Pod::Fraction(0, 1),
                        Pod::Fraction(fps, 1),
                    ],
                ),
            ),
        ],
    })
    .encode()
}

pub(crate) fn video_buffers(data_types: u32) -> Vec<u8> {
    Pod::Object(Object {
        type_: OBJECT_PARAM_BUFFERS,
        id: PARAM_BUFFERS,
        props: vec![prop(BUFFERS_DATA_TYPE, Pod::Int(data_types as i32))],
    })
    .encode()
}

pub(crate) fn audio_enum_format(rate: u32, positions: &[u32]) -> Vec<u8> {
    Pod::Object(Object {
        type_: OBJECT_FORMAT,
        id: PARAM_ENUM_FORMAT,
        props: vec![
            prop(FORMAT_MEDIA_TYPE, Pod::Id(MEDIA_TYPE_AUDIO)),
            prop(FORMAT_MEDIA_SUBTYPE, Pod::Id(MEDIA_SUBTYPE_RAW)),
            prop(FORMAT_AUDIO_FORMAT, Pod::Id(AUDIO_FORMAT_F32)),
            prop(FORMAT_AUDIO_RATE, Pod::Int(rate as i32)),
            prop(FORMAT_AUDIO_CHANNELS, Pod::Int(positions.len() as i32)),
            prop(
                FORMAT_AUDIO_POSITION,
                Pod::Array(TYPE_ID, positions.iter().map(|p| Pod::Id(*p)).collect()),
            ),
        ],
    })
    .encode()
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) struct VideoInfo {
    pub format: u32,
    pub width: u32,
    pub height: u32,
    pub framerate: (u32, u32),
    pub modifier: bool,
}

pub(crate) fn parse_video_format(bytes: &[u8]) -> Option<VideoInfo> {
    let object = parse_object(bytes, OBJECT_FORMAT)?;
    if object.get(FORMAT_MEDIA_TYPE)?.id()? != MEDIA_TYPE_VIDEO
        || object.get(FORMAT_MEDIA_SUBTYPE)?.id()? != MEDIA_SUBTYPE_RAW
    {
        return None;
    }
    let format = object.get(FORMAT_VIDEO_FORMAT)?.id()?;
    let (width, height) = object.get(FORMAT_VIDEO_SIZE)?.rectangle()?;
    if width == 0 || height == 0 {
        return None;
    }
    let framerate = object
        .get(FORMAT_VIDEO_FRAMERATE)
        .and_then(Pod::fraction)
        .unwrap_or((0, 1));
    Some(VideoInfo {
        format,
        width,
        height,
        framerate,
        modifier: object.get(FORMAT_VIDEO_MODIFIER).is_some(),
    })
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) struct AudioInfo {
    pub format: u32,
    pub rate: u32,
    pub channels: u32,
}

pub(crate) fn parse_audio_format(bytes: &[u8]) -> Option<AudioInfo> {
    let object = parse_object(bytes, OBJECT_FORMAT)?;
    if object.get(FORMAT_MEDIA_TYPE)?.id()? != MEDIA_TYPE_AUDIO
        || object.get(FORMAT_MEDIA_SUBTYPE)?.id()? != MEDIA_SUBTYPE_RAW
    {
        return None;
    }
    Some(AudioInfo {
        format: object.get(FORMAT_AUDIO_FORMAT)?.id()?,
        rate: u32::try_from(object.get(FORMAT_AUDIO_RATE)?.int()?).ok()?,
        channels: u32::try_from(object.get(FORMAT_AUDIO_CHANNELS)?.int()?).ok()?,
    })
}

pub(crate) fn chunk_region(maxsize: u32, offset: u32, size: u32) -> Option<(usize, usize)> {
    if maxsize == 0 {
        return None;
    }
    let offset = offset % maxsize;
    let size = size.min(maxsize - offset);
    (size > 0).then_some((offset as usize, size as usize))
}

pub(crate) fn frame_stride(width: u32, height: u32, stride: i32, size: usize) -> Option<u32> {
    let row = u64::from(width).checked_mul(4)?;
    let stride = if stride > 0 {
        u64::from(stride.unsigned_abs())
    } else {
        row
    };
    if width == 0 || height == 0 || stride < row {
        return None;
    }
    let needed = stride
        .checked_mul(u64::from(height - 1))?
        .checked_add(row)?;
    (needed <= size as u64).then_some(u32::try_from(stride).ok()?)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn words(words: &[u32]) -> Vec<u8> {
        words.iter().flat_map(|w| w.to_ne_bytes()).collect()
    }

    #[test]
    fn primitives_are_padded_to_eight() {
        assert_eq!(Pod::Int(-2).encode(), words(&[4, 4, (-2i32) as u32, 0]));
        assert_eq!(Pod::Id(7).encode(), words(&[4, 3, 7, 0]));
        assert_eq!(Pod::Bool(true).encode(), words(&[4, 2, 1, 0]));
        assert_eq!(Pod::None.encode(), words(&[0, 1]));
        assert_eq!(Pod::Rectangle(640, 480).encode(), words(&[8, 10, 640, 480]));
        assert_eq!(Pod::Fraction(30, 1).encode(), words(&[8, 11, 30, 1]));
        let long = Pod::Long(0x0102_0304_0506_0708).encode();
        assert_eq!(long.len(), 16);
        assert_eq!(&long[..8], &words(&[8, 5])[..]);
        assert_eq!(&long[8..], &0x0102_0304_0506_0708i64.to_ne_bytes()[..]);
    }

    #[test]
    fn choice_values_are_packed_and_only_the_whole_pod_padded() {
        let choice = Pod::Choice(
            ChoiceKind::Enum,
            0,
            vec![Pod::Id(8), Pod::Id(8), Pod::Id(7)],
        );
        assert_eq!(choice.encode(), words(&[28, 19, 3, 0, 4, 3, 8, 8, 7, 0]));
        let flags = Pod::Choice(ChoiceKind::Flags, 0, vec![Pod::Int(6)]);
        assert_eq!(flags.encode(), words(&[20, 19, 4, 0, 4, 4, 6, 0]));
        assert_eq!(
            Pod::Choice(ChoiceKind::None, 0, Vec::new()).encode(),
            words(&[16, 19, 0, 0, 0, 1])
        );
    }

    #[test]
    fn array_of_ids() {
        let array = Pod::Array(TYPE_ID, vec![Pod::Id(3), Pod::Id(4)]);
        assert_eq!(array.encode(), words(&[16, 13, 4, 3, 3, 4]));
        assert_eq!(Pod::parse(&array.encode()), Some((array, 24)));
        let empty = Pod::Array(TYPE_ID, Vec::new());
        assert_eq!(empty.encode(), words(&[8, 13, 0, 3]));
        assert_eq!(Pod::parse(&empty.encode()), Some((empty, 16)));
    }

    #[rustfmt::skip]
    #[test]
    fn enum_format_matches_hand_built_bytes() {
        let built = video_enum_format(&[VIDEO_FORMAT_BGRX, VIDEO_FORMAT_RGBX], (1920, 1080), 30);
        let expected = words(&[
            216, 15, 0x40003, 3,
            1, 0, 4, 3, 2, 0,
            2, 0, 4, 3, 1, 0,
            0x20001, 0, 28, 19, 3, 0, 4, 3, 8, 8, 7, 0,
            0x20003, 0, 40, 19, 1, 0, 8, 10, 1920, 1080, 1, 1, 8192, 8192,
            0x20004, 0, 40, 19, 1, 0, 8, 11, 30, 1, 0, 1, 30, 1,
        ]);
        assert_eq!(built.len(), 224);
        assert_eq!(built, expected);
    }

    #[test]
    fn enum_format_clamps_size_and_defaults_unknown() {
        let object = parse_object(
            &video_enum_format(&[VIDEO_FORMAT_BGRA], (0, 0), 0),
            OBJECT_FORMAT,
        )
        .unwrap();
        assert_eq!(
            object.get(FORMAT_VIDEO_SIZE).unwrap().rectangle(),
            Some((1920, 1080))
        );
        assert_eq!(
            object.get(FORMAT_VIDEO_FRAMERATE).unwrap().fraction(),
            Some((1, 1))
        );
        let object = parse_object(
            &video_enum_format(&[VIDEO_FORMAT_BGRA], (10_000, 5), 60),
            OBJECT_FORMAT,
        )
        .unwrap();
        assert_eq!(
            object.get(FORMAT_VIDEO_SIZE).unwrap().rectangle(),
            Some((8192, 5))
        );
        assert_eq!(
            object.get(FORMAT_VIDEO_FORMAT),
            Some(&Pod::Choice(
                ChoiceKind::Enum,
                0,
                vec![Pod::Id(12), Pod::Id(12)]
            ))
        );
    }

    #[test]
    fn enum_format_round_trips() {
        let bytes = video_enum_format(
            &[
                VIDEO_FORMAT_BGRX,
                VIDEO_FORMAT_RGBX,
                VIDEO_FORMAT_BGRA,
                VIDEO_FORMAT_RGBA,
            ],
            (2560, 1440),
            60,
        );
        let (pod, used) = Pod::parse(&bytes).unwrap();
        assert_eq!(used, bytes.len());
        assert_eq!(pod.encode(), bytes);
        let Pod::Object(object) = pod else {
            panic!("not an object")
        };
        assert_eq!(object.type_, OBJECT_FORMAT);
        assert_eq!(object.id, PARAM_ENUM_FORMAT);
        assert_eq!(object.props.len(), 5);
    }

    #[test]
    fn buffers_param_bytes() {
        assert_eq!(
            video_buffers((1 << DATA_MEM_PTR) | (1 << DATA_MEM_FD)),
            words(&[32, 15, 0x40004, 5, 6, 0, 4, 4, 6, 0])
        );
    }

    #[rustfmt::skip]
    #[test]
    fn audio_enum_format_bytes() {
        let built = audio_enum_format(48_000, &[AUDIO_CHANNEL_FL, AUDIO_CHANNEL_FR]);
        let expected = words(&[
            160, 15, 0x40003, 3,
            1, 0, 4, 3, 1, 0,
            2, 0, 4, 3, 1, 0,
            0x10001, 0, 4, 3, AUDIO_FORMAT_F32, 0,
            0x10003, 0, 4, 4, 48_000, 0,
            0x10004, 0, 4, 4, 2, 0,
            0x10005, 0, 16, 13, 4, 3, 3, 4,
        ]);
        assert_eq!(built, expected);
        assert_eq!(AUDIO_FORMAT_F32, if cfg!(target_endian = "little") { 283 } else { 284 });
        assert_eq!(
            parse_audio_format(&built),
            Some(AudioInfo { format: AUDIO_FORMAT_F32, rate: 48_000, channels: 2 })
        );
    }

    fn negotiated_video() -> Pod {
        Pod::Object(Object {
            type_: OBJECT_FORMAT,
            id: PARAM_FORMAT,
            props: vec![
                prop(FORMAT_MEDIA_TYPE, Pod::Id(MEDIA_TYPE_VIDEO)),
                prop(FORMAT_MEDIA_SUBTYPE, Pod::Id(MEDIA_SUBTYPE_RAW)),
                prop(FORMAT_VIDEO_FORMAT, Pod::Id(VIDEO_FORMAT_BGRA)),
                Prop {
                    key: 0x20009,
                    flags: 8,
                    value: Pod::Other(8, b"bt709\0".to_vec()),
                },
                prop(FORMAT_VIDEO_SIZE, Pod::Rectangle(1366, 768)),
                prop(FORMAT_VIDEO_FRAMERATE, Pod::Fraction(0, 1)),
                prop(
                    0x20005,
                    Pod::Choice(
                        ChoiceKind::Range,
                        0,
                        vec![
                            Pod::Fraction(60, 1),
                            Pod::Fraction(1, 1),
                            Pod::Fraction(60, 1),
                        ],
                    ),
                ),
                prop(0x2000a, Pod::Bool(false)),
            ],
        })
    }

    #[test]
    fn format_round_trips_and_parses() {
        let pod = negotiated_video();
        let bytes = pod.encode();
        assert_eq!(bytes.len() % 8, 0);
        let (parsed, used) = Pod::parse(&bytes).unwrap();
        assert_eq!(used, bytes.len());
        assert_eq!(parsed, pod);
        assert_eq!(
            parse_video_format(&bytes),
            Some(VideoInfo {
                format: VIDEO_FORMAT_BGRA,
                width: 1366,
                height: 768,
                framerate: (0, 1),
                modifier: false
            })
        );
    }

    #[test]
    fn format_values_wrapped_in_choices_use_the_default() {
        let pod = Pod::Object(Object {
            type_: OBJECT_FORMAT,
            id: PARAM_FORMAT,
            props: vec![
                prop(FORMAT_MEDIA_TYPE, Pod::Id(MEDIA_TYPE_VIDEO)),
                prop(FORMAT_MEDIA_SUBTYPE, Pod::Id(MEDIA_SUBTYPE_RAW)),
                prop(
                    FORMAT_VIDEO_FORMAT,
                    Pod::Choice(ChoiceKind::None, 0, vec![Pod::Id(VIDEO_FORMAT_RGBX)]),
                ),
                prop(
                    FORMAT_VIDEO_MODIFIER,
                    Pod::Choice(ChoiceKind::Enum, 24, vec![Pod::Long(0), Pod::Long(0)]),
                ),
                prop(
                    FORMAT_VIDEO_SIZE,
                    Pod::Choice(ChoiceKind::None, 0, vec![Pod::Rectangle(800, 600)]),
                ),
            ],
        });
        let info = parse_video_format(&pod.encode()).unwrap();
        assert_eq!(
            (
                info.format,
                info.width,
                info.height,
                info.framerate,
                info.modifier
            ),
            (VIDEO_FORMAT_RGBX, 800, 600, (0, 1), true)
        );
    }

    #[test]
    fn wrong_media_or_object_is_rejected() {
        let audio = audio_enum_format(48_000, &[AUDIO_CHANNEL_FL]);
        assert_eq!(parse_video_format(&audio), None);
        assert_eq!(parse_audio_format(&negotiated_video().encode()), None);
        assert_eq!(parse_video_format(&video_buffers(6)), None);
        assert_eq!(parse_video_format(&Pod::Int(1).encode()), None);
    }

    #[test]
    fn truncated_input_never_panics() {
        let bytes = negotiated_video().encode();
        for len in 0..bytes.len() {
            let _ = Pod::parse(&bytes[..len]);
            let _ = parse_video_format(&bytes[..len]);
        }
        assert_eq!(Pod::parse(&bytes[..bytes.len() - 1]), None);
        let mut lying = bytes.clone();
        lying[0..4].copy_from_slice(&u32::MAX.to_ne_bytes());
        assert_eq!(Pod::parse(&lying), None);
        let mut bad_prop = bytes;
        bad_prop[24..28].copy_from_slice(&0xffff_fff0u32.to_ne_bytes());
        assert_eq!(Pod::parse(&bad_prop), None);
        assert_eq!(
            Pod::parse(&words(&[24, 19, 3, 0, 0, 3, 1, 2])).map(|p| p.0),
            Some(Pod::Choice(ChoiceKind::Enum, 0, Vec::new()))
        );
        assert_eq!(Pod::parse(&words(&[16, 19, 9, 0, 4, 3])), None);
    }

    #[test]
    fn unpadded_trailing_pod_is_accepted() {
        let bytes = words(&[4, 4, 9]);
        assert_eq!(Pod::parse(&bytes), Some((Pod::Int(9), 12)));
    }

    #[test]
    fn chunk_regions_stay_inside_the_buffer() {
        assert_eq!(chunk_region(0, 0, 10), None);
        assert_eq!(chunk_region(100, 0, 0), None);
        assert_eq!(chunk_region(100, 0, 100), Some((0, 100)));
        assert_eq!(chunk_region(100, 40, 100), Some((40, 60)));
        assert_eq!(chunk_region(100, 140, 10), Some((40, 10)));
        assert_eq!(chunk_region(100, 100, 10), Some((0, 10)));
    }

    #[test]
    fn frame_stride_checks_rows_fit() {
        assert_eq!(frame_stride(4, 2, 16, 32), Some(16));
        assert_eq!(frame_stride(4, 2, 0, 32), Some(16));
        assert_eq!(frame_stride(4, 2, -16, 32), Some(16));
        assert_eq!(frame_stride(4, 2, 20, 36), Some(20));
        assert_eq!(frame_stride(4, 2, 20, 35), None);
        assert_eq!(frame_stride(4, 2, 12, 64), None);
        assert_eq!(frame_stride(0, 2, 16, 64), None);
        assert_eq!(frame_stride(4, 0, 16, 64), None);
        assert_eq!(frame_stride(u32::MAX, u32::MAX, 0, usize::MAX), None);
    }
}
