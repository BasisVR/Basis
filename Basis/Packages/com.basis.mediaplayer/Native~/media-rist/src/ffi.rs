//! Hand-written declarations for the slice of the librist 0.2.x receiver API
//! this crate drives, matching the pinned headers staged under
//! `third_party/librist/include/librist/`. The layout of every struct declared
//! here is asserted against those headers on each build by the C shim in
//! `csrc/layout_check.c` plus the `layout` test module below.
//!
//! Enums cross the boundary as `c_int` (librist can hand back values newer
//! than these headers; a Rust `enum` would be UB there).

#![allow(dead_code)]

use std::ffi::{c_char, c_int, c_void};

pub const RIST_PROFILE_MAIN: c_int = 1;
pub const RIST_LOG_DISABLE: c_int = -1;

pub const RIST_MAX_STRING_SHORT: usize = 128;
pub const RIST_MAX_STRING_LONG: usize = 256;

/// OS-level address family constants, as librist compares them.
pub const AF_INET: c_int = 2;
#[cfg(windows)]
pub const AF_INET6: c_int = 23;
#[cfg(not(windows))]
pub const AF_INET6: c_int = 10;

#[repr(C)]
pub struct RistCtx {
    _opaque: [u8; 0],
}
#[repr(C)]
pub struct RistPeer {
    _opaque: [u8; 0],
}
#[repr(C)]
pub struct RistRef {
    _opaque: [u8; 0],
}

#[repr(C)]
pub struct RistDataBlock {
    pub payload: *const c_void,
    pub payload_len: usize,
    pub ts_ntp: u64,
    pub virt_src_port: u16,
    pub virt_dst_port: u16,
    pub peer: *mut RistPeer,
    pub flow_id: u32,
    pub seq: u64,
    pub flags: u32,
    pub r#ref: *mut RistRef,
}

#[repr(C)]
pub struct RistPeerConfig {
    pub version: c_int,
    pub address_family: c_int,
    pub initiate_conn: c_int,
    pub address: [c_char; RIST_MAX_STRING_LONG],
    pub miface: [c_char; RIST_MAX_STRING_SHORT],
    pub physical_port: u16,
    pub virt_dst_port: u16,
    pub recovery_mode: c_int,
    pub recovery_maxbitrate: u32,
    pub recovery_maxbitrate_return: u32,
    pub recovery_length_min: u32,
    pub recovery_length_max: u32,
    pub recovery_reorder_buffer: u32,
    pub recovery_rtt_min: u32,
    pub recovery_rtt_max: u32,
    pub weight: u32,
    pub secret: [c_char; RIST_MAX_STRING_SHORT],
    pub key_size: c_int,
    pub key_rotation: u32,
    pub compression: c_int,
    pub cname: [c_char; RIST_MAX_STRING_SHORT],
    pub congestion_control_mode: c_int,
    pub min_retries: u32,
    pub max_retries: u32,
    pub session_timeout: u32,
    pub keepalive_interval: u32,
    pub timing_mode: c_int,
    pub srp_username: [c_char; RIST_MAX_STRING_LONG],
    pub srp_password: [c_char; RIST_MAX_STRING_LONG],
}

#[repr(C)]
pub struct RistLoggingSettings {
    pub log_level: c_int,
    pub log_cb: Option<extern "C" fn(*mut c_void, c_int, *const c_char) -> c_int>,
    pub log_cb_arg: *mut c_void,
    pub log_socket: c_int,
    pub log_stream: *mut c_void, // FILE*
}

pub type ReceiverDataCallback2 = extern "C" fn(*mut c_void, *mut RistDataBlock) -> c_int;

