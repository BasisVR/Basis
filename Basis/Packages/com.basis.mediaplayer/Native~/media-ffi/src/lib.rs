//! ABI v4 boundary: opaque generational handles, one snapshot poll
//! per frame, SPSC event drain, a lock-free audio pull, and one
//! render-event function pointer, whose event id selects the pass.
//! Poll-driven, no reverse callbacks; UTF-8 both directions;
//! `catch_unwind` at every export; `unsafe` at the boundary only.
//!
//! Graphics contract (D3D11, normative): the engine owns a shared
//! BGRA texture sized to the *coded* frame; the managed side creates a
//! BGRA32 Unity texture at the snapshot's display size, registers it via
//! `bm_session_set_output_texture`, and issues
//! `CommandBuffer.IssuePluginEventAndData(bm_render_event_func(), 1,
//! handle-as-pointer)` once per frame. The render event opens the shared
//! handle on Unity's device on first use and then only ever runs a
//! keyed-mutex acquire + `CopyResource`; it never waits on a media-path
//! lock.
//!
//! Graphics contract (D3D12, normative): the same texture and the same
//! event, with the texture an `ID3D12Resource`. `UnityPluginLoad` takes
//! `IUnityGraphicsD3D12v8` from Unity; the render event records a
//! `CopyResource` from the newest finished shared slot onto Unity's
//! current command list and never waits. The plugin holds its own
//! references to the texture and the slots until Unity's frame fence
//! passes their last copy, so the texture may be released straight after
//! `bm_session_close`.
//!
//! Teardown order: stop issuing render events, then
//! `bm_session_close`; the Unity texture must outlive the last issued
//! event, and on Vulkan by a few render events more, which the caller
//! keeps issuing as `BM_EVENT_COLLECT` (see
//! `bm_session_set_output_texture`).

mod host_log;

use std::ffi::c_void;
use std::panic::{AssertUnwindSafe, catch_unwind};
#[cfg(windows)]
use std::sync::atomic::AtomicI64;
// The import `stable_texture` needs cannot be Android-only: it compiles
// under test on every host.
#[cfg(any(target_os = "android", test))]
use std::sync::atomic::AtomicU64;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex, OnceLock};

use media_engine::{OpenRequest, PipelineShared, Session, SessionShared};
#[cfg(windows)]
use media_present::{D3d12Consumer, PresentError, SharedTextureConsumer};

pub const BM_ABI_VERSION: u32 = 4;

pub const BM_OK: i32 = 0;
pub const BM_ERR_INVALID_ARG: i32 = -1;
pub const BM_ERR_INVALID_HANDLE: i32 = -2;
pub const BM_ERR_PANIC: i32 = -3;

/// Render-event id for the per-session present pass: `data` is the session
/// handle. The id the graphics contract has always named.
pub const BM_EVENT_PRESENT: i32 = 1;

/// Render-event id for a collect-only pass on Android: `data` is ignored and
/// no session is looked up. Issued by the managed side while it is holding a
/// retired output texture, so the Vulkan objects made over that texture are
/// destroyed before it releases the image beneath them. On every other
/// platform it finds no session and does nothing.
pub const BM_EVENT_COLLECT: i32 = 2;

/// One poll fills this; the managed side dispatches locally. Fixed layout,
/// append-only across minor revisions.
#[repr(C)]
pub struct BmSnapshot {
    pub abi_version: u32,
    pub state: u32,
    pub width: u32,
    pub height: u32,
    pub position_us: i64,
    pub duration_us: i64,
    pub frames_decoded: u64,
    pub frames_presented: u64,
    /// Structured error: stable code, category (see
    /// `media_engine::ErrorCategory`), detail via the event drain.
    pub error_code: i32,
    pub error_category: u32,
    /// Compressed media banked ahead of release, milliseconds.
    pub banked_ms: i64,
    /// 1 while the startup hold is filling.
    pub bank_holding: u32,
    pub audio_sample_rate: u32,
    pub audio_channels: u32,
    /// Presented video pts minus the audio playhead, µs: the engine's own
    /// account of its A/V alignment. `i32::MIN` while either side is
    /// unknown. Diagnostic only; nothing in the engine steers on it.
    pub av_offset_us: i32,
    /// Shared-playback sync: the ladder's wanted rate offset from
    /// 1x, ppm, after a `bm_session_set_sync_target` call. On lanes with
    /// an audio track the managed audio pull MUST apply it, consuming
    /// source frames at `1 + ppm/1e6` times the stream rate through the
    /// resampler, or the slew rung silently does nothing and every
    /// correction waits for the seek rung. 0 = no correction wanted.
    pub sync_rate_ppm: i32,
    /// Events the session's log refused because its cap was already full,
    /// cumulative. Non-zero means the drained sequence has holes and must
    /// not be treated as complete. Saturates rather than wrapping, since
    /// the engine counts in `u64`.
    pub events_dropped: u32,
}

/// A record written with a whole-struct copy hands the caller its padding
/// too, and padding is indeterminate. Every one of these is sized so it
/// has none; the asserts keep it that way when a cap or a field moves.
const _: () = {
    assert!(size_of::<BmSnapshot>() == 88);
    assert!(size_of::<BmEvent>() == 8 + 4 + 4 + 4 + BM_EVENT_DETAIL_CAP);
    assert!(size_of::<BmCaption>() == 8 + 4 + BM_CAPTION_TEXT_CAP + 4);
    assert!(size_of::<BmUserData>() == 8 + BM_USER_DATA_UUID_LEN + 4 + 4);
    assert!(size_of::<BmAudioTrack>() == 4 * 4 + BM_TRACK_LANG_CAP + 4 + BM_TRACK_LABEL_CAP);
    assert!(size_of::<BmLogRecord>() == 8 + 8 + 4 * 4 + BM_LOG_DETAIL_CAP);
    // `write_detail` makes room for the mark before stepping back to a
    // boundary, so a cap at or under its length would truncate into it.
    assert!(BM_EVENT_DETAIL_CAP > DETAIL_ELLIPSIS.len());
    assert!(BM_LOG_DETAIL_CAP > DETAIL_ELLIPSIS.len());
};

pub const BM_EVENT_DETAIL_CAP: usize = 116;

/// What a truncated detail ends with, so a reader can tell a line that was
/// cut from one that happened to end there. A character rather than a
/// flag: every record's layout is fixed and padding-free, and an eight-byte
/// field to say what one character says would move all of them.
const DETAIL_ELLIPSIS: &[u8] = "…".as_bytes();

/// Copy `text` into a fixed detail buffer, truncating on a UTF-8 boundary
/// and marking it when it did. Returns the bytes written.
///
/// The mark costs three bytes of the cap. That is the right trade: the
/// caller decodes these bytes as text and shows them to a person, and a
/// sentence that stops mid-word reads as the engine having nothing more to
/// say rather than as the record having run out of room.
fn write_detail(text: &str, out: &mut [u8]) -> u32 {
    let bytes = text.as_bytes();
    if bytes.len() <= out.len() {
        out[..bytes.len()].copy_from_slice(bytes);
        return bytes.len() as u32;
    }
    // Room for the mark first, then back to a boundary. The caller decodes
    // the whole buffer and must never meet a split character.
    let mut len = out.len() - DETAIL_ELLIPSIS.len();
    while len > 0 && !text.is_char_boundary(len) {
        len -= 1;
    }
    out[..len].copy_from_slice(&bytes[..len]);
    out[len..len + DETAIL_ELLIPSIS.len()].copy_from_slice(DETAIL_ELLIPSIS);
    (len + DETAIL_ELLIPSIS.len()) as u32
}

/// One diagnostics event. `detail` is UTF-8, cut to `detail_len` bytes and
/// ending in `…` when it was cut. **There is no fuller copy across the
/// ABI**: the session event log and the process log are fed separately, so
/// a cut detail is not recoverable through `bm_drain_log`. A site whose
/// text matters beyond this cap emits a free-text line as well.
#[repr(C)]
pub struct BmEvent {
    pub wall_us: i64,
    pub code: u32,
    pub stage: u32,
    pub detail_len: u32,
    pub detail: [u8; BM_EVENT_DETAIL_CAP],
}

