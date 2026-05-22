# Basis Tracker Objects Integration

Bridges [`com.basis.trackerobjects`](../com.basis.trackerobjects/REQUIREMENTS.md) into the Basis library menu. When a prop has been instantiated and shows up in the library's instantiated-items tab, this package adds an **Assign** button to the row. Clicking it opens a tracker picker; confirming a tracker binds the prop's GameObject to that tracker via `BasisTrackerObjectManager`. Clicking **Unbind** removes the binding.

## Why a separate package

`com.basis.trackerobjects` references `Basis Framework` for the types it needs to drive a transform (`BasisInput`, `BasisLocalPlayer`, `BasisRuntimeSpawnRegistry`). That means `Basis Framework` can't reference `com.basis.trackerobjects` back — the asmdef graph would cycle. This integration package references both and is the only place that can wire a library-menu button into a `BasisTrackerObjectManager.TryCreateBinding` call. Same pattern as `com.basis.integration.audiolink`.

## What it adds

- A `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` subscriber on `LibraryProvider.OnInstanceRowCreated`. For every instantiated-object row that `LibraryProvider` builds, this appends a `StandardButton` between the existing Select and Teleport buttons.
- The button is non-interactable for `SpawnMode.Scene` and `SpawnMethod.Embedded` items — same rule the Select button applies.
- Clicking **Assign** opens a `DialogBox<BasisInput>` modal listing the currently-connected trackers eligible for prop binding. Confirming a row calls `BasisTrackerObjectManager.TryCreateBinding` with the spawn instance's `LoadedNetID` and `GameObject` transform. The picker excludes:
  - `BasisVirtualMidpointInput` instances (the virtual half of an active pair).
  - Trackers with `BasisInput.IsLinked == true` (one half of an active pair).
  - Trackers whose `UniqueDeviceIdentifier` has a `BasisTrackerRoleOverride.TryGetOverride` hit.
  - Devices the input matcher has pinned to a fixed role (HMD, named controllers, etc.).
  - Trackers currently driving an avatar bone via calibration. Decalibrate first if you want to reuse a calibrated tracker for a prop.
- Clicking **Unbind** calls `BasisTrackerObjectManager.TryRemoveBinding` directly — no confirmation dialog. Unbind isn't destructive; the binding just lifts.

## Compile guards

The assembly defines two version constraints:

- `com.basis.framework` → `BASIS_FRAMEWORK_EXISTS`
- `com.basis.trackerobjects` → `BASIS_TRACKEROBJECTS_EXISTS`

Both must be present for this package to compile. If either is removed from the project, this assembly drops out silently.

## See also

- [`com.basis.trackerobjects/REQUIREMENTS.md`](../com.basis.trackerobjects/REQUIREMENTS.md) — full v1 spec for the binding manager, pose drive, pickup veto, and registry-cleanup contract.
- `LibraryProvider.OnInstanceRowCreated` — the event this package subscribes to. Lives in `com.basis.framework`.
- `com.basis.integration.audiolink` — sibling integration package that follows the same bridge pattern.
