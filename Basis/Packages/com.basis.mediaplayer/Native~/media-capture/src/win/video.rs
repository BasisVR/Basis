use std::mem::ManuallyDrop;
use std::sync::atomic::Ordering;
use std::sync::{Arc, Condvar, Mutex};
use std::time::{Duration, Instant};

use windows::Foundation::TypedEventHandler;
use windows::Graphics::Capture::{
    Direct3D11CaptureFramePool, GraphicsCaptureItem, GraphicsCaptureSession,
};
use windows::Graphics::DirectX::Direct3D11::IDirect3DDevice;
use windows::Graphics::DirectX::DirectXPixelFormat;
use windows::Graphics::SizeInt32;
use windows::Win32::Foundation::{HMODULE, HWND, RECT};
use windows::Win32::Graphics::Direct3D::{
    D3D_DRIVER_TYPE_HARDWARE, D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_11_1,
};
use windows::Win32::Graphics::Direct3D11::{
    D3D11_BIND_RENDER_TARGET, D3D11_BIND_SHADER_RESOURCE, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
    D3D11_CREATE_DEVICE_VIDEO_SUPPORT, D3D11_SDK_VERSION, D3D11_TEX2D_VPIV, D3D11_TEX2D_VPOV,
    D3D11_TEXTURE2D_DESC, D3D11_USAGE_DEFAULT, D3D11_VIDEO_COLOR, D3D11_VIDEO_COLOR_0,
    D3D11_VIDEO_COLOR_RGBA, D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE,
    D3D11_VIDEO_PROCESSOR_COLOR_SPACE, D3D11_VIDEO_PROCESSOR_CONTENT_DESC,
    D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC, D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC_0,
    D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC, D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC_0,
    D3D11_VIDEO_PROCESSOR_STREAM, D3D11_VIDEO_USAGE_OPTIMAL_QUALITY,
    D3D11_VPIV_DIMENSION_TEXTURE2D, D3D11_VPOV_DIMENSION_TEXTURE2D, D3D11CreateDevice,
    ID3D11Device, ID3D11DeviceContext, ID3D11Multithread, ID3D11Texture2D, ID3D11VideoContext,
    ID3D11VideoDevice, ID3D11VideoProcessor, ID3D11VideoProcessorEnumerator,
    ID3D11VideoProcessorInputView, ID3D11VideoProcessorOutputView,
};
use windows::Win32::Graphics::Dxgi::Common::{
    DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_FORMAT_NV12, DXGI_RATIONAL, DXGI_SAMPLE_DESC,
};
use windows::Win32::Graphics::Dxgi::IDXGIDevice;
use windows::Win32::Graphics::Gdi::HMONITOR;
use windows::Win32::Media::MediaFoundation::{MF_VERSION, MFSTARTUP_LITE, MFShutdown, MFStartup};
use windows::Win32::System::Com::{COINIT_MULTITHREADED, CoInitializeEx};
use windows::Win32::System::WinRT::Direct3D11::{
    CreateDirect3D11DeviceFromDXGIDevice, IDirect3DDxgiInterfaceAccess,
};
use windows::Win32::System::WinRT::Graphics::Capture::IGraphicsCaptureItemInterop;
use windows::core::Interface;

use super::encoder::{Encoder, Settings};
use super::{Target, hr, ok};
use crate::{CaptureError, CaptureState, EncodedVideo, Shared, fit_size, letterbox};

const RING: usize = 8;
const REPEAT: Duration = Duration::from_millis(500);
const OUTPUT_COLOR_709_LIMITED: u32 = (1 << 2) | (1 << 4);

pub fn run(shared: Arc<Shared>, target: Target) {
    // SAFETY: per-thread COM and MF initialisation, balanced on exit.
    unsafe {
        let _ = CoInitializeEx(None, COINIT_MULTITHREADED);
        let _ = MFStartup(MF_VERSION, MFSTARTUP_LITE);
    }
    match capture(&shared, target) {
        Ok(()) => shared.update(|s| {
            if s.state != CaptureState::Error {
                s.state = CaptureState::Ended;
            }
        }),
        Err(e) => shared.fail(e.0),
    }
    // SAFETY: balances the MFStartup above.
    unsafe {
        let _ = MFShutdown();
    }
}

