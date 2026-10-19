//! Direct3D 12 handoff into Unity: the D3D11 conversion pass is
//! unchanged, and its output is copied into one of a few shared slots and
//! published on a shared fence. The render event copies the newest slot
//! whose fence value has completed into Unity's texture, recorded on
//! Unity's own command list, and marks that slot busy until Unity's frame
//! fence passes the frame the copy rode in.
//!
//! A D3D12 command list cannot wait on a fence, so neither direction waits:
//! the render thread reads two completed values and copies what is already
//! finished. A slot converted in one render event is therefore copied in a
//! later one, which the engine allows for by choosing frames one render
//! event further ahead on this path.

use std::ffi::c_void;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};

use windows::Win32::Foundation::{CloseHandle, GENERIC_ALL, HANDLE};
use windows::Win32::Graphics::Direct3D::D3D_FEATURE_LEVEL_11_0;
use windows::Win32::Graphics::Direct3D11::{
    D3D11_BIND_RENDER_TARGET, D3D11_BIND_SHADER_RESOURCE, D3D11_FENCE_FLAG_SHARED,
    D3D11_RESOURCE_MISC_SHARED, D3D11_RESOURCE_MISC_SHARED_NTHANDLE, D3D11_TEXTURE2D_DESC,
    D3D11_USAGE_DEFAULT, ID3D11Device, ID3D11Device5, ID3D11DeviceContext, ID3D11DeviceContext4,
    ID3D11Fence, ID3D11Texture2D,
};
use windows::Win32::Graphics::Direct3D12::{
    D3D12_COMMAND_LIST_TYPE_DIRECT, D3D12_COMMAND_QUEUE_DESC, D3D12_CPU_PAGE_PROPERTY_UNKNOWN,
    D3D12_FENCE_FLAG_NONE, D3D12_HEAP_FLAG_NONE, D3D12_HEAP_PROPERTIES, D3D12_HEAP_TYPE_DEFAULT,
    D3D12_HEAP_TYPE_READBACK, D3D12_MEMORY_POOL_UNKNOWN, D3D12_PLACED_SUBRESOURCE_FOOTPRINT,
    D3D12_RESOURCE_BARRIER, D3D12_RESOURCE_BARRIER_0, D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES,
    D3D12_RESOURCE_BARRIER_FLAG_NONE, D3D12_RESOURCE_BARRIER_TYPE_TRANSITION, D3D12_RESOURCE_DESC,
    D3D12_RESOURCE_DIMENSION_BUFFER, D3D12_RESOURCE_DIMENSION_TEXTURE2D, D3D12_RESOURCE_FLAG_NONE,
    D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_COPY_SOURCE,
    D3D12_RESOURCE_STATES, D3D12_RESOURCE_TRANSITION_BARRIER, D3D12_TEXTURE_COPY_LOCATION,
    D3D12_TEXTURE_COPY_LOCATION_0, D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT,
    D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX, D3D12_TEXTURE_LAYOUT_ROW_MAJOR,
    D3D12_TEXTURE_LAYOUT_UNKNOWN, D3D12CreateDevice, ID3D12CommandAllocator, ID3D12CommandQueue,
    ID3D12Device, ID3D12Fence, ID3D12GraphicsCommandList, ID3D12Resource,
};
use windows::Win32::Graphics::Dxgi::Common::{
    DXGI_FORMAT, DXGI_FORMAT_B8G8R8A8_TYPELESS, DXGI_FORMAT_B8G8R8A8_UNORM,
    DXGI_FORMAT_B8G8R8A8_UNORM_SRGB, DXGI_FORMAT_UNKNOWN, DXGI_SAMPLE_DESC,
};
use windows::Win32::Graphics::Dxgi::IDXGIResource1;
use windows::Win32::Graphics::Dxgi::{DXGI_SHARED_RESOURCE_READ, DXGI_SHARED_RESOURCE_WRITE};
use windows::Win32::System::Threading::{CreateEventW, INFINITE, WaitForSingleObject};
use windows::core::{IUnknown, Interface};