/// Twice the event detail's ceiling: a free-text line is a sentence
/// rather than a field, and the useful ones (a WHEP negotiation step, a
/// transport refusal with its `source()` chain walked) run long.
pub const BM_LOG_DETAIL_CAP: usize = 224;

/// One process-log line, in the same shape a session event has.
/// Process-level: `session` is a field rather than the addressing, because
/// the channel exists for the lines emitted before a session handle exists
/// and after it closes, and 0 means exactly that.
///
/// `wall_us` is microseconds since the engine's first diagnostic in this
/// process, which is **not** a session's clock: `BmEvent::wall_us` counts
/// from that session's start. Do not subtract one from the other.
#[repr(C)]
pub struct BmLogRecord {
    pub wall_us: i64,
    pub session: u64,
    /// `media_diag::Level`: 0 error, 1 warn, 2 info.
    pub level: u32,
    /// `media_diag::EventCode`. Always `Log` (18) today; the field is
    /// there because the record shape is shared with the session events.
    pub code: u32,
    pub stage: u32,
    pub detail_len: u32,
    pub detail: [u8; BM_LOG_DETAIL_CAP],
}

/// Cue-text ceiling; a full 4-row pop-on screen fits well under it.
pub const BM_CAPTION_TEXT_CAP: usize = 256;

/// One caption cue (in-band CEA-608): the full displayed text as of
/// `pts_us`, UTF-8, rows joined with '\n'. `text_len` 0 = display cleared.
/// Cues surface on arrival (ahead of presentation); display them when the
/// session position reaches `pts_us`.
#[repr(C)]
pub struct BmCaption {
    pub pts_us: i64,
    pub text_len: u32,
    pub text: [u8; BM_CAPTION_TEXT_CAP],
    /// Names the four bytes the `i64`'s alignment adds after `text`, so
    /// they are written rather than left holding stack garbage. Not part
    /// of the contract; always 0.
    pub reserved: u32,
}

pub const BM_USER_DATA_UUID_LEN: usize = 16;

/// One SEI `user_data_unregistered` message, surfaced with its UUID and
/// left unparsed. The bytes themselves land in the caller's byte buffer
/// at `offset` for `len`; this record only points at them. Messages
/// surface on arrival (ahead of presentation); act on one when the
/// session position reaches `pts_us`.
#[repr(C)]
pub struct BmUserData {
    pub pts_us: i64,
    pub uuid: [u8; BM_USER_DATA_UUID_LEN],
    pub offset: u32,
    pub len: u32,
}

/// The JSON descriptor `bm_session_open` accepts. The resolver-facing
/// `SourceDescriptor` grows here field by field.
#[derive(serde::Deserialize)]
struct Descriptor {
    url: String,
    /// A separate audio-only source played against `url`, which is then
    /// treated as video-only. Adaptive ladders serve everything above
    /// their muxed fallback rung this way. Both legs are cuts of the same
    /// content, and their timelines already agree. On-demand HTTP(S) and
    /// local files only. Absent = one source carrying everything.
    #[serde(default)]
    audio_url: Option<String>,
    #[serde(default)]
    allow_local_addresses: bool,
    #[serde(default)]
    buffer_depth_ms: Option<u32>,
    /// "live" | "vod"; absent = auto, which is the default and lets the
    /// engine decide from the source itself. State one only to overrule
    /// a server whose headers mislead.
    #[serde(default)]
    liveness: Option<String>,
    /// Index into the offered audio track list to bind. Absent = 0, the
    /// container's first. Switching track re-opens with a new value.
    #[serde(default)]
    audio_track: Option<u32>,
    /// Absolute path the engine writes the capture-recorder CSV
    /// to on close (sampled at 100 ms engine-side). Absent = off.
    #[serde(default)]
    diag_csv: Option<String>,
    /// Append each session's capture to `diag_csv` rather than replacing
    /// it, so a player that opens and closes repeatedly keeps every run.
    /// Absent = replace.
    #[serde(default)]
    diag_csv_append: bool,
    /// Shared-playback divergence bound on live lanes,
    /// milliseconds: a ceiling on the Bank's lag cap and so on Auto's
    /// depth growth. Absent = the default cap.
    #[serde(default)]
    max_divergence_ms: Option<u32>,
    /// Decode-route preference: `"hardware_with_fallback"` |
    /// `"hardware_only"` | `"software_only"`. A per-user machine
    /// setting, never world-authored; a rung the platform does not have
    /// refuses typed. Absent (or unrecognised) = hardware_with_fallback.
    #[serde(default)]
    decode_preference: Option<String>,
}

struct Entry {
    session: Mutex<Option<Session>>,
    shared: Arc<SessionShared>,
    pipeline: Arc<PipelineShared>,
    unity_texture: AtomicUsize,
    /// Advanced by every output-texture registration, so the Android
    /// renderer can tell a re-registered texture from the one it cached
    /// a view over even when the driver reuses handle values.
    #[cfg(target_os = "android")]
    texture_generation: AtomicU64,
    #[cfg(windows)]
    consumer: Mutex<ConsumerSlot>,
    #[cfg(target_os = "android")]
    renderer: Mutex<media_present::android::SessionRenderer>,
}

/// The consumer is opened against one shared-texture handle. The engine
/// publishes a fresh handle whenever it rebuilds the presenter, so each
/// variant carries the handle it settled on and a differing one reopens.
#[cfg(windows)]
enum ConsumerSlot {
    Unopened,
    Open(u64, Consumer),
    /// The handle whose open failed, and how many attempts it has had.
    Failed(u64, u32),
}

/// The render thread's half of the handoff, per the API Unity renders
/// with. The registered texture is an `ID3D11Texture2D*` or an
/// `ID3D12Resource*` accordingly, so the choice follows Unity's renderer
/// and never the presenter, which a busy lock could hide.
#[cfg(windows)]
enum Consumer {
    D3d11(SharedTextureConsumer),
    D3d12(D3d12Consumer),
}

/// An open that succeeded, or one that cannot be tried until a later
/// render event. Only a failed open spends an attempt.
#[cfg(windows)]
enum Opened {
    Open(Consumer),
    NotReady,
}

#[cfg(windows)]
impl Consumer {
    /// # Safety
    /// `texture` is the texture the managed side registered, live for the
    /// render events it is registered for, and on Unity's renderer.
    /// `presenter` and `lookahead_events` are the session's.
    unsafe fn open(
        texture: *mut c_void,
        shared_handle: u64,
        presenter: &Mutex<Option<media_present::SharedTexturePresenter>>,
        lookahead_events: &AtomicI64,
    ) -> Result<Opened, PresentError> {
        let host = media_present::win_unity::unity_d3d12_host();
        if host.is_none() && media_present::win_unity::renderer_is_d3d12() {
            return Err(PresentError(
                "Unity is on Direct3D 12 without IUnityGraphicsD3D12v8".into(),
            ));
        }
        // A busy lock, no presenter yet, or one that does not own the
        // handle this event read all settle on a later event. A rebuild
        // publishes the new handle before it installs its presenter, and
        // installing it drops the old one, closing the old handle.
        let Ok(mut presenter) = presenter.try_lock() else {
            return Ok(Opened::NotReady);
        };
        let Some(p) = presenter
            .as_mut()
            .filter(|p| p.shared_handle() == shared_handle)
        else {
            return Ok(Opened::NotReady);
        };
        let Some(host) = host else {
            // SAFETY: the texture is as this function's contract says, and
            // the presenter held locked keeps `shared_handle` open across
            // the call. The open checks the texture's size and format.
            return unsafe { SharedTextureConsumer::open(texture, shared_handle) }
                .map(|c| Opened::Open(Self::D3d11(c)));
        };
        // Built while Unity's device was between a shutdown and the next
        // initialize: give it the handoff now.
        if p.d3d12_handoff().is_none() {
            p.enable_d3d12_handoff()?;
            lookahead_events.store(2, Ordering::Relaxed);
        }
        let handoff = p
            .d3d12_handoff()
            .ok_or_else(|| PresentError("the presenter has no Direct3D 12 handoff".into()))?;
        // SAFETY: on Direct3D 12 the registered texture is Unity's
        // `ID3D12Resource*`, per this function's contract.
        unsafe { D3d12Consumer::open(texture, handoff, host) }.map(|c| Opened::Open(Self::D3d12(c)))
    }

