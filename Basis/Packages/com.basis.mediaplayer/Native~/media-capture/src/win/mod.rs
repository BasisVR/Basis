mod audio;
mod encoder;
mod sources;
mod video;

use std::sync::Arc;
use std::thread::JoinHandle;

use crate::{CaptureError, Shared, SourceInfo};

pub(crate) use sources::{Target, resolve};

pub fn sources() -> Vec<SourceInfo> {
    sources::list()
}

pub fn supported() -> bool {
    windows::Graphics::Capture::GraphicsCaptureSession::IsSupported().unwrap_or(false)
}

pub fn open(shared: &Arc<Shared>) -> Result<Vec<JoinHandle<()>>, CaptureError> {
    if !supported() {
        return Err(CaptureError(
            "Windows Graphics Capture is not available (Windows 10 1903 or later)".into(),
        ));
    }
    let target = resolve(&shared.config.source_id).ok_or_else(|| {
        CaptureError(format!(
            "capture source {} is gone",
            shared.config.source_id
        ))
    })?;
    let mut threads = Vec::new();
    let video_shared = Arc::clone(shared);
    threads.push(
        std::thread::Builder::new()
            .name("bm-capture-video".into())
            .spawn(move || video::run(video_shared, target))
            .map_err(|e| CaptureError(format!("spawn capture thread: {e}")))?,
    );
    if shared.config.audio {
        let audio_shared = Arc::clone(shared);
        threads.push(
            std::thread::Builder::new()
                .name("bm-capture-audio".into())
                .spawn(move || audio::run(audio_shared, target))
                .map_err(|e| CaptureError(format!("spawn audio thread: {e}")))?,
        );
    }
    Ok(threads)
}

pub(crate) fn hr(result: windows::core::Result<()>, what: &str) -> Result<(), CaptureError> {
    result.map_err(|e| CaptureError(format!("{what}: {e}")))
}

pub(crate) fn ok<T>(result: windows::core::Result<T>, what: &str) -> Result<T, CaptureError> {
    result.map_err(|e| CaptureError(format!("{what}: {e}")))
}