use crate::PresentError;

fn d3d<T>(r: windows::core::Result<T>, what: &str) -> Result<T, PresentError> {
    r.map_err(|e| PresentError(format!("{what}: {e}")))
}

/// Shared slots per presenter. One is being copied by Unity's GPU, one
/// holds the newest converted frame, and the third lets the next convert
/// go ahead while Unity's frame fence catches up.
pub const SLOTS: usize = 3;

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
enum Slot {
    Free,
    /// Claimed by the producer for the convert in progress.
    Writing,
    /// Converted; readable once the shared fence reaches this value.
    Written(u64),
    /// A copy was recorded on Unity's command list; free once Unity's
    /// frame fence reaches this value.
    Copied(u64),
}

/// What the producer and the consumer share: the slots' state and the NT
/// handles the consumer opens. The handles live as long as either side
/// holds this, so a consumer opening after a presenter rebuild cannot meet
/// a closed handle.
pub struct Handoff {
    slot_handles: [HANDLE; SLOTS],
    fence_handle: HANDLE,
    slots: Mutex<[Slot; SLOTS]>,
}

// SAFETY: the handles are process-wide kernel handles, only read after
// construction and closed once in Drop; the slot state is behind a mutex.
unsafe impl Send for Handoff {}
// SAFETY: as above.
unsafe impl Sync for Handoff {}

impl Drop for Handoff {
    fn drop(&mut self) {
        for handle in self.slot_handles.iter().chain([&self.fence_handle]) {
            if !handle.is_invalid() {
                // SAFETY: each handle came from `CreateSharedHandle` and is
                // closed exactly once, here.
                unsafe {
                    let _ = CloseHandle(*handle);
                }
            }
        }
    }
}

/// Producer half, owned by the presenter on the decode device.
pub(crate) struct Producer {
    context: ID3D11DeviceContext4,
    textures: [ID3D11Texture2D; SLOTS],
    fence: ID3D11Fence,
    value: u64,
    handoff: Arc<Handoff>,
}

// SAFETY: driven by one thread at a time under the presenter's lock, as
// the presenter itself is.
unsafe impl Send for Producer {}

impl Producer {
    pub(crate) fn new(
        device: &ID3D11Device,
        context: &ID3D11DeviceContext,
        width: u32,
        height: u32,
    ) -> Result<Self, PresentError> {
        // SAFETY: D3D11 object creation through owned wrappers with checked
        // out-parameters; every handle created is owned by `Handoff` the
        // moment it exists, so an early return closes what was made.
        unsafe {
            let device5: ID3D11Device5 = d3d(device.cast(), "cast ID3D11Device5")?;
            let context4: ID3D11DeviceContext4 = d3d(context.cast(), "cast ID3D11DeviceContext4")?;
            let mut handoff = Handoff {
                slot_handles: [HANDLE::default(); SLOTS],
                fence_handle: HANDLE::default(),
                slots: Mutex::new([Slot::Free; SLOTS]),
            };
            let desc = D3D11_TEXTURE2D_DESC {
                Width: width,
                Height: height,
                MipLevels: 1,
                ArraySize: 1,
                Format: DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDesc: DXGI_SAMPLE_DESC {
                    Count: 1,
                    Quality: 0,
                },
                Usage: D3D11_USAGE_DEFAULT,
                BindFlags: (D3D11_BIND_SHADER_RESOURCE.0 | D3D11_BIND_RENDER_TARGET.0) as u32,
                CPUAccessFlags: 0,
                MiscFlags: (D3D11_RESOURCE_MISC_SHARED.0 | D3D11_RESOURCE_MISC_SHARED_NTHANDLE.0)
                    as u32,
            };
            let mut textures = Vec::with_capacity(SLOTS);
            for i in 0..SLOTS {
                let mut texture = None;
                d3d(
                    device.CreateTexture2D(&desc, None, Some(&mut texture)),
                    "CreateTexture2D (D3D12 slot)",
                )?;
                let texture = texture.ok_or_else(|| PresentError("no slot texture".into()))?;
                let resource: IDXGIResource1 = d3d(texture.cast(), "cast IDXGIResource1 (slot)")?;
                handoff.slot_handles[i] = d3d(
                    resource.CreateSharedHandle(
                        None,
                        DXGI_SHARED_RESOURCE_READ.0 | DXGI_SHARED_RESOURCE_WRITE.0,
                        windows::core::PCWSTR::null(),
                    ),
                    "CreateSharedHandle (slot)",
                )?;
                textures.push(texture);
            }
            let mut fence: Option<ID3D11Fence> = None;
            d3d(
                device5.CreateFence(0, D3D11_FENCE_FLAG_SHARED, &mut fence),
                "CreateFence (shared)",
            )?;
            let fence = fence.ok_or_else(|| PresentError("no shared fence".into()))?;
            handoff.fence_handle = d3d(
                fence.CreateSharedHandle(None, GENERIC_ALL.0, windows::core::PCWSTR::null()),
                "CreateSharedHandle (fence)",
            )?;
            let textures: [ID3D11Texture2D; SLOTS] = textures
                .try_into()
                .map_err(|_| PresentError("slot count".into()))?;
            Ok(Self {
                context: context4,
                textures,
                fence,
                value: 0,
                handoff: Arc::new(handoff),
            })
        }
    }