unsafe extern "C" {
    pub fn rist_receiver_create(
        ctx: *mut *mut RistCtx,
        profile: c_int,
        logging_settings: *mut RistLoggingSettings,
    ) -> c_int;
    pub fn rist_receiver_data_callback_set2(
        ctx: *mut RistCtx,
        callback: ReceiverDataCallback2,
        arg: *mut c_void,
    ) -> c_int;
    pub fn rist_receiver_data_block_free2(block: *mut *mut RistDataBlock);
    pub fn rist_start(ctx: *mut RistCtx) -> c_int;
    pub fn rist_destroy(ctx: *mut RistCtx) -> c_int;

    pub fn rist_parse_address2(url: *const c_char, peer_config: *mut *mut RistPeerConfig) -> c_int;
    pub fn rist_peer_config_free2(peer_config: *mut *mut RistPeerConfig) -> c_int;
    pub fn rist_peer_create(
        ctx: *mut RistCtx,
        peer: *mut *mut RistPeer,
        config: *const RistPeerConfig,
    ) -> c_int;

    pub fn rist_logging_set(
        logging_settings: *mut *mut RistLoggingSettings,
        log_level: c_int,
        log_cb: Option<extern "C" fn(*mut c_void, c_int, *const c_char) -> c_int>,
        cb_arg: *mut c_void,
        address: *mut c_char,
        logfp: *mut c_void,
    ) -> c_int;
    pub fn rist_logging_settings_free2(logging_settings: *mut *mut RistLoggingSettings) -> c_int;

    pub fn librist_version() -> *const c_char;
}

#[cfg(test)]
mod layout {
    use super::*;
    use std::mem::{offset_of, size_of};

    unsafe extern "C" {
        fn bm_rist_sizeof_peer_config() -> usize;
        fn bm_rist_offsetof_pc_initiate_conn() -> usize;
        fn bm_rist_offsetof_pc_address() -> usize;
        fn bm_rist_offsetof_pc_physical_port() -> usize;
        fn bm_rist_offsetof_pc_secret() -> usize;
        fn bm_rist_offsetof_pc_key_size() -> usize;
        fn bm_rist_offsetof_pc_srp_password() -> usize;
        fn bm_rist_sizeof_data_block() -> usize;
        fn bm_rist_offsetof_db_payload() -> usize;
        fn bm_rist_offsetof_db_payload_len() -> usize;
        fn bm_rist_offsetof_db_flags() -> usize;
        fn bm_rist_offsetof_db_ref() -> usize;
        fn bm_rist_sizeof_logging_settings() -> usize;
        fn bm_rist_offsetof_ls_log_stream() -> usize;
    }

    #[test]
    fn declared_layout_matches_pinned_headers() {
        // SAFETY: the shim functions only return compile-time constants.
        unsafe {
            assert_eq!(size_of::<RistPeerConfig>(), bm_rist_sizeof_peer_config());
            assert_eq!(
                offset_of!(RistPeerConfig, initiate_conn),
                bm_rist_offsetof_pc_initiate_conn()
            );
            assert_eq!(
                offset_of!(RistPeerConfig, address),
                bm_rist_offsetof_pc_address()
            );
            assert_eq!(
                offset_of!(RistPeerConfig, physical_port),
                bm_rist_offsetof_pc_physical_port()
            );
            assert_eq!(
                offset_of!(RistPeerConfig, secret),
                bm_rist_offsetof_pc_secret()
            );
            assert_eq!(
                offset_of!(RistPeerConfig, key_size),
                bm_rist_offsetof_pc_key_size()
            );
            assert_eq!(
                offset_of!(RistPeerConfig, srp_password),
                bm_rist_offsetof_pc_srp_password()
            );

            assert_eq!(size_of::<RistDataBlock>(), bm_rist_sizeof_data_block());
            assert_eq!(
                offset_of!(RistDataBlock, payload),
                bm_rist_offsetof_db_payload()
            );
            assert_eq!(
                offset_of!(RistDataBlock, payload_len),
                bm_rist_offsetof_db_payload_len()
            );
            assert_eq!(
                offset_of!(RistDataBlock, flags),
                bm_rist_offsetof_db_flags()
            );
            assert_eq!(offset_of!(RistDataBlock, r#ref), bm_rist_offsetof_db_ref());

            assert_eq!(
                size_of::<RistLoggingSettings>(),
                bm_rist_sizeof_logging_settings()
            );
            assert_eq!(
                offset_of!(RistLoggingSettings, log_stream),
                bm_rist_offsetof_ls_log_stream()
            );
        }
    }
}
