#![windows_subsystem = "windows"]

#[cfg(windows)]
#[repr(transparent)]
pub struct SdkPath(*const u8);
#[cfg(windows)]
unsafe impl Sync for SdkPath {}

#[cfg(windows)]
#[unsafe(no_mangle)]
pub static D3D12SDKVersion: u32 = basis_rt_tracer::AGILITY_SDK_VERSION;
#[cfg(windows)]
#[unsafe(no_mangle)]
pub static D3D12SDKPath: SdkPath = SdkPath(b".\\D3D12\\\0".as_ptr());

fn main() { basis_rt_tracer::main_process(); }
