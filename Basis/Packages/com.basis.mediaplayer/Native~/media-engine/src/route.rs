//! Platform decoder routing: one factory pair per platform behind the same
//! signatures, so the pipeline threads stay platform-free. A refusal is
//! typed: the caller turns it into a CodecRefused diagnostic, mutes the
//! track and plays on, or fails the session if nothing else can play. A
//! software fallback engaging is reported.
//!
//! Routing honours the user's decode preference: hardware with fallback
//! (the default), hardware only, or software only. A route the platform
//! does not have is a typed refusal. Software routes also enforce a
//! performance cap, refusing content over 1080p60 coded pixel rate instead
//! of overloading a CPU that has no hardware path.

use crate::DecodePreference;
use media_decode::{AudioDecoder, VideoDecoder};

/// The software-route cap: coded pixel rate ≤ 1920 × 1088 × 60
/// (~125 Mpx/s, so 1080p with macroblock padding passes). Where no frame
/// rate is stated, the gate is dimensions alone (≤1920×1088). With a rate,
/// the budget form admits cost-equivalent shapes such as 1440p30.
/// Android's routes are all platform MediaCodec and sit outside the gate.
#[cfg(not(target_os = "android"))]
pub const SOFTWARE_CAP_WIDTH: u32 = 1920;
#[cfg(not(target_os = "android"))]
pub const SOFTWARE_CAP_HEIGHT: u32 = 1088;
#[cfg(not(target_os = "android"))]
pub const SOFTWARE_CAP_FPS: u64 = 60;

/// Whether a software decode route accepts content of this coded shape.
#[cfg(not(target_os = "android"))]
pub fn software_cap_allows(width: u32, height: u32, fps: Option<u32>) -> bool {
    match fps {
        Some(fps) => {
            u64::from(width) * u64::from(height) * u64::from(fps)
                <= u64::from(SOFTWARE_CAP_WIDTH) * u64::from(SOFTWARE_CAP_HEIGHT) * SOFTWARE_CAP_FPS
        }
        None => width <= SOFTWARE_CAP_WIDTH && height <= SOFTWARE_CAP_HEIGHT,
    }
}

#[cfg(not(target_os = "android"))]
fn software_cap_check(width: u32, height: u32) -> Result<(), media_decode::DecodeError> {
    // No demuxer states a frame rate, so only the dimensions arm applies.
    if software_cap_allows(width, height, None) {
        Ok(())
    } else {
        Err(media_decode::DecodeError(format!(
            "software decode routes accept up to {SOFTWARE_CAP_WIDTH}x{SOFTWARE_CAP_HEIGHT}@{SOFTWARE_CAP_FPS}; \
             this stream is {width}x{height}"
        )))
    }
}

/// The route a video format resolved to: the decoder, its label, and why
/// the software route carried it if the hardware path was absent.
pub struct VideoRoute {
    pub decoder: Box<dyn VideoDecoder>,
    pub label: &'static str,
    pub fallback: Option<String>,
    /// The hardware decoder's D3D11 device (`ID3D11Device*`, valid while
    /// the decoder lives): the presenter shares it so decoded slices bind
    /// into the conversion pass with no cross-device copy. `None` on
    /// software routes (the presenter keeps its own device).
    pub decode_device: Option<*mut std::ffi::c_void>,
}

/// Setting this to a number of milliseconds makes every routed video
/// decoder sleep that long for each frame it yields, simulating a decoder
/// too slow for its stream. Used by the slow-video tests and for
/// reproducing the case by hand on a device.
pub const SLOW_VIDEO_DECODE_ENV: &str = "BASIS_MEDIA_SLOW_VIDEO_DECODE_MS";

struct SlowVideoDecoder {
    inner: Box<dyn VideoDecoder>,
    per_frame: std::time::Duration,
}

impl VideoDecoder for SlowVideoDecoder {
    fn hardware_fell_back(&self) -> bool {
        self.inner.hardware_fell_back()
    }