    pub(crate) fn handoff(&self) -> Arc<Handoff> {
        Arc::clone(&self.handoff)
    }

    /// Claim a slot to convert into: a free one, else the oldest frame
    /// nothing has copied. `None` while every slot is being written or
    /// read, or the state is contended; the frame is then dropped, as a
    /// timed-out keyed mutex drops it on the D3D11 path.
    pub(crate) fn claim(&self) -> Option<usize> {
        let mut slots = self.handoff.slots.try_lock().ok()?;
        let index = slots.iter().position(|s| *s == Slot::Free).or_else(|| {
            slots
                .iter()
                .enumerate()
                .filter_map(|(i, s)| match s {
                    Slot::Written(v) => Some((i, *v)),
                    _ => None,
                })
                .min_by_key(|&(_, v)| v)
                .map(|(i, _)| i)
        })?;
        slots[index] = Slot::Writing;
        Some(index)
    }

    /// Hand a claimed slot back unused.
    pub(crate) fn unclaim(&self, index: usize) {
        if let Ok(mut slots) = self.handoff.slots.lock() {
            slots[index] = Slot::Free;
        }
    }

    /// Block until every published frame has finished on the decode device.
    /// Test use: the render path never waits.
    pub(crate) fn wait_published(&self) -> Result<(), PresentError> {
        // SAFETY: event handle created and closed here; the fence is live.
        unsafe {
            if self.fence.GetCompletedValue() >= self.value {
                return Ok(());
            }
            let event = d3d(CreateEventW(None, false, false, None), "CreateEventW")?;
            let armed = self.fence.SetEventOnCompletion(self.value, event);
            if armed.is_ok() {
                WaitForSingleObject(event, INFINITE);
            }
            let _ = CloseHandle(event);
            d3d(armed, "SetEventOnCompletion (shared)")
        }
    }

    /// Queue the copy of the converted frame into the claimed slot and the
    /// fence signal that publishes it. The caller flushes.
    pub(crate) fn publish(
        &mut self,
        index: usize,
        source: &ID3D11Texture2D,
    ) -> Result<(), PresentError> {
        self.value += 1;
        // SAFETY: both textures are live on this context's device, same
        // size and format; the fence belongs to the same device.
        unsafe {
            self.context.CopyResource(&self.textures[index], source);
            d3d(self.context.Signal(&self.fence, self.value), "Signal")?;
        }
        if let Ok(mut slots) = self.handoff.slots.lock() {
            slots[index] = Slot::Written(self.value);
        }
        Ok(())
    }
}

