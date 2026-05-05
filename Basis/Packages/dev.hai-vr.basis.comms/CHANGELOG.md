Changelog
=====

## 2026-05-01

*Authored by Haï~*

New additions:
- Add a toggle system for avatars using HVRVixxyControl, HVRVixxyMenuItem.
- The toggles are accessible in-game through "Settings > My Avatar" if at least one VixxyMenuItem component is present on the avatar.

Modifications in Basis SDK:
- The following types are now allowed in avatars: HVR.Vixxy.HVRVixxyControl, HVR.Vixxy.HVRVixxyMenuItem, and HVR.Vixxy.HVRVixxyAggregator.
- HVR.Vixxy.HVRVixxyAggregator is not implemented and cannot be used.

Modifications in existing HVR.Basis systems:
- The SettingsProvider avatar tab override now integrates with Vixxy menu items.
- Addresses and values can now be networked directly:
  - Add HVRVariableState to handle the networking of avatar state.
  - Add new packet types in AvatarMessageProcessing to support the networking of avatar state.
  - HVRAvatarComms now uses the networking carrier at index 1 (this means the second one) for networking messages related to avatar state.
  - HVRAvatarComms now holds a reference to a variable store which can be subscribed to (instead of AcquisitionService).
  - AcquisitionService now delegates to a variable store.
- Remove "Avatar" from the display name of the package, so that is now "HVR Basis Comms".
- HVRAddress is now a ScriptableObject.
- Calls to HVRLogging are no longer shown in the stack trace.
- Obsolete AcquisitionAssist and RandomAcquisition components have been removed.
- Unused JiggableSkinnedMeshRenderer class has been removed.

Editor modifications in existing HVR.Basis systems:
- Most HVR components now show additional information in Play Mode about their internal state.
    - HVRAvatarComms now shows all mutualized addresses and the corresponding values.
    - AcquisitionService now shows all registered addresses, the number of event listeners for each, and the last value.
    - OSCAcquisitionServer now shows all received addresses (OSC only) and how many messages have been received for each.
- Add icons to user-facing components.
