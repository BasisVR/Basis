//! The Direct3D 12 handoff against a headless stand-in for Unity's
//! renderer: frames reach a D3D12 texture through the shared slots and
//! fence, a slot is never converted over while a recorded copy still reads
//! it, and a mismatched destination is refused rather than copied into.

#![cfg(windows)]

use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use media_decode::{ColorInfo, YuvMatrix, YuvRange};
use media_present::{
    D3d12Consumer, D3d12Host, SharedTexturePresenter, TestD3d12Host, reference, retired_count,
};
use windows::Win32::Graphics::Dxgi::Common::{
    DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_FORMAT_B8G8R8A8_UNORM_SRGB,
};
use windows::core::Interface;

/// The retired-object list is process-wide, so the rows take turns.
static SERIAL: Mutex<()> = Mutex::new(());

const TOLERANCE: u8 = 2;

/// Flat NV12 whose luma is `luma` everywhere, so frames are told apart by
/// their first pixel.
fn flat_nv12(width: usize, height: usize, luma: u8) -> Vec<u8> {
    let mut data = vec![128u8; width * height * 3 / 2];
    data[..width * height].fill(luma);
    data
}

fn synthetic_nv12(width: usize, height: usize) -> Vec<u8> {
    let mut data = vec![0u8; width * height * 3 / 2];
    for row in 0..height {
        for col in 0..width {
            data[row * width + col] = ((row * 7 + col * 13) % 256) as u8;
        }
    }
    let uv = &mut data[width * height..];
    for row in 0..height / 2 {
        for col in 0..width / 2 {
            uv[row * width + col * 2] = ((row * 11 + col * 5) % 256) as u8;
            uv[row * width + col * 2 + 1] = ((row * 3 + col * 17 + 128) % 256) as u8;
        }
    }
    data
}

fn max_channel_diff(a: &[u8], b: &[u8]) -> u8 {
    assert_eq!(a.len(), b.len(), "texture byte lengths differ");
    a.iter()
        .zip(b)
        .map(|(x, y)| x.abs_diff(*y))
        .max()
        .unwrap_or(0)
}

struct Rig {
    host: Arc<TestD3d12Host>,
    presenter: SharedTexturePresenter,
    destination: windows::Win32::Graphics::Direct3D12::ID3D12Resource,
    consumer: D3d12Consumer,
}

fn rig(w: u32, h: u32) -> Rig {
    let host = TestD3d12Host::new().expect("D3D12 host");
    let mut presenter = SharedTexturePresenter::new(w, h).expect("presenter");
    presenter.enable_d3d12_handoff().expect("D3D12 handoff");
    let destination = host
        .create_destination(w, h, DXGI_FORMAT_B8G8R8A8_UNORM_SRGB)
        .expect("destination");
    let consumer = {
        let host: Arc<dyn D3d12Host> = host.clone();
        // SAFETY: the destination is a live resource on the host's device
        // and outlives the consumer (both live in the rig).
        unsafe {
            D3d12Consumer::open(
                destination.as_raw(),
                presenter.d3d12_handoff().expect("handoff"),
                host,
            )
        }
        .expect("consumer")
    };
    Rig {
        host,
        presenter,
        destination,
        consumer,
    }
}

/// Present one frame. A present is refused while the conversion texture is
/// still busy on the GPU, so it is retried rather than counted as a failure.
fn present(presenter: &mut SharedTexturePresenter, w: u32, h: u32, nv12: &[u8], color: ColorInfo) {
    let deadline = Instant::now() + Duration::from_secs(2);
    while !presenter
        .present_planes(w, h, nv12, color)
        .expect("present")
    {
        assert!(
            Instant::now() < deadline,
            "no producer slot became available"
        );
        std::thread::sleep(Duration::from_millis(1));
    }
}

/// Copy once the frame the producer just published has finished on its
/// device. The copy itself never waits, so the row polls.
fn copy_when_ready(consumer: &mut D3d12Consumer) {
    let deadline = Instant::now() + Duration::from_secs(2);
    while !consumer.copy_if_fresh().expect("copy") {
        assert!(Instant::now() < deadline, "no frame became copyable");
        std::thread::sleep(Duration::from_millis(1));
    }
}

#[test]
fn a_frame_reaches_the_d3d12_texture_as_the_reference_converts_it() {
    let _serial = SERIAL.lock().unwrap_or_else(|e| e.into_inner());
    let (w, h) = (64u32, 32u32);
    let mut r = rig(w, h);
    let nv12 = synthetic_nv12(w as usize, h as usize);
    for matrix in [YuvMatrix::Bt601, YuvMatrix::Bt709] {
        for range in [YuvRange::Limited, YuvRange::Full] {
            let color = ColorInfo { matrix, range };
            present(&mut r.presenter, w, h, &nv12, color);
            copy_when_ready(&mut r.consumer);
            let gpu = r.host.read_back(&r.destination).expect("read back");
            let mut cpu = Vec::new();
            reference::nv12_to_bgra(w, h, &nv12, color, w, h, &mut cpu);
            let diff = max_channel_diff(&gpu, &cpu);
            assert!(diff <= TOLERANCE, "diverged by {diff} for {color:?}");
        }
    }
}