/// What the consumer needs from the host renderer. Unity's
/// `IUnityGraphicsD3D12v8` in the player; a plain device and queue in the
/// headless rows.
pub trait D3d12Host: Send + Sync {
    fn device(&self) -> Option<ID3D12Device>;
    /// The command list the host is recording now; valid only for the
    /// current render event.
    fn command_list(&self) -> Option<ID3D12GraphicsCommandList>;
    /// Put `resource` in `state` on the current command list, with any
    /// barrier that takes.
    fn request_state(&self, resource: &ID3D12Resource, state: D3D12_RESOURCE_STATES);
    fn frame_fence(&self) -> Option<ID3D12Fence>;
    /// The value the frame fence takes once the frame now being recorded
    /// has completed on the GPU. `None` where the host cannot say, and then
    /// nothing is copied: a slot has to know when it is free again.
    fn next_frame_fence_value(&self) -> Option<u64>;
}

/// Render-thread consumer on the host's D3D12 device.
pub struct D3d12Consumer {
    host: Arc<dyn D3d12Host>,
    handoff: Arc<Handoff>,
    destination: ID3D12Resource,
    sources: [ID3D12Resource; SLOTS],
    fence: ID3D12Fence,
    frame_fence: ID3D12Fence,
    /// Frame-fence value the newest recorded copy completes at.
    pending: u64,
}

// SAFETY: only ever touched from the render thread (or a headless row's
// one thread); the wrapped D3D12 interfaces are free-threaded.
unsafe impl Send for D3d12Consumer {}

pub(crate) fn copy_compatible(format: DXGI_FORMAT) -> bool {
    [
        DXGI_FORMAT_B8G8R8A8_UNORM,
        DXGI_FORMAT_B8G8R8A8_UNORM_SRGB,
        DXGI_FORMAT_B8G8R8A8_TYPELESS,
    ]
    .contains(&format)
}

impl D3d12Consumer {
    /// # Safety
    /// `destination_texture` must be a live `ID3D12Resource*` on the host's
    /// device.
    pub unsafe fn open(
        destination_texture: *mut c_void,
        handoff: Arc<Handoff>,
        host: Arc<dyn D3d12Host>,
    ) -> Result<Self, PresentError> {
        // SAFETY: caller guarantees a live ID3D12Resource*; from_raw_borrowed
        // does not take over the caller's reference and the clone AddRefs.
        unsafe {
            let destination = ID3D12Resource::from_raw_borrowed(&destination_texture)
                .ok_or_else(|| PresentError("null destination texture".into()))?
                .clone();
            let device = host
                .device()
                .ok_or_else(|| PresentError("no D3D12 device".into()))?;
            let frame_fence = host
                .frame_fence()
                .ok_or_else(|| PresentError("no frame fence".into()))?;
            let mut sources = Vec::with_capacity(SLOTS);
            for handle in handoff.slot_handles {
                let mut resource: Option<ID3D12Resource> = None;
                d3d(
                    device.OpenSharedHandle(handle, &mut resource),
                    "OpenSharedHandle (slot)",
                )?;
                sources.push(resource.ok_or_else(|| PresentError("no slot resource".into()))?);
            }
            let mut fence: Option<ID3D12Fence> = None;
            d3d(
                device.OpenSharedHandle(handoff.fence_handle, &mut fence),
                "OpenSharedHandle (fence)",
            )?;
            let fence = fence.ok_or_else(|| PresentError("no shared fence".into()))?;

            // A D3D12 copy between mismatched resources is undefined rather
            // than ignored, so it is refused here instead of recorded.
            let want = destination.GetDesc();
            let have = sources[0].GetDesc();
            if want.Dimension != D3D12_RESOURCE_DIMENSION_TEXTURE2D
                || want.Width != have.Width
                || want.Height != have.Height
                || want.MipLevels != 1
                || want.DepthOrArraySize != 1
                || want.SampleDesc.Count != 1
                || !copy_compatible(want.Format)
            {
                return Err(PresentError(format!(
                    "destination {}x{} {:?} ({} mips) cannot take a {}x{} BGRA copy",
                    want.Width, want.Height, want.Format, want.MipLevels, have.Width, have.Height
                )));
            }
            let sources: [ID3D12Resource; SLOTS] = sources
                .try_into()
                .map_err(|_| PresentError("slot count".into()))?;
            Ok(Self {
                host,
                handoff,
                destination,
                sources,
                fence,
                frame_fence,
                pending: 0,
            })
        }
    }

