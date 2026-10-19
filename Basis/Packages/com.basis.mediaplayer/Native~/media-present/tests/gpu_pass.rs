//! The conversion pass validated against the CPU reference: synthetic
//! sweeps across every stated matrix/range, agreement with an integer
//! BT.601 conversion, and real decoded fixture frames through the full
//! producer→consumer handoff.

#![cfg(windows)]

use media_decode::{ColorInfo, VideoDecoder, YuvMatrix, YuvRange};
use media_present::{SharedTextureConsumer, SharedTexturePresenter, TestConsumerTarget, reference};

/// GPU UNORM rounding vs CPU f32 rounding can differ by one code value
/// either side.
const TOLERANCE: u8 = 2;

/// A deterministic NV12 image exercising the full code range, including
/// the out-of-swing values a limited-range transform clamps.
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

/// Round-trip one frame through presenter → shared texture → consumer →
/// readback, returning tightly packed BGRA.
fn gpu_convert(
    presenter: &mut SharedTexturePresenter,
    consumer: &mut SharedTextureConsumer,
    target: &TestConsumerTarget,
    width: u32,
    height: u32,
    nv12: &[u8],
    color: ColorInfo,
) -> Vec<u8> {
    assert!(
        presenter
            .present_planes(width, height, nv12, color)
            .expect("present"),
        "producer failed to acquire its own fresh texture"
    );
    assert!(consumer.copy_if_fresh().expect("copy"), "no fresh frame");
    target.read_back().expect("read back")
}

#[test]
fn gpu_matches_reference_across_matrices_and_ranges() {
    let (w, h) = (64u32, 32u32);
    let nv12 = synthetic_nv12(w as usize, h as usize);
    let mut presenter = SharedTexturePresenter::new(w, h).expect("presenter");
    let target = TestConsumerTarget::new(w, h).expect("target");
    let mut consumer =
        // SAFETY: the target texture is live for the whole test and its
        // device can open the presenter's shared handle.
        unsafe { SharedTextureConsumer::open(target.texture_ptr(), presenter.shared_handle()) }
            .expect("consumer");

    for matrix in [
        YuvMatrix::Unspecified,
        YuvMatrix::Bt601,
        YuvMatrix::Bt709,
        YuvMatrix::Bt2020,
    ] {
        for range in [YuvRange::Unspecified, YuvRange::Limited, YuvRange::Full] {
            let color = ColorInfo { matrix, range };
            let gpu = gpu_convert(&mut presenter, &mut consumer, &target, w, h, &nv12, color);
            let mut cpu = Vec::new();
            reference::nv12_to_bgra(w, h, &nv12, color, w, h, &mut cpu);
            let diff = max_channel_diff(&gpu, &cpu);
            assert!(
                diff <= TOLERANCE,
                "GPU vs reference diverged by {diff} for {color:?}"
            );
        }
    }
}

/// An independent integer BT.601-limited conversion as a second oracle for
/// the float reference.
#[test]
fn reference_agrees_with_the_shipped_integer_convert() {
    fn integer_bt601_limited(width: usize, height: usize, data: &[u8], out: &mut Vec<u8>) {
        out.resize(width * height * 4, 0);
        let y_plane = &data[..width * height];
        let uv_plane = &data[width * height..];
        for row in 0..height {
            for col in 0..width {
                let c = y_plane[row * width + col] as i32 - 16;
                let d = uv_plane[(row / 2) * width + (col & !1)] as i32 - 128;
                let e = uv_plane[(row / 2) * width + (col | 1)] as i32 - 128;
                let r = (298 * c + 409 * e + 128) >> 8;
                let g = (298 * c - 100 * d - 208 * e + 128) >> 8;
                let b = (298 * c + 516 * d + 128) >> 8;
                let px = &mut out[(row * width + col) * 4..][..4];
                px[0] = b.clamp(0, 255) as u8;
                px[1] = g.clamp(0, 255) as u8;
                px[2] = r.clamp(0, 255) as u8;
                px[3] = 255;
            }
        }
    }

    let (w, h) = (64usize, 64usize);
    let nv12 = synthetic_nv12(w, h);
    let mut old = Vec::new();
    integer_bt601_limited(w, h, &nv12, &mut old);
    let mut new = Vec::new();
    reference::nv12_to_bgra(
        w as u32,
        h as u32,
        &nv12,
        ColorInfo::default(),
        w as u32,
        h as u32,
        &mut new,
    );
    let diff = max_channel_diff(&old, &new);
    assert!(
        diff <= TOLERANCE,
        "reference departs from the shipped integer maths by {diff}"
    );
}