    fn copy_if_fresh(&mut self) -> Result<bool, PresentError> {
        match self {
            Self::D3d11(consumer) => consumer.copy_if_fresh(),
            Self::D3d12(consumer) => consumer.copy_if_fresh(),
        }
    }
}

/// How many times one shared handle's consumer open is attempted before
/// the slot gives up on it. A failure here is typically the handle
/// racing a presenter rebuild, which the next attempt sees through, and a
/// new handle is only published when the presenter is rebuilt. The bound
/// stops a dead handle calling `open` once per render event for the rest
/// of the session.
#[cfg(windows)]
const MAX_CONSUMER_OPENS: u32 = 8;

#[cfg(windows)]
impl ConsumerSlot {
    /// Which attempt an open for `handle` would be, or `None` where the
    /// slot already holds one for it or has spent its attempts.
    fn attempt_for(&self, handle: u64) -> Option<u32> {
        match self {
            ConsumerSlot::Unopened => Some(1),
            ConsumerSlot::Open(open_handle, _) => (*open_handle != handle).then_some(1),
            ConsumerSlot::Failed(open_handle, attempts) => {
                if *open_handle != handle {
                    Some(1)
                } else {
                    (*attempts < MAX_CONSUMER_OPENS).then_some(*attempts + 1)
                }
            }
        }
    }

    /// Record attempt `attempt` at an open for `handle`. A consumer not
    /// ready to open leaves the slot as it was. Returns the error of a
    /// failed open for the caller to log.
    fn settle(
        &mut self,
        handle: u64,
        attempt: u32,
        outcome: Result<Opened, PresentError>,
    ) -> Option<PresentError> {
        match outcome {
            Ok(Opened::Open(consumer)) => *self = ConsumerSlot::Open(handle, consumer),
            Ok(Opened::NotReady) => {}
            Err(e) => {
                *self = ConsumerSlot::Failed(handle, attempt);
                return Some(e);
            }
        }
        None
    }
}

struct Slot {
    generation: u32,
    entry: Option<Arc<Entry>>,
}

#[derive(Default)]
struct Registry {
    slots: Vec<Slot>,
}

fn registry() -> &'static Mutex<Registry> {
    static REGISTRY: OnceLock<Mutex<Registry>> = OnceLock::new();
    REGISTRY.get_or_init(|| Mutex::new(Registry::default()))
}

fn pack_handle(index: u32, generation: u32) -> u64 {
    (generation as u64) << 32 | index as u64
}

fn lookup(handle: u64) -> Option<Arc<Entry>> {
    let index = handle as u32 as usize;
    let generation = (handle >> 32) as u32;
    let registry = registry().lock().ok()?;
    let slot = registry.slots.get(index)?;
    if slot.generation != generation {
        return None;
    }
    slot.entry.clone()
}

/// Probe this before anything else; the managed side refuses a mismatch
/// loudly instead of degrading feature-by-feature.
#[unsafe(no_mangle)]
pub extern "C" fn bm_abi_version() -> u32 {
    BM_ABI_VERSION
}

/// Engine capability set (normative): writes one UTF-8 JSON blob
/// describing what this build will decode and play, and returns its byte
/// length. Engine-level, not per-session: call any time after
/// `bm_abi_version`. Call with (`NULL`, 0) to size, allocate, call again;
/// when `cap` is smaller than the blob nothing is written and the required
/// length still returns. Negative = error.
///
/// Shape (`version` is the contract version, currently 1; append-only
/// within a version):
/// `{"version":u32,"platform":str,"video":[{"codec":str,"route":str,
/// "max_width":u32,"max_height":u32,"max_fps":u32}],"audio":[{"codec":str,
/// "max_channels":u32}],"transports":[{"scheme":str,"note":str?}],
/// "containers":[str]}`
///
/// Known identifiers (string-typed; unknown strings are future additions,
/// skip them rather than failing):
/// - platform: "windows-x64", "android-arm64"
/// - video codec: "h264", "vp9", "av1", "h265", "vp8" (the last two only
///   have routes on Android's MediaCodec adapter today)
/// - route: "hardware" | "software". Every entry is a will-decode claim
///   for the primary route the engine would actually take. Hardware
///   routes state measured ceilings where the platform exposes them
///   (Android `MediaCodecList` figures); software routes state none
///   (0 = best effort; rank them conservatively).
/// - audio codec: "aac", "mp3", "opus", "flac" (later: "pcm").
///   `max_channels` is the adapter's real screen.
/// - transport scheme: "file", "http", "https", "rtsp", "rtspt", "rist"
///   ("rist" present only when the feature is compiled in)
/// - container: "mp4", "ts", "m2ts", "mkv", "webm", "hls", "flac",
///   "mp3", "adts", "ogg"
///
/// Snapshot semantics: the blob describes the moment it was built. A
/// capability-relevant runtime change surfaces as the
/// `DecodeFallbackHwToSw` diagnostics event (code 3 in the event drain);
/// treat it as the advisory to query again. Each call re-probes.
///
/// # Safety
/// `out_buf` must be `NULL` or point to `cap` writable bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bm_capabilities(out_buf: *mut u8, cap: usize) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        host_log::install();
        let json = media_engine::capabilities().to_json();
        let bytes = json.as_bytes();
        if !out_buf.is_null() && cap >= bytes.len() {
            // SAFETY: caller contract: out_buf points to cap writable
            // bytes and cap covers the blob.
            unsafe { std::ptr::copy_nonoverlapping(bytes.as_ptr(), out_buf, bytes.len()) };
        }
        bytes.len() as i32
    }))
    .unwrap_or(BM_ERR_PANIC)
}

/// Open a session from a UTF-8 JSON descriptor (`desc_ptr`, `desc_len`):
/// `{"url": "...", "audio_url": "..."?, "allow_local_addresses": bool?,
/// "buffer_depth_ms": u32?,
/// "liveness": "live"|"vod"?, "diag_csv": "path"?,
/// "decode_preference": "hardware_with_fallback"|"hardware_only"|"software_only"?}`.
/// Writes a generational handle to `out_handle`. Open runs asynchronously;
/// failures surface through the snapshot (`state == Error` + error code),
/// not this return value.
///
/// # Safety
/// `desc_ptr` must point to `desc_len` readable bytes; `out_handle` must
/// be a valid writable pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bm_session_open(
    desc_ptr: *const u8,
    desc_len: usize,
    out_handle: *mut u64,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        host_log::install();
        if desc_ptr.is_null() || out_handle.is_null() {
            return BM_ERR_INVALID_ARG;
        }
        // SAFETY: caller contract: desc_ptr points to desc_len readable
        // bytes for the duration of this call.
        let bytes = unsafe { std::slice::from_raw_parts(desc_ptr, desc_len) };
        let Ok(descriptor) = serde_json::from_slice::<Descriptor>(bytes) else {
            return BM_ERR_INVALID_ARG;
        };

        let mut request = OpenRequest::new(descriptor.url);
        request.audio_url = descriptor.audio_url;
        request.allow_local_addresses = descriptor.allow_local_addresses;
        request.buffer_depth_ms = descriptor.buffer_depth_ms;
        request.audio_track = descriptor.audio_track.unwrap_or(0) as usize;
        request.liveness = match descriptor.liveness.as_deref() {
            Some("live") => media_engine::SourceLiveness::Live,
            Some("vod") => media_engine::SourceLiveness::Vod,
            _ => media_engine::SourceLiveness::Auto,
        };
        request.diag_csv = descriptor.diag_csv.map(std::path::PathBuf::from);
        request.diag_csv_append = descriptor.diag_csv_append;
        request.max_divergence_ms = descriptor.max_divergence_ms;
        request.decode_preference = match descriptor.decode_preference.as_deref() {
            Some("hardware_only") => media_engine::DecodePreference::HardwareOnly,
            Some("software_only") => media_engine::DecodePreference::SoftwareOnly,
            _ => media_engine::DecodePreference::HardwareWithFallback,
        };
        let session = Session::open(request);
        let entry = Arc::new(Entry {
            shared: Arc::clone(session.shared()),
            pipeline: Arc::clone(session.pipeline()),
            session: Mutex::new(Some(session)),
            unity_texture: AtomicUsize::new(0),
            #[cfg(target_os = "android")]
            texture_generation: AtomicU64::new(0),
            #[cfg(windows)]
            consumer: Mutex::new(ConsumerSlot::Unopened),
            #[cfg(target_os = "android")]
            renderer: Mutex::new(media_present::android::SessionRenderer::new()),
        });

        let mut registry = registry().lock().expect("registry poisoned");
        let index = registry
            .slots
            .iter()
            .position(|slot| slot.entry.is_none())
            .unwrap_or_else(|| {
                registry.slots.push(Slot {
                    generation: 0,
                    entry: None,
                });
                registry.slots.len() - 1
            });
        let slot = &mut registry.slots[index];
        slot.generation = slot.generation.wrapping_add(1);
        slot.entry = Some(entry);
        // SAFETY: caller contract: out_handle is valid for writes.
        unsafe { *out_handle = pack_handle(index as u32, slot.generation) };
        BM_OK
    }))
    .unwrap_or(BM_ERR_PANIC)
}