    /// Record a copy of the newest finished frame onto the host's command
    /// list, if one has arrived since the last copy. Never waits.
    pub fn copy_if_fresh(&mut self) -> Result<bool, PresentError> {
        collect_retired();
        // SAFETY: reading completed values of live fences.
        let (unity_done, written_done) = unsafe {
            (
                self.frame_fence.GetCompletedValue(),
                self.fence.GetCompletedValue(),
            )
        };
        let Ok(mut slots) = self.handoff.slots.try_lock() else {
            return Ok(false);
        };
        for slot in slots.iter_mut() {
            if let Slot::Copied(v) = *slot
                && v <= unity_done
            {
                *slot = Slot::Free;
            }
        }
        let newest = slots
            .iter()
            .enumerate()
            .filter_map(|(i, s)| match s {
                Slot::Written(v) if *v <= written_done => Some((i, *v)),
                _ => None,
            })
            .max_by_key(|&(_, v)| v);
        let Some((index, value)) = newest else {
            return Ok(false);
        };
        let Some(list) = self.host.command_list() else {
            return Ok(false);
        };
        let Some(frame) = self.host.next_frame_fence_value() else {
            return Ok(false);
        };
        // Frames older than the one copied will never be shown, and
        // nothing has read them.
        for slot in slots.iter_mut() {
            if let Slot::Written(v) = *slot
                && v < value
            {
                *slot = Slot::Free;
            }
        }
        slots[index] = Slot::Copied(frame);
        drop(slots);

        self.host
            .request_state(&self.destination, D3D12_RESOURCE_STATE_COPY_DEST);
        // SAFETY: both resources are live on the host's device and were
        // checked compatible at open. The shared source is in COMMON and
        // promotes to COPY_SOURCE implicitly.
        unsafe { list.CopyResource(&self.destination, &self.sources[index]) };
        self.pending = frame;
        Ok(true)
    }
}

impl Drop for D3d12Consumer {
    fn drop(&mut self) {
        // A copy may still be queued on the host's GPU, reading the
        // sources and writing the destination: those objects are held
        // until the frame fence passes it.
        // SAFETY: reading a live fence's completed value.
        if unsafe { self.frame_fence.GetCompletedValue() } >= self.pending {
            return;
        }
        let mut held: Vec<IUnknown> = self
            .sources
            .iter()
            .map(|r| r.cast::<IUnknown>())
            .filter_map(Result::ok)
            .collect();
        if let Ok(d) = self.destination.cast::<IUnknown>() {
            held.push(d);
        }
        if let Ok(mut graveyard) = GRAVEYARD.lock() {
            graveyard.push(Retired {
                fence: self.frame_fence.clone(),
                value: self.pending,
                _held: held,
            });
        }
    }
}

struct Retired {
    fence: ID3D12Fence,
    value: u64,
    _held: Vec<IUnknown>,
}

// SAFETY: COM references held only to be released; D3D12 objects are
// free-threaded.
unsafe impl Send for Retired {}

static GRAVEYARD: Mutex<Vec<Retired>> = Mutex::new(Vec::new());

/// Release retired consumer objects whose last copy has completed. Runs
/// from every D3D12 copy, so a closed session's objects go at the next
/// render event of any session.
pub fn collect_retired() {
    let Ok(mut graveyard) = GRAVEYARD.try_lock() else {
        return;
    };
    // SAFETY: reading completed values of live fences.
    graveyard.retain(|r| unsafe { r.fence.GetCompletedValue() } < r.value);
}