    fn submit(
        &mut self,
        annexb: &[u8],
        pts_us: i64,
    ) -> Result<media_decode::SubmitOutcome, media_decode::DecodeError> {
        self.inner.submit(annexb, pts_us)
    }

    fn try_output(
        &mut self,
    ) -> Result<Option<media_decode::VideoFrame>, media_decode::DecodeError> {
        let frame = self.inner.try_output()?;
        if frame.is_some() {
            std::thread::sleep(self.per_frame);
        }
        Ok(frame)
    }

    fn begin_drain(&mut self) -> Result<(), media_decode::DecodeError> {
        self.inner.begin_drain()
    }

    fn drain_dry(&self) -> bool {
        self.inner.drain_dry()
    }

    fn reset(&mut self) -> Result<(), media_decode::DecodeError> {
        self.inner.reset()
    }
}

pub fn open_video_decoder(
    codec: media_demux::VideoCodec,
    coded_width: u32,
    coded_height: u32,
    live: bool,
    preference: DecodePreference,
    codec_private: &[u8],
) -> Result<VideoRoute, media_decode::DecodeError> {
    let mut route = route_video_decoder(
        codec,
        coded_width,
        coded_height,
        live,
        preference,
        codec_private,
    )?;
    let slow_ms = std::env::var(SLOW_VIDEO_DECODE_ENV)
        .ok()
        .and_then(|v| v.parse::<u64>().ok())
        .filter(|ms| *ms > 0);
    if let Some(ms) = slow_ms {
        route.decoder = Box::new(SlowVideoDecoder {
            inner: route.decoder,
            per_frame: std::time::Duration::from_millis(ms),
        });
    }
    Ok(route)
}

#[cfg(windows)]
fn route_video_decoder(
    codec: media_demux::VideoCodec,
    coded_width: u32,
    coded_height: u32,
    live: bool,
    preference: DecodePreference,
    codec_private: &[u8],
) -> Result<VideoRoute, media_decode::DecodeError> {
    use decode_mf::{HwCodec, HwVideoDecoder};
    use media_demux::VideoCodec;

    let hw_codec = match codec {
        VideoCodec::H264 => Some(HwCodec::H264),
        VideoCodec::H265 => Some(HwCodec::H265),
        VideoCodec::Vp9 => Some(HwCodec::Vp9),
        VideoCodec::Av1 => Some(HwCodec::Av1),
        VideoCodec::Vp8 => None,
    };

    // Hardware first unless the user opted out. Construction checks both
    // the MFT and the GPU's profile/format/config at this resolution, so a
    // failure here means there is no hardware path.
    let mut hw_failure: Option<String> = None;
    if preference != DecodePreference::SoftwareOnly {
        if let Some(hw) = hw_codec {
            match HwVideoDecoder::new(hw, coded_width, coded_height, codec_private, live) {
                Ok(decoder) => {
                    let device = decoder.device_raw();
                    return Ok(VideoRoute {
                        decoder: Box::new(decoder),
                        label: match hw {
                            HwCodec::H264 => "DXVA H.264",
                            HwCodec::H265 => "DXVA HEVC",
                            HwCodec::Vp9 => "DXVA VP9",
                            HwCodec::Av1 => "DXVA AV1",
                        },
                        fallback: None,
                        decode_device: Some(device),
                    });
                }
                Err(e) => hw_failure = Some(e.0),
            }
        }
        if preference == DecodePreference::HardwareOnly {
            return Err(media_decode::DecodeError(match hw_failure {
                Some(e) => format!("hardware-only decode preference: {e}"),
                None => format!("hardware-only decode preference: no hardware route for {codec:?}"),
            }));
        }
    }

    // Without a software route the hardware's refusal is the reason worth
    // reporting, and it goes out before the cap can stand in for it.
    match codec {
        VideoCodec::H265 => {
            return Err(media_decode::DecodeError(
                hw_failure.unwrap_or_else(|| NO_SOFTWARE_HEVC.into()),
            ));
        }
        VideoCodec::Vp8 => return Err(media_decode::DecodeError(NO_VP8.into())),
        VideoCodec::H264 | VideoCodec::Vp9 | VideoCodec::Av1 => {}
    }

    // Software: the direct route under software-only, a reported fallback
    // otherwise. The cap is checked before any decoder is built.
    software_cap_check(coded_width, coded_height)?;
    let fallback = hw_failure.map(|e| format!("hardware decode unavailable ({e})"));
    open_windows_software(codec, coded_width, coded_height, live, fallback)
}

