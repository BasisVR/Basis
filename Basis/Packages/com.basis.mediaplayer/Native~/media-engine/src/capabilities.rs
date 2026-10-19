//! The engine-declared capability set: a snapshot of what this build will
//! decode and play, read by the resolver's format selection and the
//! managed layer. Every entry is a will-decode claim for the route the
//! engine would take. Probed routes (the Store VP9 extension) are checked
//! when the set is built; constant routes (in-box MFTs, the in-process
//! floors) are stated outright.
//!
//! Runtime changes such as a software fallback engaging surface as
//! diagnostics events (`DecodeFallbackHwToSw` is the re-query advisory),
//! and the consumer queries again.

use serde::Serialize;

/// Contract version (not the engine version). Bump on breaking shape
/// changes; additive fields do not bump it.
pub const CAPABILITIES_VERSION: u32 = 1;

#[derive(Debug, Clone, Serialize)]
pub struct CapabilitySet {
    pub version: u32,
    /// "windows-x64", "android-arm64", "linux-x64", or `<os>-<arch>`
    /// elsewhere.
    pub platform: String,
    pub video: Vec<VideoCap>,
    pub audio: Vec<AudioCap>,
    pub transports: Vec<TransportCap>,
    pub containers: Vec<String>,
}

/// Route the engine would take for the codec. Both routes may appear for
/// one codec.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum Route {
    /// Hardware-accelerated decode: DXVA on Windows (claimed only when the
    /// MFT is present and `ID3D11VideoDevice` offers a matching
    /// profile, format and config), MediaCodec hardware on Android.
    Hardware,
    /// CPU decode: platform software MFTs and the in-process floors.
    Software,
}

#[derive(Debug, Clone, Serialize)]
pub struct VideoCap {
    pub codec: String,
    pub route: Route,
    /// 0 = unstated. Hardware routes state measured ceilings (the DXVA
    /// resolution-ladder walk on Windows, the Quest MediaCodecList
    /// figures on Android); software routes state the policy ceiling
    /// (1920/1088/60) the engine enforces.
    pub max_width: u32,
    pub max_height: u32,
    pub max_fps: u32,
}

#[derive(Debug, Clone, Serialize)]
pub struct AudioCap {
    pub codec: String,
    pub max_channels: u32,
}

#[derive(Debug, Clone, Serialize)]
pub struct TransportCap {
    pub scheme: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub note: Option<String>,
}

/// A software route's entry. It states the enforced ceiling so resolvers
/// rank against the same numbers the engine refuses on.
#[cfg(not(target_os = "android"))]
fn software_video(codec: &str) -> VideoCap {
    VideoCap {
        codec: codec.into(),
        route: Route::Software,
        max_width: crate::route::SOFTWARE_CAP_WIDTH,
        max_height: crate::route::SOFTWARE_CAP_HEIGHT,
        max_fps: crate::route::SOFTWARE_CAP_FPS as u32,
    }
}

fn audio(codec: &str, max_channels: u32) -> AudioCap {
    AudioCap {
        codec: codec.into(),
        max_channels,
    }
}

fn transport(scheme: &str) -> TransportCap {
    TransportCap {
        scheme: scheme.into(),
        note: None,
    }
}

/// Windows video routes. Hardware DXVA entries carry the probe's measured
/// resolution ceiling (fps is unstated: DXVA has no rate ceiling to
/// measure). The CPU routes are listed as fallbacks with the software
/// policy ceiling.
#[cfg(windows)]
fn platform_video_caps() -> Vec<VideoCap> {
    use decode_mf::HwCodec;
    let mut video_caps = Vec::new();
    let hardware = [
        ("h264", HwCodec::H264),
        ("h265", HwCodec::H265),
        ("vp9", HwCodec::Vp9),
        ("av1", HwCodec::Av1),
    ];
    for (codec, hw) in hardware {
        if let Some((max_width, max_height)) = decode_mf::probe_hardware_ceiling(hw) {
            video_caps.push(VideoCap {
                codec: codec.into(),
                route: Route::Hardware,
                max_width,
                max_height,
                max_fps: 0,
            });
        }
    }
    video_caps.push(software_video("h264"));
    if decode_mf::probe_vp9() {
        video_caps.push(software_video("vp9"));
    }
    // AV1's software route is rav1d, always compiled in. The Store AV1
    // extension contributes no entry: it misbehaves when driven
    // synchronously and is kept to a runtime fallback.
    video_caps.push(software_video("av1"));
    video_caps
}

