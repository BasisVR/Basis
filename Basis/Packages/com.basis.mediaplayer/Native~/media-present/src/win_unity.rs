//! Unity's graphics interfaces on Windows (ported from `IUnityInterface.h`,
//! `IUnityGraphics.h` and `IUnityGraphicsD3D12.h`; `UNITY_INTERFACE_API` is
//! `__stdcall`, which is the system ABI on x64). Only Direct3D 12 needs
//! them: the D3D11 path takes Unity's device from the texture it is handed.
//! Struct layouts mirror the headers slot for slot.

#![allow(non_snake_case)]

use std::ffi::{c_int, c_void};
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::sync::Arc;
use std::sync::atomic::{AtomicI32, AtomicPtr, Ordering};

use windows::Win32::Graphics::Direct3D12::{
    D3D12_RESOURCE_STATES, ID3D12Device, ID3D12Fence, ID3D12GraphicsCommandList, ID3D12Resource,
};
use windows::core::Interface;

use crate::win_d3d12::{D3d12Host, release_retired};

/// Function slots are `Option` because a Rust `fn` pointer may not be null:
/// a Unity build that leaves one empty must not make the table an invalid
/// value.
#[repr(C)]
struct IUnityInterfaces {
    get_interface: *mut c_void,
    register_interface: *mut c_void,
    get_interface_split: Option<unsafe extern "system" fn(high: u64, low: u64) -> *mut c_void>,
    register_interface_split: *mut c_void,
}

#[repr(C)]
struct IUnityGraphics {
    get_renderer: Option<unsafe extern "system" fn() -> c_int>,
    register_device_event_callback: Option<unsafe extern "system" fn(cb: DeviceEventCallback)>,
    unregister_device_event_callback: Option<unsafe extern "system" fn(cb: DeviceEventCallback)>,
    reserve_event_id_range: *mut c_void,
}

type DeviceEventCallback = unsafe extern "system" fn(event: c_int);

#[repr(C)]
struct UnityGraphicsD3D12RecordingState {
    command_list: *mut c_void,
}

#[repr(C)]
struct IUnityGraphicsD3D12v8 {
    get_device: Option<unsafe extern "system" fn() -> *mut c_void>,
    get_swap_chain: *mut c_void,
    get_sync_interval: *mut c_void,
    get_present_flags: *mut c_void,
    get_frame_fence: Option<unsafe extern "system" fn() -> *mut c_void>,
    get_next_frame_fence_value: Option<unsafe extern "system" fn() -> u64>,
    execute_command_list: *mut c_void,
    set_physical_video_memory_control_values: *mut c_void,
    get_command_queue: *mut c_void,
    texture_from_render_buffer: *mut c_void,
    texture_from_native_texture: *mut c_void,
    configure_event: *mut c_void,
    command_recording_state:
        Option<unsafe extern "system" fn(out: *mut UnityGraphicsD3D12RecordingState) -> bool>,
    request_resource_state:
        Option<unsafe extern "system" fn(resource: *mut c_void, state: D3D12_RESOURCE_STATES)>,
    notify_resource_state: *mut c_void,
}

const IUNITY_GRAPHICS: (u64, u64) = (0x7CBA0A9CA4DDB544, 0x8C5AD4926EB17B11);
/// Unity 6000.3 and later. Unity's own header marks this GUID "TODO: Get
/// proper values"; if an upgrade changes it, the lookup returns null and
/// Direct3D 12 playback refuses until this is updated.
const IUNITY_GRAPHICS_D3D12_V8: (u64, u64) = (0x9d303045d00d4cfd, 0x8febb42968b423b6);

const RENDERER_D3D12: c_int = 18;
const DEVICE_EVENT_INITIALIZE: c_int = 0;
const DEVICE_EVENT_SHUTDOWN: c_int = 1;

