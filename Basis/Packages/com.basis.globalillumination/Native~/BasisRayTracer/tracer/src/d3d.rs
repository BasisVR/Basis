use std::cell::RefCell;
use windows::Win32::Graphics::Direct3D::*;
use windows::Win32::Graphics::Direct3D12::*;
use windows::core::*;

thread_local! { static FACTORY: RefCell<Option<ID3D12DeviceFactory>> = const { RefCell::new(None) }; }

pub fn begin_embedded(sdk: &str) -> std::result::Result<(), String> {
    unsafe {
        let mut config: Option<ID3D12SDKConfiguration1> = None;
        D3D12GetInterface(&CLSID_D3D12SDKConfiguration, &mut config).map_err(|e| format!("query the SDK configuration: {}", e.message()))?;
        let config = config.ok_or("no SDK configuration")?;
        let path = std::ffi::CString::new(sdk).map_err(|_| "bad SDK path")?;
        let factory: ID3D12DeviceFactory = config.CreateDeviceFactory(crate::AGILITY_SDK_VERSION, PCSTR(path.as_ptr() as _)).map_err(|e| format!("load {sdk}: {}", e.message()))?;
        FACTORY.with(|f| *f.borrow_mut() = Some(factory));
    }
    Ok(())
}

pub fn end_embedded() { FACTORY.with(|f| *f.borrow_mut() = None); }

pub fn enable_debug() {
    unsafe {
        let factory = FACTORY.with(|f| f.borrow().clone());
        let debug: Option<ID3D12Debug> = match factory { Some(f) => f.GetConfigurationInterface(&CLSID_D3D12Debug).ok(), None => { let mut d = None; D3D12GetDebugInterface(&mut d).ok().and(d) } };
        if let Some(d) = debug { d.EnableDebugLayer(); }
    }
}

pub fn create_device<T: Interface>(adapter: &IUnknown, level: D3D_FEATURE_LEVEL) -> Result<T> {
    let mut device: Option<T> = None;
    unsafe {
        match FACTORY.with(|f| f.borrow().clone()) {
            Some(f) => f.CreateDevice(adapter, level, &mut device)?,
            None => D3D12CreateDevice(adapter, level, &mut device)?,
        }
    }
    device.ok_or_else(|| Error::from(windows::Win32::Foundation::E_FAIL))
}