/// A copy recorded on the host's command list reads its slot when the host
/// submits, which can be frames later. Converting over that slot in the
/// meantime would put a later frame on screen than the one selected.
#[test]
fn a_slot_with_a_copy_pending_is_not_converted_over() {
    let _serial = SERIAL.lock().unwrap_or_else(|e| e.into_inner());
    let (w, h) = (16u32, 16u32);
    let mut r = rig(w, h);
    let color = ColorInfo::default();
    let first = flat_nv12(w as usize, h as usize, 200);
    present(&mut r.presenter, w, h, &first, color);
    copy_when_ready(&mut r.consumer);

    // The copy is recorded but not submitted. Every later frame has to go
    // round the other slots.
    for luma in [30u8, 60, 90, 120, 150, 180] {
        let frame = flat_nv12(w as usize, h as usize, luma);
        present(&mut r.presenter, w, h, &frame, color);
    }

    let shown = r.host.read_back(&r.destination).expect("read back");
    let mut want = Vec::new();
    reference::nv12_to_bgra(w, h, &first, color, w, h, &mut want);
    let diff = max_channel_diff(&shown, &want);
    assert!(
        diff <= TOLERANCE,
        "the recorded copy read a later frame (off by {diff})"
    );
}

#[test]
fn the_newest_finished_frame_is_the_one_copied() {
    let _serial = SERIAL.lock().unwrap_or_else(|e| e.into_inner());
    let (w, h) = (16u32, 16u32);
    let mut r = rig(w, h);
    let color = ColorInfo::default();
    for luma in [40u8, 80] {
        let frame = flat_nv12(w as usize, h as usize, luma);
        present(&mut r.presenter, w, h, &frame, color);
    }
    let mut want = Vec::new();
    reference::nv12_to_bgra(
        w,
        h,
        &flat_nv12(w as usize, h as usize, 80),
        color,
        w,
        h,
        &mut want,
    );
    // Both finished before the first copy: the older is then still
    // published when the newer is taken.
    r.presenter
        .wait_for_d3d12_publishes()
        .expect("wait for the publishes");
    assert!(
        r.consumer.copy_if_fresh().expect("copy"),
        "a finished frame was not copied"
    );
    let shown = r.host.read_back(&r.destination).expect("read back");
    assert!(
        max_channel_diff(&shown, &want) <= TOLERANCE,
        "the first copy took the older frame"
    );
    assert!(
        !r.consumer.copy_if_fresh().expect("copy"),
        "the older frame was copied after the newer one"
    );
}

/// A host that cannot say when its frame completes gets no copy: a slot
/// marked with a value the fence never reaches would never come free, and
/// three such copies would leave the producer nowhere to write.
#[test]
fn no_copy_is_recorded_without_a_frame_fence_value() {
    let _serial = SERIAL.lock().unwrap_or_else(|e| e.into_inner());
    let (w, h) = (16u32, 16u32);
    let mut r = rig(w, h);
    let color = ColorInfo::default();

    r.host.set_frame_value_missing(true);
    let frame = flat_nv12(w as usize, h as usize, 70);
    present(&mut r.presenter, w, h, &frame, color);
    r.presenter
        .wait_for_d3d12_publishes()
        .expect("wait for the publish");
    assert!(
        !r.consumer.copy_if_fresh().expect("copy"),
        "a copy was recorded with no frame-fence value"
    );

    // Once the host can answer, frames keep flowing through every slot.
    r.host.set_frame_value_missing(false);
    for luma in [90u8, 110, 130, 150, 170] {
        let frame = flat_nv12(w as usize, h as usize, luma);
        present(&mut r.presenter, w, h, &frame, color);
        copy_when_ready(&mut r.consumer);
        r.host.end_frame(true).expect("end frame");
    }
}

#[test]
fn a_destination_of_another_size_is_refused() {
    let _serial = SERIAL.lock().unwrap_or_else(|e| e.into_inner());
    let host = TestD3d12Host::new().expect("D3D12 host");
    let mut presenter = SharedTexturePresenter::new(64, 32).expect("presenter");
    presenter.enable_d3d12_handoff().expect("D3D12 handoff");
    let destination = host
        .create_destination(64, 36, DXGI_FORMAT_B8G8R8A8_UNORM)
        .expect("destination");
    let dyn_host: Arc<dyn D3d12Host> = host.clone();
    // SAFETY: a live resource on the host's device, outliving the call.
    let opened = unsafe {
        D3d12Consumer::open(
            destination.as_raw(),
            presenter.d3d12_handoff().expect("handoff"),
            dyn_host,
        )
    };
    assert!(opened.is_err(), "a mismatched destination was accepted");
}

/// Objects a pending copy reads are held past the consumer until the host's
/// frame fence passes that copy, then released.
#[test]
fn a_consumer_dropped_with_a_copy_pending_is_released_once_it_completes() {
    let _serial = SERIAL.lock().unwrap_or_else(|e| e.into_inner());
    let (w, h) = (16u32, 16u32);
    let mut r = rig(w, h);
    media_present::collect_retired();
    let before = retired_count();
    let frame = flat_nv12(w as usize, h as usize, 100);
    present(&mut r.presenter, w, h, &frame, ColorInfo::default());
    copy_when_ready(&mut r.consumer);
    drop(r.consumer);
    assert_eq!(
        retired_count(),
        before + 1,
        "a consumer with a copy pending was released"
    );
    r.host.end_frame(true).expect("end frame");
    media_present::collect_retired();
    assert_eq!(
        retired_count(),
        before,
        "the completed copy's objects were kept"
    );
}
