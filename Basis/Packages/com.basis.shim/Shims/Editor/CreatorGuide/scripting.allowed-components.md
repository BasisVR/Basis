When content loads, Basis's **content police** removes every component whose type is not on the allowlist for that kind of content. Scripts marked `[Cilboxable]` are converted to sandboxed proxies first, so they survive.

## The allowlists

The lists live in `Packages/com.basis.sdk/Settings/`:

| Asset | Applies to |
| --- | --- |
| `SceneContentPoliceSelector` | Worlds |
| `PropContentPoliceSelector` | Props |
| `AvatarContentPoliceSelector` | Avatars |

Each is a list of full type names. Matching is exact: a subclass of an allowed type is **not** allowed unless it is listed itself.

## What is allowed by default

**Worlds and props:** rendering (mesh, skinned mesh, line, trail, particle and LOD components, lights, reflection probes, cameras), physics (colliders, rigidbodies, joints), audio sources and Steam Audio, animators and animation, UI canvases and TextMesh Pro, `VideoPlayer`, VFX Graph binders, AudioLink, Basis constraints, the Basis media player, mirror, seats, pickups, `BasisSyncedTransform`, `BasisPickupSyncNetworking`, `BasisSeatSync`, vehicles, `BasisPlatformSwitch`, and the Cilbox sandbox and shims. Worlds also allow navigation (`NavMeshAgent`, `NavMeshObstacle`), post processing volumes and the avatar pedestal.

**Avatars:** rendering, physics, audio, animators, constraints, seats, synced transforms, OSC and the avatar shims. No cameras, lights, UI or video.

**Not allowed by default** (client builds only): `BasisSyncedParameters`, `BasisSyncedRigidbody`, `BasisLocomotionZone`, `BasisRuntimeLoader`, and any native `MonoBehaviour` of your own.

> [!info]
> The selector assets are the source of truth and change between versions. Open them in the inspector to see the current list.

## Checking your content

- Enter play mode: Basis applies the same policing, and removed components are logged.
- **Basis > Tools > Cilbox Permissions** lists what each sandbox lets scripts call.

## Adding a component (client builds)

If you ship your own client, add the type's full name to the matching selector asset. Content built for the public client cannot rely on this; there, convert the logic to a `[Cilboxable]` script.