/// # Safety
/// Safe to call with any value; a stale handle is detected, not
/// dereferenced.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bm_session_close(handle: u64) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        let index = handle as u32 as usize;
        let generation = (handle >> 32) as u32;
        let entry = {
            let mut registry = registry().lock().expect("registry poisoned");
            let Some(slot) = registry.slots.get_mut(index) else {
                return BM_ERR_INVALID_HANDLE;
            };
            if slot.generation != generation || slot.entry.is_none() {
                return BM_ERR_INVALID_HANDLE;
            }
            slot.generation = slot.generation.wrapping_add(1);
            slot.entry.take()
        };
        if let Some(entry) = entry
            && let Ok(mut session) = entry.session.lock()
            // Explicit close (join) rather than relying on drop timing.
            && let Some(mut session) = session.take()
        {
            session.close();
        }
        BM_OK
    }))
    .unwrap_or(BM_ERR_PANIC)
}

/// Narrow an engine counter to the `u32` the snapshot carries. A wrap
/// would report a smaller loss than actually happened, which is worse
/// than a ceiling: past `u32::MAX` the only honest claim left is "a very
/// great many".
fn saturate_u32(value: u64) -> u32 {
    value.min(u32::MAX as u64) as u32
}

/// Fill `out` with the session snapshot. One call per frame replaces
/// per-field polling.
///
/// # Safety
/// `out` must be a valid writable `BmSnapshot` pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bm_session_poll(handle: u64, out: *mut BmSnapshot) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        if out.is_null() {
            return BM_ERR_INVALID_ARG;
        }
        let Some(entry) = lookup(handle) else {
            return BM_ERR_INVALID_HANDLE;
        };
        let shared = &entry.shared;
        let snapshot = BmSnapshot {
            abi_version: BM_ABI_VERSION,
            state: shared.state.load(Ordering::Relaxed),
            width: shared.width.load(Ordering::Relaxed),
            height: shared.height.load(Ordering::Relaxed),
            position_us: shared.position_us.load(Ordering::Relaxed),
            duration_us: shared.duration_us.load(Ordering::Relaxed),
            frames_decoded: shared.frames_decoded.load(Ordering::Relaxed),
            frames_presented: shared.frames_presented.load(Ordering::Relaxed),
            error_code: shared.last_error.load(Ordering::Relaxed),
            error_category: shared.last_error_category.load(Ordering::Relaxed),
            banked_ms: shared.banked_us.load(Ordering::Relaxed) / 1000,
            bank_holding: u32::from(shared.bank_holding.load(Ordering::Relaxed)),
            audio_sample_rate: shared.audio_rate.load(Ordering::Relaxed),
            audio_channels: shared.audio_channels.load(Ordering::Relaxed),
            av_offset_us: shared.av_offset_us.load(Ordering::Relaxed),
            sync_rate_ppm: entry.pipeline.sync_rate_ppm.load(Ordering::Relaxed) as i32,
            events_dropped: saturate_u32(entry.pipeline.diag.events_dropped()),
        };
        // SAFETY: caller contract: out is valid for writes.
        unsafe { out.write(snapshot) };
        BM_OK
    }))
    .unwrap_or(BM_ERR_PANIC)
}

#[unsafe(no_mangle)]
pub extern "C" fn bm_session_play(handle: u64) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        let Some(entry) = lookup(handle) else {
            return BM_ERR_INVALID_HANDLE;
        };
        if let Ok(session) = entry.session.lock()
            && let Some(session) = session.as_ref()
        {
            session.play();
        }
        BM_OK
    }))
    .unwrap_or(BM_ERR_PANIC)
}

/// Pause an on-demand session. Ignored on a live source, which is not
/// pausable (the engine says so once on the process log); the call still
/// returns `BM_OK`, since nothing about the handle is wrong.
#[unsafe(no_mangle)]
pub extern "C" fn bm_session_pause(handle: u64) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        let Some(entry) = lookup(handle) else {
            return BM_ERR_INVALID_HANDLE;
        };
        if let Ok(session) = entry.session.lock()
            && let Some(session) = session.as_ref()
        {
            session.pause();
        }
        BM_OK
    }))
    .unwrap_or(BM_ERR_PANIC)
}

#[unsafe(no_mangle)]
pub extern "C" fn bm_session_seek(handle: u64, position_us: i64) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        let Some(entry) = lookup(handle) else {
            return BM_ERR_INVALID_HANDLE;
        };
        if let Ok(session) = entry.session.lock()
            && let Some(session) = session.as_ref()
        {
            session.seek(media_clock::MediaTime::from_micros(position_us.max(0)));
        }
        BM_OK
    }))
    .unwrap_or(BM_ERR_PANIC)
}

/// Report the managed audio sink's estimated output latency in µs: the
/// chain between `bm_session_read_audio` and the speaker (DSP buffers +
/// HAL headroom). The engine shifts the audio master clock back by it so
/// video presentation paces to the *audible* position. Send whenever the
/// estimate changes (it moves only with the DSP configuration). Clamped
/// engine-side to 0..=500000; 0 (the default) means uncompensated.
#[unsafe(no_mangle)]
pub extern "C" fn bm_session_set_audio_latency(handle: u64, latency_us: i64) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        let Some(entry) = lookup(handle) else {
            return BM_ERR_INVALID_HANDLE;
        };
        Session::set_audio_latency(&entry.pipeline, latency_us);
        BM_OK
    }))
    .unwrap_or(BM_ERR_PANIC)
}

/// Feed the owner's extrapolated position as a shared-playback soft
/// sync target, microseconds; negative clears it. The engine runs
/// dead band (150 ms) → 2% slew → seek only past 2 s. The slew reaches
/// the audio consumer as the snapshot's `sync_rate_ppm` (see its
/// contract comment); wall-master lanes are corrected engine-side. Live
/// sessions ignore targets (divergence is bounded by
/// `max_divergence_ms` in the descriptor, not chased). Call at any
/// cadence: the engine extrapolates the target at 1x between calls.
#[unsafe(no_mangle)]
pub extern "C" fn bm_session_set_sync_target(handle: u64, position_us: i64) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        let Some(entry) = lookup(handle) else {
            return BM_ERR_INVALID_HANDLE;
        };
        Session::set_sync_target(&entry.pipeline, position_us);
        BM_OK
    }))
    .unwrap_or(BM_ERR_PANIC)
}

