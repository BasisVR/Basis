use std::collections::VecDeque;
use std::ffi::c_void;
use std::ptr::null_mut;

use core_foundation_sys::base::OSStatus;
use media_decode::{AudioDecoder, DecodeError, PcmChunk, SubmitOutcome};

use crate::ffi::*;

const NO_DATA: OSStatus = i32::from_be_bytes(*b"bmnd");
const READY_CAP: usize = 64;
const OUTPUT_FRAMES: u32 = 4096;
const LAYOUT_MPEG_3_0_A: u32 = (113 << 16) | 3;
const LAYOUT_MPEG_4_0_A: u32 = (115 << 16) | 4;
const LAYOUT_MPEG_5_0_A: u32 = (117 << 16) | 5;
const LAYOUT_MPEG_5_1_A: u32 = (121 << 16) | 6;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AtCodec {
    Aac,
    Mp3,
}

/// The packet the converter's input callback hands over next.
struct Feed {
    data: *const u8,
    len: u32,
    channels: u32,
    description: AudioStreamPacketDescription,
    pending: bool,
    end: bool,
}

extern "C" fn on_input(
    _converter: AudioConverterRef,
    packets: *mut u32,
    data: *mut AudioBufferList,
    description: *mut *mut AudioStreamPacketDescription,
    user: *mut c_void,
) -> OSStatus {
    // SAFETY: user is the decoder's Feed, alive for the FillComplexBuffer
    // call; the out-pointers are AudioToolbox's.
    unsafe {
        let feed = &mut *(user as *mut Feed);
        if !feed.pending {
            *packets = 0;
            return if feed.end { 0 } else { NO_DATA };
        }
        feed.pending = false;
        *packets = 1;
        let buffers = &mut *data;
        buffers.count = 1;
        buffers.buffers[0] = AudioBuffer {
            channels: feed.channels,
            data_byte_size: feed.len,
            data: feed.data as *mut c_void,
        };
        feed.description = AudioStreamPacketDescription {
            start_offset: 0,
            variable_frames: 0,
            data_byte_size: feed.len,
        };
        if !description.is_null() {
            *description = &mut feed.description;
        }
        0
    }
}

fn descriptor(out: &mut Vec<u8>, tag: u8, size: usize) {
    out.push(tag);
    for shift in [21, 14, 7] {
        out.push(((size >> shift) & 0x7f) as u8 | 0x80);
    }
    out.push((size & 0x7f) as u8);
}