/// The Windows CPU routes: the in-box H.264 MFT, the Store VP9 extension,
/// and AV1 on rav1d. The Store AV1 extension is tried only if rav1d fails,
/// because driven synchronously its `ProcessInput` blocks for over a
/// second when its queue fills.
#[cfg(windows)]
fn open_windows_software(
    codec: media_demux::VideoCodec,
    coded_width: u32,
    coded_height: u32,
    live: bool,
    fallback: Option<String>,
) -> Result<VideoRoute, media_decode::DecodeError> {
    use decode_mf::{Av1Decoder, H264Decoder, Vp9Decoder};
    use decode_sw::SwAv1Decoder;
    use media_demux::VideoCodec;
    match codec {
        VideoCodec::H264 => Ok(VideoRoute {
            decoder: Box::new(H264Decoder::new(live)?),
            label: "MF H.264",
            fallback,
            decode_device: None,
        }),
        VideoCodec::Vp9 => Ok(VideoRoute {
            decoder: Box::new(Vp9Decoder::new(coded_width, coded_height)?),
            label: "MF VP9",
            fallback,
            decode_device: None,
        }),
        VideoCodec::Av1 => match SwAv1Decoder::new(SOFTWARE_CAP_WIDTH * SOFTWARE_CAP_HEIGHT) {
            Ok(d) => Ok(VideoRoute {
                decoder: Box::new(d),
                label: "rav1d",
                fallback,
                decode_device: None,
            }),
            Err(floor) => match Av1Decoder::new(coded_width, coded_height) {
                Ok(d) => Ok(VideoRoute {
                    decoder: Box::new(d),
                    label: "MF AV1",
                    fallback: Some(match fallback {
                        Some(f) => format!("{f}; rav1d unavailable ({floor})"),
                        None => format!("rav1d unavailable ({floor})"),
                    }),
                    decode_device: None,
                }),
                Err(platform) => Err(media_decode::DecodeError(format!(
                    "rav1d: {floor}; platform: {platform}"
                ))),
            },
        },
        VideoCodec::H265 => Err(media_decode::DecodeError(NO_SOFTWARE_HEVC.into())),
        VideoCodec::Vp8 => Err(media_decode::DecodeError(NO_VP8.into())),
    }
}

#[cfg(windows)]
const NO_SOFTWARE_HEVC: &str =
    "no software H.265 route (hardware DXVA is the only Windows HEVC path)";

#[cfg(windows)]
const NO_VP8: &str = "video codec 'VP8' is not supported (supported: H.264, H.265, VP9, AV1)";

/// Android: every route is the platform MediaCodec stack. Quest has no
/// software fallback for avc/hevc/vp9 and rav1d has no Vulkan upload path,
/// so a missing platform decoder is a typed refusal. The software-only
/// preference has no route here.
#[cfg(target_os = "android")]
fn route_video_decoder(
    codec: media_demux::VideoCodec,
    coded_width: u32,
    coded_height: u32,
    live: bool,
    preference: DecodePreference,
    codec_private: &[u8],
) -> Result<VideoRoute, media_decode::DecodeError> {
    use decode_mediacodec::{McVideoDecoder, VideoMime};
    use media_demux::VideoCodec;
    if preference == DecodePreference::SoftwareOnly {
        return Err(media_decode::DecodeError(
            "software-only decode preference: no software video route on this platform".into(),
        ));
    }
    let mime = match codec {
        VideoCodec::H264 => VideoMime::H264,
        VideoCodec::H265 => VideoMime::H265,
        VideoCodec::Vp9 => VideoMime::Vp9,
        VideoCodec::Vp8 => VideoMime::Vp8,
        VideoCodec::Av1 => VideoMime::Av1,
    };
    let decoder = McVideoDecoder::new(mime, coded_width, coded_height, live, codec_private)?;
    Ok(VideoRoute {
        decoder: Box::new(decoder),
        label: "MediaCodec",
        fallback: None,
        decode_device: None,
    })
}