/// Release every retired object: the host device is going away.
pub fn release_retired() {
    if let Ok(mut graveyard) = GRAVEYARD.lock() {
        graveyard.clear();
    }
}

/// How many retired consumers are waiting on the host's GPU. Test use.
pub fn retired_count() -> usize {
    GRAVEYARD.lock().map(|g| g.len()).unwrap_or(0)
}

fn heap(kind: windows::Win32::Graphics::Direct3D12::D3D12_HEAP_TYPE) -> D3D12_HEAP_PROPERTIES {
    D3D12_HEAP_PROPERTIES {
        Type: kind,
        CPUPageProperty: D3D12_CPU_PAGE_PROPERTY_UNKNOWN,
        MemoryPoolPreference: D3D12_MEMORY_POOL_UNKNOWN,
        CreationNodeMask: 1,
        VisibleNodeMask: 1,
    }
}

/// Headless stand-in for Unity's D3D12 renderer: a device, a queue, one
/// command list per "frame", a frame fence, and a BGRA texture to copy
/// into. Test/smoke use only.
pub struct TestD3d12Host {
    device: ID3D12Device,
    queue: ID3D12CommandQueue,
    allocator: ID3D12CommandAllocator,
    list: ID3D12GraphicsCommandList,
    frame_fence: ID3D12Fence,
    frame: Mutex<u64>,
    /// Answer `None` for the next frame-fence value, as a host whose
    /// interface lacks that function would.
    frame_value_missing: AtomicBool,
    /// Destination state as this host tracks it, as Unity would.
    state: Mutex<D3D12_RESOURCE_STATES>,
}

// SAFETY: test host driven from one thread; D3D12 objects are
// free-threaded and the mutable state is behind mutexes.
unsafe impl Send for TestD3d12Host {}
// SAFETY: as above.
unsafe impl Sync for TestD3d12Host {}

