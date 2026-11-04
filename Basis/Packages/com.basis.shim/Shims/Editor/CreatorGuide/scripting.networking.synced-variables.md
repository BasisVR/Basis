A synced variable is a value the **owner** writes and every other player reads. Basis sends it as a compact delta stream, interpolates continuous values on the receiving side, and sends a full keyframe to players who join late. You do not write any serialization code.

## Choosing continuous or reliable

| You need | Use |
| --- | --- |
| A value that changes often and should look smooth (a lever, a dial, a colour fade) | A synced variable with **Interpolate** on. |
| A value that must be exact and changes rarely (a score, a mode, a door open flag) | A synced variable with **Interpolate** off (`Int`, `Bool`, `Byte`), or a reliable [network event](/en/docs/scripting/networking/network-events). |
| A moving object | [`BasisSyncedTransform`, `BasisSyncedRigidbody` or `BasisPickupSyncNetworking`](/en/docs/scripting/networking/network-components). |

> [!warn]
> `BasisSyncedParameters` and your own `BasisSyncedObject` subclasses are not on the default allowlists for uploaded content. They are stripped from a `.bee` unless the type is added to the content police selectors. In uploaded Cilbox content, sync values with [network events](/en/docs/scripting/networking/network-events#cilbox) and moving objects with **Basis Synced Transform**.

## BasisSyncedParameters: no serialization code

Add a **Basis Synced Parameters** component and declare named parameters in the inspector. Each has a type (`Float`, `Angle`, `Vector2`, `Vector3`, `Vector4`, `Color`, `Rotation`, `Int`, `UInt`, `UShort`, `Bool`, `Byte`), an **Interpolate** flag, and per axis compression.

Then read and write them by name from any script:

```csharp title="Lever.cs (Native)"
using Basis.Scripts.Networking.Sync;
using UnityEngine;

public class Lever : MonoBehaviour
{
    public BasisSyncedParameters sync;
    public Transform handle;
    int angleId;

    void Start()
    {
        angleId = sync.GetParameterId("Angle");
    }

    public void SetAngleFromHand(float degrees)
    {
        if (!sync.IsLocalOwner()) sync.TakeOwnership();
        sync.SetAngle(angleId, degrees);
    }

    void Update()
    {
        handle.localRotation = Quaternion.Euler(sync.GetAngle(angleId), 0f, 0f);
    }
}
```

- Writes only count on the owner. Take ownership before writing (see [Ownership](/en/docs/scripting/networking/ownership)).
- Reads work everywhere. On the owner you get the value you wrote; on everyone else you get the interpolated network value.
- Resolve the name to an id once with `GetParameterId` and use the id overloads in `Update` to skip the name lookup.

There are no change callbacks. Read the value in `Update` and react when it differs from what you last saw:

```csharp
int lastScore = -1;

void Update()
{
    int score = sync.GetInt(scoreId);
    if (score == lastScore) return;
    lastScore = score;
    scoreLabel.text = score.ToString();
}
```

## Custom BasisSyncedObject

When your values come from your own fields, derive from `BasisSyncedObject`. Register the fields in `Awake`, push your live values in `OnBeforeTransmit` (owner), and read them back on remote clients.

```csharp title="Turret.cs (Native)"
using Basis.Scripts.Networking.Sync;
using UnityEngine;

public class Turret : BasisSyncedObject
{
    public Transform barrel;
    public float yaw;
    public bool firing;

    BasisSyncHandle yawHandle, firingHandle;

    protected void Awake()
    {
        yawHandle = RegisterAngle(BasisQuantSpec.Ranged(-180f, 180f, 12));
        firingHandle = RegisterBool();
    }

    protected override void OnBeforeTransmit()
    {
        LocalSet(yawHandle, yaw);
        LocalSet(firingHandle, firing);
    }

    void Update()
    {
        if (!IsOwnedLocallyOnClient)
        {
            yaw = GetFloat(yawHandle);
            firing = GetBool(firingHandle);
        }
        barrel.localRotation = Quaternion.Euler(0f, yaw, 0f);
    }
}
```

> [!warn]
> Register every field in `Awake`, before the object becomes network ready. The field layout is the wire format. Every client must register the same fields in the same order.

`OnBeforeTransmit` runs only on the frames a packet is due, not every frame. Keep it a plain copy of your values.

To drive a transform directly from registered fields, call `BindTransform(target, position, rotation, scale, worldSpace)`; the driver then applies interpolated values in a job without your `Update` running.

## Compression

Every float can be sent raw (32 bits), as half precision (16 bits), or **ranged**: a minimum, maximum and bit count. A lever between 0 and 90 degrees needs far fewer bits than a world position.

```csharp
RegisterFloat(BasisQuantSpec.Ranged(0f, 1f, 8));      // 0..1 in 256 steps
RegisterFloat(BasisQuantSpec.Half);                    // 16 bit float
RegisterAngle(BasisQuantSpec.Ranged(-180f, 180f, 10)); // ~0.35 degree steps
```

In `BasisSyncedParameters` the same choice is the **Compression** setting on each axis.

## Send rate settings

`BasisSyncedObject` exposes its timing in the inspector:

| Setting | Default | Meaning |
| --- | --- | --- |
| `SendIntervalSeconds` | 0.05 | How often the owner may send (20 per second). Nothing is sent while values are unchanged. |
| `KeyframeIntervalSeconds` | 0.5 | How often a full state is sent so lost deltas heal. Backs off while idle. |
| `DistanceReduction` | on | Send less often to players far away. |
| `RelevanceCulling` / `RelevanceRadius` | off / 50 | Do not send at all to players beyond the radius. |
| `Extrapolate` | off | Keep moving remote values forward briefly when packets are late. |
| `UseDirectP2P` | on | Use a direct peer connection when one exists. |

## Cilbox

A Cilbox script cannot derive from `BasisSyncedObject`. In uploaded content:

- Moving objects: add a **Basis Synced Transform** or **Basis Pickup Sync Networking** component. No script needed.
- Values: keep them on the owner and send them as [network events](/en/docs/scripting/networking/network-events#cilbox), re-sending to [late joiners](/en/docs/scripting/networking/late-joiners). The [Synced Toggle](/en/docs/scripting/examples/synced-toggle) example is the template.