fn create_device() -> Result<(ID3D11Device, ID3D11DeviceContext), CaptureError> {
    let mut device = None;
    let mut context = None;
    // SAFETY: D3D11CreateDevice writes owned interfaces into the options.
    unsafe {
        hr(
            D3D11CreateDevice(
                None,
                D3D_DRIVER_TYPE_HARDWARE,
                HMODULE::default(),
                D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT,
                Some(&[D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0]),
                D3D11_SDK_VERSION,
                Some(&mut device),
                None,
                Some(&mut context),
            ),
            "D3D11CreateDevice",
        )?;
    }
    let device = device.ok_or_else(|| CaptureError("no D3D11 device".into()))?;
    let context = context.ok_or_else(|| CaptureError("no D3D11 context".into()))?;
    if let Ok(multithread) = device.cast::<ID3D11Multithread>() {
        // SAFETY: flag write on a live device.
        unsafe {
            let _ = multithread.SetMultithreadProtected(true);
        }
    }
    Ok((device, context))
}

fn texture(
    device: &ID3D11Device,
    width: u32,
    height: u32,
    format: windows::Win32::Graphics::Dxgi::Common::DXGI_FORMAT,
    bind: u32,
) -> Result<ID3D11Texture2D, CaptureError> {
    let desc = D3D11_TEXTURE2D_DESC {
        Width: width,
        Height: height,
        MipLevels: 1,
        ArraySize: 1,
        Format: format,
        SampleDesc: DXGI_SAMPLE_DESC {
            Count: 1,
            Quality: 0,
        },
        Usage: D3D11_USAGE_DEFAULT,
        BindFlags: bind,
        CPUAccessFlags: 0,
        MiscFlags: 0,
    };
    let mut out = None;
    hr(
        // SAFETY: descriptor is fully initialised; the texture is returned owned.
        unsafe { device.CreateTexture2D(&desc, None, Some(&mut out)) },
        "CreateTexture2D",
    )?;
    out.ok_or_else(|| CaptureError("no texture".into()))
}

struct Converter {
    video_device: ID3D11VideoDevice,
    video_context: ID3D11VideoContext,
    enumerator: ID3D11VideoProcessorEnumerator,
    processor: ID3D11VideoProcessor,
    source: ID3D11Texture2D,
    input: ID3D11VideoProcessorInputView,
    outputs: Vec<(ID3D11Texture2D, ID3D11VideoProcessorOutputView)>,
    source_size: (u32, u32),
    out_size: (u32, u32),
    fps: u32,
}

impl Converter {
    fn new(
        device: &ID3D11Device,
        context: &ID3D11DeviceContext,
        source_size: (u32, u32),
        out_size: (u32, u32),
        fps: u32,
    ) -> Result<Self, CaptureError> {
        let video_device: ID3D11VideoDevice = ok(device.cast(), "ID3D11VideoDevice")?;
        let video_context: ID3D11VideoContext = ok(context.cast(), "ID3D11VideoContext")?;
        let (enumerator, processor) = Self::processor(&video_device, source_size, out_size, fps)?;
        let source = texture(
            device,
            source_size.0,
            source_size.1,
            DXGI_FORMAT_B8G8R8A8_UNORM,
            (D3D11_BIND_RENDER_TARGET.0 | D3D11_BIND_SHADER_RESOURCE.0) as u32,
        )?;
        let input = Self::input_view(&video_device, &enumerator, &source)?;
        let mut outputs = Vec::with_capacity(RING);
        for _ in 0..RING {
            let target = texture(
                device,
                out_size.0,
                out_size.1,
                DXGI_FORMAT_NV12,
                D3D11_BIND_RENDER_TARGET.0 as u32,
            )?;
            let desc = D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC {
                ViewDimension: D3D11_VPOV_DIMENSION_TEXTURE2D,
                Anonymous: D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC_0 {
                    Texture2D: D3D11_TEX2D_VPOV { MipSlice: 0 },
                },
            };
            let mut view = None;
            hr(
                // SAFETY: the view is created over a texture we own, with a fully
                // initialised descriptor.
                unsafe {
                    video_device.CreateVideoProcessorOutputView(
                        &target,
                        &enumerator,
                        &desc,
                        Some(&mut view),
                    )
                },
                "CreateVideoProcessorOutputView",
            )?;
            outputs.push((
                target,
                view.ok_or_else(|| CaptureError("no output view".into()))?,
            ));
        }
        let converter = Self {
            video_device,
            video_context,
            enumerator,
            processor,
            source,
            input,
            outputs,
            source_size,
            out_size,
            fps,
        };
        converter.configure();
        Ok(converter)
    }