impl TestD3d12Host {
    pub fn new() -> Result<Arc<Self>, PresentError> {
        // SAFETY: D3D12 object creation through owned wrappers with checked
        // out-parameters.
        unsafe {
            let mut device: Option<ID3D12Device> = None;
            d3d(
                D3D12CreateDevice(None, D3D_FEATURE_LEVEL_11_0, &mut device),
                "D3D12CreateDevice",
            )?;
            let device = device.ok_or_else(|| PresentError("no D3D12 device".into()))?;
            let queue: ID3D12CommandQueue = d3d(
                device.CreateCommandQueue(&D3D12_COMMAND_QUEUE_DESC {
                    Type: D3D12_COMMAND_LIST_TYPE_DIRECT,
                    ..Default::default()
                }),
                "CreateCommandQueue",
            )?;
            let allocator: ID3D12CommandAllocator = d3d(
                device.CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT),
                "CreateCommandAllocator",
            )?;
            let list: ID3D12GraphicsCommandList = d3d(
                device.CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, &allocator, None),
                "CreateCommandList",
            )?;
            let frame_fence: ID3D12Fence = d3d(
                device.CreateFence(0, D3D12_FENCE_FLAG_NONE),
                "CreateFence (frame)",
            )?;
            Ok(Arc::new(Self {
                device,
                queue,
                allocator,
                list,
                frame_fence,
                frame: Mutex::new(0),
                frame_value_missing: AtomicBool::new(false),
                state: Mutex::new(D3D12_RESOURCE_STATE_COMMON),
            }))
        }
    }

    /// A BGRA texture of the kind Unity creates for the player's output.
    pub fn create_destination(
        &self,
        width: u32,
        height: u32,
        format: DXGI_FORMAT,
    ) -> Result<ID3D12Resource, PresentError> {
        let desc = D3D12_RESOURCE_DESC {
            Dimension: D3D12_RESOURCE_DIMENSION_TEXTURE2D,
            Alignment: 0,
            Width: width as u64,
            Height: height,
            DepthOrArraySize: 1,
            MipLevels: 1,
            Format: format,
            SampleDesc: DXGI_SAMPLE_DESC {
                Count: 1,
                Quality: 0,
            },
            Layout: D3D12_TEXTURE_LAYOUT_UNKNOWN,
            Flags: D3D12_RESOURCE_FLAG_NONE,
        };
        let mut texture: Option<ID3D12Resource> = None;
        // SAFETY: committed-resource creation with plain descriptors.
        unsafe {
            d3d(
                self.device.CreateCommittedResource(
                    &heap(D3D12_HEAP_TYPE_DEFAULT),
                    D3D12_HEAP_FLAG_NONE,
                    &desc,
                    D3D12_RESOURCE_STATE_COMMON,
                    None,
                    &mut texture,
                ),
                "CreateCommittedResource (destination)",
            )?;
        }
        texture.ok_or_else(|| PresentError("no destination".into()))
    }

    /// Make the host unable to report its next frame-fence value.
    pub fn set_frame_value_missing(&self, missing: bool) {
        self.frame_value_missing.store(missing, Ordering::Relaxed);
    }

    /// Close the frame's command list, submit it, signal the frame fence
    /// and start the next list. With `wait`, block until the GPU is done.
    pub fn end_frame(&self, wait: bool) -> Result<(), PresentError> {
        let mut frame = self.frame.lock().expect("frame");
        *frame += 1;
        // SAFETY: the list is closed before submission and reset only once
        // the GPU has finished with the allocator (the wait below, or the
        // caller's promise to wait before the allocator is reused).
        unsafe {
            d3d(self.list.Close(), "Close")?;
            let list: windows::Win32::Graphics::Direct3D12::ID3D12CommandList =
                d3d(self.list.cast(), "cast ID3D12CommandList")?;
            self.queue.ExecuteCommandLists(&[Some(list)]);
            d3d(
                self.queue.Signal(&self.frame_fence, *frame),
                "Signal (frame)",
            )?;
            if wait {
                self.wait_for(*frame)?;
                d3d(self.allocator.Reset(), "Reset allocator")?;
            }
            d3d(self.list.Reset(&self.allocator, None), "Reset list")?;
        }
        Ok(())
    }

    fn wait_for(&self, value: u64) -> Result<(), PresentError> {
        // SAFETY: event handle created and closed here; the fence is live.
        unsafe {
            if self.frame_fence.GetCompletedValue() >= value {
                return Ok(());
            }
            let event = d3d(CreateEventW(None, false, false, None), "CreateEventW")?;
            let armed = self.frame_fence.SetEventOnCompletion(value, event);
            if armed.is_ok() {
                WaitForSingleObject(event, INFINITE);
            }
            let _ = CloseHandle(event);
            d3d(armed, "SetEventOnCompletion")
        }
    }

    /// Read a destination back as tightly packed BGRA rows. Submits its own
    /// frame and waits for it.
    pub fn read_back(&self, texture: &ID3D12Resource) -> Result<Vec<u8>, PresentError> {
        // SAFETY: footprint query, readback buffer creation, copy and Map
        // through owned wrappers; every row read lies inside the mapped
        // footprint.
        unsafe {
            let desc = texture.GetDesc();
            let mut footprint = D3D12_PLACED_SUBRESOURCE_FOOTPRINT::default();
            let mut total = 0u64;
            self.device.GetCopyableFootprints(
                &desc,
                0,
                1,
                0,
                Some(&mut footprint),
                None,
                None,
                Some(&mut total),
            );
            let buffer_desc = D3D12_RESOURCE_DESC {
                Dimension: D3D12_RESOURCE_DIMENSION_BUFFER,
                Alignment: 0,
                Width: total,
                Height: 1,
                DepthOrArraySize: 1,
                MipLevels: 1,
                Format: DXGI_FORMAT_UNKNOWN,
                SampleDesc: DXGI_SAMPLE_DESC {
                    Count: 1,
                    Quality: 0,
                },
                Layout: D3D12_TEXTURE_LAYOUT_ROW_MAJOR,
                Flags: D3D12_RESOURCE_FLAG_NONE,
            };
            let mut buffer: Option<ID3D12Resource> = None;
            d3d(
                self.device.CreateCommittedResource(
                    &heap(D3D12_HEAP_TYPE_READBACK),
                    D3D12_HEAP_FLAG_NONE,
                    &buffer_desc,
                    D3D12_RESOURCE_STATE_COPY_DEST,
                    None,
                    &mut buffer,
                ),
                "CreateCommittedResource (readback)",
            )?;
            let buffer = buffer.ok_or_else(|| PresentError("no readback buffer".into()))?;
            self.request_state(texture, D3D12_RESOURCE_STATE_COPY_SOURCE);
            let dst = D3D12_TEXTURE_COPY_LOCATION {
                pResource: std::mem::ManuallyDrop::new(Some(buffer.clone())),
                Type: D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT,
                Anonymous: D3D12_TEXTURE_COPY_LOCATION_0 {
                    PlacedFootprint: footprint,
                },
            };
            let src = D3D12_TEXTURE_COPY_LOCATION {
                pResource: std::mem::ManuallyDrop::new(Some(texture.clone())),
                Type: D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX,
                Anonymous: D3D12_TEXTURE_COPY_LOCATION_0 {
                    SubresourceIndex: 0,
                },
            };
            self.list.CopyTextureRegion(&dst, 0, 0, 0, &src, None);
            let mut dst = dst;
            let mut src = src;
            std::mem::ManuallyDrop::drop(&mut dst.pResource);
            std::mem::ManuallyDrop::drop(&mut src.pResource);
            self.end_frame(true)?;

            let mut mapped = std::ptr::null_mut();
            d3d(buffer.Map(0, None, Some(&mut mapped)), "Map (readback)")?;
            let (w, h) = (desc.Width as usize, desc.Height as usize);
            let pitch = footprint.Footprint.RowPitch as usize;
            let mut out = vec![0u8; w * h * 4];
            for row in 0..h {
                std::ptr::copy_nonoverlapping(
                    (mapped as *const u8).add(footprint.Offset as usize + row * pitch),
                    out.as_mut_ptr().add(row * w * 4),
                    w * 4,
                );
            }
            buffer.Unmap(0, None);
            Ok(out)
        }
    }
}

