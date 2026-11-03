use std::mem::ManuallyDrop;
use std::sync::Arc;
use std::sync::mpsc::{Sender, channel};
use std::time::Duration;

use windows::Win32::Foundation::{CloseHandle, WAIT_OBJECT_0};
use windows::Win32::Media::Audio::{
    AUDCLNT_BUFFERFLAGS_SILENT, AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM,
    AUDCLNT_STREAMFLAGS_EVENTCALLBACK, AUDCLNT_STREAMFLAGS_LOOPBACK,
    AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY, AUDIOCLIENT_ACTIVATION_PARAMS,
    AUDIOCLIENT_ACTIVATION_PARAMS_0, AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK,
    AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS, ActivateAudioInterfaceAsync,
    IActivateAudioInterfaceAsyncOperation, IActivateAudioInterfaceCompletionHandler,
    IActivateAudioInterfaceCompletionHandler_Impl, IAudioCaptureClient, IAudioClient,
    PROCESS_LOOPBACK_MODE, PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE,
    PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE, VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK,
    WAVEFORMATEX,
};
use windows::Win32::Media::Multimedia::WAVE_FORMAT_IEEE_FLOAT;
use windows::Win32::System::Com::StructuredStorage::{
    PROPVARIANT, PROPVARIANT_0, PROPVARIANT_0_0, PROPVARIANT_0_0_0,
};
use windows::Win32::System::Com::{BLOB, COINIT_MULTITHREADED, CoInitializeEx};
use windows::Win32::System::Threading::{CreateEventW, WaitForSingleObject};
use windows::Win32::System::Variant::VT_BLOB;
use windows::core::{HRESULT, IUnknown, Interface, Ref, implement};

use super::{Target, hr, ok};
use crate::pack::Packer;
use crate::{AUDIO_CHANNELS, AUDIO_RATE, AudioSource, CaptureError, Shared};
use windows::Win32::Foundation::HWND;
use windows::Win32::UI::WindowsAndMessaging::GetWindowThreadProcessId;

#[implement(IActivateAudioInterfaceCompletionHandler)]
struct Completion {
    done: std::sync::Mutex<Option<Sender<()>>>,
}

impl IActivateAudioInterfaceCompletionHandler_Impl for Completion_Impl {
    fn ActivateCompleted(
        &self,
        _operation: Ref<'_, IActivateAudioInterfaceAsyncOperation>,
    ) -> windows::core::Result<()> {
        if let Some(done) = self.done.lock().unwrap_or_else(|e| e.into_inner()).take() {
            let _ = done.send(());
        }
        Ok(())
    }
}

fn loopback_target(shared: &Shared, target: Target) -> (u32, PROCESS_LOOPBACK_MODE) {
    let own = (
        std::process::id(),
        PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE,
    );
    let window = match target {
        Target::Window(handle) => handle,
        Target::Monitor(_) => return own,
    };
    if shared.config.audio_source == AudioSource::System {
        return own;
    }
    let mut pid = 0u32;
    // SAFETY: read-only query on a window handle; a stale handle yields 0.
    unsafe { GetWindowThreadProcessId(HWND(window as *mut _), Some(&mut pid)) };
    if pid == 0 || pid == std::process::id() {
        return own;
    }
    (pid, PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE)
}

fn activate(process: u32, mode: PROCESS_LOOPBACK_MODE) -> Result<IAudioClient, CaptureError> {
    let params = AUDIOCLIENT_ACTIVATION_PARAMS {
        ActivationType: AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK,
        Anonymous: AUDIOCLIENT_ACTIVATION_PARAMS_0 {
            ProcessLoopbackParams: AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS {
                TargetProcessId: process,
                ProcessLoopbackMode: mode,
            },
        },
    };
    let blob = ManuallyDrop::new(PROPVARIANT {
        Anonymous: PROPVARIANT_0 {
            Anonymous: ManuallyDrop::new(PROPVARIANT_0_0 {
                vt: VT_BLOB,
                wReserved1: 0,
                wReserved2: 0,
                wReserved3: 0,
                Anonymous: PROPVARIANT_0_0_0 {
                    blob: BLOB {
                        cbSize: size_of::<AUDIOCLIENT_ACTIVATION_PARAMS>() as u32,
                        pBlobData: &params as *const _ as *mut u8,
                    },
                },
            }),
        },
    });
    let (tx, rx) = channel();
    let handler: IActivateAudioInterfaceCompletionHandler = Completion {
        done: std::sync::Mutex::new(Some(tx)),
    }
    .into();
    // SAFETY: params and the PROPVARIANT that points at it outlive the wait
    // below, which is the only time the system reads them. The PROPVARIANT is
    // never dropped through PropVariantClear, so the borrowed blob is not freed.
    let operation = unsafe {
        ActivateAudioInterfaceAsync(
            VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK,
            &IAudioClient::IID,
            Some(&*blob),
            &handler,
        )
    };
    let operation = ok(
        operation,
        "process loopback activation (needs Windows 10 2004 or later)",
    )?;
    if rx.recv_timeout(Duration::from_secs(5)).is_err() {
        return Err(CaptureError("process loopback activation timed out".into()));
    }
    let mut result = HRESULT(0);
    let mut unknown: Option<IUnknown> = None;
    hr(
        // SAFETY: completion has fired, so the result is ready to read.
        unsafe { operation.GetActivateResult(&mut result, &mut unknown) },
        "GetActivateResult",
    )?;
    hr(result.ok(), "process loopback")?;
    ok(
        unknown
            .ok_or_else(|| windows::core::Error::from(windows::Win32::Foundation::E_FAIL))
            .and_then(|u| u.cast()),
        "IAudioClient",
    )
}