static GRAPHICS: AtomicPtr<IUnityGraphics> = AtomicPtr::new(std::ptr::null_mut());
static INTERFACES: AtomicPtr<IUnityInterfaces> = AtomicPtr::new(std::ptr::null_mut());
/// Unity's D3D12 interface while its renderer is Direct3D 12; null on any
/// other renderer, and outside Unity.
static D3D12: AtomicPtr<IUnityGraphicsD3D12v8> = AtomicPtr::new(std::ptr::null_mut());
/// Unity's renderer as last reported; -1 outside Unity.
static RENDERER: AtomicI32 = AtomicI32::new(-1);

unsafe extern "system" fn on_device_event(event: c_int) {
    // This frame returns into Unity's C++, which cannot unwind.
    let _ = catch_unwind(AssertUnwindSafe(|| device_event(event)));
}

fn device_event(event: c_int) {
    match event {
        DEVICE_EVENT_INITIALIZE => {
            capture();
            media_diag::diag_log!(
                "unity device event: initialize, renderer {}",
                RENDERER.load(Ordering::Acquire)
            );
        }
        DEVICE_EVENT_SHUTDOWN => {
            // Released before the log line, so a panicking sink cannot
            // leave the host's table in reach after its device is gone.
            D3D12.store(std::ptr::null_mut(), Ordering::Release);
            release_retired();
            media_diag::diag_log!(
                "unity device event: shutdown, renderer {}",
                RENDERER.load(Ordering::Acquire)
            );
        }
        _ => {}
    }
}

fn capture() {
    let graphics = GRAPHICS.load(Ordering::Acquire);
    let interfaces = INTERFACES.load(Ordering::Acquire);
    if graphics.is_null() || interfaces.is_null() {
        return;
    }
    // SAFETY: both tables are the live ones Unity passed to
    // `UnityPluginLoad`, which stay valid until `UnityPluginUnload`.
    let d3d12 = unsafe {
        let renderer = (*graphics).get_renderer.map(|f| f()).unwrap_or(-1);
        RENDERER.store(renderer, Ordering::Release);
        match (renderer, (*interfaces).get_interface_split) {
            (RENDERER_D3D12, Some(get)) => {
                get(IUNITY_GRAPHICS_D3D12_V8.0, IUNITY_GRAPHICS_D3D12_V8.1)
                    as *mut IUnityGraphicsD3D12v8
            }
            _ => std::ptr::null_mut(),
        }
    };
    D3D12.store(d3d12, Ordering::Release);
}

/// `UnityPluginLoad` on Windows.
///
/// # Safety
/// `interfaces` must be the live `IUnityInterfaces*` Unity passed.
pub unsafe fn plugin_load(interfaces: *mut c_void) {
    let interfaces = interfaces as *mut IUnityInterfaces;
    if interfaces.is_null() {
        return;
    }
    // SAFETY: caller contract: Unity's live interface table.
    unsafe {
        let Some(get) = (*interfaces).get_interface_split else {
            return;
        };
        let graphics = get(IUNITY_GRAPHICS.0, IUNITY_GRAPHICS.1) as *mut IUnityGraphics;
        if graphics.is_null() {
            return;
        }
        INTERFACES.store(interfaces, Ordering::Release);
        GRAPHICS.store(graphics, Ordering::Release);
        if let Some(register) = (*graphics).register_device_event_callback {
            register(on_device_event);
        }
    }
    // A plugin loaded after the device came up has missed Initialize.
    capture();
}

/// `UnityPluginUnload` on Windows.
pub fn plugin_unload() {
    let graphics = GRAPHICS.swap(std::ptr::null_mut(), Ordering::AcqRel);
    if !graphics.is_null() {
        // SAFETY: the table Unity passed at load, still live during unload.
        unsafe {
            if let Some(unregister) = (*graphics).unregister_device_event_callback {
                unregister(on_device_event);
            }
        }
    }
    INTERFACES.store(std::ptr::null_mut(), Ordering::Release);
    D3D12.store(std::ptr::null_mut(), Ordering::Release);
    // Unload can come without a device shutdown event.
    release_retired();
}

/// Unity renders with Direct3D 12, whether or not its v8 interface came
/// with it. Its textures are then `ID3D12Resource`s either way.
pub fn renderer_is_d3d12() -> bool {
    RENDERER.load(Ordering::Acquire) == RENDERER_D3D12
}