/// Android video routes: every entry is the platform MediaCodec decoder,
/// probed by creating it. The codec name separates hardware from the
/// c2.android software fallbacks, and the ceilings are the
/// `MediaCodecList` figures when the JVM probe is available (0 means
/// unstated; rank conservatively).
#[cfg(target_os = "android")]
fn platform_video_caps() -> Vec<VideoCap> {
    use decode_mediacodec::VideoMime;
    let mimes = [
        ("h264", VideoMime::H264),
        ("h265", VideoMime::H265),
        ("vp8", VideoMime::Vp8),
        ("vp9", VideoMime::Vp9),
        ("av1", VideoMime::Av1),
    ];
    mimes
        .into_iter()
        .filter_map(|(codec, mime)| {
            decode_mediacodec::probe_video_decoder(mime).map(|probe| VideoCap {
                codec: codec.into(),
                route: if probe.hardware {
                    Route::Hardware
                } else {
                    Route::Software
                },
                max_width: probe.max_width,
                max_height: probe.max_height,
                max_fps: probe.max_fps,
            })
        })
        .collect()
}

/// Headless platforms: the only video route is AV1 on rav1d, under the
/// software policy ceiling.
#[cfg(not(any(windows, target_os = "android")))]
fn platform_video_caps() -> Vec<VideoCap> {
    vec![software_video("av1")]
}

/// Build the capability set for this process. Cheap enough to rebuild on
/// every query (one decoder enumerate and activate per probed codec).
/// Callers cache; the engine does not, because a re-query after a
/// capability-change diagnostic must re-probe.
pub fn capabilities() -> CapabilitySet {
    let platform = if cfg!(all(windows, target_arch = "x86_64")) {
        "windows-x64".to_string()
    } else if cfg!(all(target_os = "android", target_arch = "aarch64")) {
        "android-arm64".to_string()
    } else if cfg!(all(target_os = "linux", target_arch = "x86_64")) {
        "linux-x64".to_string()
    } else {
        format!("{}-{}", std::env::consts::OS, std::env::consts::ARCH)
    };

    let video_caps = platform_video_caps();

    // Audio ceilings match the adapters' own checks: AAC chan_conf 1..=6,
    // MP3 mono/stereo, Opus up to 7.1, claxon's 8-channel cap
    // and the PCM adapter's 1..=8. These hold on Android too, since the
    // demux-side AAC channel check and the in-process Opus/FLAC/PCM
    // decoders are platform-free. Headless platforms list only the
    // in-process decoders: AAC and MP3 need platform decoders that do not
    // exist there.
    #[cfg(any(windows, target_os = "android"))]
    let audio_caps = vec![
        audio("aac", 6),
        audio("mp3", 2),
        audio("opus", 8),
        audio("flac", 8),
        audio("pcm", 8),
    ];
    #[cfg(not(any(windows, target_os = "android")))]
    let audio_caps = vec![audio("opus", 8), audio("flac", 8), audio("pcm", 8)];

    // `rist` appears only when the feature is compiled in; without it the
    // stub's typed refusal is the runtime backstop.
    let mut transports = vec![
        transport("file"),
        transport("http"),
        transport("https"),
        transport("rtsp"),
        transport("rtspt"),
        transport("whep"),
        transport("wheps"),
    ];
    if cfg!(feature = "rist") {
        transports.push(transport("rist"));
    }

    let containers = [
        "mp4", "ts", "m2ts", "mkv", "webm", "hls", "wav", "flac", "mp3", "adts", "ogg",
    ]
    .map(String::from)
    .to_vec();

    CapabilitySet {
        version: CAPABILITIES_VERSION,
        platform,
        video: video_caps,
        audio: audio_caps,
        transports,
        containers,
    }
}

impl CapabilitySet {
    /// The versioned UTF-8 JSON blob that crosses the ABI.
    pub fn to_json(&self) -> String {
        serde_json::to_string(self).expect("capability set serialises")
    }
}