pub fn run(shared: Arc<Shared>, target: Target) {
    // SAFETY: per-thread COM initialisation.
    unsafe {
        let _ = CoInitializeEx(None, COINIT_MULTITHREADED);
    }
    if let Err(e) = capture(&shared, target) {
        media_diag::diag_warn!("capture audio: {}", e.0);
        shared.update(|s| {
            s.audio = false;
            s.audio_error = e.0.clone();
        });
    }
}

fn capture(shared: &Arc<Shared>, target: Target) -> Result<(), CaptureError> {
    let (process, mode) = loopback_target(shared, target);
    let client = activate(process, mode)?;
    let format = WAVEFORMATEX {
        wFormatTag: WAVE_FORMAT_IEEE_FLOAT as u16,
        nChannels: AUDIO_CHANNELS as u16,
        nSamplesPerSec: AUDIO_RATE,
        nAvgBytesPerSec: AUDIO_RATE * AUDIO_CHANNELS * 4,
        nBlockAlign: (AUDIO_CHANNELS * 4) as u16,
        wBitsPerSample: 32,
        cbSize: 0,
    };
    // SAFETY: client set-up with a stack format that outlives the call; the
    // event handle is closed on every exit below.
    unsafe {
        hr(
            client.Initialize(
                AUDCLNT_SHAREMODE_SHARED,
                AUDCLNT_STREAMFLAGS_LOOPBACK
                    | AUDCLNT_STREAMFLAGS_EVENTCALLBACK
                    | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
                    | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY,
                2_000_000,
                0,
                &format,
                None,
            ),
            "IAudioClient::Initialize",
        )?;
        let event = ok(CreateEventW(None, false, false, None), "CreateEventW")?;
        let result = (|| -> Result<(), CaptureError> {
            hr(client.SetEventHandle(event), "SetEventHandle")?;
            let reader: IAudioCaptureClient = ok(client.GetService(), "IAudioCaptureClient")?;
            hr(client.Start(), "IAudioClient::Start")?;
            let mut packer = Packer::new(shared)?;
            shared.update(|s| s.audio = true);
            let mut scratch: Vec<f32> = Vec::new();
            while !shared.stopped() {
                let waited = WaitForSingleObject(event, 20);
                if waited == WAIT_OBJECT_0 {
                    loop {
                        let size = ok(reader.GetNextPacketSize(), "GetNextPacketSize")?;
                        if size == 0 {
                            break;
                        }
                        let mut data = std::ptr::null_mut();
                        let mut frames = 0u32;
                        let mut flags = 0u32;
                        hr(
                            reader.GetBuffer(&mut data, &mut frames, &mut flags, None, None),
                            "GetBuffer",
                        )?;
                        let count = frames as usize * AUDIO_CHANNELS as usize;
                        scratch.clear();
                        if flags & AUDCLNT_BUFFERFLAGS_SILENT.0 as u32 != 0 || data.is_null() {
                            scratch.resize(count, 0.0);
                        } else {
                            scratch.extend_from_slice(std::slice::from_raw_parts(
                                data as *const f32,
                                count,
                            ));
                        }
                        hr(reader.ReleaseBuffer(frames), "ReleaseBuffer")?;
                        packer.push(shared, &scratch)?;
                    }
                }
                packer.fill_silence(shared)?;
            }
            let _ = client.Stop();
            Ok(())
        })();
        let _ = CloseHandle(event);
        result
    }
}