    fn processor(
        video_device: &ID3D11VideoDevice,
        source: (u32, u32),
        out: (u32, u32),
        fps: u32,
    ) -> Result<(ID3D11VideoProcessorEnumerator, ID3D11VideoProcessor), CaptureError> {
        let rate = DXGI_RATIONAL {
            Numerator: fps,
            Denominator: 1,
        };
        let content = D3D11_VIDEO_PROCESSOR_CONTENT_DESC {
            InputFrameFormat: D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE,
            InputFrameRate: rate,
            InputWidth: source.0,
            InputHeight: source.1,
            OutputFrameRate: rate,
            OutputWidth: out.0,
            OutputHeight: out.1,
            Usage: D3D11_VIDEO_USAGE_OPTIMAL_QUALITY,
        };
        // SAFETY: creation calls with a fully initialised descriptor.
        unsafe {
            let enumerator = ok(
                video_device.CreateVideoProcessorEnumerator(&content),
                "CreateVideoProcessorEnumerator",
            )?;
            let processor = ok(
                video_device.CreateVideoProcessor(&enumerator, 0),
                "CreateVideoProcessor",
            )?;
            Ok((enumerator, processor))
        }
    }

    fn input_view(
        video_device: &ID3D11VideoDevice,
        enumerator: &ID3D11VideoProcessorEnumerator,
        source: &ID3D11Texture2D,
    ) -> Result<ID3D11VideoProcessorInputView, CaptureError> {
        let desc = D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC {
            FourCC: 0,
            ViewDimension: D3D11_VPIV_DIMENSION_TEXTURE2D,
            Anonymous: D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC_0 {
                Texture2D: D3D11_TEX2D_VPIV {
                    MipSlice: 0,
                    ArraySlice: 0,
                },
            },
        };
        let mut view = None;
        hr(
            // SAFETY: as for the output views.
            unsafe {
                video_device.CreateVideoProcessorInputView(
                    source,
                    enumerator,
                    &desc,
                    Some(&mut view),
                )
            },
            "CreateVideoProcessorInputView",
        )?;
        view.ok_or_else(|| CaptureError("no input view".into()))
    }