/// Headless platforms (Linux and anything else without a platform decoder
/// adapter): only AV1 on rav1d routes. Patented codecs are never bundled,
/// so H.264/H.265/VP9/VP8 are typed refusals. The hardware-only preference
/// has no route.
#[cfg(not(any(windows, target_os = "android")))]
fn route_video_decoder(
    codec: media_demux::VideoCodec,
    coded_width: u32,
    coded_height: u32,
    _live: bool,
    preference: DecodePreference,
    _codec_private: &[u8],
) -> Result<VideoRoute, media_decode::DecodeError> {
    use decode_sw::SwAv1Decoder;
    use media_demux::VideoCodec;
    if preference == DecodePreference::HardwareOnly {
        return Err(media_decode::DecodeError(
            "hardware-only decode preference: no hardware video route on this platform".into(),
        ));
    }
    match codec {
        VideoCodec::Av1 => {
            software_cap_check(coded_width, coded_height)?;
            Ok(VideoRoute {
                decoder: Box::new(SwAv1Decoder::new(SOFTWARE_CAP_WIDTH * SOFTWARE_CAP_HEIGHT)?),
                label: "rav1d",
                fallback: None,
                decode_device: None,
            })
        }
        VideoCodec::H264 | VideoCodec::H265 => Err(media_decode::DecodeError(
            "no platform decode path on this platform yet (VAAPI adapter pending)".into(),
        )),
        VideoCodec::Vp9 | VideoCodec::Vp8 => Err(media_decode::DecodeError(
            "no VP9/VP8 decode path (platform ceiling; VAAPI adapter pending)".into(),
        )),
    }
}

#[cfg(windows)]
pub fn open_audio_decoder(
    codec: media_demux::AudioCodec,
    sample_rate: u32,
    channels: u32,
    codec_private: &[u8],
) -> Result<Box<dyn AudioDecoder>, media_decode::DecodeError> {
    use decode_mf::{AacDecoder, Mp3Decoder};
    use decode_sw::{FlacDecoder, OpusDecoder, PcmDecoder};
    use media_demux::AudioCodec;
    Ok(match codec {
        AudioCodec::Aac => Box::new(AacDecoder::new(sample_rate, channels, codec_private)?),
        AudioCodec::Mp3 => Box::new(Mp3Decoder::new(sample_rate, channels)?),
        AudioCodec::Flac => Box::new(FlacDecoder::new(codec_private)?),
        AudioCodec::Opus => Box::new(OpusDecoder::new(codec_private)?),
        AudioCodec::Pcm => Box::new(PcmDecoder::new(sample_rate, channels, codec_private)?),
    })
}

/// Android: AAC and MP3 decode on the platform, since patented codecs are
/// never bundled. FLAC, Opus and PCM use the in-process decoders for the
/// same behaviour on every platform.
#[cfg(target_os = "android")]
pub fn open_audio_decoder(
    codec: media_demux::AudioCodec,
    sample_rate: u32,
    channels: u32,
    codec_private: &[u8],
) -> Result<Box<dyn AudioDecoder>, media_decode::DecodeError> {
    use decode_mediacodec::{AudioMime, McAudioDecoder};
    use decode_sw::{FlacDecoder, OpusDecoder, PcmDecoder};
    use media_demux::AudioCodec;
    Ok(match codec {
        AudioCodec::Aac => Box::new(McAudioDecoder::new(
            AudioMime::Aac,
            sample_rate,
            channels,
            codec_private,
        )?),
        AudioCodec::Mp3 => Box::new(McAudioDecoder::new(
            AudioMime::Mp3,
            sample_rate,
            channels,
            &[],
        )?),
        AudioCodec::Flac => Box::new(FlacDecoder::new(codec_private)?),
        AudioCodec::Opus => Box::new(OpusDecoder::new(codec_private)?),
        AudioCodec::Pcm => Box::new(PcmDecoder::new(sample_rate, channels, codec_private)?),
    })
}

