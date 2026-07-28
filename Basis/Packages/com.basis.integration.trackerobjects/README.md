# Basis Tracker Objects Integration

Bridges `com.basis.trackerobjects` into the Basis library menu. When a prop has been instantiated and shows up in the library's instantiated-items tab, this package adds an icon-only Assign/Unbind Tracker button to the row (Link/Unlink icons with tooltips, no title text). Clicking the Link icon opens a tracker picker; confirming a tracker binds the prop's GameObject to that tracker via `BasisTrackerObjectManager`. Clicking the Unlink icon removes the binding.

## Why a separate package

`com.basis.trackerobjects` references `Basis Framework` for the types it needs to drive a transform (`BasisInput`, `BasisLocalPlayer`, `BasisRuntimeSpawnRegistry`, `BasisPickupSyncNetworking`). That means `Basis Framework` can't reference `com.basis.trackerobjects` back — the asmdef graph would cycle. This integration package references both and is the only place that can wire a library-menu button into a `BasisTrackerObjectManager.TryCreateBindingAsync` call. Same pattern as `com.basis.integration.audiolink`.

## What it adds

- A `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` subscriber on `LibraryProvider.OnInstanceRowCreated`. For every instantiated-object row that `LibraryProvider` builds, this appends a `StandardButton` between the existing Select and Teleport buttons.
- The button only appears on `SpawnMode.GameObject` (prop) rows — scenes and avatars are skipped, as are `SpawnMethod.Embedded` and static-locked items. Static rows rebuild on the server's Modified broadcast, so the button reappears when the lock clears.
- The button also hides when no spare bindable tracker is connected, and reappears live when one turns up (the hook watches `AllInputDevices` and the binding events). A bound prop's row always keeps its button so Unlink stays reachable. Eligibility changes that don't touch the device list (e.g. calibrating a tracker to a body bone) are picked up on the next device or binding change, or menu rebuild.
- Clicking the Link icon opens a `DialogBox<BasisInput>` modal listing the currently-connected trackers eligible for prop binding. Confirming a row calls `BasisTrackerObjectManager.TryCreateBindingAsync` with the spawn instance's `LoadedNetID` and `GameObject` transform; the manager takes network ownership of the prop before the binding is created. The picker excludes:
  - `BasisVirtualMidpointInput` instances (the virtual half of an active pair).
  - Trackers with `BasisInput.IsLinked == true` (one half of an active pair).
  - Trackers whose `UniqueDeviceIdentifier` has a `BasisTrackerRoleOverride.TryGetOverride` hit.
  - Devices the input matcher has pinned to a fixed role (HMD, named controllers, etc.).
  - Trackers currently driving an avatar bone via calibration. Decalibrate first if you want to reuse a calibrated tracker for a prop.
  - Trackers already bound to another prop (`BasisTrackerObjectManager.IsTrackerBound`). One tracker drives at most one prop; unbind it there first.
  - Trackers out of interaction range of the prop (`BasisTrackerObjectManager.IsWithinBindRange` — the same reach rule as grabbing it). If spare trackers exist but none are in reach, the dialog says so rather than showing the generic empty message.
- Clicking the Unlink icon calls `BasisTrackerObjectManager.TryRemoveBinding` directly — no confirmation dialog. Unbinding isn't destructive; the binding just lifts.
- The row icon follows `OnBindingCreated`/`OnBindingRemoved`, so it stays correct while the menu is open even when a binding dissolves externally (ownership steal, static lock, tracker loss).

## Compile guards

The assembly defines two version constraints:

- `com.basis.framework` → `BASIS_FRAMEWORK_EXISTS`
- `com.basis.trackerobjects` → `BASIS_TRACKEROBJECTS_EXISTS`

Both must be present for this package to compile. If either is removed from the project, this assembly drops out silently.

## See also

- `BasisTrackerObjectManager` in `com.basis.trackerobjects` — the binding manager: pose drive, ownership, pickup veto, and registry-cleanup handling.
- `LibraryProvider.OnInstanceRowCreated` — the event this package subscribes to. Lives in `com.basis.framework`.
- `com.basis.integration.audiolink` — sibling integration package that follows the same bridge pattern.
