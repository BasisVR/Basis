use basis_rt_protocol::*;

pub type Result<T> = std::result::Result<T, String>;

#[cfg(windows)]
mod imp {
    use super::*;
    use windows::Win32::Foundation::*;
    use windows::Win32::System::Memory::*;
    use windows::Win32::System::Threading::*;
    use windows::core::*;

    pub type Handle = HANDLE;

    pub struct Host { pub base: *mut u8, mapping: HANDLE, wake: HANDLE, process: Option<HANDLE> }

    impl Host {
        pub fn new() -> super::Result<Host> {
            unsafe {
                let pid = GetCurrentProcessId();
                let name = HSTRING::from(mapping_name(pid));
                let mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, None, PAGE_READWRITE, (MAPPING_BYTES as u64 >> 32) as u32, MAPPING_BYTES as u32, &name).map_err(|e| e.message())?;
                let view = MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, MAPPING_BYTES);
                if view.Value.is_null() { return Err(Error::from_thread().message()); }
                let wake = CreateEventW(None, false, false, &HSTRING::from(wake_tracer_name(pid))).map_err(|e| e.message())?;
                Ok(Host { base: view.Value as *mut u8, mapping, wake, process: None })
            }
        }

        pub fn pid() -> u32 { unsafe { GetCurrentProcessId() } }

        pub fn launch(&mut self, exe: &str, vulkan: bool) -> super::Result<()> {
            unsafe {
                let pid = GetCurrentProcessId();
                let directory = std::path::Path::new(exe).parent().map(|p| p.to_string_lossy().into_owned()).unwrap_or_default();
                let api = if vulkan { " --api vulkan" } else { "" };
                let mut command: Vec<u16> = format!("\"{exe}\" --parent {pid}{api}").encode_utf16().chain(Some(0)).collect();
                let startup = STARTUPINFOW { cb: std::mem::size_of::<STARTUPINFOW>() as u32, ..Default::default() };
                let mut info = PROCESS_INFORMATION::default();
                CreateProcessW(&HSTRING::from(exe), Some(PWSTR(command.as_mut_ptr())), None, None, false, CREATE_NO_WINDOW | ABOVE_NORMAL_PRIORITY_CLASS, None, &HSTRING::from(directory), &startup, &mut info).map_err(|e| e.message())?;
                let _ = CloseHandle(info.hThread);
                self.process = Some(info.hProcess);
            }
            Ok(())
        }

        pub fn exited(&self) -> bool { self.process.is_some_and(|p| unsafe { WaitForSingleObject(p, 0) } == WAIT_OBJECT_0) }

        pub fn wake(&self) { unsafe { let _ = SetEvent(self.wake); } }

        pub fn take_handle(&mut self, value: u64) -> Option<Handle> { if value == 0 { None } else { Some(HANDLE(value as *mut core::ffi::c_void)) } }

        pub fn close_handle(handle: Handle) { unsafe { let _ = CloseHandle(handle); } }

        pub fn stop(&mut self) {
            if let Some(p) = self.process.take() {
                unsafe {
                    if WaitForSingleObject(p, 0) != WAIT_OBJECT_0 { let _ = TerminateProcess(p, 0); WaitForSingleObject(p, 2000); }
                    let _ = CloseHandle(p);
                }
            }
        }
    }

    impl Drop for Host {
        fn drop(&mut self) {
            self.stop();
            unsafe {
                let _ = UnmapViewOfFile(MEMORY_MAPPED_VIEW_ADDRESS { Value: self.base as _ });
                let _ = CloseHandle(self.mapping);
                let _ = CloseHandle(self.wake);
            }
        }
    }
}

#[cfg(unix)]
mod imp {
    use super::*;
    use std::collections::HashMap;

    pub type Handle = i32;

    pub struct Host { pub base: *mut u8, shm: i32, wake: i32, socket: i32, child_socket: i32, child: Option<i32>, exited: std::cell::Cell<bool>, received: HashMap<u64, i32> }

    fn last() -> String { std::io::Error::last_os_error().to_string() }

    impl Host {
        pub fn new() -> super::Result<Host> {
            unsafe {
                let shm = libc::memfd_create(c"BasisRayTracer".as_ptr(), libc::MFD_CLOEXEC);
                if shm < 0 { return Err(format!("create shared memory: {}", last())); }
                if libc::ftruncate(shm, MAPPING_BYTES as libc::off_t) != 0 { libc::close(shm); return Err(format!("size shared memory: {}", last())); }
                let base = libc::mmap(std::ptr::null_mut(), MAPPING_BYTES, libc::PROT_READ | libc::PROT_WRITE, libc::MAP_SHARED, shm, 0);
                if base == libc::MAP_FAILED { libc::close(shm); return Err(format!("map shared memory: {}", last())); }
                let wake = libc::eventfd(0, libc::EFD_CLOEXEC | libc::EFD_NONBLOCK);
                let mut pair = [0i32; 2];
                if wake < 0 || libc::socketpair(libc::AF_UNIX, libc::SOCK_SEQPACKET | libc::SOCK_CLOEXEC, 0, pair.as_mut_ptr()) != 0 { return Err(format!("create tracer channel: {}", last())); }
                Ok(Host { base: base as *mut u8, shm, wake, socket: pair[0], child_socket: pair[1], child: None, exited: std::cell::Cell::new(false), received: HashMap::new() })
            }
        }