/// Pull interleaved f32 audio for the Unity audio thread. Fills up to
/// `max_samples` floats (whole frames), zero-fills the remainder, and
/// returns the number of frames written. Silence while not playing. The
/// pull path takes no media-path lock.
///
/// # Safety
/// `out_ptr` must point to `max_samples` writable f32s.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bm_session_read_audio(
    handle: u64,
    out_ptr: *mut f32,
    max_samples: u32,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        if out_ptr.is_null() {
            return BM_ERR_INVALID_ARG;
        }
        // SAFETY: caller contract: out_ptr points to max_samples writable
        // f32s for the duration of this call.
        let out = unsafe { std::slice::from_raw_parts_mut(out_ptr, max_samples as usize) };
        let Some(entry) = lookup(handle) else {
            out.fill(0.0);
            return BM_ERR_INVALID_HANDLE;
        };
        Session::read_audio(&entry.pipeline, out) as i32
    }))
    .unwrap_or(BM_ERR_PANIC)
}

/// Drain pending diagnostics events into `out` (up to `cap`); returns the
/// count written, oldest first. Events beyond `cap` stay queued up to the
/// engine's own cap, so a caller whose buffer is smaller than the burst
/// calls again until a call comes back short.
///
/// # Safety
/// `out` must point to `cap` writable `BmEvent`s.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bm_session_drain_events(handle: u64, out: *mut BmEvent, cap: u32) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        if out.is_null() || cap == 0 {
            return BM_ERR_INVALID_ARG;
        }
        let Some(entry) = lookup(handle) else {
            return BM_ERR_INVALID_HANDLE;
        };
        let events = entry.pipeline.diag.take_events_up_to(cap as usize);
        let count = events.len();
        for (i, event) in events.into_iter().enumerate() {
            let mut detail = [0u8; BM_EVENT_DETAIL_CAP];
            let detail_len = write_detail(&event.detail, &mut detail);
            let record = BmEvent {
                wall_us: event.wall.as_micros(),
                code: event.code as u32,
                stage: event.stage as u32,
                detail_len,
                detail,
            };
            // SAFETY: caller contract: out points to cap writable
            // BmEvents; i < count <= cap.
            unsafe { out.add(i).write(record) };
        }
        count as i32
    }))
    .unwrap_or(BM_ERR_PANIC)
}

/// Drain pending process-log lines into `out` (up to `cap`), oldest
/// first; returns the count written. Lines beyond `cap` stay queued, so a
/// caller loops until a call comes back short.
///
/// Process-level: there is no handle, and it answers before the first
/// session opens and after the last one closes, which is where the lines
/// worth having mostly are. `out_dropped`, when non-NULL, receives the
/// cumulative count of lines the ring evicted to make room. Non-zero
/// means what follows has holes at its start, not at its end.
///
/// # Safety
/// `out` must point to `cap` writable `BmLogRecord`s; `out_dropped` must
/// be NULL or point to a writable `u64`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bm_drain_log(
    out: *mut BmLogRecord,
    cap: u32,
    out_dropped: *mut u64,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        host_log::install();
        if out.is_null() || cap == 0 {
            return BM_ERR_INVALID_ARG;
        }
        if !out_dropped.is_null() {
            // SAFETY: caller contract: non-NULL points to a writable u64.
            unsafe { out_dropped.write(media_diag::log_dropped()) };
        }
        let lines = media_diag::drain_log(cap as usize);
        let count = lines.len();
        for (i, line) in lines.into_iter().enumerate() {
            let mut detail = [0u8; BM_LOG_DETAIL_CAP];
            let detail_len = write_detail(&line.detail, &mut detail);
            let record = BmLogRecord {
                wall_us: line.wall_us,
                session: line.session,
                level: line.level as u32,
                code: line.code as u32,
                stage: line.stage as u32,
                detail_len,
                detail,
            };
            // SAFETY: caller contract: out points to cap writable
            // BmLogRecords; i < count <= cap.
            unsafe { out.add(i).write(record) };
        }
        count as i32
    }))
    .unwrap_or(BM_ERR_PANIC)
}

/// Drain pending caption cues into `out` (up to `cap`); returns the count
/// written. Cues not drained stay queued up to the engine's ring depth
/// (oldest dropped beyond it).
///
/// # Safety
/// `out` must point to `cap` writable `BmCaption`s.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bm_session_drain_captions(
    handle: u64,
    out: *mut BmCaption,
    cap: u32,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        if out.is_null() || cap == 0 {
            return BM_ERR_INVALID_ARG;
        }
        let Some(entry) = lookup(handle) else {
            return BM_ERR_INVALID_HANDLE;
        };
        let cues = Session::drain_captions(&entry.pipeline, cap as usize);
        for (i, cue) in cues.iter().enumerate() {
            let mut text = [0u8; BM_CAPTION_TEXT_CAP];
            let bytes = cue.text.as_bytes();
            let mut len = bytes.len().min(BM_CAPTION_TEXT_CAP);
            // Truncate on a UTF-8 boundary.
            while len > 0 && !cue.text.is_char_boundary(len) {
                len -= 1;
            }
            text[..len].copy_from_slice(&bytes[..len]);
            let record = BmCaption {
                pts_us: cue.pts_us,
                text_len: len as u32,
                text,
                reserved: 0,
            };
            // SAFETY: caller contract: out points to cap writable
            // BmCaptions; i < cues.len() <= cap.
            unsafe { out.add(i).write(record) };
        }
        cues.len() as i32
    }))
    .unwrap_or(BM_ERR_PANIC)
}

/// Drain pending SEI user-data messages: records into `out` (up to
/// `cap`), their bytes packed into `bytes` (up to `bytes_cap`), each
/// record's `offset`/`len` locating its payload. Returns the record
/// count. Takes only whole messages that fit; the rest stay queued up to
/// the engine's ring depth (oldest dropped beyond it). A message whose
/// payload alone exceeds `bytes_cap` can never be delivered through this
/// buffer and is dropped, so size `bytes` to at least the engine's
/// per-message ceiling (64 KiB) to see everything the stream carries.
///
/// # Safety
/// `out` must point to `cap` writable `BmUserData`s and `bytes` to
/// `bytes_cap` writable bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bm_session_drain_user_data(
    handle: u64,
    out: *mut BmUserData,
    cap: u32,
    bytes: *mut u8,
    bytes_cap: u32,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        if out.is_null() || cap == 0 || bytes.is_null() || bytes_cap == 0 {
            return BM_ERR_INVALID_ARG;
        }
        let Some(entry) = lookup(handle) else {
            return BM_ERR_INVALID_HANDLE;
        };
        let messages = Session::drain_user_data(&entry.pipeline, cap as usize, bytes_cap as usize);
        let mut offset = 0usize;
        for (i, m) in messages.iter().enumerate() {
            let record = BmUserData {
                pts_us: m.pts_us,
                uuid: m.uuid,
                offset: offset as u32,
                len: m.payload.len() as u32,
            };
            // SAFETY: caller contract: out points to cap writable
            // BmUserDatas and bytes to bytes_cap writable bytes; i <
            // messages.len() <= cap, and the drain bounded the payload
            // total to bytes_cap, so offset + len <= bytes_cap.
            unsafe {
                std::ptr::copy_nonoverlapping(
                    m.payload.as_ptr(),
                    bytes.add(offset),
                    m.payload.len(),
                );
                out.add(i).write(record);
            }
            offset += m.payload.len();
        }
        messages.len() as i32
    }))
    .unwrap_or(BM_ERR_PANIC)
}

/// ISO 639 codes are three bytes; a track name is free text, so it gets
/// a sane ceiling rather than an exact one.
pub const BM_TRACK_LANG_CAP: usize = 16;
pub const BM_TRACK_LABEL_CAP: usize = 64;

/// One selectable audio track. `language` is the container's ISO 639 code
/// and `label` its track name; either can be absent (a recording with a
/// microphone on its own track typically states neither), so a picker must
/// be able to tell rows apart by index alone. Lengths are byte counts into
/// the fixed buffers, which hold UTF-8 truncated on a character boundary.
#[repr(C)]
pub struct BmAudioTrack {
    pub track_id: u32,
    pub sample_rate: u32,
    pub channels: u32,
    pub language_len: u32,
    pub language: [u8; BM_TRACK_LANG_CAP],
    pub label_len: u32,
    pub label: [u8; BM_TRACK_LABEL_CAP],
}