/// Headless platforms: FLAC, Opus and PCM use the in-process decoders. AAC
/// and MP3 have no platform decoder here and are typed refusals.
#[cfg(not(any(windows, target_os = "android")))]
pub fn open_audio_decoder(
    codec: media_demux::AudioCodec,
    sample_rate: u32,
    channels: u32,
    codec_private: &[u8],
) -> Result<Box<dyn AudioDecoder>, media_decode::DecodeError> {
    use decode_sw::{FlacDecoder, OpusDecoder, PcmDecoder};
    use media_demux::AudioCodec;
    Ok(match codec {
        AudioCodec::Flac => Box::new(FlacDecoder::new(codec_private)?),
        AudioCodec::Opus => Box::new(OpusDecoder::new(codec_private)?),
        AudioCodec::Aac | AudioCodec::Mp3 => {
            return Err(media_decode::DecodeError(
                "no AAC/MP3 decode path on this platform (platform decoders only)".into(),
            ));
        }
        AudioCodec::Pcm => Box::new(PcmDecoder::new(sample_rate, channels, codec_private)?),
    })
}

#[cfg(test)]
mod tests {
    use super::software_cap_allows;

    #[test]
    fn software_cap_budget_form() {
        // At or under the 1080p60 pixel-rate budget.
        assert!(software_cap_allows(1920, 1088, Some(60)));
        assert!(software_cap_allows(1920, 1080, Some(60)));
        assert!(software_cap_allows(2560, 1440, Some(30))); // ~110 Mpx/s
        assert!(software_cap_allows(1280, 720, Some(120)));
        // Over budget: 1440p60, 4K at any rate, 1080p120.
        assert!(!software_cap_allows(2560, 1440, Some(60)));
        assert!(!software_cap_allows(3840, 2160, Some(30)));
        assert!(!software_cap_allows(1920, 1088, Some(120)));
    }

    /// A software route for over-cap content refuses before any decoder is
    /// built.
    #[cfg(windows)]
    #[test]
    fn software_route_refuses_over_cap_content() {
        let err = super::open_video_decoder(
            media_demux::VideoCodec::H264,
            3840,
            2160,
            false,
            crate::DecodePreference::SoftwareOnly,
            &[],
        )
        .err()
        .expect("over-cap software route must refuse");
        assert!(
            err.0.contains("software decode routes accept up to"),
            "{}",
            err.0
        );
    }

    /// HEVC has no Windows software route, so a refusal carries the
    /// hardware's reason: here, a track that announced no frame size.
    #[cfg(windows)]
    #[test]
    fn sizeless_hevc_is_refused_for_its_missing_size() {
        let err = super::open_video_decoder(
            media_demux::VideoCodec::H265,
            0,
            0,
            false,
            crate::DecodePreference::HardwareWithFallback,
            &[],
        )
        .err()
        .expect("a sizeless HEVC track must refuse");
        assert_eq!(
            err.0,
            "video track (H.265) announced no frame size, so the decoder cannot be configured"
        );
    }

    #[cfg(windows)]
    #[test]
    fn vp8_is_refused_as_unsupported() {
        let err = super::open_video_decoder(
            media_demux::VideoCodec::Vp8,
            640,
            360,
            false,
            crate::DecodePreference::HardwareWithFallback,
            &[],
        )
        .err()
        .expect("VP8 has no Windows route");
        assert_eq!(
            err.0,
            "video codec 'VP8' is not supported (supported: H.264, H.265, VP9, AV1)"
        );
    }

    #[test]
    fn software_cap_dims_only_when_fps_unstated() {
        assert!(software_cap_allows(1920, 1088, None));
        assert!(software_cap_allows(1920, 1080, None));
        assert!(software_cap_allows(640, 360, None));
        assert!(!software_cap_allows(2560, 1440, None));
        assert!(!software_cap_allows(3840, 2160, None));
        assert!(!software_cap_allows(1921, 1080, None));
    }
}