/// Real decoded frames from the A/V fixture through the full handoff.
#[test]
fn gpu_matches_reference_on_fixture_frames() {
    let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../fixtures/h264-aac-640x360-30fps.mp4");
    let bytes = std::fs::read(&path).expect("fixture bytes");
    let mut demuxer = media_demux::open_auto(
        Box::new(media_demux::MemSource(bytes)),
        media_demux::DemuxLimits::default(),
        media_clock::Generation(0),
    )
    .expect("open fixture");

    let mut video_track = None;
    let mut decoder = decode_mf::H264Decoder::new(false).expect("decoder");
    let mut checked = 0usize;
    let mut rig: Option<(
        SharedTexturePresenter,
        TestConsumerTarget,
        SharedTextureConsumer,
    )> = None;

    'outer: loop {
        let event = demuxer.next_event().expect("demux event");
        match event {
            media_demux::StreamEvent::Format(track, media_demux::Format::Video { .. }) => {
                video_track = Some(track);
            }
            media_demux::StreamEvent::Au(au) if Some(au.track) == video_track => {
                let mut submitted = false;
                while !submitted {
                    submitted = matches!(
                        decoder
                            .submit(&au.data, au.pts.as_micros())
                            .expect("submit"),
                        media_decode::SubmitOutcome::Accepted
                    );
                    while let Some(frame) = decoder.try_output().expect("output") {
                        let frame = frame.as_nv12().expect("MF frames are NV12");
                        let (presenter, target, consumer) = rig.get_or_insert_with(|| {
                            let presenter = SharedTexturePresenter::new(frame.width, frame.height)
                                .expect("presenter");
                            let target =
                                TestConsumerTarget::new(frame.width, frame.height).expect("target");
                            let consumer =
                                // SAFETY: target texture lives alongside the
                                // consumer in the same rig tuple.
                                unsafe {
                                    SharedTextureConsumer::open(
                                        target.texture_ptr(),
                                        presenter.shared_handle(),
                                    )
                                }
                                .expect("consumer");
                            (presenter, target, consumer)
                        });
                        let gpu = gpu_convert(
                            presenter,
                            consumer,
                            target,
                            frame.width,
                            frame.height,
                            &frame.data,
                            frame.color,
                        );
                        let mut cpu = Vec::new();
                        reference::nv12_to_bgra(
                            frame.width,
                            frame.height,
                            &frame.data,
                            frame.color,
                            frame.width,
                            frame.height,
                            &mut cpu,
                        );
                        let diff = max_channel_diff(&gpu, &cpu);
                        assert!(
                            diff <= TOLERANCE,
                            "GPU vs reference diverged by {diff} on fixture frame {checked} \
                             ({:?})",
                            frame.color
                        );
                        checked += 1;
                        if checked >= 8 {
                            break 'outer;
                        }
                    }
                }
            }
            media_demux::StreamEvent::Eos(_) => break,
            _ => {}
        }
    }
    assert!(checked >= 8, "only validated {checked} fixture frames");
}

