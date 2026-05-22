# Basis Tracker Objects — v1 requirements

## Purpose

Bind a SteamVR / OpenXR tracker's pose to an arbitrary GameObject so the GameObject follows the tracker in real time, locally, with remote players seeing the motion via the existing networked object sync pipeline. Example use cases: physical prop tracking (e.g. a tracker chip stuck to a juggling ball), and assigning a tracker to a real-life dolly system that can drive the handheld camera.

## v1 scope in one breath

A single MonoBehaviour-free runtime (`BasisTrackerObjectManager`) maintains a list of `BasisTrackerBinding` records and writes each binding's tracker pose to its target transform every render frame. A "Assign Tracker" button on each instantiated-object row in the library menu opens a tracker picker; confirming captures the prop's current relative pose to the tracker as a fixed offset and locks the binding in. Remote players see the motion because the existing `BasisObjectSyncNetworking` on the spawned instance already replicates transform updates. Pickup is vetoed while a binding is active, including for the binder. Bindings auto-clear when the underlying spawn instance is removed by anyone (local user, server admin, session cleanup) and are not persisted across sessions.

## Package split

| Package                                  | Owns                                                                                                                                                |
| ---------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------- |
| `com.basis.trackerobjects`               | Binding data type, manager, per-frame pose drive, offset capture, pickup-veto registration, registry-removal subscription, events.                  |
| `com.basis.integration.trackerobjects`   | Library row subscriber and tracker picker dialog — the bridge that calls into the manager when the user clicks Assign/Unbind on an instance.        |
| `com.basis.framework`                    | `LibraryProvider.OnInstanceRowCreated` event the bridge subscribes to, plus the localization keys (`library.assignTracker`, `library.unbindTracker`, `library.trackerPicker.*`) in `Localization/Languages/*.json`. |

A three-package split because `com.basis.trackerobjects` references `Basis Framework` for `BasisInput`/`BasisLocalPlayer`/`BasisRuntimeSpawnRegistry`, which means framework can't reference back into trackerobjects without a circular asmdef ref. `com.basis.integration.trackerobjects` references both and is the only place that can wire UI clicks into manager calls. Pattern mirrors `com.basis.integration.audiolink`. No framework-side type leaks into `BasisTrackerBinding` or the manager's public API.

## Public API

### `BasisTrackerBinding`

```csharp
public class BasisTrackerBinding
{
    public int Id;
    public BasisInput Tracker;
    public Transform Target;
    public string UniqueDeviceIdentifier;
    public string LoadedNetID;
    public Vector3 LocalPositionOffset;
    public Quaternion LocalRotationOffset;
}
```

`LoadedNetID` is the `BasisRuntimeSpawnRegistry.SpawnInstance.LoadedNetID` of the target's spawn instance. Used to match registry-removal events by ID (the Target transform may already be Unity-null by the time the event fires).

Offsets are expressed in tracker-local space so the per-frame drive is `target.SetPositionAndRotation(trackerPos + trackerRot * LocalPositionOffset, trackerRot * LocalRotationOffset)`.

### `BasisTrackerObjectManager`

```csharp
public static class BasisTrackerObjectManager
{
    public const int RenderPriority = 99;

    public static readonly List<BasisTrackerBinding> Bindings;

    public static event Action<BasisTrackerBinding> OnBindingCreated;
    public static event Action<BasisTrackerBinding> OnBindingRemoved;

    public static bool TryCreateBinding(BasisInput tracker, Transform target, string loadedNetID, out int id);
    public static bool TryRemoveBinding(int id);
    public static bool TryGetBindingByLoadedNetID(string loadedNetID, out BasisTrackerBinding binding);
}
```

`TryCreateBinding` extends the existing signature with `loadedNetID` (required for registry-removal cleanup). It captures the current relative pose of `target` to `tracker` as the offset, registers a pickup veto on the target's `BasisPickupInteractable` if present, and stores the pre-bind `Rigidbody.isKinematic` value internally so it can be restored on unbind. Returns false if either argument is null, if `loadedNetID` is null/empty, or if a binding for that `LoadedNetID` already exists.

`TryRemoveBinding` unregisters the pickup veto, restores the prior `isKinematic`, and removes the binding from the list.

`OnBindingCreated` / `OnBindingRemoved` exist so the UI can refresh row labels without polling. The events fire on the main thread, synchronously, after the binding list mutation.

## Pose drive

The manager subscribes to `BasisLocalPlayer.AfterSimulateOnRender` at priority `99` (existing). The handler walks `Bindings`, skips entries where `Tracker == null` or `Target == null` (handles destroyed-during-frame races), reads the tracker's world pose, applies the per-binding offset, and writes to the target.