    fn configure(&self) {
        let (x0, y0, x1, y1) = letterbox(
            self.source_size.0,
            self.source_size.1,
            self.out_size.0,
            self.out_size.1,
        );
        let dest = RECT {
            left: x0 as i32,
            top: y0 as i32,
            right: x1 as i32,
            bottom: y1 as i32,
        };
        let source = RECT {
            left: 0,
            top: 0,
            right: self.source_size.0 as i32,
            bottom: self.source_size.1 as i32,
        };
        let black = D3D11_VIDEO_COLOR {
            Anonymous: D3D11_VIDEO_COLOR_0 {
                RGBA: D3D11_VIDEO_COLOR_RGBA {
                    R: 0.0,
                    G: 0.0,
                    B: 0.0,
                    A: 1.0,
                },
            },
        };
        // SAFETY: state setters on our own processor with stack-held inputs.
        unsafe {
            let vc = &self.video_context;
            vc.VideoProcessorSetStreamFrameFormat(
                &self.processor,
                0,
                D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE,
            );
            vc.VideoProcessorSetStreamColorSpace(
                &self.processor,
                0,
                &D3D11_VIDEO_PROCESSOR_COLOR_SPACE { _bitfield: 0 },
            );
            vc.VideoProcessorSetOutputColorSpace(
                &self.processor,
                &D3D11_VIDEO_PROCESSOR_COLOR_SPACE {
                    _bitfield: OUTPUT_COLOR_709_LIMITED,
                },
            );
            vc.VideoProcessorSetStreamSourceRect(&self.processor, 0, true, Some(&source));
            vc.VideoProcessorSetStreamDestRect(&self.processor, 0, true, Some(&dest));
            vc.VideoProcessorSetOutputBackgroundColor(&self.processor, false, &black);
            vc.VideoProcessorSetStreamAutoProcessingMode(&self.processor, 0, false);
        }
    }

    fn resize(
        &mut self,
        device: &ID3D11Device,
        source_size: (u32, u32),
    ) -> Result<(), CaptureError> {
        let (enumerator, processor) =
            Self::processor(&self.video_device, source_size, self.out_size, self.fps)?;
        let source = texture(
            device,
            source_size.0,
            source_size.1,
            DXGI_FORMAT_B8G8R8A8_UNORM,
            (D3D11_BIND_RENDER_TARGET.0 | D3D11_BIND_SHADER_RESOURCE.0) as u32,
        )?;
        self.input = Self::input_view(&self.video_device, &enumerator, &source)?;
        let mut outputs = Vec::with_capacity(RING);
        for (target, _) in self.outputs.drain(..) {
            let desc = D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC {
                ViewDimension: D3D11_VPOV_DIMENSION_TEXTURE2D,
                Anonymous: D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC_0 {
                    Texture2D: D3D11_TEX2D_VPOV { MipSlice: 0 },
                },
            };
            let mut view = None;
            hr(
                // SAFETY: as in new().
                unsafe {
                    self.video_device.CreateVideoProcessorOutputView(
                        &target,
                        &enumerator,
                        &desc,
                        Some(&mut view),
                    )
                },
                "CreateVideoProcessorOutputView",
            )?;
            outputs.push((
                target,
                view.ok_or_else(|| CaptureError("no output view".into()))?,
            ));
        }
        self.outputs = outputs;
        self.enumerator = enumerator;
        self.processor = processor;
        self.source = source;
        self.source_size = source_size;
        self.configure();
        Ok(())
    }

    fn ingest(&self, context: &ID3D11DeviceContext, frame: &ID3D11Texture2D, content: (u32, u32)) {
        let region = windows::Win32::Graphics::Direct3D11::D3D11_BOX {
            left: 0,
            top: 0,
            front: 0,
            right: content.0.min(self.source_size.0),
            bottom: content.1.min(self.source_size.1),
            back: 1,
        };
        // SAFETY: GPU copy between two live textures on the same device, the
        // box clipped to both.
        unsafe { context.CopySubresourceRegion(&self.source, 0, 0, 0, 0, frame, 0, Some(&region)) };
    }

    fn convert(&self, slot: usize) -> Result<&ID3D11Texture2D, CaptureError> {
        let (target, view) = &self.outputs[slot];
        let stream = D3D11_VIDEO_PROCESSOR_STREAM {
            Enable: true.into(),
            pInputSurface: ManuallyDrop::new(Some(self.input.clone())),
            ..Default::default()
        };
        let streams = [stream];
        // SAFETY: blit with a stream struct that holds one extra reference to
        // the input view, released right after.
        let result = unsafe {
            self.video_context
                .VideoProcessorBlt(&self.processor, view, 0, &streams)
        };
        let [mut stream] = streams;
        // SAFETY: drops the reference taken for the call, once.
        unsafe { ManuallyDrop::drop(&mut stream.pInputSurface) };
        hr(result, "VideoProcessorBlt")?;
        Ok(target)
    }
}