/// How many audio tracks the source offers *instead of* the bound one.
/// Zero means there is no choice to present: either one track, or a
/// container that does not enumerate them. Stable for the session's life,
/// because switching track re-opens rather than switching in place.
///
/// # Safety
///
/// `handle` must be a live session handle, or 0.
#[unsafe(no_mangle)]
pub extern "C" fn bm_session_audio_track_count(handle: u64) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        let Some(entry) = lookup(handle) else {
            return BM_ERR_INVALID_HANDLE;
        };
        let Ok(session) = entry.session.lock() else {
            return BM_ERR_INVALID_HANDLE;
        };
        session
            .as_ref()
            .map_or(0, |s| s.audio_tracks().len() as i32)
    }))
    .unwrap_or(BM_ERR_PANIC)
}

/// Byte length of the container's cover art, or 0 where it carries none
/// (negative on error). Call before [`bm_session_get_artwork`] to size the
/// buffer.
#[unsafe(no_mangle)]
pub extern "C" fn bm_session_artwork_len(handle: u64) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        let Some(entry) = lookup(handle) else {
            return BM_ERR_INVALID_HANDLE;
        };
        let Ok(session) = entry.session.lock() else {
            return BM_ERR_INVALID_HANDLE;
        };
        session
            .as_ref()
            .and_then(|s| s.artwork())
            .map_or(0, |art| art.data.len().min(i32::MAX as usize) as i32)
    }))
    .unwrap_or(BM_ERR_PANIC)
}

/// Copy the cover art into `out` and its MIME type into `mime`, returning
/// the bytes written (0 = no art, negative = error). The bytes are the
/// container's own (JPEG or PNG as it stored them) and the caller
/// decodes them; nothing in the engine parses an image.
///
/// A buffer shorter than [`bm_session_artwork_len`] reports
/// `BM_ERR_INVALID_ARG` rather than a truncated picture.
///
/// # Safety
///
/// `out` must point to `cap` writable bytes and `mime` to `mime_cap`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bm_session_get_artwork(
    handle: u64,
    out: *mut u8,
    cap: u32,
    mime: *mut u8,
    mime_cap: u32,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        if out.is_null() || cap == 0 {
            return BM_ERR_INVALID_ARG;
        }
        let Some(entry) = lookup(handle) else {
            return BM_ERR_INVALID_HANDLE;
        };
        let Ok(session) = entry.session.lock() else {
            return BM_ERR_INVALID_HANDLE;
        };
        let Some(art) = session.as_ref().and_then(|s| s.artwork()) else {
            return 0;
        };
        if art.data.len() > cap as usize {
            return BM_ERR_INVALID_ARG;
        }
        // SAFETY: caller contract: out points to cap writable bytes and
        // the copy is bounded by the check above.
        unsafe { std::ptr::copy_nonoverlapping(art.data.as_ptr(), out, art.data.len()) };
        if !mime.is_null() && mime_cap > 0 {
            let bytes = art.mime.as_bytes();
            let n = bytes.len().min(mime_cap as usize - 1);
            // SAFETY: caller contract: mime points to mime_cap writable
            // bytes; n leaves room for the terminator.
            unsafe {
                std::ptr::copy_nonoverlapping(bytes.as_ptr(), mime, n);
                mime.add(n).write(0);
            }
        }
        art.data.len() as i32
    }))
    .unwrap_or(BM_ERR_PANIC)
}

/// Copy the offered audio tracks into `out`, writing at most `cap` of
/// them and returning how many were written (or a negative error).
///
/// # Safety
///
/// `out` must point to `cap` writable `BmAudioTrack`s.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bm_session_get_audio_tracks(
    handle: u64,
    out: *mut BmAudioTrack,
    cap: u32,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        if out.is_null() || cap == 0 {
            return BM_ERR_INVALID_ARG;
        }
        let Some(entry) = lookup(handle) else {
            return BM_ERR_INVALID_HANDLE;
        };
        let Ok(session) = entry.session.lock() else {
            return BM_ERR_INVALID_HANDLE;
        };
        let Some(tracks) = session.as_ref().map(|s| s.audio_tracks()) else {
            return 0;
        };
        let n = tracks.len().min(cap as usize);
        for (i, track) in tracks.iter().take(n).enumerate() {
            let mut language = [0u8; BM_TRACK_LANG_CAP];
            let language_len = copy_utf8(track.language.as_deref(), &mut language);
            let mut label = [0u8; BM_TRACK_LABEL_CAP];
            let label_len = copy_utf8(track.label.as_deref(), &mut label);
            let record = BmAudioTrack {
                track_id: track.id.0,
                sample_rate: track.sample_rate,
                channels: track.channels,
                language_len,
                language,
                label_len,
                label,
            };
            // SAFETY: caller contract: out points to cap writable
            // BmAudioTracks; i < n <= cap.
            unsafe { out.add(i).write(record) };
        }
        n as i32
    }))
    .unwrap_or(BM_ERR_PANIC)
}

/// Copy `text` into `buf` as UTF-8, truncated on a character boundary.
fn copy_utf8(text: Option<&str>, buf: &mut [u8]) -> u32 {
    let Some(text) = text else { return 0 };
    let bytes = text.as_bytes();
    let mut len = bytes.len().min(buf.len());
    while len > 0 && !text.is_char_boundary(len) {
        len -= 1;
    }
    buf[..len].copy_from_slice(&bytes[..len]);
    len as u32
}

/// Register the Unity-created output texture (from `GetNativeTexturePtr`)
/// the render event writes into. D3D11 and D3D12: a BGRA32 `Texture2D`
/// at the snapshot's display size. Vulkan on
/// Android: an RGBA32 `RenderTexture` with the default read/write (sRGB in
/// a Linear colour-space project), created at the snapshot's display size
/// (see `media_present::android` for the full contract).
///
/// # Safety
/// Calls for one session must not overlap: the Android render event
/// pairs the pointer with a counter this brackets, and the bracket is a
/// single-writer construction. Two registrations in flight at once can
/// leave the counter even across a pointer store and hand the renderer
/// the mismatched pair it exists to refuse. Unity registers from the
/// main thread, so this costs a caller nothing.
///
/// `texture` must be the live native texture owned by Unity's device
/// (`ID3D11Texture2D*` on D3D11, `ID3D12Resource*` on D3D12, `VkImage` on
/// Vulkan). The plugin builds its own objects over it (a shared-texture
/// consumer on D3D11, an image view on Vulkan) and cannot destroy them
/// until it can prove the GPU is done with them, which takes render
/// events. So on Vulkan the texture must stay alive for a few render
/// events past `bm_session_close`, not be released alongside it:
/// destroying the image while views over it still exist breaks Vulkan's
/// object-lifetime rules.
///
/// A texture this call *replaces* is under the same requirement, for a
/// second reason too: a render event already in flight may have taken
/// the previous pointer before this call and reach its own render after
/// it returns. The counter stops a stale view being used against a new
/// image; it does not extend any image's life. So the caller holds a
/// replaced texture for a few render events, exactly as it holds a
/// closed one.
///
/// Neither is enforced here: that would mean the plugin gating the
/// caller's own release on GPU completion, so it is a contract the
/// managed side keeps. The boundary provides the means. The objects are
/// destroyed by a render event, and a closed session has no render events
/// of its own, so the caller issues `BM_EVENT_COLLECT` for the frames it
/// holds the retired texture. Releasing without it destroys the image
/// under a live view however long the wait was.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bm_session_set_output_texture(handle: u64, texture: *mut c_void) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        let Some(entry) = lookup(handle) else {
            return BM_ERR_INVALID_HANDLE;
        };
        // The Android render event pairs the pointer with the counter,
        // so the registration brackets the store rather than trailing it:
        // odd while it is in flight, even and advanced once it lands. A
        // reader that overlaps any part of the write then sees an odd
        // counter or a changed one. A single bump afterwards is not
        // enough: release/acquire only orders the pointer *before* the
        // bump a reader observed, so a reader could take the new pointer
        // and read the same old counter either side of it.
        #[cfg(target_os = "android")]
        entry.texture_generation.fetch_add(1, Ordering::AcqRel);
        entry
            .unity_texture
            .store(texture as usize, Ordering::Release);
        #[cfg(target_os = "android")]
        entry.texture_generation.fetch_add(1, Ordering::Release);
        #[cfg(windows)]
        if let Ok(mut consumer) = entry.consumer.lock() {
            *consumer = ConsumerSlot::Unopened;
        }
        BM_OK
    }))
    .unwrap_or(BM_ERR_PANIC)
}

