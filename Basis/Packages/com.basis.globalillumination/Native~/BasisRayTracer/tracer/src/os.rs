use basis_rt_protocol::*;

pub struct Args {
    pub parent: u32,
    pub vulkan: bool,
    pub list_adapters: Option<String>,
    #[cfg(unix)]
    pub shm: i32,
    #[cfg(unix)]
    pub wake: i32,
    #[cfg(unix)]
    pub socket: i32,
}

impl Args {
    pub fn parse() -> Args {
        let args: Vec<String> = std::env::args().collect();
        let value = |name: &str| args.iter().position(|a| a == name).and_then(|i| args.get(i + 1)).cloned();
        let number = |name: &str| value(name).and_then(|v| v.parse::<i64>().ok());
        Args {
            parent: number("--parent").unwrap_or(0) as u32,
            vulkan: cfg!(not(windows)) || value("--api").is_some_and(|v| v.eq_ignore_ascii_case("vulkan")),
            list_adapters: value("--list-adapters"),
            #[cfg(unix)]
            shm: number("--shm").unwrap_or(-1) as i32,
            #[cfg(unix)]
            wake: number("--wake").unwrap_or(-1) as i32,
            #[cfg(unix)]
            socket: number("--socket").unwrap_or(-1) as i32,
        }
    }
}

#[cfg(windows)]
mod imp {
    use super::*;
    use windows::Win32::Foundation::*;
    use windows::Win32::System::Memory::*;
    use windows::Win32::System::Threading::*;
    use windows::core::*;

    pub struct Link { pub base: *mut u8, _mapping: HANDLE, pub parent: HANDLE, wake: HANDLE }

    impl Link {
        pub fn open_mapping(args: &Args) -> std::result::Result<Link, String> {
            unsafe {
                let name = HSTRING::from(mapping_name(args.parent));
                let mapping = OpenFileMappingW(FILE_MAP_ALL_ACCESS.0, false, &name).map_err(|e| format!("open shared memory: {e}"))?;
                let view = MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, MAPPING_BYTES);
                if view.Value.is_null() { return Err(format!("open shared memory: {}", Error::from_thread())); }
                Ok(Link { base: view.Value as *mut u8, _mapping: mapping, parent: HANDLE::default(), wake: HANDLE::default() })
            }
        }

        pub fn connect(&mut self, args: &Args) -> std::result::Result<(), String> {
            unsafe {
                self.parent = OpenProcess(PROCESS_DUP_HANDLE | PROCESS_SYNCHRONIZE, false, args.parent).map_err(|e| format!("open Unity process: {e}"))?;
                self.wake = OpenEventW(EVENT_ALL_ACCESS, false, &HSTRING::from(wake_tracer_name(args.parent))).map_err(|e| format!("open wake event: {e}"))?;
            }
            Ok(())
        }

        pub fn parent_alive(&self) -> bool { unsafe { WaitForSingleObject(self.parent, 0) != WAIT_OBJECT_0 } }

        pub fn wait_wake(&self, ms: u32) { unsafe { WaitForSingleObject(self.wake, ms); } }

        pub fn share_handle(&self, local: HANDLE) -> Result<u64> {
            unsafe {
                let mut remote = HANDLE::default();
                DuplicateHandle(GetCurrentProcess(), local, self.parent, &mut remote, 0, false, DUPLICATE_SAME_ACCESS)?;
                Ok(remote.0 as u64)
            }
        }
    }
}

#[cfg(unix)]
mod imp {
    use super::*;

    pub struct Link { pub base: *mut u8, parent: u32, wake: i32, socket: i32, next_tag: std::cell::Cell<u64> }

    impl Link {
        pub fn open_mapping(args: &Args) -> std::result::Result<Link, String> {
            if args.shm < 0 { return Err("missing --shm".into()); }
            let base = unsafe { libc::mmap(std::ptr::null_mut(), MAPPING_BYTES, libc::PROT_READ | libc::PROT_WRITE, libc::MAP_SHARED, args.shm, 0) };
            if base == libc::MAP_FAILED { return Err(format!("map shared memory: {}", std::io::Error::last_os_error())); }
            Ok(Link { base: base as *mut u8, parent: args.parent, wake: args.wake, socket: args.socket, next_tag: std::cell::Cell::new(1) })
        }

        pub fn connect(&mut self, _args: &Args) -> std::result::Result<(), String> {
            unsafe { libc::prctl(libc::PR_SET_PDEATHSIG, libc::SIGKILL); }
            if self.wake < 0 || self.socket < 0 { return Err("missing --wake or --socket".into()); }
            if !self.parent_alive() { return Err("Unity exited before the tracer started".into()); }
            Ok(())
        }

        pub fn parent_alive(&self) -> bool { unsafe { libc::getppid() as u32 == self.parent } }

        pub fn wait_wake(&self, ms: u32) {
            let mut fd = libc::pollfd { fd: self.wake, events: libc::POLLIN, revents: 0 };
            unsafe {
                if libc::poll(&mut fd, 1, ms as i32) > 0 && fd.revents & libc::POLLIN != 0 {
                    let mut value = 0u64;
                    libc::read(self.wake, &mut value as *mut u64 as *mut libc::c_void, 8);
                }
            }
        }

        pub fn share_fd(&self, fd: i32) -> std::result::Result<u64, String> {
            let tag = self.next_tag.get();
            self.next_tag.set(tag + 1);
            let result = send_fd(self.socket, tag, fd);
            unsafe { libc::close(fd); }
            result.map(|_| tag)
        }
    }

    pub fn send_fd(socket: i32, tag: u64, fd: i32) -> std::result::Result<(), String> {
        unsafe {
            let mut payload = tag;
            let mut iov = libc::iovec { iov_base: &mut payload as *mut u64 as *mut libc::c_void, iov_len: 8 };
            let space = libc::CMSG_SPACE(std::mem::size_of::<i32>() as u32) as usize;
            let mut control = vec![0u8; space];
            let mut msg: libc::msghdr = std::mem::zeroed();
            msg.msg_iov = &mut iov;
            msg.msg_iovlen = 1;
            msg.msg_control = control.as_mut_ptr() as *mut libc::c_void;
            msg.msg_controllen = space as _;
            let cmsg = libc::CMSG_FIRSTHDR(&msg);
            (*cmsg).cmsg_level = libc::SOL_SOCKET;
            (*cmsg).cmsg_type = libc::SCM_RIGHTS;
            (*cmsg).cmsg_len = libc::CMSG_LEN(std::mem::size_of::<i32>() as u32) as _;
            std::ptr::write_unaligned(libc::CMSG_DATA(cmsg) as *mut i32, fd);
            if libc::sendmsg(socket, &msg, libc::MSG_NOSIGNAL) != 8 { return Err(format!("send handle to Unity: {}", std::io::Error::last_os_error())); }
        }
        Ok(())
    }
}

pub use imp::*;

impl Link {
    pub fn header<'a>(&self) -> &'a mut Header { unsafe { &mut *(self.base as *mut Header) } }
}
