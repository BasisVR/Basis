## Local player zone

Polling the local player's position against a trigger's bounds works the same in native and Cilbox scripts, and does not depend on physics layers.

```csharp title="LocalPlayerZone.cs (Cilbox)"
using Basis.Scripts.BasisSdk.Players;
using Cilbox;
using UnityEngine;

[Cilboxable]
public class LocalPlayerZone : MonoBehaviour
{
    public Collider area;
    public GameObject showWhileInside;
    bool inside;

    void Start()
    {
        showWhileInside.SetActive(false);
    }

    void Update()
    {
        BasisLocalPlayer local = BasisLocalPlayer.Instance;
        if (local == null) return;
        local.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
        bool now = area.bounds.Contains(position);
        if (now == inside) return;
        inside = now;
        showWhileInside.SetActive(inside);
    }
}
```

> [!info]
> For movement changes inside an area (low gravity, flying, faster walking), use the locomotion overrides on `BasisLocalPlayer` from [Common Usages](/en/docs/scripting/common-usages). In client builds, the **Basis Locomotion Zone** component does the same without code.

## Teleporter

Add to a pad or a button. Teleport only affects the local player. Every player teleports themselves and nothing is networked.

```csharp title="Teleporter.cs (Cilbox)"
using Basis.Scripts.BasisSdk.Players;
using Cilbox;
using UnityEngine;

[Cilboxable]
public class Teleporter : MonoBehaviour
{
    public Collider pad;
    public Transform destination;
    bool wasOnPad;

    void Update()
    {
        BasisLocalPlayer local = BasisLocalPlayer.Instance;
        if (local == null) return;
        local.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
        bool onPad = pad.bounds.Contains(position);
        if (onPad && !wasOnPad) TeleportNow();
        wasOnPad = onPad;
    }

    public void TeleportNow()
    {
        BasisLocalPlayer.Instance.Teleport(destination.position, destination.rotation, BasisTeleportMode.Instant);
    }
}
```

## Trampoline

`BasisVelocityShim` is available in worlds. Keep the horizontal speed and replace the fall with a bounce.

```csharp title="Trampoline.cs (Cilbox)"
using Basis.Scripts.BasisSdk.Players;
using Basis.Shims;
using Cilbox;
using UnityEngine;

[Cilboxable]
public class Trampoline : MonoBehaviour
{
    public Collider surface;
    public float bounce = 9f;

    void Update()
    {
        BasisLocalPlayer local = BasisLocalPlayer.Instance;
        if (local == null) return;
        local.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
        if (!surface.bounds.Contains(position)) return;
        Vector3 velocity = BasisVelocityShim.Velocity;
        if (velocity.y > 0.5f) return;
        BasisVelocityShim.SetVelocity(new Vector3(velocity.x, bounce, velocity.z));
    }
}
```

## Lobby count

Show how many players are standing in a lobby area. Every client counts for itself from player positions and no messages are sent.

```csharp title="LobbyCounter.cs (Cilbox)"
using Basis.Scripts.BasisSdk.Players;
using Basis.Shims;
using Cilbox;
using TMPro;
using UnityEngine;

[Cilboxable]
public class LobbyCounter : MonoBehaviour
{
    public Transform center;
    public float radius = 4f;
    public int needed = 4;
    public TextMeshPro label;
    float nextCheck;

    void Update()
    {
        if (Time.time < nextCheck) return;
        nextCheck = Time.time + 0.5f;
        int count = 0;
        foreach (IBasisPlayer player in BasisPlayersShim.Within(center.position, radius)) count++;
        label.text = count >= needed ? "Ready!" : $"{count} / {needed} players";
    }
}
```

Checking twice a second instead of every frame is plenty for a sign.