/// The DXVA input path: `present_slice` on a presenter sharing the
/// decode device must convert exactly the addressed texture-array slice,
/// honouring the MFT's subresource index rather than assuming slice 0. Two
/// slices carry distinct patterns; each present must reproduce its own
/// slice's reference conversion.
#[test]
fn present_slice_honours_the_subresource_index() {
    use windows::Win32::Graphics::Direct3D::D3D_DRIVER_TYPE_HARDWARE;
    use windows::Win32::Graphics::Direct3D11::{
        D3D11_CPU_ACCESS_WRITE, D3D11_CREATE_DEVICE_BGRA_SUPPORT, D3D11_MAP_WRITE,
        D3D11_SDK_VERSION, D3D11_TEXTURE2D_DESC, D3D11_USAGE_DEFAULT, D3D11_USAGE_STAGING,
        D3D11CreateDevice,
    };
    use windows::Win32::Graphics::Dxgi::Common::{DXGI_FORMAT_NV12, DXGI_SAMPLE_DESC};
    use windows::core::Interface;

    let (w, h) = (64u32, 32u32);
    let patterns: Vec<Vec<u8>> = (0..2u8)
        .map(|slice| {
            let mut p = synthetic_nv12(w as usize, h as usize);
            for byte in p.iter_mut() {
                *byte = byte.wrapping_add(slice * 41);
            }
            p
        })
        .collect();

    // SAFETY: D3D11 object creation through owned wrappers; the staging
    // map writes stay inside RowPitch * height * 3 / 2 (the planar
    // layout), and every interface out-param is checked.
    unsafe {
        let mut device = None;
        let mut context = None;
        D3D11CreateDevice(
            None,
            D3D_DRIVER_TYPE_HARDWARE,
            Default::default(),
            D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            None,
            D3D11_SDK_VERSION,
            Some(&mut device),
            None,
            Some(&mut context),
        )
        .expect("device");
        let device = device.expect("device");
        let context = context.expect("context");

        let array_desc = D3D11_TEXTURE2D_DESC {
            Width: w,
            Height: h,
            MipLevels: 1,
            ArraySize: 2,
            Format: DXGI_FORMAT_NV12,
            SampleDesc: DXGI_SAMPLE_DESC {
                Count: 1,
                Quality: 0,
            },
            Usage: D3D11_USAGE_DEFAULT,
            BindFlags: 0,
            CPUAccessFlags: 0,
            MiscFlags: 0,
        };
        let mut array = None;
        device
            .CreateTexture2D(&array_desc, None, Some(&mut array))
            .expect("array texture");
        let array = array.expect("array texture");

        let staging_desc = D3D11_TEXTURE2D_DESC {
            ArraySize: 1,
            Usage: D3D11_USAGE_STAGING,
            CPUAccessFlags: D3D11_CPU_ACCESS_WRITE.0 as u32,
            ..array_desc
        };
        for (slice, pattern) in patterns.iter().enumerate() {
            let mut staging = None;
            device
                .CreateTexture2D(&staging_desc, None, Some(&mut staging))
                .expect("staging");
            let staging = staging.expect("staging");
            let mut mapped = Default::default();
            context
                .Map(&staging, 0, D3D11_MAP_WRITE, 0, Some(&mut mapped))
                .expect("map");
            let pitch = mapped.RowPitch as usize;
            let base = mapped.pData as *mut u8;
            let (wu, hu) = (w as usize, h as usize);
            for row in 0..hu {
                std::ptr::copy_nonoverlapping(
                    pattern.as_ptr().add(row * wu),
                    base.add(row * pitch),
                    wu,
                );
            }
            let uv = pattern.as_ptr().add(wu * hu);
            let uv_base = base.add(pitch * hu);
            for row in 0..hu / 2 {
                std::ptr::copy_nonoverlapping(uv.add(row * wu), uv_base.add(row * pitch), wu);
            }
            context.Unmap(&staging, 0);
            context.CopySubresourceRegion(&array, slice as u32, 0, 0, 0, &staging, 0, None);
        }

        let mut presenter =
            SharedTexturePresenter::new_on_device(device.as_raw(), w, h).expect("presenter");
        let target = TestConsumerTarget::new(w, h).expect("target");
        let mut consumer =
            SharedTextureConsumer::open(target.texture_ptr(), presenter.shared_handle())
                .expect("consumer");

        let color = ColorInfo {
            matrix: YuvMatrix::Bt709,
            range: YuvRange::Limited,
        };
        // Slice 1 first: an index-ignoring implementation would show
        // slice 0 here and fail against pattern 1's reference.
        for slice in [1usize, 0] {
            assert!(
                presenter
                    .present_slice(array.as_raw(), slice as u32, color)
                    .expect("present_slice"),
                "producer failed to acquire its own fresh texture"
            );
            assert!(consumer.copy_if_fresh().expect("copy"), "no fresh frame");
            let gpu = target.read_back().expect("read back");
            let mut cpu = Vec::new();
            reference::nv12_to_bgra(w, h, &patterns[slice], color, w, h, &mut cpu);
            let diff = max_channel_diff(&gpu, &cpu);
            assert!(
                diff <= TOLERANCE,
                "slice {slice}: GPU vs reference diverged by {diff}"
            );
        }
    }
}