#[cfg(windows)]
unsafe extern "system" fn on_render_event(_event_id: i32, data: *mut c_void) {
    let _ = catch_unwind(AssertUnwindSafe(|| {
        let handle = data as usize as u64;
        let Some(entry) = lookup(handle) else { return };
        // Frame selection runs here, at display cadence: the
        // engine stamps consumer liveness, picks the due frame against
        // its mirrored clock with a vsync of lookahead, and converts into
        // the shared texture; the keyed-mutex copy below then lands it in
        // Unity's texture within the same event. On Direct3D 12 the copy
        // takes the newest frame already finished, which is an earlier
        // event's, and the engine selects a refresh further ahead to match.
        media_engine::render_present(&entry.pipeline);
        let Ok(mut slot) = entry.consumer.lock() else {
            return;
        };
        // Read under the slot's lock: a registration stores the texture
        // and then resets the slot under it, so a pointer read here is the
        // one the slot's state belongs to.
        let texture = entry.unity_texture.load(Ordering::Acquire);
        let shared_handle = entry.shared.shared_texture_handle.load(Ordering::Acquire);
        if texture == 0 || shared_handle == 0 {
            return;
        }
        if let Some(attempt) = slot.attempt_for(shared_handle) {
            // SAFETY: texture is the one the managed side registered and
            // contracts to keep alive, on Unity's renderer.
            let outcome = unsafe {
                Consumer::open(
                    texture as *mut c_void,
                    shared_handle,
                    &entry.pipeline.presenter,
                    &entry.pipeline.present.lookahead_events,
                )
            };
            // Log only the first failure and the last attempt; the ones
            // between would be a line per render event.
            if let Some(e) = slot.settle(shared_handle, attempt, outcome)
                && (attempt == 1 || attempt == MAX_CONSUMER_OPENS)
            {
                media_diag::diag_log!(
                    "consumer open failed (attempt {attempt}/{MAX_CONSUMER_OPENS}): {e}"
                );
            }
        }
        if let ConsumerSlot::Open(_, consumer) = &mut *slot
            && consumer.copy_if_fresh().unwrap_or(false)
        {
            entry
                .shared
                .frames_presented
                .fetch_add(1, Ordering::Relaxed);
        }
    }));
}

/// Headless platforms have no present target: the render event is a
/// no-op (the engine's tick-paced fallback consumes due frames), kept so
/// `bm_render_event_func` stays a total function on every platform.
#[cfg(not(any(windows, target_os = "android")))]
unsafe extern "system" fn on_render_event(_event_id: i32, _data: *mut c_void) {}

/// Returns the render-event callback for
/// `CommandBuffer.IssuePluginEventAndData`. The event id selects the pass:
/// `BM_EVENT_PRESENT` with `data` as the session handle, or (Android)
/// `BM_EVENT_COLLECT` with no data, which destroys retired Vulkan objects
/// and needs no session.
#[unsafe(no_mangle)]
pub extern "C" fn bm_render_event_func() -> *mut c_void {
    on_render_event as *mut c_void
}

/// A registered output texture and the registration it belongs to, read as
/// a pair.
///
/// The pointer and the generation are separate writes, so reading one of
/// each can straddle a registration and hand the render event a new
/// texture under the previous registration. The present layer's view
/// cache cannot survive that: it keys a cached image view on the
/// generation because Unity may destroy an image and give a later one the
/// same handle value, so a new image under an old generation can match a
/// view over the destroyed one.
///
/// A pair is stable only if the generation is even and reads the same
/// either side of the pointer. Otherwise the frame is skipped: the
/// registration is mid-flight and the next render event has a settled
/// pair.
///
/// The pointer arrives as a closure rather than a second atomic so a row
/// can drive the unstable case: the race itself is two adjacent stores
/// wide and cannot be scheduled on demand.
#[cfg(any(target_os = "android", test))]
fn stable_texture(generation: &AtomicU64, load: impl Fn() -> usize) -> Option<(usize, u64)> {
    let before = generation.load(Ordering::Acquire);
    // Odd is a registration caught between its two bumps: the pointer may
    // be the outgoing one or the incoming one and the counter cannot say
    // which, so there is no pair to take.
    if !before.is_multiple_of(2) {
        return None;
    }
    let texture = load();
    (generation.load(Ordering::Acquire) == before).then_some((texture, before))
}

/// Android render event: select the due frame at display cadence
/// (the engine grades due-ness against its mirrored clock with a
/// vsync of lookahead) and run the Vulkan conversion pass into the
/// registered Unity RenderTexture (see `media_present::android` for the
/// managed graphics contract).
#[cfg(target_os = "android")]
unsafe extern "system" fn on_render_event(event_id: i32, data: *mut c_void) {
    let _ = catch_unwind(AssertUnwindSafe(|| {
        if event_id == BM_EVENT_COLLECT {
            // Deliberately ahead of any handle lookup: the case this
            // exists for is the one where no session is left to find.
            media_present::android::drain_graveyard();
            return;
        }
        let handle = data as usize as u64;
        let Some(entry) = lookup(handle) else { return };
        let Some((texture, generation)) = stable_texture(&entry.texture_generation, || {
            entry.unity_texture.load(Ordering::Acquire)
        }) else {
            return;
        };
        let texture = texture as *mut c_void;
        let frame = media_engine::render_take(&entry.pipeline);
        let Ok(mut renderer) = entry.renderer.lock() else {
            return;
        };
        // SAFETY: `texture` is what the managed side registered through
        // bm_session_set_output_texture, whose contract requires the
        // texture to outlive the render events it is registered for.
        if unsafe { renderer.render(frame, texture, generation) } {
            entry
                .shared
                .frames_presented
                .fetch_add(1, Ordering::Relaxed);
        }
    }));
}

/// Unity plugin lifecycle: forwarded to the present layer, which
/// registers the Vulkan-init interception (the plugin must be preloaded
/// so this runs before graphics initialisation).
///
/// # Safety
/// Called by Unity with its live `IUnityInterfaces*`.
#[cfg(target_os = "android")]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn UnityPluginLoad(interfaces: *mut c_void) {
    let _ = catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: Unity passes its live interface table.
        unsafe { media_present::android::unity_plugin_load(interfaces) };
    }));
}

/// # Safety
/// Called by Unity at plugin unload; takes nothing.
#[cfg(target_os = "android")]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn UnityPluginUnload() {}

/// Unity plugin lifecycle on Windows: records Unity's Direct3D 12
/// interface when that is the renderer. Direct3D 11 needs nothing from it.
///
/// # Safety
/// Called by Unity with its live `IUnityInterfaces*`.
#[cfg(windows)]
#[unsafe(no_mangle)]
pub unsafe extern "system" fn UnityPluginLoad(interfaces: *mut c_void) {
    let _ = catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: Unity passes its live interface table.
        unsafe { media_present::win_unity::plugin_load(interfaces) };
    }));
}

/// # Safety
/// Called by Unity at plugin unload; takes nothing.
#[cfg(windows)]
#[unsafe(no_mangle)]
pub unsafe extern "system" fn UnityPluginUnload() {
    let _ = catch_unwind(media_present::win_unity::plugin_unload);
}