impl D3d12Host for TestD3d12Host {
    fn device(&self) -> Option<ID3D12Device> {
        Some(self.device.clone())
    }

    fn command_list(&self) -> Option<ID3D12GraphicsCommandList> {
        Some(self.list.clone())
    }

    fn request_state(&self, resource: &ID3D12Resource, state: D3D12_RESOURCE_STATES) {
        let mut current = self.state.lock().expect("state");
        if *current == state {
            return;
        }
        let barrier = D3D12_RESOURCE_BARRIER {
            Type: D3D12_RESOURCE_BARRIER_TYPE_TRANSITION,
            Flags: D3D12_RESOURCE_BARRIER_FLAG_NONE,
            Anonymous: D3D12_RESOURCE_BARRIER_0 {
                Transition: std::mem::ManuallyDrop::new(D3D12_RESOURCE_TRANSITION_BARRIER {
                    pResource: std::mem::ManuallyDrop::new(Some(resource.clone())),
                    Subresource: D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES,
                    StateBefore: *current,
                    StateAfter: state,
                }),
            },
        };
        // SAFETY: recording a transition on the open list for a live
        // resource; the barrier's reference is released straight after.
        unsafe {
            self.list.ResourceBarrier(std::slice::from_ref(&barrier));
            let transition = std::mem::ManuallyDrop::into_inner(barrier.Anonymous.Transition);
            drop(std::mem::ManuallyDrop::into_inner(transition.pResource));
        }
        *current = state;
    }

    fn frame_fence(&self) -> Option<ID3D12Fence> {
        Some(self.frame_fence.clone())
    }

    fn next_frame_fence_value(&self) -> Option<u64> {
        (!self.frame_value_missing.load(Ordering::Relaxed))
            .then(|| *self.frame.lock().expect("frame") + 1)
    }
}