/// A decoder rebuilt mid-stream brings a new device while frames the old
/// one decoded still wait to present. A copy between devices is undefined
/// in the driver, so the presenter drops such a slice; its own device's
/// slice still presents.
#[test]
fn present_slice_drops_a_slice_from_another_device() {
    use windows::Win32::Graphics::Direct3D::D3D_DRIVER_TYPE_HARDWARE;
    use windows::Win32::Graphics::Direct3D11::{
        D3D11_CREATE_DEVICE_BGRA_SUPPORT, D3D11_SDK_VERSION, D3D11_TEXTURE2D_DESC,
        D3D11_USAGE_DEFAULT, D3D11CreateDevice, ID3D11Device, ID3D11Texture2D,
    };
    use windows::Win32::Graphics::Dxgi::Common::{DXGI_FORMAT_NV12, DXGI_SAMPLE_DESC};
    use windows::core::Interface;

    let (w, h) = (64u32, 32u32);
    // SAFETY: D3D11 object creation through owned wrappers; every
    // interface out-param is checked.
    let slice_on_new_device = || unsafe {
        let mut device: Option<ID3D11Device> = None;
        D3D11CreateDevice(
            None,
            D3D_DRIVER_TYPE_HARDWARE,
            Default::default(),
            D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            None,
            D3D11_SDK_VERSION,
            Some(&mut device),
            None,
            None,
        )
        .expect("device");
        let device = device.expect("device");
        let desc = D3D11_TEXTURE2D_DESC {
            Width: w,
            Height: h,
            MipLevels: 1,
            ArraySize: 1,
            Format: DXGI_FORMAT_NV12,
            SampleDesc: DXGI_SAMPLE_DESC {
                Count: 1,
                Quality: 0,
            },
            Usage: D3D11_USAGE_DEFAULT,
            BindFlags: 0,
            CPUAccessFlags: 0,
            MiscFlags: 0,
        };
        let mut texture: Option<ID3D11Texture2D> = None;
        device
            .CreateTexture2D(&desc, None, Some(&mut texture))
            .expect("slice texture");
        (device, texture.expect("slice texture"))
    };
    let color = ColorInfo {
        matrix: YuvMatrix::Bt709,
        range: YuvRange::Limited,
    };

    let (_old_device, old_slice) = slice_on_new_device();
    let (new_device, new_slice) = slice_on_new_device();
    // SAFETY: both devices and both textures are live for the whole test.
    unsafe {
        let mut presenter =
            SharedTexturePresenter::new_on_device(new_device.as_raw(), w, h).expect("presenter");
        assert!(
            !presenter
                .present_slice(old_slice.as_raw(), 0, color)
                .expect("present_slice (old device)"),
            "a slice from another device was presented"
        );
        assert!(
            presenter
                .present_slice(new_slice.as_raw(), 0, color)
                .expect("present_slice (own device)"),
            "the presenter's own device's slice was not presented"
        );
    }
}