/// `System.loadLibrary` hands over the JavaVM here; the MediaCodec
/// capability probe uses it for the `MediaCodecList` ceilings query.
/// It is also the first moment the library runs on this platform, so the
/// host's diagnostic sink is installed from here, earlier than any entry
/// point a caller could reach.
///
/// # Safety
/// Called by the Android runtime with the process's `JavaVM*`.
#[cfg(target_os = "android")]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn JNI_OnLoad(vm: *mut c_void, _reserved: *mut c_void) -> i32 {
    let _ = catch_unwind(AssertUnwindSafe(|| {
        host_log::install();
        // SAFETY: `vm` is the runtime's own JavaVM*, which is exactly
        // what this entry point is called with.
        unsafe { decode_mediacodec::set_java_vm(vm) };
    }));
    // JNI_VERSION_1_6
    0x0001_0006
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A detail that was cut has to say so. The records are fixed-width
    /// and most engine detail fits, so a reader who meets a truncated one
    /// reads a sentence that stops mid-word and takes it for the whole of
    /// what the engine had to say.
    #[test]
    fn a_truncated_detail_says_so() {
        let mut out = [0u8; 16];
        let len = write_detail("short", &mut out) as usize;
        assert_eq!(&out[..len], b"short", "a detail that fits is untouched");

        let mut out = [0u8; 16];
        let len = write_detail("0123456789abcdefghij", &mut out) as usize;
        assert!(len <= out.len());
        assert_eq!(
            std::str::from_utf8(&out[..len]).expect("valid UTF-8"),
            "0123456789abc…"
        );

        // The mark takes its room from the end first, so the step back to
        // a boundary has to happen after that rather than before it.
        let source = "0123456789abé cdef";
        let mut out = [0u8; 16];
        let len = write_detail(source, &mut out) as usize;
        let text = std::str::from_utf8(&out[..len]).expect("valid UTF-8");
        assert!(text.ends_with('…'), "{text:?} does not say it was cut");
        assert!(source.starts_with(text.trim_end_matches('…')));
    }

    /// The refused-event total is the one figure that says the drained
    /// sequence has holes in it. Wrapping it would report a smaller loss
    /// than happened, and a small loss is exactly what a reader discounts.
    #[test]
    fn a_counter_past_the_snapshot_field_saturates() {
        assert_eq!(saturate_u32(0), 0);
        assert_eq!(saturate_u32(7), 7);
        assert_eq!(saturate_u32(u32::MAX as u64), u32::MAX);
        assert_eq!(saturate_u32(u32::MAX as u64 + 1), u32::MAX);
        assert_eq!(saturate_u32(u64::MAX), u32::MAX);
    }

    /// A failed consumer open is retried for the same handle, but not
    /// forever. Without retries, a session whose open lost a race against
    /// a presenter rebuild would never present again: only a fresh handle
    /// clears the slot, and one is published only when the presenter is
    /// rebuilt.
    #[cfg(windows)]
    #[test]
    fn a_failed_consumer_open_is_retried_a_bounded_number_of_times() {
        const HANDLE: u64 = 0x1234;

        assert_eq!(
            ConsumerSlot::Unopened.attempt_for(HANDLE),
            Some(1),
            "an unopened slot opens"
        );

        // Each failure is the next attempt, up to the bound.
        let mut slot = ConsumerSlot::Unopened;
        let mut attempts = Vec::new();
        while let Some(attempt) = slot.attempt_for(HANDLE) {
            attempts.push(attempt);
            slot = ConsumerSlot::Failed(HANDLE, attempt);
        }
        assert_eq!(
            attempts,
            (1..=MAX_CONSUMER_OPENS).collect::<Vec<_>>(),
            "every attempt up to the bound is made, and then no more"
        );

        // A different handle is a different question, however spent the
        // slot is for the old one.
        assert_eq!(
            ConsumerSlot::Failed(HANDLE, MAX_CONSUMER_OPENS).attempt_for(HANDLE + 1),
            Some(1),
            "a new handle starts over"
        );
    }

    /// A consumer that cannot be opened yet (the presenter's lock busy, or
    /// a rebuild half done) is asked again on the next event without
    /// spending an attempt. Were it counted, a run of busy events would
    /// exhaust the bound and the session would never present.
    #[cfg(windows)]
    #[test]
    fn an_open_that_is_not_ready_spends_no_attempt() {
        const HANDLE: u64 = 0x1234;

        let mut slot = ConsumerSlot::Unopened;
        for _ in 0..MAX_CONSUMER_OPENS * 4 {
            let attempt = slot.attempt_for(HANDLE).expect("still open to attempts");
            assert_eq!(attempt, 1, "a not-ready event spent an attempt");
            assert!(slot.settle(HANDLE, attempt, Ok(Opened::NotReady)).is_none());
        }

        let attempt = slot.attempt_for(HANDLE).expect("an attempt");
        let failed = slot.settle(HANDLE, attempt, Err(PresentError("refused".into())));
        assert!(failed.is_some(), "a failed open reports its error");
        assert_eq!(
            slot.attempt_for(HANDLE),
            Some(2),
            "a failed open spends its attempt"
        );
    }

    /// The render event reads the shared handle apart from the presenter
    /// that owns it, and a presenter rebuild publishes the new handle,
    /// then drops the old presenter, closing the old one. The consumer
    /// opens only while the presenter owning the handle is held; anything
    /// else waits for a later event, spending no attempt.
    #[cfg(windows)]
    #[test]
    fn a_consumer_opens_only_while_the_owning_presenter_is_held() {
        use media_present::{SharedTexturePresenter, TestConsumerTarget};

        let (w, h) = (64, 32);
        let installed = SharedTexturePresenter::new(w, h).expect("presenter");
        let replaced = SharedTexturePresenter::new(w, h).expect("presenter");
        let (installed_handle, replaced_handle) =
            (installed.shared_handle(), replaced.shared_handle());
        let target = TestConsumerTarget::new(w, h).expect("target");
        let lookahead = AtomicI64::new(1);
        // SAFETY: the target texture is live for the whole test.
        let open = |slot: &Mutex<Option<SharedTexturePresenter>>, handle| unsafe {
            Consumer::open(target.texture_ptr(), handle, slot, &lookahead)
        };

        let empty = Mutex::new(None);
        assert!(matches!(
            open(&empty, installed_handle),
            Ok(Opened::NotReady)
        ));

        let slot = Mutex::new(Some(installed));
        // `replaced` is still open, so an unchecked open would succeed.
        assert!(
            matches!(open(&slot, replaced_handle), Ok(Opened::NotReady)),
            "opened a handle the installed presenter does not own"
        );
        {
            let _held = slot.lock().expect("presenter lock");
            assert!(matches!(
                open(&slot, installed_handle),
                Ok(Opened::NotReady)
            ));
        }
        assert!(
            matches!(
                open(&slot, installed_handle),
                Ok(Opened::Open(Consumer::D3d11(_)))
            ),
            "the installed presenter's handle did not open"
        );
        drop(replaced);
    }

    /// The pair the render event acts on has to come from one registration.
    /// A generation that moves while the pointer is being read means the
    /// managed side is mid-registration, and the pointer read either side
    /// of that belongs to a different texture than the generation does.
    #[test]
    fn a_texture_read_across_a_registration_is_refused() {
        // Registrations bracket their store, so a settled counter is even
        // and a completed one has advanced by two.
        let generation = AtomicU64::new(8);

        let settled = stable_texture(&generation, || 0xDEAD_BEEF);
        assert_eq!(
            settled,
            Some((0xDEAD_BEEF, 8)),
            "a quiet registration reads as itself"
        );

        // A whole registration lands between the two generation reads.
        let straddled = stable_texture(&generation, || {
            generation.fetch_add(2, Ordering::Release);
            0x1234_5678
        });
        assert_eq!(straddled, None, "a moving registration yields no pair");

        // And the one after it is stable again rather than stuck.
        assert_eq!(
            stable_texture(&generation, || 0x1234_5678),
            Some((0x1234_5678, 10)),
            "the settled registration reads normally"
        );
    }

    /// The pointer store has landed and the counter has not finished
    /// moving. Reading the counter either side of the pointer sees no
    /// change and would pair a new texture with the previous registration;
    /// bracketing the write makes that state odd, so it is refused.
    #[test]
    fn a_registration_in_flight_is_refused() {
        // The first of the two bumps has landed, the second has not.
        let generation = AtomicU64::new(9);
        assert_eq!(
            stable_texture(&generation, || 0x1234_5678),
            None,
            "a counter mid-registration yields no pair"
        );

        // The second bump completes it and the pair reads normally.
        generation.fetch_add(1, Ordering::Release);
        assert_eq!(
            stable_texture(&generation, || 0x1234_5678),
            Some((0x1234_5678, 10)),
            "the completed registration reads normally"
        );
    }
}