The handler MUST be allocation-free on the hot path (per `STYLE.md` rules). Use cached locals; no LINQ; no per-frame closure capture.

## UI integration (framework side)

### Entry point

A new `PanelButton` is added to each instantiated-object row in `LibraryProvider.CreateListEntry`, placed between the existing "Select" and "Teleport" buttons.

| Binding state for this `InstanceId` | Button style       | Button label (localization key)        |
| ----------------------------------- | ------------------ | -------------------------------------- |
| No binding                          | `StandardButton`   | `library.assignTracker` ("Assign")     |
| Binding exists                      | `StandardButton`   | `library.unbindTracker` ("Unbind")     |

Labels are deliberately one word — the prop-row context already makes "what's being assigned" obvious, and the row's equal-share horizontal layout means a longer label would squeeze Select/Teleport/Remove visibly.

The style stays fixed; only the label flips. This matches the existing Select/Deselect pattern on the same row and avoids signalling unbind as destructive (it isn't — no data loss, the binding just lifts).

The button is skipped entirely (not added to the row) when the instance's `SpawnMode == Scene` or `SpawnMethod == Embedded`. A disabled fourth button would push Select/Teleport/Remove off the right of the row, and those instances can't host a tracker binding regardless — a scene spawn isn't user-owned and an embedded one has no pickup/rigid surface to drive.

### Tracker picker dialog

Clicking "Assign Tracker" opens a modal dialog listing currently-connected `BasisInput` devices whose role is `GenericTracker` (or whatever the equivalent enum value resolves to for SteamVR/OpenXR pucks and chips). The list further excludes:

- Trackers with `BasisInput.IsLinked == true` — currently fused into a virtual midpoint pair and committed to an avatar body role via the partner pairing.
- Trackers whose `UniqueDeviceIdentifier` matches a `BasisTrackerRoleOverride.TryGetOverride` hit — calibration will claim them as a body joint, so binding them to a prop would race with the calibration assignment.
- `BasisVirtualMidpointInput` instances — caught by the `GenericTracker` role filter today (midpoints take a body role), but excluded explicitly so a future shift in the role taxonomy can't accidentally surface a midpoint here.

Each row shows the tracker's role / display name and its `UniqueDeviceIdentifier`. Selecting a row calls `BasisTrackerObjectManager.TryCreateBinding(tracker, instanceGo.transform, instance.LoadedNetID, out _)` and closes the dialog.

Clicking "Unbind Tracker" calls `TryGetBindingByLoadedNetID` then `TryRemoveBinding(binding.Id)` directly, no confirmation dialog.

The picker is user-initiated, so the host must always show it — if the dialogue helper exposes a `divertible` flag (as `BasisMenuDialoguePanel.CreateNew` does), it stays `false`. Closing the picker without a selection is equivalent to cancel: no re-prompt, no notification-center pending entry (i.e. don't pass a `reopen` callback to `DialogBox<T>.Create`).

Localization strings land in every `Localization/Languages/*.json` file. English values:

- `library.assignTracker` — "Assign"
- `library.unbindTracker` — "Unbind"
- `library.trackerPicker.title` — "Choose Tracker"
- `library.trackerPicker.empty` — "No trackers are currently connected."
- `library.trackerPicker.confirm` — "Bind"
- `library.trackerPicker.cancel` — "Cancel"

### Calibration model (offset capture)

The offset is snapshot-on-bind: at the moment `TryCreateBinding` runs, the relative pose of `target` to `tracker` is captured and stored on the binding. This means the user's workflow is:

1. Spawn the prop.
2. Position the prop where it should sit relative to the tracker chip (e.g. velcro the chip to a juggling ball, or hold the prop against the tracker in the desired orientation).
3. Open the library menu, find the instance, click "Assign Tracker", pick the tracker, confirm.

The captured offset is whatever the relative pose was at confirm time. No numeric input UI; no recalibration affordance in v1 (see "out of v1").

### Refusing release while bound

When a binding is created against a target that has a `BasisPickupInteractable`, the manager registers two predicates that always return `false`:

- `BasisPickupInteractable.CanHoverInjected.Add(...)` — prevents the pickup-hover highlight from appearing.
- `BasisPickupInteractable.CanInteractInjected.Add(...)` — prevents any input from initiating a grab.

These are removed in `TryRemoveBinding`. The veto applies to the binder's own inputs too — for v1 this is acceptable because the natural unbind path is the library menu, and the juggling-balls / dolly-camera cases don't want the binder fighting the tracker by accidentally grabbing the prop. A future iteration can refine this if a use case emerges.

Existing `BasisObjectSyncNetworking.CanNetworkSteal` is unchanged; the veto sits in front of it, so steal attempts from remote players are blocked at the local `CanInteract` check before any ownership transfer is initiated.

### Kinematic capture and restore

`BasisObjectSyncNetworking.ControlState` sets `Rigidbody.isKinematic = false` when the object is locally owned, so physics drives the rigidbody. With a tracker also writing the transform every render frame, the two compete and the prop jitters. To prevent this:

- `TryCreateBinding` captures `pickupInteractable.RigidRef.isKinematic` and stores it on the binding.
- It then sets `isKinematic = true`, and the per-frame pose drive re-asserts `isKinematic = true` each frame. Re-assertion (rather than one-shot) is required because `BasisObjectSyncNetworking.Awake` and `ControlState` both set `isKinematic = false` for locally-owned props, and `ControlState` can fire after bind on ownership-transfer events. Physics moving the rigidbody between our writes shows up as Scene-view flicker even when Game view (which renders right after `onBeforeRender`) looks clean.
- `TryRemoveBinding` restores the captured value.

If the target has no `BasisPickupInteractable` or no `Rigidbody`, the kinematic toggle is skipped.

## Removal handling

The manager subscribes to `BasisRuntimeSpawnRegistry.OnRegistryChanged` at init and handles all four change types:

| `RegistryChangeType` | `instance` payload | Action                                                                              |
| -------------------- | ------------------ | ----------------------------------------------------------------------------------- |
| `Added`              | non-null           | Ignore.                                                                             |
| `Removed`            | non-null           | If a binding exists for `instance.LoadedNetID`, call `TryRemoveBinding(binding.Id)`. |
| `ClearedUrl`         | non-null           | Same as `Removed`.                                                                  |
| `ClearedAll`         | null               | Clear all bindings.                                                                 |

By the time the event fires, `SpawnedGameobjects` has already been cleared and the GameObject may be mid-destroy; the binding's stored `LoadedNetID` is the only reliable identifier. The pose-drive loop tolerates a Unity-null `Target` between the destroy and the event firing.

This same path covers:

- Local user removing their own prop via the library menu.
- Server admin removing someone else's prop (network unload broadcast → `BasisNetworkSpawnItem.DestroyGameobject` → `BasisRuntimeSpawnRegistry.RemoveByLoadedNetId` → event fires on the binder's client too).
- Session cleanup via `BasisRuntimeSpawnRegistry.ClearAllNetworking`.

There is no separate per-source removal plumbing; the registry funnel is sufficient.

The no-arg `BasisRuntimeSpawnRegistry.ClearAll()` overload does not raise any event. If it is called, existing bindings will not be cleared by this mechanism. The pose-drive loop's `Target == null` guard prevents this from causing a hard error; the binding entries leak until process exit. This is acceptable for v1.

## Network sync

The binding does not introduce a new network channel. Spawned game objects already carry `BasisObjectSyncNetworking`, which replicates their transform via `SendCustomNetworkEvent(... DeliveryMethod.Sequenced)` while ownership is local. Because the manager writes the local `target.transform` every render frame, the next outbound sync sample includes the tracker-driven pose, and remote players interpolate to it through the existing `BasisObjectSyncDriver` path.

Two consequences:

- Remote update rate is whatever `BasisObjectSyncDriver` sends at — not the avatar bone rate. If this turns out to be visibly too low for hand-bone-rate motion (the juggling case), it's a follow-up question for the object-sync subsystem rather than a tracker-objects problem.
- Network compression is the existing `BasisCompression.QuaternionCompressor` path. No special handling for tracker-driven motion.

A peer-to-peer transport (`BasisP2PManager`) carries voice and avatar transforms at configurable rates (20–250 Hz, default 60 Hz). Object sync is not currently routed over this transport — `BasisObjectSyncNetworking` continues to send through the server peer. If an object-sync-over-P2P path lands later, tracker-bound props inherit the new rate for free because this package writes to the local transform only and does not own the sync surface.

If the target has no `BasisNetworkContentBase` / `BasisObjectSyncNetworking` (i.e. it's a local-only object), binding still works for the local player but no remote replication occurs. This is expected; a warning is logged via `BasisDebug.Log` with `BasisDebug.LogTag.TrackerObjects` so the user can see why their bound object doesn't appear to move for others.

## Logging

All logging uses `BasisDebug.Log*` with `BasisDebug.LogTag.TrackerObjects`. No `UnityEngine.Debug.Log*` calls in committed code. If `LogTag.TrackerObjects` is not yet present in `BasisDebug`, it is added there as part of the integration.

## Lifecycle and threading

- All operations are main-thread only. The pose-drive loop runs in `AfterSimulateOnRender` (main thread by construction).
- The manager initializes via `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]` and is idempotent under repeated registration (`_subscribed` flag).
- No `MonoBehaviour`; no scene discovery via `FindObjectsByType` / `FindFirstObjectByType`; no allocations in the per-frame path.

## Out of scope — and why

### Persistence across sessions

A binding does not survive a disconnect/reconnect or a session restart. The user re-binds on next session.

**Why**: Persistence requires a stable identifier per binding (likely tracker `UniqueDeviceIdentifier` plus a stable instance reference), a serialization format, and a restore-on-spawn handshake that handles the case where the tracker isn't connected at restore time. None of this is needed for the v1 use cases (juggling demo, dolly camera) which are both per-session activities. Adding it post-v1 is purely additive.

### Manual offset editing UI

The offset is captured at bind time and cannot be tweaked numerically.

**Why**: A numeric XYZ + Euler editor in VR is clunky enough that snapshot-on-bind covers the practical workflow better. If the snapshot is wrong, the user unbinds, repositions, and rebinds. A "Recalibrate" button that re-snapshots without going through unbind/rebind is a trivial follow-up if the friction shows up in practice.

### Multiple trackers per object

A single binding maps one tracker to one transform's root pose. Multi-point rigging (e.g. two trackers driving an articulated prop with IK) is out.

**Why**: Single-tracker covers all v1 use cases. Multi-point rigging is a much larger design surface (which tracker drives which joint, conflict resolution between trackers, calibration UX for multi-anchor poses) and shouldn't be smuggled into the first slice.

### Higher-rate or custom sync path

The binding does not introduce its own network sync. It relies on whatever `BasisObjectSyncNetworking` provides.

**Why**: Reinventing sync inside this package would duplicate compression, ownership, and delivery code that already exists and is already tested. If the existing rate proves insufficient for hand-bone-rate motion, the fix belongs in `BasisObjectSyncDriver` (raise the cadence or expose a per-instance high-rate flag), not here.

### Allow binder to grab through the veto

The pickup veto blocks the binder's own inputs too.

**Why**: For the provided use cases there's no reason the binder needs to grab the prop while it's bound — the tracker is the driver. If a future use case wants "bound but still hand-grabbable for fine adjustment", the predicate can be made input-aware.

### Calibration affordances beyond snapshot-on-bind

No "Recalibrate" button, no "Reset offset to zero" button, no pose-preview during the picker dialog.

**Why**: Snapshot-on-bind plus unbind/rebind covers the workflow. Each of these affordances is independently small to add once a real need surfaces; bundling them speculatively into the current scope stretches the surface unnecessarily.

## Future outlook

Follow-ups likely to be promoted into a later release once the version 1 surface is validated in real use:

- Recalibrate button on the library row when a binding exists (re-snapshots offset without unbind/rebind).
- Per-binding "allow binder to grab" toggle for fine-adjustment workflows.
- Visible binding indicator in-world (small icon above the prop showing it's tracker-driven, and which tracker).
- Object sync routed over the `BasisP2PManager` transport. Not a change in this package — `BasisObjectSyncNetworking` would gain a P2P send path, and tracker-bound props would inherit the higher remote rate automatically. Tracked here so a future reader knows where the rate ceiling moves once that lands.

These are listed for visibility, not committed.

## Integration points to respect

- **Logging**: `BasisDebug.Log*` with `BasisDebug.LogTag.TrackerObjects`. Never `UnityEngine.Debug.Log*` in committed code.
- **STYLE.md compliance**: no allocations on the per-frame path; prefer `TryGetComponent` over `GetComponent`; no `FindObjectsByType` for scene discovery; events driven through `BasisEventDriver` patterns where applicable; Burst-jobbable hot paths considered where the work justifies it.
- **Pickup integration**: use `BasisPickupInteractable.CanHoverInjected` / `CanInteractInjected` for veto. Do not introduce new predicate lists on the pickup component.
- **Registry integration**: subscribe to `BasisRuntimeSpawnRegistry.OnRegistryChanged`. Do not subscribe to per-source removal paths (the registry funnel is exhaustive).
- **Render order**: keep `AfterSimulateOnRender` priority at the existing value (`99`). Other systems may depend on this slot's relative ordering.
