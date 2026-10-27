Most syncing needs no script. Add one of these components and configure it in the inspector. **Uploaded** means the component is on the default allowlist for uploaded worlds and props; the others work in client builds, or after you add them to the content police selectors (see [Allowed Components](/en/docs/scripting/allowed-components)).

| Component | Syncs | Uploaded | Notes |
| --- | --- | --- | --- |
| **Basis Pickup Sync Networking** | A held object's pose and who holds it. | Yes | Takes ownership on grab. `CanNetworkSteal`, `AttachToHandOnGrab`, `FullRateWhileHeld`, `RemoteDeadReckon`. |
| **Basis Synced Transform** | Position, rotation and scale. Per axis toggles and compression. | Yes | For anything the owner moves from code or animation. |
| **Basis Seat Sync** | Who sits in a **Basis Seat**. | Yes | Arbitrates when two players try to sit at once. |
| **Basis Media Player** | Video URL, play state and time. | Yes | See [Media Player](/en/docs/world/media-player). |
| **Basis Synced Rigidbody** | Pose plus linear and angular velocity. | No | Remote copies are kinematic (`DriveRemoteKinematic`) and follow the owner's simulation. |
| **Basis Synced Parameters** | Named values: floats, vectors, colours, ints, bools. | No | See [Synced Variables](/en/docs/scripting/networking/synced-variables). |

> [!warn]
> **Basis Object Sync Networking** is deprecated. It still loads in old content as a pickup, but new content should use **Basis Pickup Sync Networking**.

## A synced pickup

1. Add a collider and a **Rigidbody** to the object.
2. Add **Basis Pickup Interactable**.
3. Add **Basis Pickup Sync Networking** and assign the pickup to its `BasisPickupInteractable` field.

The inspector shows live sync state (owner, buffer depth, bandwidth) in play mode.

## Cilbox

Cilbox scripts cannot derive from the sync components, but they can sit next to them: let **Basis Synced Transform** move the object and use a `BasisNetworkShim` for everything else.

## Avatars

Avatars allow `BasisSyncedTransform`, `BasisPickupSyncNetworking` and `BasisNetworkShim`. See [Avatar Scripting](/en/docs/scripting/avatar-scripting).
