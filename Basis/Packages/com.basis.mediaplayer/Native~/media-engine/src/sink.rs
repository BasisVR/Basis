//! Platform video sink: the video thread's half of presentation.
//!
//! Frame selection normally happens in the Unity render event
//! (`present.rs`). This sink configures the output target and carries the
//! tick-paced fallback for consumers that issue no render events, such as
//! headless sessions. On Windows the shared D3D11 texture presenter lives
//! in `PipelineShared::presenter`, so the render event and the fallback
//! drive the same conversion pass. On Android the conversion pass needs
//! Unity's device, and a fallback-presented frame is just consumed.

use crate::pipeline::PipelineShared;
use crate::pool::Lease;

#[cfg(windows)]
pub struct VideoSink {
    configured: bool,
}

#[cfg(windows)]
impl VideoSink {
    pub fn new() -> Self {
        Self { configured: false }
    }

    /// (Re)build the shared texture for a newly announced coded size and
    /// expose its handle to the managed side. `decode_device` is the
    /// hardware route's D3D11 device: the presenter builds on it so decoded
    /// slices bind straight into the conversion pass. `None` (software
    /// routes) keeps the presenter's own device.
    ///
    /// # Safety
    /// `decode_device`, when `Some`, must be a live `ID3D11Device*` for the
    /// duration of the call. The presenter clones its own reference, so the
    /// caller's may drop once this returns. If the device went away during
    /// the call, the vtable dispatch inside `new_on_device` would read freed
    /// memory as a function pointer.
    pub unsafe fn configure(
        &mut self,
        px: &PipelineShared,
        coded_width: u32,
        coded_height: u32,
        decode_device: Option<*mut std::ffi::c_void>,
    ) -> Result<(), media_present::PresentError> {
        let mut presenter = match decode_device {
            // SAFETY: the contract above admits only a device live
            // across this call; new_on_device clones its own reference.
            Some(device) => unsafe {
                media_present::SharedTexturePresenter::new_on_device(
                    device,
                    coded_width.max(2),
                    coded_height.max(2),
                )?
            },
            None => {
                media_present::SharedTexturePresenter::new(coded_width.max(2), coded_height.max(2))?
            }
        };
        let d3d12 = media_present::win_unity::host_is_d3d12();
        if d3d12 {
            presenter.enable_d3d12_handoff()?;
        }
        // Written on both paths, so it always describes the presenter
        // being installed.
        px.present.lookahead_events.store(
            if d3d12 { 2 } else { 1 },
            std::sync::atomic::Ordering::Relaxed,
        );
        px.shared.shared_texture_handle.store(
            presenter.shared_handle(),
            std::sync::atomic::Ordering::Release,
        );
        *px.presenter.lock().expect("presenter lock") = Some(presenter);
        self.configured = true;
        Ok(())
    }

    pub fn ready(&self) -> bool {
        self.configured
    }

    /// Fallback present: convert and publish one due frame. `Ok(false)` =
    /// the frame was dropped, never blocking the pipeline: the consumer
    /// still owned the texture, or a replaced decoder's device holds it.
    pub fn present(
        &mut self,
        px: &PipelineShared,
        lease: &mut Lease,
    ) -> Result<bool, media_present::PresentError> {
        let mut slot = px.presenter.lock().expect("presenter lock");
        let Some(presenter) = slot.as_mut() else {
            return Ok(false);
        };
        present_lease_frame(presenter, lease)
    }
}

/// Convert one leased frame through the presenter. Shared by the video
/// thread's fallback present and the render event (`present.rs`). The
/// DXVA slice stays alive for the whole call (the lease holds the frame),
/// so the GPU copy is ordered ahead of the decoder reusing the surface.
#[cfg(windows)]
pub fn present_lease_frame(
    presenter: &mut media_present::SharedTexturePresenter,
    lease: &Lease,
) -> Result<bool, media_present::PresentError> {
    match lease.frame() {
        Some(media_decode::VideoFrame::Nv12(frame)) => {
            presenter.present_planes(frame.width, frame.height, &frame.data, frame.color)
        }
        Some(media_decode::VideoFrame::Opaque(frame)) => {
            match frame.image.d3d11_slice() {
                // SAFETY: the payload guarantees texture+index valid for
                // its own lifetime, which spans this call via the lease.
                // A slice from a decoder since replaced sits on another
                // device, which `present_slice` checks and drops.
                Some((texture, subresource)) => unsafe {
                    presenter.present_slice(texture, subresource, frame.color)
                },
                // A non-D3D11 opaque payload cannot occur on Windows;
                // tolerate rather than crash the thread.
                None => Ok(false),
            }
        }
        None => Ok(false),
    }
}

#[cfg(target_os = "android")]
pub struct VideoSink {
    configured: bool,
}

#[cfg(target_os = "android")]
impl VideoSink {
    pub fn new() -> Self {
        Self { configured: false }
    }

    /// # Safety
    /// As the Windows implementation, so the signature is the same on
    /// every target. This one never dereferences `decode_device`.
    pub unsafe fn configure(
        &mut self,
        _px: &PipelineShared,
        _coded_width: u32,
        _coded_height: u32,
        _decode_device: Option<*mut std::ffi::c_void>,
    ) -> Result<(), media_present::PresentError> {
        // The output target is Unity's own RenderTexture, registered
        // through the ABI; nothing to build producer-side.
        self.configured = true;
        Ok(())
    }

    pub fn ready(&self) -> bool {
        self.configured
    }

    /// Fallback present: the Vulkan conversion pass runs only inside a
    /// render event, so with no render consumer the due frame is consumed
    /// here. Position, EOS and buffer accounting keep moving, and the
    /// frame's buffer returns to its image reader.
    pub fn present(
        &mut self,
        _px: &PipelineShared,
        lease: &mut Lease,
    ) -> Result<bool, media_present::PresentError> {
        Ok(lease.take_frame().is_some())
    }
}

/// Headless platforms have no present target. The tick-paced fallback
/// consumes each due frame to keep position, EOS and buffer accounting
/// moving, and a consumed frame counts as presented.
#[cfg(not(any(windows, target_os = "android")))]
pub struct VideoSink {
    configured: bool,
}

#[cfg(not(any(windows, target_os = "android")))]
impl VideoSink {
    pub fn new() -> Self {
        Self { configured: false }
    }

    /// # Safety
    /// As the Windows implementation, so the signature is the same on
    /// every target. This one never dereferences `decode_device`.
    pub unsafe fn configure(
        &mut self,
        _px: &PipelineShared,
        _coded_width: u32,
        _coded_height: u32,
        _decode_device: Option<*mut std::ffi::c_void>,
    ) -> Result<(), media_present::PresentError> {
        self.configured = true;
        Ok(())
    }

    pub fn ready(&self) -> bool {
        self.configured
    }

    pub fn present(
        &mut self,
        _px: &PipelineShared,
        lease: &mut Lease,
    ) -> Result<bool, media_present::PresentError> {
        Ok(lease.take_frame().is_some())
    }
}
