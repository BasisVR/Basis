//! The capability contract. The serialisation shape is pinned byte-exact
//! because the blob is a versioned ABI surface and field renames are
//! breaking. The built set is checked against what this build routes.

use media_engine::{AudioCap, CapabilitySet, Route, TransportCap, VideoCap, capabilities};

/// The exact wire shape, pinned. A failure here means the contract
/// changed: bump `CAPABILITIES_VERSION` if the change is breaking, and
/// update the normative comment on `bm_capabilities` either way.
#[test]
fn serialisation_shape_pinned() {
    let set = CapabilitySet {
        version: 1,
        platform: "windows-x64".into(),
        video: vec![VideoCap {
            codec: "h264".into(),
            route: Route::Software,
            max_width: 0,
            max_height: 0,
            max_fps: 0,
        }],
        audio: vec![AudioCap {
            codec: "aac".into(),
            max_channels: 6,
        }],
        transports: vec![
            TransportCap {
                scheme: "http".into(),
                note: None,
            },
            TransportCap {
                scheme: "rist".into(),
                note: Some("not built".into()),
            },
        ],
        containers: vec!["mp4".into()],
    };
    assert_eq!(
        set.to_json(),
        r#"{"version":1,"platform":"windows-x64","video":[{"codec":"h264","route":"software","max_width":0,"max_height":0,"max_fps":0}],"audio":[{"codec":"aac","max_channels":6}],"transports":[{"scheme":"http"},{"scheme":"rist","note":"not built"}],"containers":["mp4"]}"#
    );
}

#[test]
fn hardware_route_serialises_lowercase() {
    let cap = VideoCap {
        codec: "h264".into(),
        route: Route::Hardware,
        max_width: 4096,
        max_height: 2304,
        max_fps: 30,
    };
    assert_eq!(
        serde_json::to_string(&cap).unwrap(),
        r#"{"codec":"h264","route":"hardware","max_width":4096,"max_height":2304,"max_fps":30}"#
    );
}

#[cfg(windows)]
#[test]
fn built_set_matches_this_build() {
    let set = capabilities();
    assert_eq!(set.version, media_engine::CAPABILITIES_VERSION);
    assert_eq!(set.platform, "windows-x64");

    // Software routes: H.264 (in-box MFT) and AV1 (rav1d) are constant;
    // VP9 appears exactly when the Store extension probe finds a decoder.
    // Every software entry states the enforced ceiling.
    let software = |c: &str| {
        set.video
            .iter()
            .find(|v| v.codec == c && v.route == Route::Software)
    };
    assert!(software("h264").is_some());
    assert!(software("av1").is_some());
    assert_eq!(software("vp9").is_some(), decode_mf::probe_vp9());
    for cap in set.video.iter().filter(|v| v.route == Route::Software) {
        assert_eq!(
            (cap.max_width, cap.max_height, cap.max_fps),
            (1920, 1088, 60),
            "{}",
            cap.codec
        );
    }
    // Hardware entries appear exactly where the DXVA probe passes, with a
    // measured resolution ceiling (fps unstated).
    for (codec, hw) in [
        ("h264", decode_mf::HwCodec::H264),
        ("h265", decode_mf::HwCodec::H265),
        ("vp9", decode_mf::HwCodec::Vp9),
        ("av1", decode_mf::HwCodec::Av1),
    ] {
        let entry = set
            .video
            .iter()
            .find(|v| v.codec == codec && v.route == Route::Hardware);
        match decode_mf::probe_hardware_ceiling(hw) {
            Some((w, h)) => {
                let entry = entry.unwrap_or_else(|| panic!("{codec}: probed but not listed"));
                assert_eq!((entry.max_width, entry.max_height), (w, h), "{codec}");
            }
            None => assert!(entry.is_none(), "{codec}: listed but not probed"),
        }
    }

    // The adapters' own channel limits.
    let channels = |c: &str| {
        set.audio
            .iter()
            .find(|a| a.codec == c)
            .map(|a| a.max_channels)
    };
    assert_eq!(channels("aac"), Some(6));
    assert_eq!(channels("mp3"), Some(2));
    assert_eq!(channels("opus"), Some(8));
    assert_eq!(channels("flac"), Some(8));
    // Integer PCM: RIFF/WAVE and the LPCM carried in MPEG-TS, one adapter,
    // 1..=8 channels.
    assert_eq!(channels("pcm"), Some(8));

    let scheme = |s: &str| set.transports.iter().any(|t| t.scheme == s);
    for s in ["file", "http", "https", "rtsp", "rtspt"] {
        assert!(scheme(s), "{s}");
    }
    assert_eq!(scheme("rist"), cfg!(feature = "rist"));

    for c in [
        "mp4", "ts", "m2ts", "mkv", "webm", "hls", "wav", "flac", "mp3", "adts", "ogg",
    ] {
        assert!(set.containers.iter().any(|x| x == c), "{c}");
    }
}

/// Headless builds route only the in-process decoders. Every entry is a
/// will-decode claim for a decoder compiled into this binary, so the
/// platform codecs (H.264/AAC/MP3) must be absent.
#[cfg(not(any(windows, target_os = "android")))]
#[test]
fn built_set_matches_this_build() {
    let set = capabilities();
    assert_eq!(set.version, media_engine::CAPABILITIES_VERSION);
    if cfg!(all(target_os = "linux", target_arch = "x86_64")) {
        assert_eq!(set.platform, "linux-x64");
    }

    let codec = |c: &str| set.video.iter().find(|v| v.codec == c);
    assert!(codec("av1").is_some());
    for absent in ["h264", "h265", "vp8", "vp9"] {
        assert!(codec(absent).is_none(), "{absent}");
    }
    for cap in &set.video {
        assert_eq!(cap.route, Route::Software, "{}", cap.codec);
        // Software entries state the enforced ceiling.
        assert_eq!(
            (cap.max_width, cap.max_height, cap.max_fps),
            (1920, 1088, 60)
        );
    }

    let channels = |c: &str| {
        set.audio
            .iter()
            .find(|a| a.codec == c)
            .map(|a| a.max_channels)
    };
    assert_eq!(channels("opus"), Some(8));
    assert_eq!(channels("flac"), Some(8));
    // PCM needs no platform decoder, so it is present here too.
    assert_eq!(channels("pcm"), Some(8));
    for absent in ["aac", "mp3"] {
        assert_eq!(channels(absent), None, "{absent}");
    }

    let scheme = |s: &str| set.transports.iter().any(|t| t.scheme == s);
    for s in ["file", "http", "https", "rtsp", "rtspt", "whep", "wheps"] {
        assert!(scheme(s), "{s}");
    }
    assert_eq!(scheme("rist"), cfg!(feature = "rist"));
}