        pub fn pid() -> u32 { std::process::id() }

        pub fn launch(&mut self, exe: &str, _vulkan: bool) -> super::Result<()> {
            use std::os::unix::fs::PermissionsExt;
            use std::os::unix::process::CommandExt;
            if let Ok(meta) = std::fs::metadata(exe) {
                let mode = meta.permissions().mode();
                if mode & 0o111 == 0 { let _ = std::fs::set_permissions(exe, std::fs::Permissions::from_mode(mode | 0o755)); }
            }
            let (shm, wake, socket) = (self.shm, self.wake, self.child_socket);
            let directory = std::path::Path::new(exe).parent().map(|p| p.to_path_buf()).unwrap_or_default();
            let mut command = std::process::Command::new(exe);
            command.arg("--parent").arg(std::process::id().to_string()).arg("--api").arg("vulkan").arg("--shm").arg(shm.to_string()).arg("--wake").arg(wake.to_string()).arg("--socket").arg(socket.to_string()).current_dir(directory);
            unsafe {
                command.pre_exec(move || {
                    for fd in [shm, wake, socket] { libc::fcntl(fd, libc::F_SETFD, 0); }
                    libc::prctl(libc::PR_SET_PDEATHSIG, libc::SIGKILL);
                    Ok(())
                });
            }
            let child = command.spawn().map_err(|e| format!("launch {exe}: {e}"))?;
            self.child = Some(child.id() as i32);
            std::mem::forget(child);
            self.exited.set(false);
            Ok(())
        }

        pub fn exited(&self) -> bool {
            if self.exited.get() { return true; }
            let Some(pid) = self.child else { return false; };
            let mut status = 0;
            let r = unsafe { libc::waitpid(pid, &mut status, libc::WNOHANG) };
            if r == pid || r < 0 { self.exited.set(true); }
            self.exited.get()
        }

        pub fn wake(&self) { let one = 1u64; unsafe { libc::write(self.wake, &one as *const u64 as *const libc::c_void, 8); } }

        fn drain(&mut self) {
            loop {
                unsafe {
                    let mut tag = 0u64;
                    let mut iov = libc::iovec { iov_base: &mut tag as *mut u64 as *mut libc::c_void, iov_len: 8 };
                    let space = libc::CMSG_SPACE(std::mem::size_of::<i32>() as u32) as usize;
                    let mut control = vec![0u8; space];
                    let mut msg: libc::msghdr = std::mem::zeroed();
                    msg.msg_iov = &mut iov;
                    msg.msg_iovlen = 1;
                    msg.msg_control = control.as_mut_ptr() as *mut libc::c_void;
                    msg.msg_controllen = space as _;
                    if libc::recvmsg(self.socket, &mut msg, libc::MSG_DONTWAIT | libc::MSG_CMSG_CLOEXEC) != 8 { return; }
                    let cmsg = libc::CMSG_FIRSTHDR(&msg);
                    if cmsg.is_null() || (*cmsg).cmsg_type != libc::SCM_RIGHTS { continue; }
                    let fd = std::ptr::read_unaligned(libc::CMSG_DATA(cmsg) as *const i32);
                    if let Some(old) = self.received.insert(tag, fd) { libc::close(old); }
                }
            }
        }

        pub fn take_handle(&mut self, value: u64) -> Option<Handle> {
            self.drain();
            self.received.remove(&value)
        }

        pub fn close_handle(handle: Handle) { unsafe { libc::close(handle); } }

        pub fn stop(&mut self) {
            if let Some(pid) = self.child.take() {
                unsafe {
                    if !self.exited.get() {
                        libc::kill(pid, libc::SIGKILL);
                        let mut status = 0;
                        libc::waitpid(pid, &mut status, 0);
                    }
                }
                self.exited.set(true);
            }
        }
    }

    impl Drop for Host {
        fn drop(&mut self) {
            self.stop();
            unsafe {
                for (_, fd) in self.received.drain() { libc::close(fd); }
                libc::munmap(self.base as *mut libc::c_void, MAPPING_BYTES);
                for fd in [self.shm, self.wake, self.socket, self.child_socket] { libc::close(fd); }
            }
        }
    }
}

pub use imp::*;
