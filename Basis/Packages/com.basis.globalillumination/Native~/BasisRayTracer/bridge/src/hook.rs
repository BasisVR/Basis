use std::ffi::c_void;
use std::sync::Mutex;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use windows::Win32::Graphics::Direct3D11::*;
use windows::Win32::System::Memory::*;
use windows::core::{HRESULT, Interface};

type Create2D = unsafe extern "system" fn(*mut c_void, *const D3D11_TEXTURE2D_DESC, *const D3D11_SUBRESOURCE_DATA, *mut *mut c_void) -> HRESULT;
type Create2D1 = unsafe extern "system" fn(*mut c_void, *const D3D11_TEXTURE2D_DESC1, *const D3D11_SUBRESOURCE_DATA, *mut *mut c_void) -> HRESULT;

static ORIGINAL: AtomicUsize = AtomicUsize::new(0);
static ORIGINAL1: AtomicUsize = AtomicUsize::new(0);
static INSTALLED: AtomicBool = AtomicBool::new(false);
static ARMED: Mutex<Vec<(u32, u32)>> = Mutex::new(Vec::new());
const SHARE: u32 = D3D11_RESOURCE_MISC_SHARED.0 as u32 | D3D11_RESOURCE_MISC_SHARED_NTHANDLE.0 as u32;

pub fn installed() -> bool { INSTALLED.load(Ordering::Acquire) }
pub fn arm(sizes: &[(u32, u32)]) { ARMED.lock().unwrap().extend_from_slice(sizes); }
pub fn disarm() { ARMED.lock().unwrap().clear(); }

fn claim(width: u32, height: u32, mips: u32, array: u32, samples: u32, bind: u32, misc: u32) -> bool {
    if bind & D3D11_BIND_UNORDERED_ACCESS.0 as u32 == 0 || misc & SHARE != 0 || mips != 1 || array != 1 || samples != 1 { return false; }
    let mut armed = ARMED.lock().unwrap();
    match armed.iter().position(|&(w, h)| w == width && h == height) { Some(i) => { armed.remove(i); true } None => false }
}

unsafe extern "system" fn create_2d(this: *mut c_void, desc: *const D3D11_TEXTURE2D_DESC, init: *const D3D11_SUBRESOURCE_DATA, out: *mut *mut c_void) -> HRESULT {
    let original: Create2D = unsafe { std::mem::transmute(ORIGINAL.load(Ordering::Acquire)) };
    if let Some(d) = unsafe { desc.as_ref() } {
        if claim(d.Width, d.Height, d.MipLevels, d.ArraySize, d.SampleDesc.Count, d.BindFlags, d.MiscFlags) {
            let mut shared = *d;
            shared.MiscFlags |= SHARE;
            let hr = unsafe { original(this, &shared, init, out) };
            if hr.is_ok() { return hr; }
        }
    }
    unsafe { original(this, desc, init, out) }
}

unsafe extern "system" fn create_2d1(this: *mut c_void, desc: *const D3D11_TEXTURE2D_DESC1, init: *const D3D11_SUBRESOURCE_DATA, out: *mut *mut c_void) -> HRESULT {
    let original: Create2D1 = unsafe { std::mem::transmute(ORIGINAL1.load(Ordering::Acquire)) };
    if let Some(d) = unsafe { desc.as_ref() } {
        if claim(d.Width, d.Height, d.MipLevels, d.ArraySize, d.SampleDesc.Count, d.BindFlags, d.MiscFlags) {
            let mut shared = *d;
            shared.MiscFlags |= SHARE;
            let hr = unsafe { original(this, &shared, init, out) };
            if hr.is_ok() { return hr; }
        }
    }
    unsafe { original(this, desc, init, out) }
}

unsafe fn patch(object: *mut c_void, offset: usize, hook: usize, store: &AtomicUsize) -> bool {
    unsafe {
        let table = *(object as *const *mut usize);
        let entry = table.add(offset / std::mem::size_of::<usize>());
        if *entry == hook { return true; }
        let mut old = PAGE_PROTECTION_FLAGS(0);
        if VirtualProtect(entry as *const c_void, std::mem::size_of::<usize>(), PAGE_READWRITE, &mut old).is_err() { return false; }
        store.store(*entry, Ordering::Release);
        std::ptr::write_volatile(entry, hook);
        let _ = VirtualProtect(entry as *const c_void, std::mem::size_of::<usize>(), old, &mut old);
        true
    }
}

pub fn install(device: &ID3D11Device) {
    if installed() { return; }
    unsafe {
        if !patch(device.as_raw(), std::mem::offset_of!(ID3D11Device_Vtbl, CreateTexture2D), create_2d as usize, &ORIGINAL) { return; }
        if let Ok(d3) = device.cast::<ID3D11Device3>() { if !patch(d3.as_raw(), std::mem::offset_of!(ID3D11Device3_Vtbl, CreateTexture2D1), create_2d1 as usize, &ORIGINAL1) { return; } }
    }
    INSTALLED.store(true, Ordering::Release);
}