/// Unity's renderer is Direct3D 12 and its v8 interface is available.
pub fn host_is_d3d12() -> bool {
    !D3D12.load(Ordering::Acquire).is_null()
}

/// Unity's Direct3D 12 renderer as a consumer host, while it is the one
/// running.
pub fn unity_d3d12_host() -> Option<Arc<dyn D3d12Host>> {
    host_is_d3d12().then(|| Arc::new(UnityD3d12) as Arc<dyn D3d12Host>)
}

/// Reads the interface afresh on every call, so a device shutdown between
/// two calls yields `None` rather than a dangling table.
struct UnityD3d12;

fn table() -> Option<&'static IUnityGraphicsD3D12v8> {
    let ptr = D3D12.load(Ordering::Acquire);
    // SAFETY: non-null only between a D3D12 Initialize and its Shutdown,
    // while Unity keeps the table alive.
    unsafe { ptr.as_ref() }
}

/// Take a reference to a COM object Unity returned without transferring
/// one, so ours is released independently of Unity's.
///
/// # Safety
/// `raw` must be null or a live object of interface `T`.
unsafe fn borrowed<T: Interface + Clone>(raw: *mut c_void) -> Option<T> {
    // SAFETY: caller contract.
    unsafe { T::from_raw_borrowed(&raw).cloned() }
}

impl D3d12Host for UnityD3d12 {
    fn device(&self) -> Option<ID3D12Device> {
        let get = table()?.get_device?;
        // SAFETY: Unity returns its live device, keeping its own reference.
        unsafe { borrowed(get()) }
    }

    fn command_list(&self) -> Option<ID3D12GraphicsCommandList> {
        let get = table()?.command_recording_state?;
        let mut state = UnityGraphicsD3D12RecordingState {
            command_list: std::ptr::null_mut(),
        };
        // SAFETY: Unity fills the struct with the list it is recording,
        // valid for the current render event.
        unsafe {
            if !get(&mut state) {
                return None;
            }
            borrowed(state.command_list)
        }
    }

    fn request_state(&self, resource: &ID3D12Resource, state: D3D12_RESOURCE_STATES) {
        if let Some(request) = table().and_then(|t| t.request_resource_state) {
            // SAFETY: a live resource on Unity's device, inside a render event.
            unsafe { request(resource.as_raw(), state) };
        }
    }

    fn frame_fence(&self) -> Option<ID3D12Fence> {
        let get = table()?.get_frame_fence?;
        // SAFETY: Unity returns its live frame fence, keeping its own
        // reference.
        unsafe { borrowed(get()) }
    }

    fn next_frame_fence_value(&self) -> Option<u64> {
        table()
            .and_then(|t| t.get_next_frame_fence_value)
            // SAFETY: plain value query on Unity's live table.
            .map(|f| unsafe { f() })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn panics_on_device_events(line: &str) {
        assert!(
            !line.starts_with("unity device event"),
            "sink refused: {line}"
        );
        media_diag::stderr_sink(line);
    }

    /// Unity calls this from C++, so a panic in its body would abort the
    /// process rather than unwind. A shutdown whose log line panics still
    /// lets go of the host.
    #[test]
    fn a_panic_under_a_device_event_stays_inside_the_callback() {
        media_diag::set_log_sink(panics_on_device_events);
        // SAFETY: the callback takes no pointers; outside Unity both
        // events touch only the plugin's own statics.
        unsafe { on_device_event(DEVICE_EVENT_INITIALIZE) };

        // SAFETY: every slot is a raw pointer or an `Option` of a fn
        // pointer, for which all zeroes is null and `None`.
        let table: &'static mut IUnityGraphicsD3D12v8 =
            Box::leak(Box::new(unsafe { std::mem::zeroed() }));
        D3D12.store(table, Ordering::Release);
        assert!(host_is_d3d12());

        // SAFETY: as above.
        unsafe { on_device_event(DEVICE_EVENT_SHUTDOWN) };
        assert!(!host_is_d3d12());
    }
}
