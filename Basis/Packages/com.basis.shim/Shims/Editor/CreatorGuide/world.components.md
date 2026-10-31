These components are the building blocks of a Basis world. None of them need code. Items marked *(client builds)* are not on the default allowlist for uploaded content; see [Allowed Components](/en/docs/scripting/allowed-components).

## Required

| Component | What it does |
| --- | --- |
| **Basis Scene** | Marks the scene as a Basis world. Sets the **Spawn Point**, the **Respawn Height** players fall back from, the audio mixer group and the main camera. One per world. See [Building](/en/docs/world/building). |

## Interaction

| Component | What it does |
| --- | --- |
| **Basis Interactable Shim** | Makes an object pressable from a script: `ButtonDown` and `ButtonUp`, with hover and press colours. |
| **Basis Pickup Interactable** | Lets players grab, hold and use an object. Raises `OnPickupUse` when the trigger is pressed while held. |
| **Basis Interactable Button** | A ready made pressable button (com.basis.examples, `ExampleInteractableButton` prefab). |
| **Basis Seat** | Lets players sit. Rotation limits, highlight mode, leg and hip control, leg poses, hand and foot targets, a script-set offset, and scripting through `TrySeatLocalPlayer`, `EjectLocalPlayer` and `IsAvailable`. |
| **Basis Avatar Pedestal** | Shows an avatar and lets players switch to it. Worlds only. |
| **Basis Runtime Loader** *(client builds)* | Spawns props from a load config, for example from a button. See [Scene Props](/en/docs/world/scene-props). |

## Networking

| Component | What it does |
| --- | --- |
| **Basis Pickup Sync Networking** | Syncs a pickup's pose and holder. |
| **Basis Synced Transform** | Syncs a moving object. **Basis Synced Rigidbody** *(client builds)* adds velocities. |
| **Basis Synced Parameters** *(client builds)* | Sync named values (floats, ints, bools, colours). |
| **Basis Seat Sync** | Syncs who is sitting in a seat. |
| **Basis Networked Vehicle** | Synced drivable vehicles, built from vehicle body, wheel, thruster and pilot seat parts. |

See [Network Components](/en/docs/scripting/networking/network-components).

## Media and rendering

| Component | What it does |
| --- | --- |
| **Basis Media Player** | Synced video and stream playback. See [Media Player](/en/docs/world/media-player). |
| **Basis SDK Mirror** | A mirror. Set the render texture size, far clip and whether post processing renders in the reflection. Mirrors render the scene again. Keep them small and off by default. |
| **TextMesh Pro** | Text on signs and UI. Supported in worlds and in Cilbox scripts. |
| **Canvas** | World space UI that works with the Basis pointer. See [UI Canvas](/en/docs/world/ui-canvas). |

## Player and platform

| Component | What it does |
| --- | --- |
| **Basis Locomotion Zone** *(client builds)* | Changes jump height, walk and run speed, gravity or movement mode while the local player is inside its trigger. |
| **Basis Platform Switch** | Enables or disables objects and components per platform (VR, desktop, mobile) from a list of rules. |

## Scripting

| Component | What it does |
| --- | --- |
| **Cilbox Scene Basis** / **Cilbox Prop Basis** | The sandbox your world's or prop's `[Cilboxable]` scripts run in. See [Scripting](/en/docs/scripting/scripting). |
| **Basis OSC** | Publish and subscribe to OSC messages. See [OSC](/en/docs/scripting/osc). |
| **Basis Action Trigger** | Forwards UI pointer events (enter, exit, click, drag) to a Cilbox script. |

> [!info]
> Every component is documented in the Unity editor: select it and use the documentation button in its inspector, or open **Basis > Creator Guide** for this guide offline.
