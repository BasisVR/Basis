use windows::Win32::Foundation::{HWND, LPARAM, RECT};
use windows::Win32::Graphics::Dwm::{DWMWA_CLOAKED, DwmGetWindowAttribute};
use windows::Win32::Graphics::Gdi::{
    EnumDisplayMonitors, GetMonitorInfoW, HDC, HMONITOR, MONITORINFO, MONITORINFOEXW,
};
use windows::Win32::UI::WindowsAndMessaging::{
    EnumWindows, GA_ROOT, GWL_EXSTYLE, GetAncestor, GetClientRect, GetWindowLongPtrW,
    GetWindowPlacement, GetWindowTextW, IsIconic, IsWindow,
    IsWindowVisible, WINDOWPLACEMENT, WS_EX_TOOLWINDOW,
};
use windows::core::BOOL;

use crate::{SourceInfo, SourceKind};

#[derive(Clone, Copy, Debug)]
pub(crate) enum Target {
    Monitor(isize),
    Window(isize),
}

const MONITORINFOF_PRIMARY: u32 = 1;

fn monitors() -> Vec<(HMONITOR, MONITORINFOEXW)> {
    unsafe extern "system" fn collect(
        monitor: HMONITOR,
        _dc: HDC,
        _rect: *mut RECT,
        data: LPARAM,
    ) -> BOOL {
        // SAFETY: data is the &mut Vec passed to EnumDisplayMonitors below,
        // alive for the whole enumeration.
        let list = unsafe { &mut *(data.0 as *mut Vec<(HMONITOR, MONITORINFOEXW)>) };
        let mut info = MONITORINFOEXW::default();
        info.monitorInfo.cbSize = size_of::<MONITORINFOEXW>() as u32;
        // SAFETY: info is a correctly sized MONITORINFOEXW.
        if unsafe {
            GetMonitorInfoW(
                monitor,
                &mut info as *mut MONITORINFOEXW as *mut MONITORINFO,
            )
        }
        .as_bool()
        {
            list.push((monitor, info));
        }
        BOOL(1)
    }
    let mut list: Vec<(HMONITOR, MONITORINFOEXW)> = Vec::new();
    // SAFETY: the callback only touches list through the LPARAM, and list
    // outlives the synchronous enumeration.
    unsafe {
        let _ = EnumDisplayMonitors(
            None,
            None,
            Some(collect),
            LPARAM(&mut list as *mut _ as isize),
        );
    }
    list
}

fn window_title(hwnd: HWND) -> String {
    let mut buf = [0u16; 256];
    // SAFETY: buf is a writable UTF-16 buffer whose length is passed.
    let n = unsafe { GetWindowTextW(hwnd, &mut buf) };
    String::from_utf16_lossy(&buf[..n.max(0) as usize])
}

fn shareable_window(hwnd: HWND) -> Option<(String, u32, u32)> {
    // SAFETY: read-only queries on a window handle handed to the enum
    // callback; a stale handle only makes them fail.
    unsafe {
        if !IsWindowVisible(hwnd).as_bool() || GetAncestor(hwnd, GA_ROOT) != hwnd {
            return None;
        }
        if GetWindowLongPtrW(hwnd, GWL_EXSTYLE) as u32 & WS_EX_TOOLWINDOW.0 != 0 {
            return None;
        }
        let mut cloaked = 0u32;
        if DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, &mut cloaked as *mut u32 as *mut _, 4).is_ok()
            && cloaked != 0
        {
            return None;
        }
        let title = window_title(hwnd);
        if title.trim().is_empty() {
            return None;
        }
        let (w, h) = window_size(hwnd)?;
        (w >= 64 && h >= 64).then_some((title, w, h))
    }
}

pub(crate) fn window_size(hwnd: HWND) -> Option<(u32, u32)> {
    let mut rect = RECT::default();
    // SAFETY: read-only queries on a window handle; a stale one only fails.
    unsafe {
        if IsIconic(hwnd).as_bool() {
            let mut placement = WINDOWPLACEMENT {
                length: size_of::<WINDOWPLACEMENT>() as u32,
                ..Default::default()
            };
            GetWindowPlacement(hwnd, &mut placement).ok()?;
            rect = placement.rcNormalPosition;
        } else {
            GetClientRect(hwnd, &mut rect).ok()?;
        }
    }
    Some((
        (rect.right - rect.left).max(0) as u32,
        (rect.bottom - rect.top).max(0) as u32,
    ))
}

pub fn list() -> Vec<SourceInfo> {
    let mut out = Vec::new();
    for (index, (monitor, info)) in monitors().into_iter().enumerate() {
        let rect = info.monitorInfo.rcMonitor;
        let device = String::from_utf16_lossy(
            &info.szDevice[..info
                .szDevice
                .iter()
                .position(|&c| c == 0)
                .unwrap_or(info.szDevice.len())],
        );
        let primary = info.monitorInfo.dwFlags & MONITORINFOF_PRIMARY != 0;
        out.push(SourceInfo {
            id: format!("monitor:{}:{}", monitor.0 as isize, device),
            kind: SourceKind::Monitor,
            name: format!("Display {}", index + 1),
            width: (rect.right - rect.left).max(0) as u32,
            height: (rect.bottom - rect.top).max(0) as u32,
            primary,
        });
    }
    unsafe extern "system" fn collect(hwnd: HWND, data: LPARAM) -> BOOL {
        // SAFETY: data is the &mut Vec passed to EnumWindows below.
        let list = unsafe { &mut *(data.0 as *mut Vec<HWND>) };
        list.push(hwnd);
        BOOL(1)
    }
    let mut windows: Vec<HWND> = Vec::new();
    // SAFETY: as for the monitor enumeration, the callback only appends to
    // the vector named by the LPARAM.
    unsafe {
        let _ = EnumWindows(Some(collect), LPARAM(&mut windows as *mut _ as isize));
    }
    for hwnd in windows {
        if let Some((title, width, height)) = shareable_window(hwnd) {
            out.push(SourceInfo {
                id: format!("window:{}", hwnd.0 as isize),
                kind: SourceKind::Window,
                name: title,
                width,
                height,
                primary: false,
            });
        }
    }
    out
}

pub(crate) fn resolve(id: &str) -> Option<Target> {
    if id.is_empty() {
        let monitors = monitors();
        let primary = monitors
            .iter()
            .find(|(_, info)| info.monitorInfo.dwFlags & MONITORINFOF_PRIMARY != 0)
            .or(monitors.first())?;
        return Some(Target::Monitor(primary.0.0 as isize));
    }
    let mut parts = id.splitn(3, ':');
    match (parts.next()?, parts.next()?.parse::<isize>().ok()?) {
        ("monitor", handle) => monitors()
            .iter()
            .any(|(m, _)| m.0 as isize == handle)
            .then_some(Target::Monitor(handle)),
        ("window", handle) => {
            // SAFETY: IsWindow only validates the handle value.
            let alive = unsafe { IsWindow(Some(HWND(handle as *mut _))) }.as_bool();
            alive.then_some(Target::Window(handle))
        }
        _ => None,
    }
}