/// The ES descriptor AudioToolbox takes as an AAC decoder's magic cookie,
/// wrapping the AudioSpecificConfig.
fn esds(asc: &[u8]) -> Vec<u8> {
    let mut out = Vec::with_capacity(asc.len() + 40);
    descriptor(&mut out, 0x03, 3 + 5 + 13 + 5 + asc.len());
    out.extend_from_slice(&[0, 0, 0]);
    descriptor(&mut out, 0x04, 13 + 5 + asc.len());
    out.extend_from_slice(&[0x40, 0x15, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
    descriptor(&mut out, 0x05, asc.len());
    out.extend_from_slice(asc);
    out
}

/// The input format AudioToolbox reads from the cookie, its highest
/// quality layer first (HE-AAC's SBR rate rather than the core's).
fn input_format(base: AudioStreamBasicDescription, cookie: &[u8]) -> AudioStreamBasicDescription {
    if cookie.is_empty() {
        return base;
    }
    let info = AudioFormatInfo {
        asbd: base,
        cookie: cookie.as_ptr().cast(),
        cookie_size: cookie.len() as u32,
    };
    let spec_size = size_of::<AudioFormatInfo>() as u32;
    let spec = (&raw const info).cast::<c_void>();
    // SAFETY: the specifier is a live AudioFormatInfo and each out-buffer is
    // sized from the size AudioToolbox reports.
    unsafe {
        let mut size = 0u32;
        if AudioFormatGetPropertyInfo(PROPERTY_FORMAT_LIST, spec_size, spec, &mut size) == 0 {
            let count = size as usize / size_of::<AudioFormatListItem>();
            let mut items = vec![AudioFormatListItem::default(); count.max(1)];
            let mut size = (items.len() * size_of::<AudioFormatListItem>()) as u32;
            if count > 0
                && AudioFormatGetProperty(
                    PROPERTY_FORMAT_LIST,
                    spec_size,
                    spec,
                    &mut size,
                    items.as_mut_ptr().cast(),
                ) == 0
                && items[0].asbd.sample_rate > 0.0
            {
                return items[0].asbd;
            }
        }
        let mut asbd = base;
        let mut size = size_of::<AudioStreamBasicDescription>() as u32;
        if AudioFormatGetProperty(
            PROPERTY_FORMAT_INFO,
            cookie.len() as u32,
            cookie.as_ptr().cast(),
            &mut size,
            (&raw mut asbd).cast(),
        ) == 0
            && asbd.sample_rate > 0.0
        {
            return asbd;
        }
    }
    base
}

pub struct AtAudioDecoder {
    converter: AudioConverterRef,
    sample_rate: u32,
    channels: u32,
    feed: Box<Feed>,
    input: Vec<u8>,
    output: Vec<f32>,
    ready: VecDeque<PcmChunk>,
}

// SAFETY: the converter is owned by this value and driven from one thread
// at a time.
unsafe impl Send for AtAudioDecoder {}

impl AtAudioDecoder {
    /// `config` is the AudioSpecificConfig for AAC, empty for MP3.
    pub fn new(
        codec: AtCodec,
        sample_rate: u32,
        channels: u32,
        config: &[u8],
    ) -> Result<Self, DecodeError> {
        let base = AudioStreamBasicDescription {
            sample_rate: f64::from(sample_rate.max(1)),
            format_id: match codec {
                AtCodec::Aac => FORMAT_AAC,
                AtCodec::Mp3 => FORMAT_MP3,
            },
            frames_per_packet: match codec {
                AtCodec::Aac => 1024,
                AtCodec::Mp3 => 1152,
            },
            channels_per_frame: channels.max(1),
            ..AudioStreamBasicDescription::default()
        };
        let cookie = match codec {
            AtCodec::Aac if !config.is_empty() => esds(config),
            _ => Vec::new(),
        };
        let source = input_format(base, &cookie);
        let rate = source.sample_rate.round().max(1.0) as u32;
        let channels = source.channels_per_frame.clamp(1, 8);
        let destination = AudioStreamBasicDescription {
            sample_rate: f64::from(rate),
            format_id: FORMAT_LPCM,
            format_flags: FORMAT_FLAG_FLOAT | FORMAT_FLAG_PACKED,
            bytes_per_packet: 4 * channels,
            frames_per_packet: 1,
            bytes_per_frame: 4 * channels,
            channels_per_frame: channels,
            bits_per_channel: 32,
            reserved: 0,
        };
        let mut converter: AudioConverterRef = null_mut();
        // SAFETY: both descriptions are live; the converter is released by Drop.
        let status = unsafe { AudioConverterNew(&source, &destination, &mut converter) };
        if status != 0 || converter.is_null() {
            return Err(DecodeError(format!(
                "AudioToolbox has no {} decoder for {rate} Hz x{channels} ({status})",
                match codec {
                    AtCodec::Aac => "AAC",
                    AtCodec::Mp3 => "MP3",
                }
            )));
        }
        let decoder = Self {
            converter,
            sample_rate: rate,
            channels,
            feed: Box::new(Feed {
                data: std::ptr::null(),
                len: 0,
                channels: source.channels_per_frame,
                description: AudioStreamPacketDescription::default(),
                pending: false,
                end: false,
            }),
            input: Vec::new(),
            output: Vec::new(),
            ready: VecDeque::new(),
        };
        // SAFETY: the converter is live; the cookie and layout outlive the calls.
        unsafe {
            if !cookie.is_empty() {
                let status = AudioConverterSetProperty(
                    converter,
                    CONVERTER_MAGIC_COOKIE,
                    cookie.len() as u32,
                    cookie.as_ptr().cast(),
                );
                if status != 0 {
                    return Err(DecodeError(format!(
                        "AudioToolbox rejected the AAC configuration ({status})"
                    )));
                }
            }
            let tag = match channels {
                3 => LAYOUT_MPEG_3_0_A,
                4 => LAYOUT_MPEG_4_0_A,
                5 => LAYOUT_MPEG_5_0_A,
                6 => LAYOUT_MPEG_5_1_A,
                _ => 0,
            };
            if tag != 0 {
                let layout = AudioChannelLayout {
                    tag,
                    bitmap: 0,
                    descriptions: 0,
                    first: [0; 5],
                };
                AudioConverterSetProperty(
                    converter,
                    CONVERTER_OUTPUT_LAYOUT,
                    size_of::<AudioChannelLayout>() as u32,
                    (&raw const layout).cast(),
                );
            }
        }
        Ok(decoder)
    }

    fn pull(&mut self, pts_us: i64) {
        let channels = self.channels as usize;
        self.output.resize(OUTPUT_FRAMES as usize * channels, 0.0);
        loop {
            let mut frames = OUTPUT_FRAMES;
            let mut list = AudioBufferList {
                count: 1,
                buffers: [AudioBuffer {
                    channels: self.channels,
                    data_byte_size: (self.output.len() * 4) as u32,
                    data: self.output.as_mut_ptr().cast(),
                }],
            };
            let feed = (&raw mut *self.feed).cast::<c_void>();
            // SAFETY: the output list points at a buffer sized for `frames`
            // frames; the feed outlives the call.
            let status = unsafe {
                AudioConverterFillComplexBuffer(
                    self.converter,
                    on_input,
                    feed,
                    &mut frames,
                    &mut list,
                    null_mut(),
                )
            };
            let produced = (frames as usize).min(OUTPUT_FRAMES as usize);
            if produced > 0 {
                self.ready.push_back(PcmChunk {
                    sample_rate: self.sample_rate,
                    channels: self.channels,
                    pts_us,
                    data: self.output[..produced * channels].to_vec(),
                });
            }
            if status != 0 || produced == 0 || self.ready.len() >= READY_CAP {
                self.feed.pending = false;
                break;
            }
        }
    }
}

impl AudioDecoder for AtAudioDecoder {
    fn output_format(&self) -> (u32, u32) {
        (self.sample_rate, self.channels)
    }

    fn submit(&mut self, au: &[u8], pts_us: i64) -> Result<SubmitOutcome, DecodeError> {
        if self.ready.len() >= READY_CAP {
            return Ok(SubmitOutcome::NotAccepting);
        }
        if au.is_empty() {
            return Ok(SubmitOutcome::Accepted);
        }
        self.input.clear();
        self.input.extend_from_slice(au);
        self.feed.data = self.input.as_ptr();
        self.feed.len = self.input.len() as u32;
        self.feed.pending = true;
        self.feed.end = false;
        self.pull(pts_us);
        Ok(SubmitOutcome::Accepted)
    }

    fn try_output(&mut self) -> Result<Option<PcmChunk>, DecodeError> {
        Ok(self.ready.pop_front())
    }

    fn begin_drain(&mut self) -> Result<(), DecodeError> {
        self.feed.pending = false;
        self.feed.end = true;
        let pts = self.ready.back().map_or(0, |c| c.pts_us);
        self.pull(pts);
        Ok(())
    }

    fn reset(&mut self) -> Result<(), DecodeError> {
        self.ready.clear();
        self.feed.pending = false;
        self.feed.end = false;
        // SAFETY: the converter is live.
        unsafe { AudioConverterReset(self.converter) };
        Ok(())
    }
}

impl Drop for AtAudioDecoder {
    fn drop(&mut self) {
        // SAFETY: the converter is ours and disposed exactly once.
        unsafe { AudioConverterDispose(self.converter) };
    }
}

#[cfg(test)]
mod tests {
    use super::esds;

    #[test]
    fn esds_wraps_the_audio_specific_config() {
        let cookie = esds(&[0x11, 0x90]);
        assert_eq!(cookie[0], 0x03);
        assert_eq!(&cookie[1..5], &[0x80, 0x80, 0x80, 3 + 5 + 13 + 5 + 2]);
        assert_eq!(
            &cookie[cookie.len() - 7..],
            &[0x05, 0x80, 0x80, 0x80, 2, 0x11, 0x90]
        );
        assert_eq!(cookie.len(), 5 + 3 + 5 + 13 + 5 + 2);
    }
}