/// `CopyResource` between textures of different sizes is dropped without
/// an error, which would leave a picture that never updates. The consumer
/// refuses such a destination when it opens instead.
#[test]
fn the_consumer_refuses_a_destination_of_another_size() {
    let (w, h) = (64u32, 32u32);
    let presenter = SharedTexturePresenter::new(w, h).expect("presenter");
    for (tw, th) in [(w + 1, h), (w - 1, h), (w, h + 1), (w, h - 1)] {
        let target = TestConsumerTarget::new(tw, th).expect("target");
        // SAFETY: the target texture is live for the loop body, its device
        // can open the handle, and the presenter keeps the handle open.
        let opened =
            unsafe { SharedTextureConsumer::open(target.texture_ptr(), presenter.shared_handle()) };
        assert!(
            opened.is_err(),
            "a {tw}x{th} destination opened on a {w}x{h} texture"
        );
    }
    let target = TestConsumerTarget::new(w, h).expect("target");
    // SAFETY: as above.
    let opened =
        unsafe { SharedTextureConsumer::open(target.texture_ptr(), presenter.shared_handle()) };
    assert!(opened.is_ok(), "a matching destination was refused");
}

/// The presenter owns the NT handle `CreateSharedHandle` hands back, so
/// dropping it must close the handle. Otherwise every rebuild strands a
/// kernel handle and pins the texture's video memory for the process's life.
///
/// A duplicate pins the kernel object for the length of the test, so a
/// handle value the OS recycles after the close still reads as "no longer
/// the shared texture" rather than as a leak.
#[test]
fn dropping_the_presenter_closes_its_shared_handle() {
    use windows::Win32::Foundation::{
        CloseHandle, CompareObjectHandles, DUPLICATE_SAME_ACCESS, DuplicateHandle,
        GetHandleInformation, HANDLE,
    };
    use windows::Win32::System::Threading::GetCurrentProcess;

    let presenter = SharedTexturePresenter::new(64, 64).expect("presenter");
    let raw = HANDLE(presenter.shared_handle() as usize as *mut std::ffi::c_void);
    let mut dup = HANDLE::default();
    // SAFETY: handles this process owns; the duplicate is closed below.
    unsafe {
        let me = GetCurrentProcess();
        DuplicateHandle(me, raw, me, &mut dup, 0, false, DUPLICATE_SAME_ACCESS)
            .expect("duplicate the shared handle");
    }
    // SAFETY: both handles are live and name the same shared texture.
    let duplicated = unsafe { CompareObjectHandles(raw, dup) }.as_bool();
    if !duplicated {
        // Closed before failing: panicking here would leak the duplicate
        // for the rest of the binary, and the sibling rows in it build
        // D3D11 devices that recycle freed handle values.
        // SAFETY: `dup` was created above and is closed exactly once.
        unsafe {
            let _ = CloseHandle(dup);
        }
        panic!("the duplicate does not name the shared texture");
    }

    drop(presenter);

    let mut flags = 0u32;
    // SAFETY: reading a handle's flags and comparing object identity. In
    // an ordinary run a closed handle is reported as invalid; the
    // duplicate pins the object in case a sibling row recycles the freed
    // value. Under strict handle checking (which a debugger enables for its
    // child) the closed value raises instead, so this row is meant for a
    // plain `cargo test`.
    let still_ours = unsafe {
        GetHandleInformation(raw, &mut flags).is_ok() && CompareObjectHandles(raw, dup).as_bool()
    };
    // SAFETY: the duplicate is this test's own handle, closed exactly once.
    unsafe {
        let _ = CloseHandle(dup);
    }
    assert!(
        !still_ours,
        "shared texture handle still open after the presenter dropped"
    );
}