fn capture_item(target: Target) -> Result<GraphicsCaptureItem, CaptureError> {
    let interop = ok(
        windows::core::factory::<GraphicsCaptureItem, IGraphicsCaptureItemInterop>(),
        "capture interop",
    )?;
    // SAFETY: the handles were validated by resolve(); a stale one fails
    // with an error rather than touching memory.
    unsafe {
        match target {
            Target::Monitor(handle) => ok(
                interop.CreateForMonitor(HMONITOR(handle as *mut _)),
                "capture monitor",
            ),
            Target::Window(handle) => ok(
                interop.CreateForWindow(HWND(handle as *mut _)),
                "capture window",
            ),
        }
    }
}

fn size_of_item(size: SizeInt32) -> (u32, u32) {
    (size.Width.max(2) as u32, size.Height.max(2) as u32)
}

fn capture(shared: &Arc<Shared>, target: Target) -> Result<(), CaptureError> {
    let config = shared.config.clone();
    let (device, context) = create_device()?;
    let dxgi: IDXGIDevice = ok(device.cast(), "IDXGIDevice")?;
    // SAFETY: wraps our live DXGI device as a WinRT device.
    let rt_device: IDirect3DDevice = ok(
        unsafe { CreateDirect3D11DeviceFromDXGIDevice(&dxgi) }.and_then(|i| i.cast()),
        "WinRT D3D device",
    )?;
    let item = capture_item(target)?;
    let mut pool_size = ok(item.Size(), "capture size")?;
    let source = size_of_item(pool_size);
    let out = fit_size(source.0, source.1, config.max_width, config.max_height);
    let pool = ok(
        Direct3D11CaptureFramePool::CreateFreeThreaded(
            &rt_device,
            DirectXPixelFormat::B8G8R8A8UIntNormalized,
            2,
            pool_size,
        ),
        "frame pool",
    )?;
    let signal = Arc::new((Mutex::new(false), Condvar::new()));
    let closed = Arc::new(std::sync::atomic::AtomicBool::new(false));
    {
        let signal = Arc::clone(&signal);
        ok(
            pool.FrameArrived(&TypedEventHandler::new(move |_, _| {
                let (flag, wake) = &*signal;
                *flag.lock().unwrap_or_else(|e| e.into_inner()) = true;
                wake.notify_one();
                Ok(())
            })),
            "FrameArrived",
        )?;
    }
    {
        let closed = Arc::clone(&closed);
        let signal = Arc::clone(&signal);
        ok(
            item.Closed(&TypedEventHandler::new(move |_, _| {
                closed.store(true, Ordering::Release);
                signal.1.notify_one();
                Ok(())
            })),
            "item Closed",
        )?;
    }
    let session: GraphicsCaptureSession =
        ok(pool.CreateCaptureSession(&item), "CreateCaptureSession")?;
    let _ = session.SetIsCursorCaptureEnabled(config.cursor);
    let _ = session.SetIsBorderRequired(false);
    let mut converter = Converter::new(&device, &context, source, out, config.fps)?;
    let mut bitrate = shared.bitrate_kbps.load(Ordering::Acquire);
    let settings = Settings {
        width: out.0,
        height: out.1,
        fps: config.fps,
        bitrate_kbps: bitrate,
        gop_frames: (config.fps * config.keyframe_interval_ms / 1000).max(1),
    };
    let mut encoder = Encoder::create(&device, &settings, config.encoder)?;
    shared.update(|s| {
        s.state = CaptureState::Running;
        s.width = out.0;
        s.height = out.1;
        s.encoder = encoder.name.clone();
        s.hardware = encoder.hardware;
    });
    ok(session.StartCapture(), "StartCapture")?;
    let interval = Duration::from_secs_f64(1.0 / f64::from(config.fps));
    let mut last_encode: Option<Instant> = None;
    let mut have_frame = false;
    let mut dirty = false;
    let mut slot = 0usize;
    let mut outputs: Vec<EncodedVideo> = Vec::new();
    let mut last_pts = -1i64;
    while !shared.stopped() && !closed.load(Ordering::Acquire) {
        let wait = match last_encode {
            _ if encoder.busy() => Duration::from_millis(1),
            Some(t) if have_frame => {
                let next = if dirty { interval } else { REPEAT };
                next.saturating_sub(t.elapsed())
                    .clamp(Duration::from_millis(1), Duration::from_millis(50))
            }
            _ => Duration::from_millis(50),
        };
        {
            let (flag, wake) = &*signal;
            let mut arrived = flag.lock().unwrap_or_else(|e| e.into_inner());
            if !*arrived {
                arrived = wake
                    .wait_timeout(arrived, wait)
                    .unwrap_or_else(|e| e.into_inner())
                    .0;
            }
            *arrived = false;
        }
        let mut newest = None;
        while let Ok(frame) = pool.TryGetNextFrame() {
            if let Some(old) = newest.replace(frame) {
                let _: Result<(), _> =
                    Interface::cast::<windows::Foundation::IClosable>(&old).and_then(|c| c.Close());
            }
        }
        if let Some(frame) = newest {
            shared.captured.fetch_add(1, Ordering::Relaxed);
            let content = ok(frame.ContentSize(), "ContentSize")?;
            if content.Width != pool_size.Width || content.Height != pool_size.Height {
                pool_size = SizeInt32 {
                    Width: content.Width.max(2),
                    Height: content.Height.max(2),
                };
                ok(
                    pool.Recreate(
                        &rt_device,
                        DirectXPixelFormat::B8G8R8A8UIntNormalized,
                        2,
                        pool_size,
                    ),
                    "frame pool Recreate",
                )?;
                converter.resize(&device, size_of_item(pool_size))?;
            } else {
                let surface = ok(frame.Surface(), "frame surface")?;
                let access: IDirect3DDxgiInterfaceAccess = ok(surface.cast(), "surface access")?;
                // SAFETY: borrows the frame's texture for the copy below,
                // while the frame is still open.
                let texture: ID3D11Texture2D =
                    ok(unsafe { access.GetInterface() }, "frame texture")?;
                converter.ingest(&context, &texture, size_of_item(content));
                have_frame = true;
                dirty = true;
            }
            let _ = frame.Close();
        }
        let want_key = shared.keyframe.load(Ordering::Acquire);
        let since = last_encode.map(|t| t.elapsed());
        let due = since.is_none_or(|e| e >= interval);
        if have_frame && due && (dirty || want_key || since.is_none_or(|e| e >= REPEAT)) {
            let requested = bitrate_now(shared);
            if requested != bitrate {
                encoder.set_bitrate(requested);
                bitrate = requested;
            }
            let target = converter.convert(slot)?;
            let mut pts = shared.now_us();
            if pts <= last_pts {
                pts = last_pts + 1;
            }
            let key = want_key && shared.keyframe.swap(false, Ordering::AcqRel);
            if encoder.encode(&context, target, pts, key, &mut outputs)? {
                slot = (slot + 1) % RING;
                last_encode = Some(Instant::now());
                last_pts = pts;
                dirty = false;
            } else {
                shared.dropped.fetch_add(1, Ordering::Relaxed);
                if key {
                    shared.keyframe.store(true, Ordering::Release);
                }
            }
        }
        encoder.poll(&mut outputs)?;
        for frame in outputs.drain(..) {
            shared.encoded.fetch_add(1, Ordering::Relaxed);
            shared.push_video(frame);
        }
    }
    let _ = session.Close();
    let _ = pool.Close();
    Ok(())
}

fn bitrate_now(shared: &Shared) -> u32 {
    shared.bitrate_kbps.load(Ordering::Acquire)
}
